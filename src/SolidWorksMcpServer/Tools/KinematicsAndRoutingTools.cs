using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcpServer.Core;
using SolidWorksMcpServer.Protocol;

namespace SolidWorksMcpServer.Tools
{
    public static class KinematicsAndRoutingTools
    {
        public static void Register(McpStdioHost host, SwConnectionManager swMgr)
        {
            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_query_assembly_transforms",
                Description = "Query world-to-local and local-to-world 4x4 transforms, world bounding boxes, constraint status, and mate counts for assembly components (including nested subassembly paths like 'sub-1/sub-2/part-1'), and optionally convert 3D points/vectors between any two frames.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "target_doc", JsonHelper.Prop("string", "Optional assembly title/path.") },
                    { "component_paths", JsonHelper.ArrayProp("string", "Component names or nested slash paths (omit to query all top-level components).") },
                    { "points", JsonHelper.ArrayProp("object", "Optional 3D points [[x,y,z], ...] to convert between frames.") },
                    { "vectors", JsonHelper.ArrayProp("object", "Optional 3D direction vectors [[vx,vy,vz], ...] to rotate between frames.") },
                    { "from_frame", JsonHelper.Prop("string", "Source coordinate frame: 'world' or a component name/path.", "world") },
                    { "to_frame", JsonHelper.Prop("string", "Target coordinate frame: 'world' or a component name/path.", "world") },
                    { "units", JsonHelper.Prop("string", "Units for translation/points/boxes: mm, in, or m (defaults to active doc units).") }
                }),
                Handler = args => HandleQueryAssemblyTransforms(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_route_cable_or_loom",
                Description = "Generate a solid or hollow circular cable/split-loom/cord sweep from assembly-frame waypoints or a tangent rolling u_loop_spec (auto-transforming into target_component's local part frame or creating a new MMGS part), pre-validate min bend radius vs D/2, and check subset collisions/clearances.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "target_doc", JsonHelper.Prop("string", "Optional active part or assembly title/path.") },
                    { "target_component", JsonHelper.Prop("string", "Optional component path in an assembly whose .SLDPRT will receive the routed sweep.") },
                    { "create_part_path", JsonHelper.Prop("string", "Optional new .SLDPRT path to create an MMGS cable part cleanly.") },
                    { "insert_into_assembly", JsonHelper.Prop("boolean", "When create_part_path is used with an active assembly, insert and mate principal planes automatically.", true) },
                    { "waypoints", JsonHelper.ArrayProp("object", "Array of 3D waypoints [[x,y,z], ...] for spline/line routing.") },
                    { "waypoint_frame", JsonHelper.Prop("string", "Coordinate frame of waypoints / u_loop_spec: 'assembly' (default) or 'local'.", "assembly", new[] { "assembly", "local" }) },
                    { "u_loop_spec", JsonHelper.Prop("object", "Tangent rolling U-loop spec: {plane_axis ('X'|'Y'|'Z'), x_pos (or plane_coord), z_front, z_rear, bend_radius, y_bottom, y_front_top, y_rear_top}.") },
                    { "outer_diameter_mm", JsonHelper.Prop("number", "Cable / split-loom outer diameter in mm.") },
                    { "wall_thickness_mm", JsonHelper.Prop("number", "Optional thin-wall thickness in mm for hollow loom (0 = solid cable).", 0.0) },
                    { "min_bend_radius_mm", JsonHelper.Prop("number", "Minimum DFM bend radius in mm (0 = auto 3.0 * outer_diameter_mm).", 0.0) },
                    { "enforce_min_bend_radius", JsonHelper.Prop("boolean", "Fail instead of warn if measured bend radius falls below min_bend_radius_mm (R <= D/2 self-intersection always fails).", false) },
                    { "clearance_check_components", JsonHelper.ArrayProp("string", "Optional neighboring assembly components to check subset interference and minimum ClosestDistance against.") },
                    { "feature_name", JsonHelper.Prop("string", "Sweep feature name.", "Cable_Route_Sweep") },
                    { "units", JsonHelper.Prop("string", "Units for waypoints/u_loop_spec: mm or in (default mm).", "mm") }
                }, "outer_diameter_mm"),
                Handler = args => HandleRouteCableOrLoom(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_verify_motion_stroke",
                Description = "Verify multi-stop linear or rotary kinematic travel on an assembly using fast EditRebuild3() solves: temporarily suppresses limit mates and static cables/U-loops, drives Distance/Angle stops, measures rail bearing overrun & limit-switch clearances + subset interference, and guarantees restoration of the resting pose.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "target_doc", JsonHelper.Prop("string", "Optional assembly title/path.") },
                    { "moving_component", JsonHelper.Prop("string", "Primary moving carriage/door/slide component name or path.") },
                    { "drive_mode", JsonHelper.Prop("string", "Distance or angle drive.", "distance", new[] { "distance", "angle" }) },
                    { "drive_entity_a", JsonHelper.Prop("string", "Fixed reference plane/face selector (e.g. 'Top Plane' or 'comp:spine-1::face:...').") },
                    { "drive_entity_b", JsonHelper.Prop("string", "Moving component plane/face selector (e.g. 'comp:router carriage-1::Top Plane').") },
                    { "existing_drive_mate", JsonHelper.Prop("string", "Optional existing Distance/Angle mate feature name to drive instead of creating a temporary mate.") },
                    { "flip_drive_dimension", JsonHelper.Prop("boolean", "Flip temporary drive mate direction sign.", false) },
                    { "alignment", JsonHelper.Prop("string", "Temporary mate alignment: aligned, anti_aligned, or closest.", "closest", new[] { "closest", "aligned", "anti_aligned" }) },
                    { "suppress_mates", JsonHelper.ArrayProp("string", "Mate names to temporarily suppress during stroke sweep (e.g. LimitDistance1).") },
                    { "suppress_components", JsonHelper.ArrayProp("string", "Flexible static-pose components (cables, U-loops, bellows) to temporarily suppress during stroke sweep.") },
                    { "stops", JsonHelper.ArrayProp("object", "Stop targets: [{label, value}] in document/specified units (or deg for angle).") },
                    { "rail_checks", JsonHelper.ArrayProp("object", "Optional bearing margin checks: [{rail_component, bearing_components:[], axis:[ax,ay,az]}].") },
                    { "limit_switch_checks", JsonHelper.ArrayProp("object", "Optional limit-switch actuation & housing crash checks: [{switch_component, striker_component, actuation_axis:[ax,ay,az], min_housing_clearance_mm}].") },
                    { "clearance_check_components", JsonHelper.ArrayProp("string", "Optional component subset for InterferenceDetectionManager at each stop.") },
                    { "ignore_pairs", JsonHelper.ArrayProp("object", "Optional [{component_a, component_b}] pairs to ignore in interference results.") },
                    { "units", JsonHelper.Prop("string", "Distance units: mm or in (defaults to active doc units).") }
                }, "moving_component", "stops"),
                Handler = args => HandleVerifyMotionStroke(swMgr, args)
            });
        }

        private static McpToolResult HandleQueryAssemblyTransforms(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            if (!(doc is IAssemblyDoc assy))
                return McpToolResult.Error("sw_query_assembly_transforms requires an active Assembly (.SLDASM) document.");

            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            List<string> paths = JsonHelper.GetStringList(args, "component_paths");

            List<Tuple<string, IComponent2>> targets = new List<Tuple<string, IComponent2>>();
            if (paths.Count == 0)
            {
                object[] top = assy.GetComponents(true) as object[];
                if (top != null)
                {
                    foreach (object co in top)
                    {
                        if (co is IComponent2 c)
                            targets.Add(Tuple.Create(c.Name2, c));
                    }
                }
            }
            else
            {
                foreach (string p in paths)
                {
                    IComponent2 c = AssemblyGeometryHelper.ResolveComponentByPath(assy, p);
                    targets.Add(Tuple.Create(p, c));
                }
            }

            List<object> compResults = new List<object>();
            foreach (Tuple<string, IComponent2> t in targets)
            {
                IComponent2 c = t.Item2;
                if (c == null)
                {
                    compResults.Add(new { requested_path = t.Item1, found = false });
                    continue;
                }

                double[] mat = c.Transform2 != null
                    ? (c.Transform2.ArrayData as double[] ?? AssemblyGeometryHelper.IdentityTransform16())
                    : AssemblyGeometryHelper.IdentityTransform16();
                double[] inv = AssemblyGeometryHelper.InvertTransform16(mat);
                object[] matesArr = null;
                try { matesArr = c.GetMates() as object[]; } catch { }

                object boxOut = null;
                try
                {
                    if (c.GetModelDoc2() is IPartDoc pd)
                    {
                        double[] pb = pd.GetPartBox(true) as double[];
                        if (pb != null && pb.Length >= 6)
                        {
                            AssemblyGeometryHelper.TransformAabbFast(mat, pb, out double[] wMin, out double[] wMax);
                            boxOut = FormatBox(wMin, wMax, units);
                        }
                    }
                    else
                    {
                        double[] cb = c.GetBox(false, false) as double[];
                        if (cb != null && cb.Length >= 6)
                            boxOut = FormatBox(new[] { cb[0], cb[1], cb[2] }, new[] { cb[3], cb[4], cb[5] }, units);
                    }
                }
                catch { }

                compResults.Add(new
                {
                    requested_path = t.Item1,
                    found = true,
                    component_name = c.Name2,
                    part_path = c.GetPathName(),
                    suppressed = c.IsSuppressed(),
                    is_fixed = c.IsFixed(),
                    mates_count = matesArr != null ? matesArr.Length : 0,
                    origin = ScaleVec(new[] { mat[9], mat[10], mat[11] }, units),
                    local_axes_in_world = new
                    {
                        x_axis = new[] { Math.Round(mat[0], 6), Math.Round(mat[1], 6), Math.Round(mat[2], 6) },
                        y_axis = new[] { Math.Round(mat[3], 6), Math.Round(mat[4], 6), Math.Round(mat[5], 6) },
                        z_axis = new[] { Math.Round(mat[6], 6), Math.Round(mat[7], 6), Math.Round(mat[8], 6) }
                    },
                    local_to_world_4x4 = AssemblyGeometryHelper.ToRowMajor4x4(mat, units),
                    world_to_local_4x4 = AssemblyGeometryHelper.ToRowMajor4x4(inv, units),
                    world_bounding_box = boxOut
                });
            }

            string fromFrame = JsonHelper.GetString(args, "from_frame", "world");
            string toFrame = JsonHelper.GetString(args, "to_frame", "world");
            List<object> rawPts = JsonHelper.GetList(args, "points");
            List<object> rawVecs = JsonHelper.GetList(args, "vectors");

            List<double[]> convertedPts = null;
            List<double[]> convertedVecs = null;
            if (rawPts.Count > 0 || rawVecs.Count > 0)
            {
                double[] fromMat = ResolveFrameMatrix(assy, fromFrame, out string fromErr);
                if (fromErr != null) return McpToolResult.Error(fromErr);
                double[] toMat = ResolveFrameMatrix(assy, toFrame, out string toErr);
                if (toErr != null) return McpToolResult.Error(toErr);
                double[] toInv = AssemblyGeometryHelper.InvertTransform16(toMat);

                if (rawPts.Count > 0)
                {
                    convertedPts = new List<double[]>();
                    foreach (object pObj in rawPts)
                    {
                        double[] p = ToMetersPoint(pObj, units);
                        if (p == null) continue;
                        double[] wp = AssemblyGeometryHelper.TransformPointFast(fromMat, p[0], p[1], p[2]);
                        double[] tp = AssemblyGeometryHelper.TransformPointFast(toInv, wp[0], wp[1], wp[2]);
                        convertedPts.Add(ScaleVec(tp, units));
                    }
                }

                if (rawVecs.Count > 0)
                {
                    convertedVecs = new List<double[]>();
                    foreach (object vObj in rawVecs)
                    {
                        double[] v = ToRawVec3(vObj);
                        if (v == null) continue;
                        double[] wv = AssemblyGeometryHelper.TransformVectorFast(fromMat, v[0], v[1], v[2]);
                        double[] tv = AssemblyGeometryHelper.TransformVectorFast(toInv, wv[0], wv[1], wv[2]);
                        convertedVecs.Add(new[] { Math.Round(tv[0], 6), Math.Round(tv[1], 6), Math.Round(tv[2], 6) });
                    }
                }
            }

            return McpToolResult.JsonText(new
            {
                document = doc.GetTitle(),
                units = units,
                component_count = compResults.Count,
                components = compResults,
                from_frame = fromFrame,
                to_frame = toFrame,
                transformed_points = convertedPts,
                transformed_vectors = convertedVecs
            });
        }

        private static double[] ResolveFrameMatrix(IAssemblyDoc assy, string frameName, out string errMsg)
        {
            errMsg = null;
            if (string.IsNullOrWhiteSpace(frameName) ||
                string.Equals(frameName.Trim(), "world", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(frameName.Trim(), "assembly", StringComparison.OrdinalIgnoreCase))
                return AssemblyGeometryHelper.IdentityTransform16();

            IComponent2 comp = AssemblyGeometryHelper.ResolveComponentByPath(assy, frameName.Trim());
            if (comp == null)
            {
                errMsg = "Could not resolve coordinate frame component: " + frameName;
                return null;
            }
            return comp.Transform2 != null
                ? (comp.Transform2.ArrayData as double[] ?? AssemblyGeometryHelper.IdentityTransform16())
                : AssemblyGeometryHelper.IdentityTransform16();
        }

        private static McpToolResult HandleRouteCableOrLoom(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            ISldWorks app = swMgr.GetApp();
            IModelDoc2 activeOrTarget = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            string targetCompPath = JsonHelper.GetString(args, "target_component");
            string createPartPath = JsonHelper.GetString(args, "create_part_path");
            bool insertIntoAssy = JsonHelper.GetBool(args, "insert_into_assembly", true);
            string waypointFrame = JsonHelper.GetString(args, "waypoint_frame", "assembly").ToLowerInvariant();
            double odMm = JsonHelper.GetDouble(args, "outer_diameter_mm", 0.0);
            if (odMm <= 0.05)
                return McpToolResult.Error("outer_diameter_mm must be > 0.05 mm.");
            double wallMm = JsonHelper.GetDouble(args, "wall_thickness_mm", 0.0);
            double minBendMm = JsonHelper.GetDouble(args, "min_bend_radius_mm", 0.0);
            if (minBendMm <= 0.0) minBendMm = 3.0 * odMm;
            bool enforceMinBend = JsonHelper.GetBool(args, "enforce_min_bend_radius", false);
            string units = SwConnectionManager.ResolveUnits(activeOrTarget, JsonHelper.GetString(args, "units", "mm"));
            string featName = JsonHelper.GetString(args, "feature_name", "Cable_Route_Sweep");
            List<object> rawWaypoints = JsonHelper.GetList(args, "waypoints");
            Dictionary<string, object> uLoop = JsonHelper.GetDict(args, "u_loop_spec");
            List<string> clearanceCheckComps = JsonHelper.GetStringList(args, "clearance_check_components");

            if (rawWaypoints.Count < 2 && uLoop == null)
                return McpToolResult.Error("Provide either 'waypoints' (>=2 points) or 'u_loop_spec'.");

            IAssemblyDoc assyCtx = activeOrTarget as IAssemblyDoc;
            IModelDoc2 assyDoc = assyCtx != null ? activeOrTarget : null;
            IComponent2 targetComp = null;
            IModelDoc2 partDoc = null;
            bool createdNewPart = false;
            double[] worldToLocalMat = AssemblyGeometryHelper.IdentityTransform16();

            return SwConnectionManager.RunWithModalGuard(app, () =>
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(createPartPath))
                    {
                        string dir = Path.GetDirectoryName(createPartPath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        string tpl = SwConnectionManager.ResolveDefaultTemplate(app, "part");
                        partDoc = app.NewDocument(tpl, (int)swDwgPaperSizes_e.swDwgPaperBsize, 0, 0) as IModelDoc2;
                        if (partDoc == null)
                            return McpToolResult.Error("Failed to create new cable part document.");
                        SwConnectionManager.SetDocumentUnitSystem(partDoc, "3d_printing");
                        int se = 0, swErr = 0;
                        partDoc.Extension.SaveAs3(createPartPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref se, ref swErr);
                        createdNewPart = true;
                    }
                    else if (!string.IsNullOrWhiteSpace(targetCompPath))
                    {
                        if (assyCtx == null)
                            return McpToolResult.Error("target_component requires an active Assembly (.SLDASM) document.");
                        targetComp = AssemblyGeometryHelper.ResolveComponentByPath(assyCtx, targetCompPath);
                        if (targetComp == null)
                            return McpToolResult.Error("Could not find target_component: " + targetCompPath);
                        partDoc = targetComp.GetModelDoc2() as IModelDoc2;
                        if (partDoc == null || !(partDoc is IPartDoc))
                            return McpToolResult.Error("target_component must point to a loaded Part (.SLDPRT) component.");
                        double[] cMat = targetComp.Transform2 != null ? targetComp.Transform2.ArrayData as double[] : AssemblyGeometryHelper.IdentityTransform16();
                        if (waypointFrame == "assembly")
                            worldToLocalMat = AssemblyGeometryHelper.InvertTransform16(cMat);
                    }
                    else if (activeOrTarget is IPartDoc)
                    {
                        partDoc = activeOrTarget;
                    }
                    else
                    {
                        return McpToolResult.Error("On an assembly document, specify either target_component or create_part_path.");
                    }

                    SwConnectionManager.ActivateDocumentSafe(app, partDoc);
                    ISketchManager sm = partDoc.SketchManager;
                    sm.Insert3DSketch(true);
                    ISketch activeSk = sm.ActiveSketch;
                    bool prevAdd = sm.AddToDB;
                    sm.AddToDB = true;

                    Func<double[], double[]> toLocalM = ptM =>
                        AssemblyGeometryHelper.TransformPointFast(worldToLocalMat, ptM[0], ptM[1], ptM[2]);

                    int segCount = 0;
                    if (uLoop != null)
                    {
                        segCount = BuildULoopSegments(sm, uLoop, units, toLocalM);
                    }
                    else
                    {
                        List<double[]> localPtsM = new List<double[]>();
                        foreach (object wObj in rawWaypoints)
                        {
                            double[] ptM = ToMetersPoint(wObj, units);
                            if (ptM != null) localPtsM.Add(toLocalM(ptM));
                        }
                        if (localPtsM.Count < 2)
                        {
                            sm.AddToDB = prevAdd;
                            sm.Insert3DSketch(true);
                            return McpToolResult.Error("Need at least 2 valid waypoints.");
                        }
                        if (localPtsM.Count == 2)
                        {
                            sm.CreateLine(localPtsM[0][0], localPtsM[0][1], localPtsM[0][2],
                                          localPtsM[1][0], localPtsM[1][1], localPtsM[1][2]);
                            segCount = 1;
                        }
                        else
                        {
                            double[] flat = new double[localPtsM.Count * 3];
                            for (int i = 0; i < localPtsM.Count; i++)
                            {
                                flat[i * 3] = localPtsM[i][0];
                                flat[i * 3 + 1] = localPtsM[i][1];
                                flat[i * 3 + 2] = localPtsM[i][2];
                            }
                            sm.CreateSpline2(flat, false);
                            segCount = 1;
                        }
                    }

                    sm.AddToDB = prevAdd;
                    IFeature skFeat = activeSk as IFeature;
                    string skName = featName + "_Path_Sk";
                    if (skFeat != null)
                    {
                        try { skFeat.Name = skName; skName = skFeat.Name; } catch { skName = skFeat.Name; }
                    }
                    sm.Insert3DSketch(true);

                    double odM = odMm * 0.001;
                    FeatureAndFixtureTools.SweepPathValidation val = FeatureAndFixtureTools.ValidateSweepPathCurvature(partDoc, skName, odM, "mm");
                    if (!val.Valid)
                    {
                        try { partDoc.ClearSelection2(true); skFeat?.Select2(false, 0); partDoc.Extension.DeleteSelection2(0); } catch { }
                        return McpToolResult.Error(val.Issue, new
                        {
                            outer_diameter_mm = odMm,
                            measured_min_bend_radius_mm = !double.IsInfinity(val.MinBendRadiusM) ? (object)Math.Round(val.MinBendRadiusM * 1000.0, 2) : null
                        });
                    }

                    double? minMeasBendMm = !double.IsInfinity(val.MinBendRadiusM)
                        ? (double?)Math.Round(val.MinBendRadiusM * 1000.0, 2)
                        : null;
                    string bendWarning = null;
                    if (minMeasBendMm.HasValue && minMeasBendMm.Value < minBendMm - 0.05)
                    {
                        bendWarning = string.Format(CultureInfo.InvariantCulture,
                            "Measured min bend radius {0:F2} mm is tighter than recommended min_bend_radius_mm={1:F2} mm.",
                            minMeasBendMm.Value, minBendMm);
                        if (enforceMinBend)
                        {
                            try { partDoc.ClearSelection2(true); skFeat?.Select2(false, 0); partDoc.Extension.DeleteSelection2(0); } catch { }
                            return McpToolResult.Error(bendWarning);
                        }
                    }

                    partDoc.ClearSelection2(true);
                    if (!SwConnectionManager.TrySelectByToken(partDoc, skName, false, 4))
                        return McpToolResult.Error("Failed to select created 3D sweep path sketch: " + skName);

                    bool thin = wallMm > 0.01;
                    IFeature sweepFeat = partDoc.FeatureManager.InsertProtrusionSwept4(
                        false, false, (int)swTwistControlType_e.swTwistControlFollowPath,
                        false, false, (int)swTangencyType_e.swTangencyNone, (int)swTangencyType_e.swTangencyNone,
                        thin, thin ? wallMm * 0.001 : 0.0, 0.0,
                        (int)swThinWallType_e.swThinWallOneDirection, 0,
                        true, true, true, 0.0, true, true, odM, 0);

                    if (sweepFeat == null)
                        return McpToolResult.Error("InsertProtrusionSwept4 returned null even after curvature pre-check. Check guide endpoints and wall thickness vs OD.");

                    try { sweepFeat.Name = featName; } catch { }

                    if (!string.IsNullOrWhiteSpace(partDoc.GetPathName()))
                    {
                        int se = 0, swErr = 0;
                        partDoc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref se, ref swErr);
                    }

                    // If newly created part should be inserted into the active assembly
                    if (createdNewPart && assyCtx != null && insertIntoAssy)
                    {
                        SwConnectionManager.ActivateDocumentSafe(app, assyDoc);
                        targetComp = assyCtx.AddComponent5(
                            createPartPath,
                            (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                            "", false, "", 0.0, 0.0, 0.0);
                        if (targetComp != null)
                            SwHelper.MateByPrincipalPlanes(assyCtx, targetComp, null, null);
                    }
                    else if (assyDoc != null)
                    {
                        SwConnectionManager.ActivateDocumentSafe(app, assyDoc);
                        assyDoc.EditRebuild3();
                    }

                    // Optional clearance & interference checks against neighboring assembly components
                    object subsetInterference = null;
                    List<object> neighborDistances = new List<object>();
                    if (assyCtx != null && clearanceCheckComps.Count > 0 && targetComp != null)
                    {
                        List<IComponent2> neighbors = AssemblyGeometryHelper.ResolveComponentsMatching(assyCtx, clearanceCheckComps);
                        List<IComponent2> checkSet = new List<IComponent2> { targetComp };
                        foreach (IComponent2 nb in neighbors)
                        {
                            if (!string.Equals(nb.Name2, targetComp.Name2, StringComparison.OrdinalIgnoreCase))
                                checkSet.Add(nb);
                        }

                        if (checkSet.Count >= 2 && checkSet.Count <= 40)
                        {
                            IMathUtility mu = app.GetMathUtility() as IMathUtility;
                            subsetInterference = TopologyDatumExtractor.RunSubsetInterferenceDetection(
                                assyDoc, assyCtx, mu, checkSet, null, excludeThread: true, groupByPair: true);
                        }

                        foreach (IComponent2 nb in neighbors)
                        {
                            if (string.Equals(nb.Name2, targetComp.Name2, StringComparison.OrdinalIgnoreCase)) continue;
                            assyDoc.ClearSelection2(true);
                            object pt1 = null, pt2 = null;
                            double distM = assyDoc.ClosestDistance(targetComp, nb, out pt1, out pt2);
                            neighborDistances.Add(new
                            {
                                neighbor = nb.Name2,
                                min_distance_mm = Math.Round(distM * 1000.0, 3)
                            });
                        }
                        assyDoc.ClearSelection2(true);
                    }

                    return McpToolResult.JsonText(new
                    {
                        part_title = partDoc.GetTitle(),
                        part_path = partDoc.GetPathName(),
                        component_name = targetComp?.Name2,
                        sketch_name = skName,
                        segments_created = segCount,
                        feature_name = sweepFeat.Name,
                        outer_diameter_mm = odMm,
                        wall_thickness_mm = wallMm,
                        path_length_mm = Math.Round(val.TotalLengthM * 1000.0, 2),
                        min_measured_bend_radius_mm = minMeasBendMm,
                        min_recommended_bend_radius_mm = minBendMm,
                        bend_radius_warning = bendWarning,
                        neighbor_clearances_mm = neighborDistances.Count > 0 ? neighborDistances : null,
                        subset_interference = subsetInterference
                    });
                }
                finally
                {
                    if (assyDoc != null)
                        SwConnectionManager.ActivateDocumentSafe(app, assyDoc);
                }
            });
        }

        private static int BuildULoopSegments(
            ISketchManager sm,
            Dictionary<string, object> uLoop,
            string units,
            Func<double[], double[]> toLocalM)
        {
            string axis = (JsonHelper.GetString(uLoop, "plane_axis", "X") ?? "X").Trim().ToUpperInvariant();
            double planeCoord = JsonHelper.GetNullableDouble(uLoop, "plane_coord")
                ?? JsonHelper.GetDouble(uLoop, "x_pos", 0.0);
            double uFront = JsonHelper.GetNullableDouble(uLoop, "z_front")
                ?? JsonHelper.GetDouble(uLoop, "u_front", 0.0);
            double uRear = JsonHelper.GetNullableDouble(uLoop, "z_rear")
                ?? JsonHelper.GetDouble(uLoop, "u_rear", 60.0);
            double vBottom = JsonHelper.GetNullableDouble(uLoop, "y_bottom")
                ?? JsonHelper.GetDouble(uLoop, "v_bottom", 0.0);
            double vFrontTop = JsonHelper.GetNullableDouble(uLoop, "y_front_top")
                ?? JsonHelper.GetDouble(uLoop, "v_front_top", 50.0);
            double vRearTop = JsonHelper.GetNullableDouble(uLoop, "y_rear_top")
                ?? JsonHelper.GetDouble(uLoop, "v_rear_top", 50.0);

            double spanU = Math.Abs(uRear - uFront);
            double rad = JsonHelper.GetDouble(uLoop, "bend_radius", 0.5 * spanU);
            if (rad <= 0.0 || 2.0 * rad > spanU + 1e-6)
                rad = 0.5 * spanU;

            double signU = uRear >= uFront ? 1.0 : -1.0;
            double dirV = vFrontTop >= vBottom ? 1.0 : -1.0;
            double vArcCenter = vBottom + dirV * rad;
            double invS2 = Math.Sqrt(0.5);

            Func<double, double, double[]> mapUV = (uVal, vVal) =>
            {
                double[] raw = axis == "Y"
                    ? new[] { uVal, planeCoord, vVal }
                    : (axis == "Z"
                        ? new[] { uVal, vVal, planeCoord }
                        : new[] { planeCoord, vVal, uVal });
                return toLocalM(new[]
                {
                    ShopCatalogService.ToMeters(raw[0], units),
                    ShopCatalogService.ToMeters(raw[1], units),
                    ShopCatalogService.ToMeters(raw[2], units)
                });
            };

            int segs = 0;
            double uArc1End = uFront + signU * rad;
            double uArc2Start = uRear - signU * rad;

            // Front vertical leg
            if (Math.Abs(vFrontTop - vArcCenter) > 0.05)
            {
                double[] p0 = mapUV(uFront, vFrontTop);
                double[] p1 = mapUV(uFront, vArcCenter);
                sm.CreateLine(p0[0], p0[1], p0[2], p1[0], p1[1], p1[2]);
                segs++;
            }

            // First 90 deg tangent quarter arc from (uFront, vArcCenter) to (uArc1End, vBottom)
            {
                double[] p0 = mapUV(uFront, vArcCenter);
                double[] p1 = mapUV(uArc1End, vBottom);
                double[] mid = mapUV(uFront + signU * rad * (1.0 - invS2), vArcCenter - dirV * rad * invS2);
                sm.Create3PointArc(p0[0], p0[1], p0[2], p1[0], p1[1], p1[2], mid[0], mid[1], mid[2]);
                segs++;
            }

            // Optional flat bottom leg when spanU > 2*R
            if (spanU - 2.0 * rad > 0.05)
            {
                double[] p0 = mapUV(uArc1End, vBottom);
                double[] p1 = mapUV(uArc2Start, vBottom);
                sm.CreateLine(p0[0], p0[1], p0[2], p1[0], p1[1], p1[2]);
                segs++;
            }

            // Second 90 deg tangent quarter arc from (uArc2Start, vBottom) to (uRear, vArcCenter)
            {
                double[] p0 = mapUV(uArc2Start, vBottom);
                double[] p1 = mapUV(uRear, vArcCenter);
                double[] mid = mapUV(uRear - signU * rad * (1.0 - invS2), vArcCenter - dirV * rad * invS2);
                sm.Create3PointArc(p0[0], p0[1], p0[2], p1[0], p1[1], p1[2], mid[0], mid[1], mid[2]);
                segs++;
            }

            // Rear vertical leg
            if (Math.Abs(vRearTop - vArcCenter) > 0.05)
            {
                double[] p0 = mapUV(uRear, vArcCenter);
                double[] p1 = mapUV(uRear, vRearTop);
                sm.CreateLine(p0[0], p0[1], p0[2], p1[0], p1[1], p1[2]);
                segs++;
            }

            return segs;
        }

        private class SavedMateState
        {
            public IFeature Feature;
            public bool WasSuppressed;
        }

        private class SavedCompState
        {
            public IComponent2 Component;
            public int PrevSuppression;
        }

        private static McpToolResult HandleVerifyMotionStroke(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            if (!(doc is IAssemblyDoc assy))
                return McpToolResult.Error("sw_verify_motion_stroke requires an active Assembly (.SLDASM) document.");

            ISldWorks app = swMgr.GetApp();
            IMathUtility mu = app.GetMathUtility() as IMathUtility;
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            string movingPath = JsonHelper.GetString(args, "moving_component");
            string driveMode = (JsonHelper.GetString(args, "drive_mode", "distance") ?? "distance").ToLowerInvariant();
            string driveEntA = JsonHelper.GetString(args, "drive_entity_a");
            string driveEntB = JsonHelper.GetString(args, "drive_entity_b");
            string existingMateName = JsonHelper.GetString(args, "existing_drive_mate");
            bool flipDim = JsonHelper.GetBool(args, "flip_drive_dimension", false);
            string alignStr = (JsonHelper.GetString(args, "alignment", "closest") ?? "closest").ToLowerInvariant();
            List<string> supMateNames = JsonHelper.GetStringList(args, "suppress_mates");
            List<string> supCompNames = JsonHelper.GetStringList(args, "suppress_components");
            List<object> rawStops = JsonHelper.GetList(args, "stops");
            List<object> railChecks = JsonHelper.GetList(args, "rail_checks");
            List<object> switchChecks = JsonHelper.GetList(args, "limit_switch_checks");
            List<string> clrCompPatterns = JsonHelper.GetStringList(args, "clearance_check_components");
            List<Tuple<string, string>> ignorePairs = TopologyDatumExtractor.ParseIgnorePairs(JsonHelper.GetList(args, "ignore_pairs"));

            if (rawStops.Count == 0)
                return McpToolResult.Error("'stops' array is required.");
            if (string.IsNullOrWhiteSpace(existingMateName) &&
                (string.IsNullOrWhiteSpace(driveEntA) || string.IsNullOrWhiteSpace(driveEntB)))
                return McpToolResult.Error("Provide either 'existing_drive_mate' or both 'drive_entity_a' and 'drive_entity_b'.");

            IComponent2 movingComp = AssemblyGeometryHelper.ResolveComponentByPath(assy, movingPath);
            if (movingComp == null)
                return McpToolResult.Error("Could not resolve moving_component: " + movingPath);

            double[] initialMat = movingComp.Transform2 != null
                ? (double[])((double[])movingComp.Transform2.ArrayData).Clone()
                : AssemblyGeometryHelper.IdentityTransform16();
            double[] initialOriginM = { initialMat[9], initialMat[10], initialMat[11] };

            return SwConnectionManager.RunWithModalGuard(app, () =>
            {
                SwConnectionManager.ActivateDocumentSafe(app, doc);

                List<SavedMateState> savedMates = new List<SavedMateState>();
                List<SavedCompState> savedComps = new List<SavedCompState>();
                IFeature tempDriveMateFeat = null;
                IDimension existingDim = null;
                double existingDimOrigVal = 0.0;
                List<object> stopReports = new List<object>();

                try
                {
                    // Suppress requested limit/lock mates cleanly
                    foreach (string mn in supMateNames)
                    {
                        IFeature mf = FindMateFeatureByName(doc, mn);
                        if (mf != null)
                        {
                            bool wasSup = mf.IsSuppressed();
                            savedMates.Add(new SavedMateState { Feature = mf, WasSuppressed = wasSup });
                            if (!wasSup)
                                mf.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null);
                        }
                    }

                    // Suppress requested flexible static-pose components (cables, U-loops)
                    if (supCompNames.Count > 0)
                    {
                        List<IComponent2> toSup = AssemblyGeometryHelper.ResolveComponentsMatching(assy, supCompNames, includeSuppressed: true);
                        foreach (IComponent2 sc in toSup)
                        {
                            int prevState = sc.GetSuppression2();
                            savedComps.Add(new SavedCompState { Component = sc, PrevSuppression = prevState });
                            if (prevState != (int)swComponentSuppressionState_e.swComponentSuppressed)
                                sc.SetSuppression2((int)swComponentSuppressionState_e.swComponentSuppressed);
                        }
                    }

                    IDimension driveDim = null;
                    if (!string.IsNullOrWhiteSpace(existingMateName))
                    {
                        IFeature em = FindMateFeatureByName(doc, existingMateName);
                        if (em == null)
                            return McpToolResult.Error("Could not find existing_drive_mate: " + existingMateName);
                        if (em.GetSpecificFeature2() is IMate2 m2 && m2.DisplayDimension != null)
                        {
                            driveDim = m2.DisplayDimension.GetDimension2(0);
                            existingDim = driveDim;
                            if (driveDim != null) existingDimOrigVal = driveDim.SystemValue;
                        }
                        if (driveDim == null)
                            return McpToolResult.Error("existing_drive_mate '" + existingMateName + "' does not expose a drivable dimension.");
                    }
                    else
                    {
                        int mType = driveMode == "angle" ? (int)swMateType_e.swMateANGLE : (int)swMateType_e.swMateDISTANCE;
                        int align = alignStr == "aligned"
                            ? (int)swMateAlign_e.swMateAlignALIGNED
                            : (alignStr == "anti_aligned" ? (int)swMateAlign_e.swMateAlignANTI_ALIGNED : (int)swMateAlign_e.swMateAlignCLOSEST);

                        Dictionary<string, object> firstStop = rawStops[0] as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(rawStops[0]));
                        double firstValRaw = JsonHelper.GetDouble(firstStop, "value", 0.0);
                        double firstValSys = driveMode == "angle"
                            ? ShopCatalogService.ToRadians(firstValRaw)
                            : ShopCatalogService.ToMeters(firstValRaw, units);

                        IMate2 created = AssemblyGeometryHelper.CreateMateBySelectors(
                            doc, assy, driveEntA, driveEntB, mType, align, flipDim,
                            driveMode == "distance" ? firstValSys : 0.0,
                            driveMode == "angle" ? firstValSys : 0.0,
                            false, out int mErr);
                        tempDriveMateFeat = created as IFeature;
                        if (created == null || mErr != (int)swAddMateError_e.swAddMateError_NoError || created.DisplayDimension == null)
                            return McpToolResult.Error("Failed to create temporary drive mate (err=" + mErr + ") between '" + driveEntA + "' and '" + driveEntB + "'.");
                        driveDim = created.DisplayDimension.GetDimension2(0);
                    }

                    // Resolve clearance component subset once (excluding suppressed static cables)
                    List<IComponent2> resolvedClrComps = clrCompPatterns.Count > 0
                        ? AssemblyGeometryHelper.ResolveComponentsMatching(assy, clrCompPatterns, includeSuppressed: false)
                        : new List<IComponent2>();
                    if (resolvedClrComps.Count > 0 && !resolvedClrComps.Exists(c => string.Equals(c.Name2, movingComp.Name2, StringComparison.OrdinalIgnoreCase)))
                        resolvedClrComps.Insert(0, movingComp);

                    foreach (object stObj in rawStops)
                    {
                        Dictionary<string, object> st = stObj as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(stObj));
                        string label = JsonHelper.GetString(st, "label", "stop");
                        double valRaw = JsonHelper.GetDouble(st, "value", 0.0);
                        double valSys = driveMode == "angle"
                            ? ShopCatalogService.ToRadians(valRaw)
                            : ShopCatalogService.ToMeters(valRaw, units);

                        driveDim.SetSystemValue3(valSys, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
                        doc.EditRebuild3();

                        double[] curMat = movingComp.Transform2 != null ? movingComp.Transform2.ArrayData as double[] : initialMat;
                        double[] curOrig = curMat != null && curMat.Length >= 12 ? new[] { curMat[9], curMat[10], curMat[11] } : initialOriginM;
                        double[] movingBox = movingComp.GetBox(false, false) as double[];

                        // Evaluate rail bearing margin checks
                        List<object> railOut = new List<object>();
                        foreach (object rcObj in railChecks)
                        {
                            Dictionary<string, object> rc = rcObj as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(rcObj));
                            string railPat = JsonHelper.GetString(rc, "rail_component");
                            List<string> bearPats = JsonHelper.GetStringList(rc, "bearing_components");
                            double[] axRaw = JsonHelper.GetDoubleArray(rc, "axis");
                            double[] axU = axRaw != null && axRaw.Length >= 3 ? AssemblyGeometryHelper.Normalize(axRaw) : new[] { 0.0, 1.0, 0.0 };

                            IComponent2 railComp = AssemblyGeometryHelper.ResolveComponentByPath(assy, railPat);
                            List<IComponent2> bears = AssemblyGeometryHelper.ResolveComponentsMatching(assy, bearPats);
                            if (railComp == null || bears.Count == 0) continue;

                            if (TryGetCompAxisSpan(railComp, axU, out double rMin, out double rMax))
                            {
                                double bMinAll = double.MaxValue, bMaxAll = double.MinValue;
                                foreach (IComponent2 b in bears)
                                {
                                    if (TryGetCompAxisSpan(b, axU, out double bMin, out double bMax))
                                    {
                                        bMinAll = Math.Min(bMinAll, bMin);
                                        bMaxAll = Math.Max(bMaxAll, bMax);
                                    }
                                }
                                double minMarginMm = (bMinAll - rMin) * 1000.0;
                                double maxMarginMm = (rMax - bMaxAll) * 1000.0;
                                railOut.Add(new
                                {
                                    rail = railComp.Name2,
                                    bearings_count = bears.Count,
                                    min_end_margin_mm = Math.Round(minMarginMm, 2),
                                    max_end_margin_mm = Math.Round(maxMarginMm, 2),
                                    overrun_detected = minMarginMm < -0.05 || maxMarginMm < -0.05
                                });
                            }
                        }

                        // Evaluate limit-switch checks
                        List<object> swOut = new List<object>();
                        foreach (object scObj in switchChecks)
                        {
                            Dictionary<string, object> sc = scObj as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(scObj));
                            string swPat = JsonHelper.GetString(sc, "switch_component");
                            string stPat = JsonHelper.GetString(sc, "striker_component");
                            double minClrMm = JsonHelper.GetDouble(sc, "min_housing_clearance_mm", 5.0);
                            double[] axRaw = JsonHelper.GetDoubleArray(sc, "actuation_axis");
                            double[] axU = axRaw != null && axRaw.Length >= 3 ? AssemblyGeometryHelper.Normalize(axRaw) : null;

                            IComponent2 swComp = AssemblyGeometryHelper.ResolveComponentByPath(assy, swPat);
                            IComponent2 stComp = AssemblyGeometryHelper.ResolveComponentByPath(assy, stPat);
                            if (swComp == null || stComp == null) continue;

                            doc.ClearSelection2(true);
                            object p1 = null, p2 = null;
                            double distM = doc.ClosestDistance(swComp, stComp, out p1, out p2);
                            double distMm = Math.Round(distM * 1000.0, 3);
                            double? axisGapMm = null;
                            if (axU != null &&
                                TryGetCompAxisSpan(swComp, axU, out double s0, out double s1) &&
                                TryGetCompAxisSpan(stComp, axU, out double t0, out double t1))
                            {
                                // Positive = air gap before contact; negative = feeler/lever overlap along actuation_axis
                                double gapM = Math.Max(t0 - s1, s0 - t1);
                                axisGapMm = Math.Round(gapM * 1000.0, 2);
                            }
                            swOut.Add(new
                            {
                                switch_component = swComp.Name2,
                                striker_component = stComp.Name2,
                                closest_distance_mm = distMm,
                                axis_gap_or_compression_mm = axisGapMm,
                                required_min_clearance_mm = minClrMm
                            });
                        }

                        // Subset interference check at this stop
                        object clrReport = null;
                        if (resolvedClrComps.Count >= 2 && resolvedClrComps.Count <= 40)
                        {
                            clrReport = TopologyDatumExtractor.RunSubsetInterferenceDetection(
                                doc, assy, mu, resolvedClrComps, ignorePairs, excludeThread: true, groupByPair: true);
                        }

                        stopReports.Add(new
                        {
                            label = label,
                            value = valRaw,
                            units = driveMode == "angle" ? "deg" : units,
                            moving_origin = ScaleVec(curOrig, units),
                            moving_box = movingBox != null && movingBox.Length >= 6
                                ? FormatBox(new[] { movingBox[0], movingBox[1], movingBox[2] }, new[] { movingBox[3], movingBox[4], movingBox[5] }, units)
                                : null,
                            rail_checks = railOut.Count > 0 ? railOut : null,
                            limit_switch_checks = swOut.Count > 0 ? swOut : null,
                            clearance = clrReport
                        });
                    }
                }
                finally
                {
                    if (tempDriveMateFeat != null)
                    {
                        try
                        {
                            doc.ClearSelection2(true);
                            tempDriveMateFeat.Select2(false, 0);
                            doc.Extension.DeleteSelection2(0);
                            doc.ClearSelection2(true);
                        }
                        catch { }
                    }
                    else if (existingDim != null)
                    {
                        try
                        {
                            existingDim.SetSystemValue3(existingDimOrigVal, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
                        }
                        catch { }
                    }

                    foreach (SavedCompState cs in savedComps)
                    {
                        try
                        {
                            if (cs.Component.GetSuppression2() != cs.PrevSuppression)
                                cs.Component.SetSuppression2(cs.PrevSuppression);
                        }
                        catch { }
                    }

                    foreach (SavedMateState ms in savedMates)
                    {
                        try
                        {
                            int actionFlag = ms.WasSuppressed
                                ? (int)swFeatureSuppressionAction_e.swSuppressFeature
                                : (int)swFeatureSuppressionAction_e.swUnSuppressFeature;
                            ms.Feature.SetSuppression2(actionFlag, (int)swInConfigurationOpts_e.swThisConfiguration, null);
                        }
                        catch { }
                    }

                    if (existingDim == null)
                    {
                        try
                        {
                            MathTransform restoreXf = mu.CreateTransform(initialMat) as MathTransform;
                            if (restoreXf != null && !movingComp.SetTransformAndSolve3(restoreXf, false))
                                movingComp.Transform2 = restoreXf;
                        }
                        catch { }
                    }

                    try { doc.EditRebuild3(); } catch { }
                    try { doc.ClearSelection2(true); } catch { }
                }

                double[] finalMat = movingComp.Transform2 != null ? movingComp.Transform2.ArrayData as double[] : initialMat;
                double driftMm = finalMat != null && finalMat.Length >= 12
                    ? Math.Sqrt(
                        Math.Pow((finalMat[9] - initialOriginM[0]) * 1000.0, 2) +
                        Math.Pow((finalMat[10] - initialOriginM[1]) * 1000.0, 2) +
                        Math.Pow((finalMat[11] - initialOriginM[2]) * 1000.0, 2))
                    : 0.0;

                return McpToolResult.JsonText(new
                {
                    document = doc.GetTitle(),
                    moving_component = movingComp.Name2,
                    drive_mode = driveMode,
                    units = units,
                    stops_verified = stopReports.Count,
                    stops = stopReports,
                    restored_cleanly = driftMm <= 0.1,
                    resting_pose_drift_mm = Math.Round(driftMm, 3)
                });
            });
        }

        private static bool TryGetCompAxisSpan(IComponent2 comp, double[] axisU, out double sMin, out double sMax)
        {
            sMin = double.MaxValue;
            sMax = double.MinValue;
            if (comp == null) return false;
            double[] minM = null, maxM = null;
            try
            {
                if (comp.GetModelDoc2() is IPartDoc pd && pd.GetPartBox(true) is double[] pb && pb.Length >= 6)
                {
                    double[] mat = comp.Transform2 != null ? comp.Transform2.ArrayData as double[] : null;
                    AssemblyGeometryHelper.TransformAabbFast(mat, pb, out minM, out maxM);
                }
                else if (comp.GetBox(false, false) is double[] cb && cb.Length >= 6)
                {
                    minM = new[] { cb[0], cb[1], cb[2] };
                    maxM = new[] { cb[3], cb[4], cb[5] };
                }
            }
            catch { }

            if (minM == null || maxM == null) return false;
            for (int ix = 0; ix <= 1; ix++)
            for (int iy = 0; iy <= 1; iy++)
            for (int iz = 0; iz <= 1; iz++)
            {
                double s = (ix == 0 ? minM[0] : maxM[0]) * axisU[0] +
                           (iy == 0 ? minM[1] : maxM[1]) * axisU[1] +
                           (iz == 0 ? minM[2] : maxM[2]) * axisU[2];
                if (s < sMin) sMin = s;
                if (s > sMax) sMax = s;
            }
            return true;
        }

        private static IFeature FindMateFeatureByName(IModelDoc2 doc, string mateName)
        {
            if (doc == null || string.IsNullOrWhiteSpace(mateName)) return null;
            IFeature f = doc.FirstFeature() as IFeature;
            while (f != null)
            {
                if (string.Equals(f.GetTypeName2(), "MateGroup", StringComparison.OrdinalIgnoreCase))
                {
                    IFeature found = FindMateRecursive(f.GetFirstSubFeature() as IFeature, mateName.Trim());
                    if (found != null) return found;
                }
                f = f.GetNextFeature() as IFeature;
            }
            return null;
        }

        private static IFeature FindMateRecursive(IFeature sub, string mateName)
        {
            while (sub != null)
            {
                if (string.Equals(sub.GetTypeName2(), "FtrFolder", StringComparison.OrdinalIgnoreCase))
                {
                    IFeature inner = FindMateRecursive(sub.GetFirstSubFeature() as IFeature, mateName);
                    if (inner != null) return inner;
                }
                else if (string.Equals(sub.Name, mateName, StringComparison.OrdinalIgnoreCase))
                {
                    return sub;
                }
                sub = sub.GetNextSubFeature() as IFeature;
            }
            return null;
        }

        private static object FormatBox(double[] minM, double[] maxM, string units)
        {
            return new
            {
                min = ScaleVec(minM, units),
                max = ScaleVec(maxM, units),
                span = new
                {
                    x = Math.Round(ShopCatalogService.FromMeters(maxM[0] - minM[0], units), 3),
                    y = Math.Round(ShopCatalogService.FromMeters(maxM[1] - minM[1], units), 3),
                    z = Math.Round(ShopCatalogService.FromMeters(maxM[2] - minM[2], units), 3)
                }
            };
        }

        private static double[] ScaleVec(double[] m, string units)
        {
            return new[]
            {
                Math.Round(ShopCatalogService.FromMeters(m[0], units), 3),
                Math.Round(ShopCatalogService.FromMeters(m[1], units), 3),
                Math.Round(ShopCatalogService.FromMeters(m[2], units), 3)
            };
        }

        private static double[] ToMetersPoint(object ptObj, string units)
        {
            List<object> list = JsonHelper.AsObjectList(ptObj);
            if (list.Count < 3) return null;
            return new[]
            {
                ShopCatalogService.ToMeters(Convert.ToDouble(list[0], CultureInfo.InvariantCulture), units),
                ShopCatalogService.ToMeters(Convert.ToDouble(list[1], CultureInfo.InvariantCulture), units),
                ShopCatalogService.ToMeters(Convert.ToDouble(list[2], CultureInfo.InvariantCulture), units)
            };
        }

        private static double[] ToRawVec3(object vecObj)
        {
            List<object> list = JsonHelper.AsObjectList(vecObj);
            if (list.Count < 3) return null;
            return new[]
            {
                Convert.ToDouble(list[0], CultureInfo.InvariantCulture),
                Convert.ToDouble(list[1], CultureInfo.InvariantCulture),
                Convert.ToDouble(list[2], CultureInfo.InvariantCulture)
            };
        }
    }
}
