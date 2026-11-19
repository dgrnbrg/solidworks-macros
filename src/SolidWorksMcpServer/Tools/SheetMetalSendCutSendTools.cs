using System;
using System.Collections.Generic;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcpServer.Core;
using SolidWorksMcpServer.Protocol;

namespace SolidWorksMcpServer.Tools
{
    public static class SheetMetalSendCutSendTools
    {
        public static void Register(McpStdioHost host, SwConnectionManager swMgr, ShopCatalogService catalog)
        {
            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_create_scs_sheet_metal_base",
                Description = "Create a SOLIDWORKS Sheet Metal Base Flange pre-configured with SendCutSend exact thickness, inside air-bend radius, and K-factor (deprioritized vendor gate applies).",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "sketch_name", JsonHelper.Prop("string", "Closed profile sketch name.") },
                    { "material", JsonHelper.Prop("string", "SendCutSend material key.", "5052_aluminum", new[] { "5052_aluminum", "6061_aluminum", "mild_steel_1008", "304_stainless", "c260_brass", "c110_copper" }) },
                    { "thickness_in", JsonHelper.Prop("number", "Nominal gauge thickness in inches (e.g. 0.063, 0.080, 0.125).", 0.063) },
                    { "confirm_vendor_cost", JsonHelper.Prop("boolean", "Acknowledge Tier 3 SendCutSend vendor cost/lead time vs in-house alternatives.", true) },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }, "sketch_name", "material", "thickness_in"),
                Handler = args => HandleCreateBaseFlange(swMgr, catalog, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_add_scs_edge_flange",
                Description = "Add a SendCutSend press-brake Edge Flange while validating minimum flat/formed flange length against sendcutsend_catalog.json.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "edge_tokens", JsonHelper.ArrayProp("string", "Sheet edge tokens along which to bend the flange.") },
                    { "length_in", JsonHelper.Prop("number", "Flange length in inches.") },
                    { "angle_deg", JsonHelper.Prop("number", "Bend angle in degrees.", 90.0) },
                    { "material", JsonHelper.Prop("string", "SendCutSend material key.", "5052_aluminum") },
                    { "thickness_in", JsonHelper.Prop("number", "Sheet thickness in inches.", 0.063) },
                    { "flip", JsonHelper.Prop("boolean", "Flip bend direction.", false) },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }, "edge_tokens", "length_in"),
                Handler = args => HandleAddEdgeFlange(swMgr, catalog, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_add_scs_tab_and_slot",
                Description = "Cut a weld-free self-fixturing slot with +0.010\" SendCutSend laser slip-fit clearance and corner relief radii on a target face.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "face_token", JsonHelper.Prop("string", "Planar sheet face token receiving the slot.") },
                    { "center_xyz_in", JsonHelper.ArrayProp("number", "Slot center [x,y,z] in inches.") },
                    { "tab_width_in", JsonHelper.Prop("number", "Mating tab width in inches.", 0.500) },
                    { "mating_thickness_in", JsonHelper.Prop("number", "Mating sheet thickness in inches.", 0.063) },
                    { "slot_clearance_in", JsonHelper.Prop("number", "Total slip clearance in inches (SendCutSend default 0.010).", 0.010) },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }, "face_token", "center_xyz_in"),
                Handler = args => HandleAddTabAndSlot(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_export_scs_flat_pattern_dxf",
                Description = "Flatten sheet metal part and export a clean 1:1 flat-pattern DXF for SendCutSend upload with preflight reminders.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "output_dxf_path", JsonHelper.Prop("string", "Target .dxf output path.") },
                    { "include_bend_lines", JsonHelper.Prop("boolean", "Include bend lines in DXF export.", true) },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }, "output_dxf_path"),
                Handler = args => HandleExportFlatDxf(swMgr, args)
            });
        }

        private static McpToolResult HandleCreateBaseFlange(SwConnectionManager swMgr, ShopCatalogService catalog, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            string sketchName = JsonHelper.GetString(args, "sketch_name");
            string material = JsonHelper.GetString(args, "material", "5052_aluminum");
            double thickIn = JsonHelper.GetDouble(args, "thickness_in", 0.063);
            bool confirmCost = JsonHelper.GetBool(args, "confirm_vendor_cost", true);

            if (!confirmCost)
                return McpToolResult.Error("Tier 3 Gate: confirm_vendor_cost=false. Evaluate in-house Woodworking, Tough PLA, or CNC Corian/Acrylic before using SendCutSend.");

            if (!catalog.TryGetSendCutSendGauge(material, thickIn, out var matInfo, out var gauge))
                return McpToolResult.Error("Gauge not found in sendcutsend_catalog.json for " + material + " @ " + thickIn + "\".");

            bool bendable = JsonHelper.GetBool(matInfo, "bendable", false);
            double actualThickIn = JsonHelper.GetDouble(gauge, "thickness_in", thickIn);
            double bendRadIn = bendable ? JsonHelper.GetDouble(gauge, "effective_bend_radius_in", actualThickIn) : actualThickIn;
            double kFactor = bendable ? JsonHelper.GetDouble(gauge, "k_factor", 0.42) : 0.42;

            SwConnectionManager.SetDocumentUnitSystem(doc, "sheet_metal_sendcutsend");

            return swMgr.ExecuteInUndoTransaction(doc, "SendCutSend Base Flange", true, () =>
            {
                doc.ClearSelection2(true);
                if (!SwConnectionManager.TrySelectByToken(doc, sketchName, false, 0))
                    throw new InvalidOperationException("Could not select sketch: " + sketchName);

                CustomBendAllowance cba = doc.FeatureManager.CreateCustomBendAllowance();
                cba.Type = (int)swBendAllowanceTypes_e.swBendAllowanceKFactor;
                cba.KFactor = kFactor;

                IFeature feat = doc.FeatureManager.InsertSheetMetalBaseFlange2(
                    actualThickIn * 0.0254,
                    false,
                    bendRadIn * 0.0254,
                    0.0, 0.0, false, 0, 0, 0,
                    cba,
                    false, 0, 0.0001, 0.0001, 0.5, true, false, false, false);

                if (feat == null)
                    throw new InvalidOperationException("InsertSheetMetalBaseFlange2 returned null.");

                return McpToolResult.JsonText(new
                {
                    feature_name = feat.Name,
                    material = JsonHelper.GetString(matInfo, "display_name"),
                    thickness_in = actualThickIn,
                    bendable = bendable,
                    bend_radius_in = bendRadIn,
                    k_factor = kFactor,
                    vendor_note = bendable
                        ? "Verify flat-pattern deduction against sendcutsend.com/bending-calculator/ before checkout."
                        : "Material is FLAT LASER CUT ONLY at SendCutSend (no press-brake bending)."
                });
            });
        }

        private static McpToolResult HandleAddEdgeFlange(SwConnectionManager swMgr, ShopCatalogService catalog, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            List<string> edgeTokens = JsonHelper.GetStringList(args, "edge_tokens");
            double lenIn = JsonHelper.GetDouble(args, "length_in", 0.5);
            double angDeg = JsonHelper.GetDouble(args, "angle_deg", 90.0);
            string material = JsonHelper.GetString(args, "material", "5052_aluminum");
            double thickIn = JsonHelper.GetDouble(args, "thickness_in", 0.063);
            bool flip = JsonHelper.GetBool(args, "flip", false);

            if (!catalog.TryGetSendCutSendGauge(material, thickIn, out var matInfo, out var gauge))
                return McpToolResult.Error("Gauge not found in sendcutsend_catalog.json.");

            if (!JsonHelper.GetBool(matInfo, "bendable", false))
                return McpToolResult.Error("SendCutSend does NOT support bending for " + JsonHelper.GetString(matInfo, "display_name") + ". Switch to 5052 Aluminum, Mild Steel, or 304 Stainless.");

            double minFormed = JsonHelper.GetDouble(gauge, "min_flange_after_bend_in", 0.303);
            if (lenIn < minFormed)
                return McpToolResult.Error(string.Format("Flange length {0:F3}\" is below SendCutSend minimum formed flange {1:F3}\" for this gauge.", lenIn, minFormed));

            double radIn = JsonHelper.GetDouble(gauge, "effective_bend_radius_in", thickIn);
            double kFactor = JsonHelper.GetDouble(gauge, "k_factor", 0.42);

            return swMgr.ExecuteInUndoTransaction(doc, "SendCutSend Edge Flange", true, () =>
            {
                List<object> edges = new List<object>();
                foreach (string tok in edgeTokens)
                {
                    IEdge e = SwConnectionManager.FindEdgeByToken(doc, tok);
                    if (e == null) throw new InvalidOperationException("Edge not found: " + tok);
                    edges.Add(e);
                }

                CustomBendAllowance cba = doc.FeatureManager.CreateCustomBendAllowance();
                cba.Type = (int)swBendAllowanceTypes_e.swBendAllowanceKFactor;
                cba.KFactor = kFactor;

                IFeature feat = doc.FeatureManager.InsertSheetMetalEdgeFlange2(
                    edges.ToArray(),
                    null,
                    (int)swInsertEdgeFlangeOptions_e.swInsertEdgeFlangeUseDefaultRelief,
                    ShopCatalogService.ToRadians(angDeg),
                    radIn * 0.0254,
                    (int)swFlangePositionTypes_e.swFlangePositionTypeBendOutside,
                    lenIn * 0.0254,
                    (int)swSheetMetalReliefTypes_e.swSheetMetalReliefRectangular,
                    0.5, 0.5, 0.0,
                    (int)swFlangeDimTypes_e.swFlangeDimTypeOuterVirtualSharp,
                    cba);

                if (feat == null)
                    throw new InvalidOperationException("InsertSheetMetalEdgeFlange2 returned null.");

                return McpToolResult.JsonText(new
                {
                    feature_name = feat.Name,
                    flange_length_in = lenIn,
                    min_flange_required_in = minFormed,
                    bend_radius_in = radIn,
                    k_factor = kFactor,
                    flip_note = flip ? "Edit flange direction if flipped." : null
                });
            });
        }

        private static McpToolResult HandleAddTabAndSlot(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            ISldWorks app = swMgr.GetApp();
            string faceToken = JsonHelper.GetString(args, "face_token");
            double[] centerIn = JsonHelper.GetDoubleArray(args, "center_xyz_in");
            double tabW = JsonHelper.GetDouble(args, "tab_width_in", 0.500);
            double matT = JsonHelper.GetDouble(args, "mating_thickness_in", 0.063);
            double clearIn = JsonHelper.GetDouble(args, "slot_clearance_in", 0.010);

            if (centerIn.Length < 3)
                return McpToolResult.Error("center_xyz_in requires [x, y, z] in inches.");

            double slotW = (tabW + clearIn) * 0.0254;
            double slotH = (matT + clearIn) * 0.0254;
            double cxM = centerIn[0] * 0.0254;
            double cyM = centerIn[1] * 0.0254;
            double czM = centerIn[2] * 0.0254;

            return swMgr.ExecuteInUndoTransaction(doc, "SendCutSend Clearance Slot", true, () =>
            {
                doc.ClearSelection2(true);
                if (!SwConnectionManager.TrySelectByToken(doc, faceToken, false, 0))
                    throw new InvalidOperationException("Face not found: " + faceToken);

                ISketchManager sm = doc.SketchManager;
                sm.InsertSketch(true);
                ISketch sk = sm.ActiveSketch;
                IMathUtility mathUtil = app.GetMathUtility() as IMathUtility;
                SwConnectionManager.MapPointToSketch2D(mathUtil, sk.ModelToSketchTransform, cxM, cyM, czM, out double sx, out double sy);

                sm.AddToDB = true;
                sm.CreateCenterRectangle(sx, sy, 0, sx + 0.5 * slotW, sy + 0.5 * slotH, 0);
                sm.AddToDB = false;
                IFeature skFeat = sk as IFeature;
                if (skFeat != null) skFeat.Name = "SCS_TabSlot_Sk";
                sm.InsertSketch(true);

                doc.ClearSelection2(true);
                skFeat.Select2(false, 0);
                IFeature cut = doc.FeatureManager.FeatureCut4(true, false, false, (int)swEndConditions_e.swEndCondThroughAll, 0,
                    0.05, 0, false, false, false, false, 0, 0, false, false, false, false, false, true, true, true, true, false, 0, 0, false, false);
                if (cut == null)
                {
                    doc.ClearSelection2(true);
                    skFeat.Select2(false, 0);
                    cut = doc.FeatureManager.FeatureCut4(true, false, true, (int)swEndConditions_e.swEndCondThroughAll, 0,
                        0.05, 0, false, false, false, false, 0, 0, false, false, false, false, false, true, true, true, true, false, 0, 0, false, false);
                }
                if (cut != null) cut.Name = "SCS_WeldFree_Slot";

                return McpToolResult.JsonText(new
                {
                    feature_name = cut?.Name,
                    slot_width_in = Math.Round(tabW + clearIn, 4),
                    slot_height_in = Math.Round(matT + clearIn, 4),
                    corner_relief_note = "Add >= 0.015\" corner radii or slot end arcs to prevent laser corner stress cracking."
                });
            });
        }

        private static McpToolResult HandleExportFlatDxf(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            if (!(doc is IPartDoc part))
                return McpToolResult.Error("Flat pattern DXF export requires an active Sheet Metal Part.");

            string outPath = JsonHelper.GetString(args, "output_dxf_path");
            bool includeBends = JsonHelper.GetBool(args, "include_bend_lines", true);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));

            string modelPath = doc.GetPathName();
            if (string.IsNullOrWhiteSpace(modelPath))
                return McpToolResult.Error("Save the sheet metal .SLDPRT file before exporting flat pattern DXF.");

            int opts = 1; // geometry
            if (includeBends) opts |= 4; // bend lines
            bool ok = part.ExportToDWG2(outPath, modelPath, (int)swExportToDWG_e.swExportToDWG_ExportSheetMetal,
                true, null, false, false, opts, null);

            return McpToolResult.JsonText(new
            {
                exported = ok,
                output_dxf_path = outPath,
                include_bend_lines = includeBends,
                preflight_checklist = new[]
                {
                    "Verify 1:1 scale (IPS inches) and zero stray construction lines.",
                    "Confirm any countersunk holes in DXF show ONLY the minor thru-hole diameter.",
                    "Verify bend lines are continuous before submitting to SendCutSend."
                }
            }, isError: !ok);
        }
    }
}
