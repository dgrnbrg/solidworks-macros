using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksJoineryAddin.Data;

namespace SolidWorksJoineryAddin.MacroFeatures
{
    /// <summary>
    /// Headless in-process & standalone execution entry point for Multi-Body Joinery
    /// (Tab & Slot, Finger/Box Joint, Through/Blind Dado, Rabbet, Dado+Tab Combo with dogbones & poka-yoke keying).
    /// Bypasses PropertyManager Page UI so MCP automation creates editable Joinery MacroFeatures or native features.
    /// </summary>
    public static class JoineryHeadlessBridge
    {
        public static string ExecuteJson(ISldWorks swApp, string requestJson, bool allowMacroFeature = true)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            try
            {
                if (swApp == null)
                    return SerializeError(serializer, "ISldWorks reference is null inside JoineryHeadlessBridge.");

                Dictionary<string, object> req = ParseDict(serializer, requestJson);
                string targetDoc = GetString(req, "target_doc", null);
                IModelDoc2 model = ResolvePartDoc(swApp, targetDoc);
                if (model == null)
                    return SerializeError(serializer, "No active or matching SOLIDWORKS Part (.SLDPRT) document found.");

                double unitScale = ResolveUnitScaleToMeters(model, GetString(req, "units", null));
                JointGroupDefinition group = ProjectSettings.CreateDefaultGroup();
                group.FeatureConfigId = Guid.NewGuid().ToString("D");

                if (req.ContainsKey("joint_type"))
                    group.DefaultType = ParseJointType(GetString(req, "joint_type", "tab_and_slot"));
                if (req.ContainsKey("tab_count"))
                    group.DefaultTabCount = Math.Max(1, Math.Min(50, GetInt(req, "tab_count", group.DefaultTabCount)));
                if (req.ContainsKey("tab_width"))
                    group.DefaultTabWidth = Math.Max(0.001, GetDouble(req, "tab_width", 1.0) * unitScale);
                if (req.ContainsKey("tool_bit_diameter"))
                    group.DefaultToolBitRadius = Math.Max(0.0002, 0.5 * GetDouble(req, "tool_bit_diameter", 0.5) * unitScale);
                else if (req.ContainsKey("tool_bit_radius"))
                    group.DefaultToolBitRadius = Math.Max(0.0002, GetDouble(req, "tool_bit_radius", 0.25) * unitScale);
                if (req.ContainsKey("clearance"))
                    group.DefaultSignedClearance = GetDouble(req, "clearance", 0.0) * unitScale;
                if (req.ContainsKey("enable_dogbones"))
                    group.MasterEnableDogbones = GetBool(req, "enable_dogbones", group.MasterEnableDogbones);
                if (req.ContainsKey("relief_type"))
                    group.DefaultReliefType = ParseReliefType(GetString(req, "relief_type", "diagonal_dogbone"));
                if (req.ContainsKey("enable_keying"))
                    group.MasterEnableKeying = GetBool(req, "enable_keying", group.MasterEnableKeying);
                if (req.ContainsKey("key_delta"))
                    group.DefaultKeyDelta = Math.Max(0.0005, GetDouble(req, "key_delta", 0.5) * unitScale);

                List<IBody2> explicitBodies = ResolveBodies(model, req);
                group.BodyPersistIds.Clear();
                foreach (IBody2 b in explicitBodies)
                {
                    byte[] pid = JoineryTopologyScanner.GetPersistReferenceBytes(model, b);
                    if (pid != null && pid.Length > 0)
                        group.BodyPersistIds.Add(pid);
                }

                List<IFace2> selFaces = ResolveEntities<IFace2>(model, req, "face_persist_ids_b64");
                List<IEdge> selEdges = ResolveEntities<IEdge>(model, req, "edge_persist_ids_b64");
                group.SelectedFacePersistIds.Clear();
                foreach (IFace2 f in selFaces)
                {
                    byte[] fpid = JoineryTopologyScanner.GetPersistReferenceBytes(model, f);
                    if (fpid != null && fpid.Length > 0)
                        group.SelectedFacePersistIds.Add(fpid);
                }

                List<IBody2> targetBodies = JoineryTopologyScanner.GetTargetSolidBodies(model, explicitBodies);
                List<DetectedJointContact> contacts = (selFaces.Count > 0 || selEdges.Count > 0)
                    ? JoineryTopologyScanner.BuildJointsFromSelectedEntities(model, selFaces, selEdges, targetBodies, group)
                    : JoineryTopologyScanner.ScanContactingBodyPairs(model, targetBodies, group);

                group.Joints.Clear();
                foreach (DetectedJointContact c in contacts)
                    group.Joints.Add(c.Joint);

                int keyedCount = 0;
                List<IFeature> created = new List<IFeature>();
                if (group.Joints.Count > 0)
                {
                    KeyingEngine.ResolveKeying(group);
                    foreach (JointDefinition j in group.Joints)
                    {
                        if (j.IsKeyed) keyedCount++;
                    }
                    created = GeometryBuilder.ApplyJointGroupToModel(swApp, model, group);
                }

                HideManagedSketches(model, group);

                bool wantMacroFeature = allowMacroFeature && GetBool(req, "use_macro_feature", true);
                if (wantMacroFeature && created.Count > 0)
                {
                    JoineryMacroFeatureHelper.CommitAndNestFeatures(model, null, group, created);
                }

                List<string> createdNames = new List<string>(created.Count);
                foreach (IFeature f in created)
                {
                    if (f != null && !string.IsNullOrEmpty(f.Name))
                        createdNames.Add(f.Name);
                }

                string execPath = (wantMacroFeature && !string.IsNullOrEmpty(group.ManagedMacroFeatureName))
                    ? "addin_macro_feature"
                    : "standalone_native_features";

                return serializer.Serialize(new Dictionary<string, object>
                {
                    { "ok", true },
                    { "tool", "joinery" },
                    { "execution_path", execPath },
                    { "macro_feature_name", group.ManagedMacroFeatureName },
                    { "joints_created", group.Joints.Count },
                    { "keyed_joints", keyedCount },
                    { "features_created_count", createdNames.Count },
                    { "child_features_created", createdNames },
                    { "message", group.Joints.Count == 0
                        ? "No contacting planar multibody joinery interfaces detected for the specified bodies/faces."
                        : string.Format(CultureInfo.InvariantCulture, "Created {0} joinery joint(s) ({1} poka-yoke keyed) with {2} SOLIDWORKS feature(s).", group.Joints.Count, keyedCount, createdNames.Count) }
                });
            }
            catch (Exception ex)
            {
                SwAddin.Log("JoineryHeadlessBridge.ExecuteJson error: " + ex);
                return SerializeError(serializer, "JoineryHeadlessBridge exception: " + ex.Message, ex.ToString());
            }
        }

        private static void HideManagedSketches(IModelDoc2 model, JointGroupDefinition group)
        {
            if (model == null || group?.Joints == null) return;
            HashSet<string> skNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JointDefinition j in group.Joints)
            {
                if (!string.IsNullOrEmpty(j.ManagedSlotSketchName)) skNames.Add(j.ManagedSlotSketchName);
                if (!string.IsNullOrEmpty(j.ManagedTabSketchName)) skNames.Add(j.ManagedTabSketchName);
            }

            foreach (string skName in skNames)
            {
                try
                {
                    model.ClearSelection2(true);
                    if (model.Extension.SelectByID2(skName, "SKETCH", 0, 0, 0, false, 0, null, 0))
                        model.BlankSketch();
                }
                catch { }
            }
            try { model.ClearSelection2(true); } catch { }
        }

        private static JointType ParseJointType(string raw)
        {
            string s = (raw ?? "tab_and_slot").Trim().ToLowerInvariant().Replace("-", "_");
            if (s == "finger" || s == "finger_joint" || s == "box" || s == "box_joint") return JointType.FingerJoint;
            if (s == "through_dado" || s == "dado") return JointType.ThroughDado;
            if (s == "blind_dado") return JointType.BlindDado;
            if (s == "rabbet") return JointType.Rabbet;
            if (s == "dado_tab_combo" || s == "dado_with_tabs") return JointType.DadoTabCombo;
            return JointType.TabAndSlot;
        }

        private static CornerReliefType ParseReliefType(string raw)
        {
            string s = (raw ?? "diagonal_dogbone").Trim().ToLowerInvariant().Replace("-", "_");
            if (s == "t_bone" || s == "tbone") return CornerReliefType.TBone;
            if (s == "none" || s == "sharp") return CornerReliefType.None;
            return CornerReliefType.DiagonalDogbone;
        }

        private static double ResolveUnitScaleToMeters(IModelDoc2 model, string requestedUnits)
        {
            if (!string.IsNullOrWhiteSpace(requestedUnits))
            {
                string u = requestedUnits.Trim().ToLowerInvariant();
                if (u == "mm" || u == "mmgs" || u == "millimeter" || u == "millimeters") return 0.001;
                if (u == "m" || u == "mks" || u == "meter" || u == "meters") return 1.0;
                return 0.0254;
            }
            try
            {
                int sys = model.Extension.GetUserPreferenceInteger(
                    (int)swUserPreferenceIntegerValue_e.swUnitSystem,
                    (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);
                if (sys == (int)swUnitSystem_e.swUnitSystem_MMGS) return 0.001;
                if (sys == (int)swUnitSystem_e.swUnitSystem_MKS) return 1.0;
            }
            catch { }
            return 0.0254;
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
            List<IBody2> list = ResolveEntities<IBody2>(model, req, "body_persist_ids_b64");
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

        private static List<T> ResolveEntities<T>(IModelDoc2 model, Dictionary<string, object> req, string key) where T : class
        {
            List<T> list = new List<T>();
            foreach (string b64 in GetStringList(req, key))
            {
                try
                {
                    object obj = JoineryTopologyScanner.ResolvePersistReference(model, Convert.FromBase64String(b64));
                    if (obj is T typed && !list.Contains(typed))
                        list.Add(typed);
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

        private static int GetInt(Dictionary<string, object> d, string key, int fallback)
        {
            if (d != null && d.TryGetValue(key, out object v) && v != null)
            {
                try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { }
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
