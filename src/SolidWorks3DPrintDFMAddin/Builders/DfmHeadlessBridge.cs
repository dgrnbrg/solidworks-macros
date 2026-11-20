using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks3DPrintDFMAddin.Core;
using SolidWorks3DPrintDFMAddin.Data;
using SolidWorks3DPrintDFMAddin.UI;

namespace SolidWorks3DPrintDFMAddin.Builders
{
    /// <summary>
    /// Headless in-process & standalone execution entry point for 3D Print DFM features
    /// (40 deg capped Teardrop Fillets and 2+ tier Integral Crisscross Bridges).
    /// Bypasses PropertyManager Page UI and modal message boxes so MCP automation creates
    /// genuine editable MacroFeatures (or standalone native cuts when fallback is needed).
    /// </summary>
    public static class DfmHeadlessBridge
    {
        public static string ExecuteJson(ISldWorks swApp, string requestJson, bool allowMacroFeature = true)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            bool prevSuppressDialogs = DfmFeatureBuilder.SuppressUserDialogs;
            DfmFeatureBuilder.SuppressUserDialogs = true;
            try
            {
                if (swApp == null)
                    return SerializeError(serializer, "ISldWorks reference is null inside DfmHeadlessBridge.");

                Dictionary<string, object> req = ParseDict(serializer, requestJson);
                string rawMode = (GetString(req, "mode", null) ?? GetString(req, "tool", "teardrop")).Trim().ToLowerInvariant();
                bool isBridge = rawMode == "bridge" || rawMode == "crisscross_bridge" || rawMode == "3dprint_bridge";
                DfmCommandMode cmdMode = isBridge ? DfmCommandMode.CrisscrossBridge : DfmCommandMode.TeardropFillet;

                string targetDoc = GetString(req, "target_doc", null);
                IModelDoc2 model = ResolvePartDoc(swApp, targetDoc);
                if (model == null)
                    return SerializeError(serializer, "No active or matching SOLIDWORKS Part (.SLDPRT) document found.");

                DfmConfig config = DfmConfig.LoadDefaults();
                config.MigrateInMemory();
                config.FeatureConfigId = Guid.NewGuid().ToString("D");
                config.EnableTeardrops = !isBridge;
                config.EnableCrisscrossBridges = isBridge;

                if (req.ContainsKey("overhang_angle_deg"))
                    config.OverhangAngleFromBedDeg = Math.Max(15.0, Math.Min(75.0, GetDouble(req, "overhang_angle_deg", config.OverhangAngleFromBedDeg)));
                if (req.ContainsKey("enable_cap"))
                    config.EnableMaxHeightCap = GetBool(req, "enable_cap", config.EnableMaxHeightCap);
                if (req.ContainsKey("max_cap_height_mm"))
                    config.MaxCapHeightMm = Math.Max(0.1, GetDouble(req, "max_cap_height_mm", config.MaxCapHeightMm));
                if (req.ContainsKey("min_circle_angle_deg"))
                    config.MinCircleAngleToBedDeg = Math.Max(5.0, Math.Min(90.0, GetDouble(req, "min_circle_angle_deg", config.MinCircleAngleToBedDeg)));
                if (req.ContainsKey("min_hole_diameter_mm"))
                    config.MinHoleDiameterMeters = Math.Max(0.2, GetDouble(req, "min_hole_diameter_mm", config.MinHoleDiameterMeters * 1000.0)) * 0.001;
                if (req.ContainsKey("layer_height_mm"))
                    config.LayerHeightMm = Math.Max(0.05, Math.Min(1.0, GetDouble(req, "layer_height_mm", config.LayerHeightMm)));
                if (req.ContainsKey("bridge_angle_offset_deg"))
                    config.BridgeAngleOffsetDeg = GetDouble(req, "bridge_angle_offset_deg", config.BridgeAngleOffsetDeg);
                if (req.ContainsKey("flip_bed_direction"))
                    config.FlipBedDirection = GetBool(req, "flip_bed_direction", false);

                // Resolve explicit solid bodies (by persist ID and/or name)
                List<IBody2> explicitBodies = ResolveBodies(model, req);
                config.BodyPersistIds.Clear();
                foreach (IBody2 b in explicitBodies)
                {
                    byte[] pid = BedStateStore.GetPersistReferenceBytes(model, b);
                    if (pid != null && pid.Length > 0)
                        config.BodyPersistIds.Add(pid);
                }
                List<IBody2> targetBodies = TopologyScanner.GetTargetSolidBodies(model, explicitBodies);

                // Resolve Print Bed orientation
                object resolvedBedEntity = null;
                string bedB64 = GetString(req, "bed_persist_id_b64", null);
                if (!string.IsNullOrWhiteSpace(bedB64))
                {
                    byte[] bedPid = Convert.FromBase64String(bedB64);
                    resolvedBedEntity = BedStateStore.ResolvePersistReference(model, bedPid);
                    if (resolvedBedEntity != null &&
                        TopologyScanner.TryResolveBedReference(resolvedBedEntity, config.FlipBedDirection, out Vector3D bedUp, out Vector3D refPt))
                    {
                        config.BedRefPersistId = bedPid;
                        config.BedUpVector = bedUp;
                        config.BedReferencePoint = refPt;
                        BedStateStore.SaveBedForBodies(model, targetBodies, resolvedBedEntity, config);
                    }
                }

                if (resolvedBedEntity == null)
                {
                    double[] buildDir = GetDoubleArray(req, "build_direction");
                    if (buildDir != null && buildDir.Length >= 3)
                    {
                        Vector3D v = new Vector3D(buildDir[0], buildDir[1], buildDir[2]);
                        if (v.Length > 1e-9)
                        {
                            v = v.Normalized();
                            if (config.FlipBedDirection) v = -v;
                            config.BedUpVector = v;
                        }
                    }
                    else
                    {
                        SavedBedInfo savedBed = BedStateStore.TryLoadBedForBodies(model, targetBodies);
                        if (savedBed != null)
                        {
                            config.BedRefPersistId = savedBed.BedRefPersistId;
                            config.BedUpVector = savedBed.BedUpVector;
                            config.BedReferencePoint = savedBed.BedReferencePoint;
                            config.FlipBedDirection = savedBed.FlipBedDirection;
                        }
                        else
                        {
                            config.BedUpVector = new Vector3D(0, 0, config.FlipBedDirection ? -1 : 1);
                        }
                    }
                }

                // Resolve explicit faces (if specified); otherwise auto-scan targetBodies
                List<IFace2> selectedFaces = ResolveFaces(model, req);
                List<IFeature> createdFeatures = new List<IFeature>();
                int targetsDetected = 0;

                if (!isBridge)
                {
                    List<DetectedHoleFaceGroup> groups = selectedFaces.Count > 0
                        ? TopologyScanner.BuildTeardropTargetsFromSelectedFaces(selectedFaces, config)
                        : TopologyScanner.ScanQualifyingTeardropHoles(targetBodies, config);

                    config.TeardropTargets.Clear();
                    config.TeardropFacePersistIds.Clear();
                    foreach (DetectedHoleFaceGroup g in groups)
                    {
                        config.TeardropTargets.Add(g.Target);
                        if (g.Faces != null)
                        {
                            foreach (IFace2 f in g.Faces)
                            {
                                byte[] fpid = BedStateStore.GetPersistReferenceBytes(model, f);
                                if (fpid != null && fpid.Length > 0)
                                    config.TeardropFacePersistIds.Add(fpid);
                            }
                        }
                    }
                    targetsDetected = groups.Count;

                    if (groups.Count > 0)
                    {
                        createdFeatures = DfmFeatureBuilder.ApplyTeardropFeatures(swApp, model, config, groups);
                    }
                }
                else
                {
                    List<DetectedRecessFace> recesses = selectedFaces.Count > 0
                        ? TopologyScanner.BuildBridgeTargetsFromSelectedFaces(selectedFaces, config)
                        : TopologyScanner.ScanQualifyingDownwardRecesses(targetBodies, config);

                    config.BridgeTargets.Clear();
                    config.BridgeFacePersistIds.Clear();
                    foreach (DetectedRecessFace r in recesses)
                    {
                        config.BridgeTargets.Add(r.Target);
                        byte[] fpid = BedStateStore.GetPersistReferenceBytes(model, r.Face);
                        if (fpid != null && fpid.Length > 0)
                            config.BridgeFacePersistIds.Add(fpid);
                    }
                    targetsDetected = recesses.Count;

                    if (recesses.Count > 0)
                    {
                        createdFeatures = DfmFeatureBuilder.ApplyCrisscrossBridgeFeatures(swApp, model, config, recesses);
                    }
                }

                // Hide all created child sketches cleanly
                HideManagedSketches(model, config);

                bool wantMacroFeature = allowMacroFeature && GetBool(req, "use_macro_feature", true);
                if (wantMacroFeature && createdFeatures.Count > 0)
                {
                    DfmMacroFeatureHelper.CommitAndNestFeatures(model, null, config, cmdMode, createdFeatures);
                }

                List<string> createdNames = new List<string>(createdFeatures.Count);
                foreach (IFeature f in createdFeatures)
                {
                    if (f != null && !string.IsNullOrEmpty(f.Name))
                        createdNames.Add(f.Name);
                }

                string execPath = (wantMacroFeature && !string.IsNullOrEmpty(config.ManagedMacroFeatureName))
                    ? "addin_macro_feature"
                    : "standalone_native_features";

                return serializer.Serialize(new Dictionary<string, object>
                {
                    { "ok", true },
                    { "tool", isBridge ? "3dprint_bridge" : "3dprint_teardrop" },
                    { "execution_path", execPath },
                    { "macro_feature_name", config.ManagedMacroFeatureName },
                    { "targets_detected", targetsDetected },
                    { "features_created_count", createdNames.Count },
                    { "child_features_created", createdNames },
                    { "bed_up", new[]
                        {
                            Math.Round(config.BedUpVector.X, 4),
                            Math.Round(config.BedUpVector.Y, 4),
                            Math.Round(config.BedUpVector.Z, 4)
                        }
                    },
                    { "message", targetsDetected == 0
                        ? "No qualifying target features detected for the specified geometry/bed orientation."
                        : string.Format(CultureInfo.InvariantCulture, "Created {0} DFM feature(s) across {1} target(s).", createdNames.Count, targetsDetected) }
                });
            }
            catch (Exception ex)
            {
                SwAddin.Log("DfmHeadlessBridge.ExecuteJson error: " + ex);
                return SerializeError(serializer, "DfmHeadlessBridge exception: " + ex.Message, ex.ToString());
            }
            finally
            {
                DfmFeatureBuilder.SuppressUserDialogs = prevSuppressDialogs;
            }
        }

        private static void HideManagedSketches(IModelDoc2 model, DfmConfig config)
        {
            if (model == null || config == null) return;
            HashSet<string> skNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (config.TeardropTargets != null)
            {
                foreach (TeardropHoleTarget t in config.TeardropTargets)
                {
                    if (!string.IsNullOrEmpty(t.ManagedSketchName))
                        skNames.Add(t.ManagedSketchName);
                }
            }
            if (config.BridgeTargets != null)
            {
                foreach (CrisscrossBridgeTarget b in config.BridgeTargets)
                {
                    if (!string.IsNullOrEmpty(b.ManagedTier1SketchName)) skNames.Add(b.ManagedTier1SketchName);
                    if (!string.IsNullOrEmpty(b.ManagedTier2SketchName)) skNames.Add(b.ManagedTier2SketchName);
                    if (b.ManagedSketchNames != null)
                    {
                        foreach (string s in b.ManagedSketchNames)
                            if (!string.IsNullOrEmpty(s)) skNames.Add(s);
                    }
                }
            }

            foreach (string name in skNames)
            {
                try
                {
                    model.ClearSelection2(true);
                    if (model.Extension.SelectByID2(name, "SKETCH", 0, 0, 0, false, 0, null, 0))
                        model.BlankSketch();
                }
                catch { }
            }
            try { model.ClearSelection2(true); } catch { }
        }

        private static IModelDoc2 ResolvePartDoc(ISldWorks swApp, string targetDoc)
        {
            IModelDoc2 active = swApp.ActiveDoc as IModelDoc2;
            if (string.IsNullOrWhiteSpace(targetDoc))
                return (active is IPartDoc) ? active : null;

            if (MatchesDoc(active, targetDoc))
                return (active is IPartDoc) ? active : null;

            object[] docs = swApp.GetDocuments() as object[];
            if (docs != null)
            {
                foreach (object dObj in docs)
                {
                    if (dObj is IModelDoc2 candidate && MatchesDoc(candidate, targetDoc))
                    {
                        string path = candidate.GetPathName() ?? "";
                        string title = candidate.GetTitle() ?? "";
                        string actName = !string.IsNullOrEmpty(path) ? Path.GetFileName(path) : title;
                        int err = 0;
                        swApp.ActivateDoc3(actName, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref err);
                        return (candidate is IPartDoc) ? candidate : null;
                    }
                }
            }
            return null;
        }

        private static bool MatchesDoc(IModelDoc2 candidate, string identifier)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(identifier)) return false;
            string clean = identifier.Trim();
            string path = candidate.GetPathName() ?? string.Empty;
            string title = candidate.GetTitle() ?? string.Empty;
            string cleanBase = Path.GetFileNameWithoutExtension(clean);
            return string.Equals(path, clean, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(Path.GetFileName(path), clean, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(title, clean, StringComparison.OrdinalIgnoreCase) ||
                   (!string.IsNullOrEmpty(cleanBase) &&
                    string.Equals(Path.GetFileNameWithoutExtension(title), cleanBase, StringComparison.OrdinalIgnoreCase));
        }

        private static List<IBody2> ResolveBodies(IModelDoc2 model, Dictionary<string, object> req)
        {
            List<IBody2> list = new List<IBody2>();
            foreach (string b64 in GetStringList(req, "body_persist_ids_b64"))
            {
                try
                {
                    object obj = BedStateStore.ResolvePersistReference(model, Convert.FromBase64String(b64));
                    if (obj is IBody2 b && !list.Contains(b)) list.Add(b);
                }
                catch { }
            }

            List<string> bodyNames = GetStringList(req, "body_names");
            if (bodyNames.Count > 0 && model is IPartDoc partDoc)
            {
                HashSet<string> wanted = new HashSet<string>(bodyNames, StringComparer.OrdinalIgnoreCase);
                object[] rawBodies = partDoc.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                if (rawBodies != null)
                {
                    foreach (object bObj in rawBodies)
                    {
                        if (bObj is IBody2 b)
                        {
                            string bn = "";
                            try { bn = b.Name ?? ""; } catch { }
                            if (wanted.Contains(bn) && !list.Contains(b))
                                list.Add(b);
                        }
                    }
                }
            }
            return list;
        }

        private static List<IFace2> ResolveFaces(IModelDoc2 model, Dictionary<string, object> req)
        {
            List<IFace2> list = new List<IFace2>();
            foreach (string b64 in GetStringList(req, "face_persist_ids_b64"))
            {
                try
                {
                    object obj = BedStateStore.ResolvePersistReference(model, Convert.FromBase64String(b64));
                    if (obj is IFace2 f && !list.Contains(f)) list.Add(f);
                }
                catch { }
            }
            return list;
        }

        private static string SerializeError(JavaScriptSerializer serializer, string error, string details = null)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "ok", false },
                { "error", error }
            };
            if (!string.IsNullOrEmpty(details)) payload["details"] = details;
            return serializer.Serialize(payload);
        }

        private static Dictionary<string, object> ParseDict(JavaScriptSerializer serializer, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> raw = serializer.Deserialize<Dictionary<string, object>>(json);
            return raw != null
                ? new Dictionary<string, object>(raw, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        private static string GetString(Dictionary<string, object> d, string key, string fallback)
        {
            if (d != null && d.TryGetValue(key, out object v) && v != null)
                return Convert.ToString(v, CultureInfo.InvariantCulture);
            return fallback;
        }

        private static bool GetBool(Dictionary<string, object> d, string key, bool fallback)
        {
            if (d != null && d.TryGetValue(key, out object v) && v != null)
            {
                if (v is bool b) return b;
                if (bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out bool parsed)) return parsed;
            }
            return fallback;
        }

        private static double GetDouble(Dictionary<string, object> d, string key, double fallback)
        {
            if (d != null && d.TryGetValue(key, out object v) && v != null)
            {
                try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { }
            }
            return fallback;
        }

        private static double[] GetDoubleArray(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out object v) || v == null) return null;
            if (v is System.Collections.IEnumerable seq && !(v is string))
            {
                List<double> nums = new List<double>();
                foreach (object item in seq)
                {
                    if (item == null) continue;
                    try { nums.Add(Convert.ToDouble(item, CultureInfo.InvariantCulture)); } catch { }
                }
                return nums.Count > 0 ? nums.ToArray() : null;
            }
            return null;
        }

        private static List<string> GetStringList(Dictionary<string, object> d, string key)
        {
            List<string> list = new List<string>();
            if (d == null || !d.TryGetValue(key, out object v) || v == null) return list;
            if (v is System.Collections.IEnumerable seq && !(v is string))
            {
                foreach (object item in seq)
                {
                    if (item == null) continue;
                    string s = Convert.ToString(item, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
            }
            return list;
        }
    }
}
