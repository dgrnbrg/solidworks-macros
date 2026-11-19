using System;
using System.Collections.Generic;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcpServer.Core;
using SolidWorksMcpServer.Protocol;

namespace SolidWorksMcpServer.Tools
{
    internal static class FastenerStackInstaller
    {
        private class ResolvedPartAsset
        {
            public string PartNumber;
            public string PartPath;
            public FastenerPartProfile Profile;
        }

        private class CachedComponentInfo
        {
            public IComponent2 Component;
            public double[] Matrix;
        }

        public static McpToolResult HandleInstallFastenerStack(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            if (!(doc is IAssemblyDoc assy))
                return McpToolResult.Error("sw_install_fastener_stack requires an active Assembly (.SLDASM) document.");

            List<object> rawStacks = JsonHelper.GetList(args, "stacks");
            if (rawStacks.Count == 0)
                return McpToolResult.Error("Provide a non-empty 'stacks' array.");

            ISldWorks app = swMgr.GetApp();
            IMathUtility mu = app.GetMathUtility() as IMathUtility;
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            string libDir = DrawingAndMcMasterTools.ResolveHardwareLibraryDir(doc, JsonHelper.GetString(args, "library_dir"));
            bool downloadIfMissing = JsonHelper.GetBool(args, "download_if_missing", true);
            bool createMates = JsonHelper.GetBool(args, "create_mates", true);
            string folderName = JsonHelper.GetString(args, "folder_name", "McMaster Hardware");
            Directory.CreateDirectory(libDir);

            // Normalize stacks and collect unique part numbers & referenced component names
            List<Dictionary<string, object>> stacks = new List<Dictionary<string, object>>();
            HashSet<string> uniquePns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> referencedCompNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (object sObj in rawStacks)
            {
                Dictionary<string, object> st = sObj as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(sObj));
                if (st == null || st.Count == 0) continue;
                stacks.Add(st);

                foreach (string pnKey in new[] { "bolt_pn", "head_washer_pn", "nut_washer_pn", "nut_pn", "jam_nut_pn" })
                {
                    string pn = (JsonHelper.GetString(st, pnKey) ?? string.Empty).Trim().ToUpperInvariant();
                    if (!string.IsNullOrEmpty(pn)) uniquePns.Add(pn);
                }

                AddCompName(referencedCompNames, JsonHelper.GetString(st, "hole_component"));
                AddCompName(referencedCompNames, JsonHelper.GetString(st, "head_face_component"));
                AddCompName(referencedCompNames, JsonHelper.GetString(st, "nut_face_component"));
                foreach (string hc in JsonHelper.GetStringList(st, "head_face_components")) AddCompName(referencedCompNames, hc);
                foreach (string nc in JsonHelper.GetStringList(st, "nut_face_components")) AddCompName(referencedCompNames, nc);
            }

            if (uniquePns.Count == 0)
                return McpToolResult.Error("No McMaster part numbers (bolt_pn, washer/nut PNs) specified in stacks.");

            // Phase 1: Resolve, download (if missing), ensure Fastener_Axis, and inspect geometry upfront
            Dictionary<string, ResolvedPartAsset> assets = new Dictionary<string, ResolvedPartAsset>(StringComparer.OrdinalIgnoreCase);
            List<string> missingPns = new List<string>();
            List<object> resolveFailures = new List<object>();

            foreach (string pn in uniquePns)
            {
                Dictionary<string, object> res = DrawingAndMcMasterTools.ResolveDownloadAndVerifySingle(
                    app, pn, "", libDir, downloadIfMissing, true, 0.0, "", out bool ok, out _);
                if (!ok)
                {
                    missingPns.Add(pn);
                    resolveFailures.Add(res);
                    continue;
                }

                string partPath = JsonHelper.GetString(res, "part_path");
                FastenerPartProfile profile = res["fastener_profile"] as FastenerPartProfile
                    ?? AssemblyGeometryHelper.EnsureFastenerAxisAndInspect(app, partPath);
                if (profile == null)
                {
                    missingPns.Add(pn);
                    resolveFailures.Add(new { mcmaster_pn = pn, error = "Failed to inspect part geometry for " + partPath });
                    continue;
                }

                assets[pn] = new ResolvedPartAsset
                {
                    PartNumber = pn,
                    PartPath = partPath,
                    Profile = profile
                };
            }

            if (missingPns.Count > 0)
            {
                return McpToolResult.Error(
                    "Unable to resolve genuine McMaster-Carr .SLDPRT files before installation: " + string.Join(", ", missingPns),
                    new { missing_pns = missingPns, library_dir = libDir, failures = resolveFailures });
            }

            // Phase 2: Pre-open hardware parts silently and cache referenced structural components
            List<string> openedTitles = PreOpenPartsSilently(app, assets);
            Dictionary<string, CachedComponentInfo> compCache = BuildComponentCache(assy, referencedCompNames);

            List<IComponent2> insertedComps = new List<IComponent2>();
            Dictionary<string, int> skuCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<object> stackSummaries = new List<object>();
            int matesSucceeded = 0;
            int matesFailed = 0;

            bool prevCmd = false;
            try { prevCmd = app.CommandInProgress; app.CommandInProgress = true; } catch { }
            IFeatureManager fm = doc.FeatureManager;
            bool prevTree = true;
            try { prevTree = fm != null ? fm.EnableFeatureTree : true; if (fm != null) fm.EnableFeatureTree = false; } catch { }

            try
            {
                SwConnectionManager.ActivateDocumentSafe(app, doc);
                ISelectionMgr selMgr = doc.SelectionManager as ISelectionMgr;
                SelectData sd = selMgr != null ? selMgr.CreateSelectData() as SelectData : null;
                if (sd != null) sd.Mark = 1;

                // Phase 3: Batch insert, pose, unfix, and mate each stack
                foreach (Dictionary<string, object> st in stacks)
                {
                    string label = JsonHelper.GetString(st, "label", "Stack");
                    double[] rawAxis = JsonHelper.GetDoubleArray(st, "axis");
                    if (rawAxis == null || rawAxis.Length < 3)
                    {
                        stackSummaries.Add(new { label = label, error = "Missing 3D 'axis' vector [ax, ay, az]." });
                        continue;
                    }
                    double[] boltDir = AssemblyGeometryHelper.Normalize(rawAxis);
                    double[] headOutDir = new[] { -boltDir[0], -boltDir[1], -boltDir[2] };

                    List<object> rawHoles = JsonHelper.GetList(st, "holes");
                    string holeCompName = JsonHelper.GetString(st, "hole_component");
                    CachedComponentInfo holeCompInfo = GetCachedComponent(compCache, assy, holeCompName);

                    string defaultHeadCompName = JsonHelper.GetString(st, "head_face_component");
                    if (string.IsNullOrWhiteSpace(defaultHeadCompName)) defaultHeadCompName = holeCompName;
                    List<string> headCompList = JsonHelper.GetStringList(st, "head_face_components");
                    double? headFaceCoordUnits = JsonHelper.GetNullableDouble(st, "head_face_coord");

                    string defaultNutCompName = JsonHelper.GetString(st, "nut_face_component");
                    List<string> nutCompList = JsonHelper.GetStringList(st, "nut_face_components");
                    double? nutFaceCoordUnits = JsonHelper.GetNullableDouble(st, "nut_face_coord");

                    string boltPn = (JsonHelper.GetString(st, "bolt_pn") ?? string.Empty).Trim().ToUpperInvariant();
                    string headWasherPn = (JsonHelper.GetString(st, "head_washer_pn") ?? string.Empty).Trim().ToUpperInvariant();
                    string nutWasherPn = (JsonHelper.GetString(st, "nut_washer_pn") ?? string.Empty).Trim().ToUpperInvariant();
                    string nutPn = (JsonHelper.GetString(st, "nut_pn") ?? string.Empty).Trim().ToUpperInvariant();
                    string jamNutPn = (JsonHelper.GetString(st, "jam_nut_pn") ?? string.Empty).Trim().ToUpperInvariant();
                    double clockDeg = JsonHelper.GetDouble(st, "clock_angle_deg", 0.0);
                    bool lockRot = JsonHelper.GetBool(st, "lock_rotation", false) || Math.Abs(clockDeg) > 0.01;

                    ResolvedPartAsset boltAsset = !string.IsNullOrEmpty(boltPn) && assets.ContainsKey(boltPn) ? assets[boltPn] : null;
                    ResolvedPartAsset headWasherAsset = !string.IsNullOrEmpty(headWasherPn) && assets.ContainsKey(headWasherPn) ? assets[headWasherPn] : null;
                    ResolvedPartAsset nutWasherAsset = !string.IsNullOrEmpty(nutWasherPn) && assets.ContainsKey(nutWasherPn) ? assets[nutWasherPn] : null;
                    ResolvedPartAsset nutAsset = !string.IsNullOrEmpty(nutPn) && assets.ContainsKey(nutPn) ? assets[nutPn] : null;
                    ResolvedPartAsset jamNutAsset = !string.IsNullOrEmpty(jamNutPn) && assets.ContainsKey(jamNutPn) ? assets[jamNutPn] : null;

                    int stackInserted = 0;
                    int stackMatesOk = 0;
                    int stackMatesFail = 0;

                    for (int hIdx = 0; hIdx < rawHoles.Count; hIdx++)
                    {
                        double[] holeCoord = ConvertPointList(rawHoles[hIdx]);
                        if (holeCoord == null || holeCoord.Length < 3) continue;

                        double[] holePtM = new[]
                        {
                            ShopCatalogService.ToMeters(holeCoord[0], units),
                            ShopCatalogService.ToMeters(holeCoord[1], units),
                            ShopCatalogService.ToMeters(holeCoord[2], units)
                        };

                        IEntity holeEnt = holeCompInfo != null
                            ? FindHoleEntity(holeCompInfo.Component, holeCompInfo.Matrix, holePtM, boltDir)
                            : null;

                        string hCompName = (hIdx < headCompList.Count && !string.IsNullOrWhiteSpace(headCompList[hIdx]))
                            ? headCompList[hIdx] : defaultHeadCompName;
                        CachedComponentInfo headCompInfo = GetCachedComponent(compCache, assy, hCompName);

                        IEntity headPlaneEnt = null;
                        double[] entryPtM = ResolveBearingPlaneAndPoint(
                            headCompInfo, holePtM, headOutDir, headFaceCoordUnits, units, true, out headPlaneEnt);

                        // Skip head washer automatically if bolt is a flush countersunk screw
                        bool skipHeadWasher = boltAsset != null &&
                            string.Equals(boltAsset.Profile.PartKind, "csk_screw", StringComparison.OrdinalIgnoreCase);

                        double[] boltSeatPtM = entryPtM;
                        IEntity boltTargetPlaneEnt = headPlaneEnt;

                        // Step A: Head Washer
                        if (headWasherAsset != null && !skipHeadWasher)
                        {
                            IComponent2 hw = PlaceAndMateComponent(
                                doc, assy, mu, sd, headWasherAsset, entryPtM, headOutDir, holeEnt, headPlaneEnt,
                                createMates, 0.0, false,
                                out IEntity hwOppEnt, out int cOk, out int cFail);
                            if (hw != null)
                            {
                                insertedComps.Add(hw);
                                IncrementSkuCount(skuCounts, headWasherAsset.PartNumber);
                                stackInserted++;
                                stackMatesOk += cOk; stackMatesFail += cFail;
                                double wThkM = headWasherAsset.Profile.MeasuredThicknessMm * 0.001;
                                boltSeatPtM = new[]
                                {
                                    entryPtM[0] + wThkM * headOutDir[0],
                                    entryPtM[1] + wThkM * headOutDir[1],
                                    entryPtM[2] + wThkM * headOutDir[2]
                                };
                                if (hwOppEnt != null) boltTargetPlaneEnt = hwOppEnt;
                            }
                        }

                        // Step B: Bolt / Screw
                        if (boltAsset != null)
                        {
                            IComponent2 boltComp = PlaceAndMateComponent(
                                doc, assy, mu, sd, boltAsset, boltSeatPtM, boltDir, holeEnt, boltTargetPlaneEnt,
                                createMates, 0.0, false,
                                out _, out int cOk, out int cFail);
                            if (boltComp != null)
                            {
                                insertedComps.Add(boltComp);
                                IncrementSkuCount(skuCounts, boltAsset.PartNumber);
                                stackInserted++;
                                stackMatesOk += cOk; stackMatesFail += cFail;
                            }
                        }

                        // Exit side (nut washer + primary nut + jam nut)
                        if (nutWasherAsset != null || nutAsset != null || jamNutAsset != null)
                        {
                            string nCompName = (hIdx < nutCompList.Count && !string.IsNullOrWhiteSpace(nutCompList[hIdx]))
                                ? nutCompList[hIdx] : defaultNutCompName;
                            CachedComponentInfo nutCompInfo = GetCachedComponent(compCache, assy, nCompName);

                            IEntity nutPlaneEnt = null;
                            double[] exitPtM = ResolveBearingPlaneAndPoint(
                                nutCompInfo, holePtM, boltDir, nutFaceCoordUnits, units, false, out nutPlaneEnt);

                            double[] nutSeatPtM = exitPtM;
                            IEntity nutTargetPlaneEnt = nutPlaneEnt;

                            // Step C: Nut Washer
                            if (nutWasherAsset != null)
                            {
                                IComponent2 nw = PlaceAndMateComponent(
                                    doc, assy, mu, sd, nutWasherAsset, exitPtM, boltDir, holeEnt, nutPlaneEnt,
                                    createMates, 0.0, false,
                                    out IEntity nwOppEnt, out int cOk, out int cFail);
                                if (nw != null)
                                {
                                    insertedComps.Add(nw);
                                    IncrementSkuCount(skuCounts, nutWasherAsset.PartNumber);
                                    stackInserted++;
                                    stackMatesOk += cOk; stackMatesFail += cFail;
                                    double wThkM = nutWasherAsset.Profile.MeasuredThicknessMm * 0.001;
                                    nutSeatPtM = new[]
                                    {
                                        exitPtM[0] + wThkM * boltDir[0],
                                        exitPtM[1] + wThkM * boltDir[1],
                                        exitPtM[2] + wThkM * boltDir[2]
                                    };
                                    if (nwOppEnt != null) nutTargetPlaneEnt = nwOppEnt;
                                }
                            }

                            double[] jamSeatPtM = nutSeatPtM;
                            IEntity jamTargetPlaneEnt = nutTargetPlaneEnt;

                            // Step D: Primary Nut
                            if (nutAsset != null)
                            {
                                IComponent2 nutComp = PlaceAndMateComponent(
                                    doc, assy, mu, sd, nutAsset, nutSeatPtM, boltDir, holeEnt, nutTargetPlaneEnt,
                                    createMates, clockDeg, lockRot,
                                    out IEntity nutOppEnt, out int cOk, out int cFail);
                                if (nutComp != null)
                                {
                                    insertedComps.Add(nutComp);
                                    IncrementSkuCount(skuCounts, nutAsset.PartNumber);
                                    stackInserted++;
                                    stackMatesOk += cOk; stackMatesFail += cFail;
                                    double nThkM = nutAsset.Profile.MeasuredThicknessMm * 0.001;
                                    jamSeatPtM = new[]
                                    {
                                        nutSeatPtM[0] + nThkM * boltDir[0],
                                        nutSeatPtM[1] + nThkM * boltDir[1],
                                        nutSeatPtM[2] + nThkM * boltDir[2]
                                    };
                                    if (nutOppEnt != null) jamTargetPlaneEnt = nutOppEnt;
                                }
                            }

                            // Step E: Jam Nut
                            if (jamNutAsset != null)
                            {
                                IComponent2 jamComp = PlaceAndMateComponent(
                                    doc, assy, mu, sd, jamNutAsset, jamSeatPtM, boltDir, holeEnt, jamTargetPlaneEnt,
                                    createMates, clockDeg, lockRot,
                                    out _, out int cOk, out int cFail);
                                if (jamComp != null)
                                {
                                    insertedComps.Add(jamComp);
                                    IncrementSkuCount(skuCounts, jamNutAsset.PartNumber);
                                    stackInserted++;
                                    stackMatesOk += cOk; stackMatesFail += cFail;
                                }
                            }
                        }
                    }

                    matesSucceeded += stackMatesOk;
                    matesFailed += stackMatesFail;
                    stackSummaries.Add(new
                    {
                        label = label,
                        hole_count = rawHoles.Count,
                        inserted_components = stackInserted,
                        mates_succeeded = stackMatesOk,
                        mates_failed = stackMatesFail
                    });
                }

                // Phase 4: Restore tree, group all inserted hardware into folder, rebuild, and audit mate health
                try { if (fm != null) fm.EnableFeatureTree = prevTree; } catch { }
                int groupedCount = AssemblyGeometryHelper.MoveComponentsToFolder(doc, assy, insertedComps, folderName);
                doc.EditRebuild3();

                int fixedCount = 0;
                foreach (IComponent2 c in insertedComps)
                {
                    if (c != null && c.IsFixed()) fixedCount++;
                }

                int assemblyMateErrors = CountMateErrors(doc);
                return McpToolResult.JsonText(new
                {
                    document = doc.GetTitle(),
                    units = units,
                    inserted_count = insertedComps.Count,
                    fixed_count = fixedCount,
                    mates_succeeded = matesSucceeded,
                    mates_failed = matesFailed,
                    assembly_mate_errors = assemblyMateErrors,
                    folder_name = folderName,
                    grouped_count = groupedCount,
                    sku_counts = skuCounts,
                    stack_summaries = stackSummaries
                });
            }
            finally
            {
                try { if (fm != null) fm.EnableFeatureTree = prevTree; } catch { }
                try { app.CommandInProgress = prevCmd; } catch { }
                foreach (string title in openedTitles)
                {
                    try { app.CloseDoc(title); } catch { }
                }
            }
        }

        private static void AddCompName(HashSet<string> set, string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
                set.Add(name.Trim());
        }

        private static void IncrementSkuCount(Dictionary<string, int> counts, string pn)
        {
            if (string.IsNullOrEmpty(pn)) return;
            counts[pn] = counts.TryGetValue(pn, out int prev) ? prev + 1 : 1;
        }

        private static double[] ConvertPointList(object ptObj)
        {
            List<object> list = JsonHelper.AsObjectList(ptObj);
            if (list.Count < 3) return null;
            return new[]
            {
                Convert.ToDouble(list[0], System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToDouble(list[1], System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToDouble(list[2], System.Globalization.CultureInfo.InvariantCulture)
            };
        }

        private static List<string> PreOpenPartsSilently(ISldWorks app, Dictionary<string, ResolvedPartAsset> assets)
        {
            HashSet<string> alreadyOpen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IModelDoc2 iter = app.GetFirstDocument() as IModelDoc2;
            while (iter != null)
            {
                string p = iter.GetPathName();
                if (!string.IsNullOrEmpty(p)) alreadyOpen.Add(p);
                iter = iter.GetNext() as IModelDoc2;
            }

            List<string> openedTitles = new List<string>();
            app.DocumentVisible(false, (int)swDocumentTypes_e.swDocPART);
            try
            {
                foreach (ResolvedPartAsset asset in assets.Values)
                {
                    if (alreadyOpen.Contains(asset.PartPath)) continue;
                    int err = 0, warn = 0;
                    IModelDoc2 partDoc = app.OpenDoc6(asset.PartPath, (int)swDocumentTypes_e.swDocPART,
                        (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref err, ref warn);
                    if (partDoc != null)
                        openedTitles.Add(partDoc.GetTitle());
                }
            }
            finally
            {
                try { app.DocumentVisible(true, (int)swDocumentTypes_e.swDocPART); } catch { }
            }
            return openedTitles;
        }

        private static Dictionary<string, CachedComponentInfo> BuildComponentCache(IAssemblyDoc assy, HashSet<string> extraNames)
        {
            Dictionary<string, CachedComponentInfo> cache = new Dictionary<string, CachedComponentInfo>(StringComparer.OrdinalIgnoreCase);
            object[] topComps = assy.GetComponents(true) as object[];
            if (topComps != null)
            {
                foreach (object co in topComps)
                {
                    if (!(co is IComponent2 c) || c.IsSuppressed()) continue;
                    cache[c.Name2] = new CachedComponentInfo
                    {
                        Component = c,
                        Matrix = c.Transform2 != null ? c.Transform2.ArrayData as double[] : null
                    };
                }
            }

            foreach (string name in extraNames)
            {
                if (cache.ContainsKey(name)) continue;
                IComponent2 found = AssemblyGeometryHelper.FindComponentByName(assy, name);
                if (found != null && !found.IsSuppressed())
                {
                    cache[name] = new CachedComponentInfo
                    {
                        Component = found,
                        Matrix = found.Transform2 != null ? found.Transform2.ArrayData as double[] : null
                    };
                }
            }
            return cache;
        }

        private static CachedComponentInfo GetCachedComponent(Dictionary<string, CachedComponentInfo> cache, IAssemblyDoc assy, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (cache.TryGetValue(name.Trim(), out CachedComponentInfo info)) return info;
            IComponent2 found = AssemblyGeometryHelper.FindComponentByName(assy, name.Trim());
            if (found != null && !found.IsSuppressed())
            {
                info = new CachedComponentInfo
                {
                    Component = found,
                    Matrix = found.Transform2 != null ? found.Transform2.ArrayData as double[] : null
                };
                cache[name.Trim()] = info;
                return info;
            }
            return null;
        }

        private static double[] ResolveBearingPlaneAndPoint(
            CachedComponentInfo compInfo,
            double[] holePtM,
            double[] expectedOutNorm,
            double? coordInUnits,
            string units,
            bool isEntrySide,
            out IEntity planeEnt)
        {
            planeEnt = null;
            if (coordInUnits.HasValue)
            {
                double cM = ShopCatalogService.ToMeters(coordInUnits.Value, units);
                double[] ptM = new[] { holePtM[0], holePtM[1], holePtM[2] };
                if (Math.Abs(expectedOutNorm[0]) > 0.99) ptM[0] = cM;
                else if (Math.Abs(expectedOutNorm[1]) > 0.99) ptM[1] = cM;
                else if (Math.Abs(expectedOutNorm[2]) > 0.99) ptM[2] = cM;
                else
                {
                    double targetSigned = cM * (isEntrySide ? -1.0 : 1.0);
                    double cur = AssemblyGeometryHelper.Dot(holePtM, expectedOutNorm);
                    double delta = targetSigned - cur;
                    ptM[0] += delta * expectedOutNorm[0];
                    ptM[1] += delta * expectedOutNorm[1];
                    ptM[2] += delta * expectedOutNorm[2];
                }

                if (compInfo != null)
                    planeEnt = FindPlanarFaceEntity(compInfo.Component, compInfo.Matrix, ptM, expectedOutNorm);
                return ptM;
            }

            // Fallback when explicit coordinate is omitted: locate extreme matching face on compInfo near holePtM
            if (compInfo != null)
            {
                double[] detectedPt = FindOutermostPlanarFaceNearHole(compInfo.Component, compInfo.Matrix, holePtM, expectedOutNorm, out planeEnt);
                if (detectedPt != null) return detectedPt;
            }
            return new[] { holePtM[0], holePtM[1], holePtM[2] };
        }

        private static IEntity FindHoleEntity(IComponent2 comp, double[] m, double[] ptM, double[] axis)
        {
            if (comp == null || !(comp.GetModelDoc2() is IPartDoc pd)) return null;
            object[] bodies = pd.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
            if (bodies == null) return null;

            IFace2 best = null;
            double bestScore = double.MaxValue;
            double bestDiam = double.MaxValue;

            foreach (object bo in bodies)
            {
                if (!(bo is IBody2 b)) continue;
                object[] faces = b.GetFaces() as object[];
                if (faces == null) continue;
                foreach (object fo in faces)
                {
                    if (!(fo is IFace2 f)) continue;
                    ISurface s = f.GetSurface() as ISurface;
                    if (s == null || !s.IsCylinder() || !f.FaceInSurfaceSense()) continue;
                    double[] cp = s.CylinderParams as double[];
                    if (cp == null || cp.Length < 7) continue;

                    double[] ga = AssemblyGeometryHelper.TransformVectorFast(m, cp[3], cp[4], cp[5]);
                    if (Math.Abs(AssemblyGeometryHelper.Dot(ga, axis)) < 0.98) continue;

                    double[] go = AssemblyGeometryHelper.TransformPointFast(m, cp[0], cp[1], cp[2]);
                    double dx = ptM[0] - go[0], dy = ptM[1] - go[1], dz = ptM[2] - go[2];
                    double proj = dx * ga[0] + dy * ga[1] + dz * ga[2];
                    double px = dx - proj * ga[0], py = dy - proj * ga[1], pz = dz - proj * ga[2];
                    double perp = Math.Sqrt(px * px + py * py + pz * pz);
                    if (perp > 0.002) continue;

                    double diam = 2.0 * cp[6];
                    if (perp < bestScore - 0.00005 || (Math.Abs(perp - bestScore) <= 0.00005 && diam < bestDiam))
                    {
                        bestScore = perp;
                        bestDiam = diam;
                        best = f;
                    }
                }
            }
            return best != null ? comp.GetCorrespondingEntity(best) as IEntity : null;
        }

        private static IEntity FindPlanarFaceEntity(IComponent2 comp, double[] m, double[] ptM, double[] normDir)
        {
            if (comp == null || !(comp.GetModelDoc2() is IPartDoc pd)) return null;
            object[] bodies = pd.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
            if (bodies == null) return null;

            IFace2 bestContained = null;
            double bestContainedDist = double.MaxValue;
            double bestContainedArea = -1.0;

            IFace2 bestPlaneFallback = null;
            double bestFallbackArea = -1.0;

            foreach (object bo in bodies)
            {
                if (!(bo is IBody2 b)) continue;
                object[] faces = b.GetFaces() as object[];
                if (faces == null) continue;
                foreach (object fo in faces)
                {
                    if (!(fo is IFace2 f)) continue;
                    ISurface s = f.GetSurface() as ISurface;
                    if (s == null || !s.IsPlane()) continue;
                    if (!AssemblyGeometryHelper.TryGetPlanarFaceNormalAndPoint(f, s, out double[] ln, out double[] lp))
                        continue;
                    double[] gn = AssemblyGeometryHelper.TransformVectorFast(m, ln[0], ln[1], ln[2]);
                    if (AssemblyGeometryHelper.Dot(gn, normDir) < 0.98) continue;

                    double[] gp = AssemblyGeometryHelper.TransformPointFast(m, lp[0], lp[1], lp[2]);
                    double pDist = Math.Abs((ptM[0] - gp[0]) * gn[0] + (ptM[1] - gp[1]) * gn[1] + (ptM[2] - gp[2]) * gn[2]);
                    if (pDist > 0.0005) continue;

                    double area = f.GetArea();
                    if (pDist <= 0.00035)
                    {
                        double[] fb = f.GetBox() as double[];
                        if (fb != null && fb.Length >= 6)
                        {
                            AssemblyGeometryHelper.TransformAabbFast(m, fb, out double[] bMin, out double[] bMax);
                            double pad = 0.005;
                            if (ptM[0] >= bMin[0] - pad && ptM[0] <= bMax[0] + pad &&
                                ptM[1] >= bMin[1] - pad && ptM[1] <= bMax[1] + pad &&
                                ptM[2] >= bMin[2] - pad && ptM[2] <= bMax[2] + pad)
                            {
                                if (pDist < bestContainedDist - 0.00002 ||
                                    (Math.Abs(pDist - bestContainedDist) <= 0.00002 && area > bestContainedArea))
                                {
                                    bestContainedDist = pDist;
                                    bestContainedArea = area;
                                    bestContained = f;
                                }
                            }
                        }
                    }

                    if (area > bestFallbackArea)
                    {
                        bestFallbackArea = area;
                        bestPlaneFallback = f;
                    }
                }
            }

            IFace2 chosen = bestContained ?? bestPlaneFallback;
            return chosen != null ? comp.GetCorrespondingEntity(chosen) as IEntity : null;
        }

        private static double[] FindOutermostPlanarFaceNearHole(
            IComponent2 comp,
            double[] m,
            double[] holePtM,
            double[] outwardNorm,
            out IEntity faceEnt)
        {
            faceEnt = null;
            if (comp == null || !(comp.GetModelDoc2() is IPartDoc pd)) return null;
            object[] bodies = pd.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
            if (bodies == null) return null;

            IFace2 best = null;
            double bestProj = double.MinValue;
            foreach (object bo in bodies)
            {
                if (!(bo is IBody2 b)) continue;
                object[] faces = b.GetFaces() as object[];
                if (faces == null) continue;
                foreach (object fo in faces)
                {
                    if (!(fo is IFace2 f)) continue;
                    ISurface s = f.GetSurface() as ISurface;
                    if (s == null || !s.IsPlane()) continue;
                    if (!AssemblyGeometryHelper.TryGetPlanarFaceNormalAndPoint(f, s, out double[] ln, out double[] lp))
                        continue;
                    double[] gn = AssemblyGeometryHelper.TransformVectorFast(m, ln[0], ln[1], ln[2]);
                    if (AssemblyGeometryHelper.Dot(gn, outwardNorm) < 0.98) continue;

                    double[] gp = AssemblyGeometryHelper.TransformPointFast(m, lp[0], lp[1], lp[2]);
                    double sVal = AssemblyGeometryHelper.Dot(gp, outwardNorm);
                    double[] fb = f.GetBox() as double[];
                    if (fb == null || fb.Length < 6) continue;
                    AssemblyGeometryHelper.TransformAabbFast(m, fb, out double[] bMin, out double[] bMax);

                    // Project holePtM onto this plane and verify it lies inside face AABB
                    double holeS = AssemblyGeometryHelper.Dot(holePtM, outwardNorm);
                    double d = sVal - holeS;
                    double[] hit = new[]
                    {
                        holePtM[0] + d * outwardNorm[0],
                        holePtM[1] + d * outwardNorm[1],
                        holePtM[2] + d * outwardNorm[2]
                    };
                    double pad = 0.008;
                    if (hit[0] >= bMin[0] - pad && hit[0] <= bMax[0] + pad &&
                        hit[1] >= bMin[1] - pad && hit[1] <= bMax[1] + pad &&
                        hit[2] >= bMin[2] - pad && hit[2] <= bMax[2] + pad)
                    {
                        if (sVal > bestProj)
                        {
                            bestProj = sVal;
                            best = f;
                        }
                    }
                }
            }

            if (best == null) return null;
            faceEnt = comp.GetCorrespondingEntity(best) as IEntity;
            double holeS0 = AssemblyGeometryHelper.Dot(holePtM, outwardNorm);
            double shift = bestProj - holeS0;
            return new[]
            {
                holePtM[0] + shift * outwardNorm[0],
                holePtM[1] + shift * outwardNorm[1],
                holePtM[2] + shift * outwardNorm[2]
            };
        }

        private static IComponent2 PlaceAndMateComponent(
            IModelDoc2 doc,
            IAssemblyDoc assy,
            IMathUtility mu,
            SelectData sd,
            ResolvedPartAsset asset,
            double[] seatPtM,
            double[] targetAxis,
            IEntity holeEnt,
            IEntity targetPlaneEnt,
            bool createMates,
            double clockDeg,
            bool lockRot,
            out IEntity oppositeFaceEnt,
            out int matesOk,
            out int matesFail)
        {
            oppositeFaceEnt = null;
            matesOk = 0;
            matesFail = 0;

            FastenerPartProfile profile = asset.Profile;
            IComponent2 comp = assy.AddComponent5(
                asset.PartPath,
                (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                "",
                !string.IsNullOrEmpty(profile.ActiveConfig),
                profile.ActiveConfig ?? "",
                seatPtM[0], seatPtM[1], seatPtM[2]) as IComponent2;
            if (comp == null) return null;

            MathTransform xf = AssemblyGeometryHelper.BuildTransformFromPose(
                mu, seatPtM, targetAxis, profile.LocalAxis,
                profile.LocalBearingOffsetMm * 0.001, clockDeg, profile.HexFlatLocalAngleDeg);
            if (xf != null) comp.Transform2 = xf;

            if (comp.IsFixed())
            {
                doc.ClearSelection2(true);
                comp.Select4(false, null, false);
                assy.UnfixComponent();
                doc.ClearSelection2(true);
            }

            IModelDoc2 compDoc = comp.GetModelDoc2() as IModelDoc2;
            double[] compMat = comp.Transform2 != null ? comp.Transform2.ArrayData as double[] : null;

            if (createMates)
            {
                // Concentric mate using Fastener_Axis
                string axisName = !string.IsNullOrEmpty(profile.AxisFeatureName) ? profile.AxisFeatureName : "Fastener_Axis";
                IFeature axFeat = comp.FeatureByName(axisName);
                if (axFeat != null && holeEnt != null)
                {
                    doc.ClearSelection2(true);
                    bool s1 = axFeat.Select2(false, 1);
                    bool s2 = holeEnt.Select4(true, sd);
                    int cErr = -1;
                    if (s1 && s2)
                    {
                        assy.AddMate5(
                            (int)swMateType_e.swMateCONCENTRIC,
                            (int)swMateAlign_e.swMateAlignCLOSEST,
                            false, 0, 0, 0, 1, 1, 0, 0, 0, false, lockRot, 0, out cErr);
                    }
                    doc.ClearSelection2(true);
                    if (cErr == (int)swAddMateError_e.swAddMateError_NoError) matesOk++; else matesFail++;
                }
                else
                {
                    matesFail++;
                }

                // Coincident mate on bearing face
                IEntity bearEnt = null;
                if (!string.IsNullOrEmpty(profile.BearingFaceToken) && compDoc != null)
                {
                    IFace2 bf = SwConnectionManager.FindFaceByToken(compDoc, profile.BearingFaceToken);
                    if (bf != null) bearEnt = comp.GetCorrespondingEntity(bf) as IEntity;
                }
                if (bearEnt == null)
                {
                    double[] expectedBearNorm = profile.LocalBearingNormalSign >= 0
                        ? targetAxis
                        : new[] { -targetAxis[0], -targetAxis[1], -targetAxis[2] };
                    bearEnt = FindPlanarFaceEntity(comp, compMat, seatPtM, expectedBearNorm);
                }

                if (bearEnt != null && targetPlaneEnt != null)
                {
                    doc.ClearSelection2(true);
                    bool s1 = bearEnt.Select4(false, sd);
                    bool s2 = targetPlaneEnt.Select4(true, sd);
                    int coErr = -1;
                    if (s1 && s2)
                    {
                        assy.AddMate5(
                            (int)swMateType_e.swMateCOINCIDENT,
                            (int)swMateAlign_e.swMateAlignCLOSEST,
                            false, 0, 0, 0, 1, 1, 0, 0, 0, false, false, 0, out coErr);
                    }
                    doc.ClearSelection2(true);
                    if (coErr == (int)swAddMateError_e.swAddMateError_NoError) matesOk++; else matesFail++;
                }
                else
                {
                    matesFail++;
                }
            }

            // Resolve opposite planar face for stacking next component (washers / nuts)
            if (!string.IsNullOrEmpty(profile.OppositeFaceToken) && compDoc != null)
            {
                IFace2 of = SwConnectionManager.FindFaceByToken(compDoc, profile.OppositeFaceToken);
                if (of != null) oppositeFaceEnt = comp.GetCorrespondingEntity(of) as IEntity;
            }
            if (oppositeFaceEnt == null && profile.MeasuredThicknessMm > 0.1)
            {
                double tM = profile.MeasuredThicknessMm * 0.001;
                double[] oppPtM = new[]
                {
                    seatPtM[0] + tM * targetAxis[0],
                    seatPtM[1] + tM * targetAxis[1],
                    seatPtM[2] + tM * targetAxis[2]
                };
                oppositeFaceEnt = FindPlanarFaceEntity(comp, compMat, oppPtM, targetAxis);
            }

            return comp;
        }

        private static int CountMateErrors(IModelDoc2 doc)
        {
            int mateErrors = 0;
            IFeature feat = doc.FirstFeature() as IFeature;
            while (feat != null)
            {
                if (string.Equals(feat.GetTypeName2(), "MateGroup", StringComparison.OrdinalIgnoreCase))
                {
                    IFeature sub = feat.GetFirstSubFeature() as IFeature;
                    while (sub != null)
                    {
                        int ec = sub.GetErrorCode2(out bool isWarn);
                        if (ec != 0 && !isWarn) mateErrors++;
                        sub = sub.GetNextSubFeature() as IFeature;
                    }
                }
                feat = feat.GetNextFeature() as IFeature;
            }
            return mateErrors;
        }
    }
}
