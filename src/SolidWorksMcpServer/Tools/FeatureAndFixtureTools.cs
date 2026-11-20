using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcpServer.Core;
using SolidWorksMcpServer.Protocol;

namespace SolidWorksMcpServer.Tools
{
    public static class FeatureAndFixtureTools
    {
        public static void Register(McpStdioHost host, SwConnectionManager swMgr, ShopCatalogService catalog)
        {
            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_extrude_boss_or_cut",
                Description = "Extrude a boss or cut from a named sketch (or inline 'sketch' / batch 'operations' array), with optional signed start_offset and automatic single-direction flip fallback on cuts.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "sketch_name", JsonHelper.Prop("string", "Sketch feature name (or omit when using operations batch).") },
                    { "sketch", JsonHelper.Prop("object", "Optional inline 2D sketch spec {plane_or_face, sketch_name, entities, ...} created automatically before extruding.") },
                    { "mode", JsonHelper.Prop("string", "boss or cut.", "boss", new[] { "boss", "cut" }) },
                    { "is_cut", JsonHelper.Prop("boolean", "Alternative bool flag: true for cut, false for boss.", false) },
                    { "depth", JsonHelper.Prop("number", "Extrude depth in document/specified units.", 1.0) },
                    { "start_offset", JsonHelper.Prop("number", "Signed start offset distance in document/specified units (0 = start on sketch plane).", 0.0) },
                    { "start_offset_mode", JsonHelper.Prop("string", "How positive start_offset is interpreted: model_positive (along +X/+Y/+Z when sketch aligns to a cardinal plane) or sketch_normal (along sketch Z normal).", "model_positive", new[] { "model_positive", "sketch_normal" }) },
                    { "through_all", JsonHelper.Prop("boolean", "Use Through All end condition.", false) },
                    { "mid_plane", JsonHelper.Prop("boolean", "Use Mid Plane end condition.", false) },
                    { "flip_direction", JsonHelper.Prop("boolean", "Flip extrude/cut direction.", false) },
                    { "draft_deg", JsonHelper.Prop("number", "Draft angle in degrees.", 0.0) },
                    { "merge", JsonHelper.Prop("boolean", "Merge result with existing solid.", true) },
                    { "feature_name", JsonHelper.Prop("string", "Optional semantic feature name.") },
                    { "hide_sketch", JsonHelper.Prop("boolean", "Hide the consumed sketch after creating the feature.", false) },
                    { "operations", JsonHelper.ArrayProp("object", "Batch mode: array of extrude spec objects executed inside one Undo transaction.") },
                    { "units", JsonHelper.Prop("string", "Units: mm, in, or m.") },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }),
                Handler = args => HandleExtrude(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_revolve_boss_or_cut",
                Description = "Create a revolved boss or cut (or batch via 'operations' array) from a sketch containing a centerline axis (or named axis).",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "sketch_name", JsonHelper.Prop("string", "Sketch feature name (or omit when using operations batch).") },
                    { "sketch", JsonHelper.Prop("object", "Optional inline 2D sketch spec created automatically before revolving.") },
                    { "mode", JsonHelper.Prop("string", "boss or cut.", "boss", new[] { "boss", "cut" }) },
                    { "is_cut", JsonHelper.Prop("boolean", "Alternative bool flag: true for cut, false for boss.", false) },
                    { "angle_deg", JsonHelper.Prop("number", "Revolution angle in degrees.", 360.0) },
                    { "merge", JsonHelper.Prop("boolean", "Merge result.", true) },
                    { "feature_name", JsonHelper.Prop("string", "Optional semantic feature name.") },
                    { "hide_sketch", JsonHelper.Prop("boolean", "Hide the consumed sketch after creating the feature.", false) },
                    { "operations", JsonHelper.ArrayProp("object", "Batch mode: array of revolve spec objects executed inside one Undo transaction.") },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }),
                Handler = args => HandleRevolve(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_create_airflow_loft_or_sweep",
                Description = "Create a smooth airflow, dust-collection, or air-blast loft or sweep (including circular_diameter single-path sweeps with pre-validated bend curvature vs R > D/2) with optional thin-wall shelling and 3D-print DFM telemetry.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "mode", JsonHelper.Prop("string", "loft or sweep.", "loft", new[] { "loft", "sweep" }) },
                    { "profiles", JsonHelper.ArrayProp("string", "Ordered list of closed profile sketch names (2+ for loft, 1 for sweep unless circular_diameter > 0).") },
                    { "path_sketch", JsonHelper.Prop("string", "Path sketch name (required for sweep).") },
                    { "circular_diameter", JsonHelper.Prop("number", "Outer diameter for built-in Circular Profile sweep when profiles is omitted.", 0.0) },
                    { "guide_curves", JsonHelper.ArrayProp("string", "Optional guide curve sketch names.") },
                    { "start_tangent_normal", JsonHelper.Prop("boolean", "Apply Normal-to-Profile start/end tangency on lofts.", true) },
                    { "wall_thickness_mm", JsonHelper.Prop("number", "Optional thin-wall thickness in mm (e.g. 1.6 or 2.0 for Tough PLA ducts).", 0.0) },
                    { "merge", JsonHelper.Prop("boolean", "Merge with existing solid.", true) },
                    { "units", JsonHelper.Prop("string", "Units for circular_diameter and bend telemetry: mm or in.") },
                    { "feature_name", JsonHelper.Prop("string", "Optional semantic feature name.") },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }),
                Handler = args => HandleAirflowLoftOrSweep(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_shell_duct_or_body",
                Description = "Hollow a solid body leaving selected inlet/outlet port faces open.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "thickness", JsonHelper.Prop("number", "Shell wall thickness.") },
                    { "open_face_tokens", JsonHelper.ArrayProp("string", "Face tokens to remove (open duct ends).") },
                    { "outward", JsonHelper.Prop("boolean", "Shell outward instead of inward.", false) },
                    { "units", JsonHelper.Prop("string", "Units: mm or in.") },
                    { "feature_name", JsonHelper.Prop("string", "Optional feature name.") },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }, "thickness", "open_face_tokens"),
                Handler = args => HandleShell(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_fillet_or_chamfer",
                Description = "Apply constant-radius fillets or chamfers to edge or face tokens (or batch via 'operations' array).",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "kind", JsonHelper.Prop("string", "fillet or chamfer.", "fillet", new[] { "fillet", "chamfer" }) },
                    { "edge_or_face_tokens", JsonHelper.ArrayProp("string", "Edge or face tokens.") },
                    { "size", JsonHelper.Prop("number", "Fillet radius or chamfer distance.") },
                    { "angle_deg", JsonHelper.Prop("number", "Chamfer angle in degrees.", 45.0) },
                    { "units", JsonHelper.Prop("string", "Units: mm or in.") },
                    { "feature_name", JsonHelper.Prop("string", "Optional feature name.") },
                    { "operations", JsonHelper.ArrayProp("object", "Batch mode: array of fillet/chamfer spec objects executed inside one Undo transaction.") },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }),
                Handler = args => HandleFilletOrChamfer(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_pattern_or_mirror",
                Description = "Create linear, circular, or mirror patterns of features or bodies (or batch via 'operations' array).",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "kind", JsonHelper.Prop("string", "linear, circular, or mirror.", "linear", new[] { "linear", "circular", "mirror" }) },
                    { "feature_names", JsonHelper.ArrayProp("string", "Features to pattern/mirror.") },
                    { "direction_ref", JsonHelper.Prop("string", "Edge/axis/plane token for Direction 1 or Mirror plane.") },
                    { "count", JsonHelper.Prop("integer", "Instance count (linear/circular).", 2) },
                    { "spacing", JsonHelper.Prop("number", "Linear spacing or circular total angle (deg).", 1.0) },
                    { "flip", JsonHelper.Prop("boolean", "Flip direction.", false) },
                    { "units", JsonHelper.Prop("string", "Units for linear spacing: mm or in.") },
                    { "operations", JsonHelper.ArrayProp("object", "Batch mode: array of pattern/mirror spec objects executed inside one Undo transaction.") },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }),
                Handler = args => HandlePatternOrMirror(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_hole_preset",
                Description = "Cut shop-specific preset holes/mortises at [x,y] points on a planar face (or batch via 'operations' array): McMaster heat-set inserts, metric Forstner, Blum 35mm hinge cup, 1/8 NPT vac tap, QCW 3/8 dowel grid, or Domino mortises.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "preset_type", JsonHelper.Prop("string", "Preset category.", "mcmaster_insert", new[] { "mcmaster_insert", "forstner_metric", "qcw_dowel_grid", "domino_mortise", "blum_hinge_cup", "npt_1_8_vac_tap", "fastener_thru" }) },
                    { "preset_id", JsonHelper.Prop("string", "Specific ID (e.g. 94180A333, 20mm, 5x30, M4, 1/4-20).") },
                    { "face_token", JsonHelper.Prop("string", "Target planar face token or plane name.") },
                    { "locations", JsonHelper.ArrayProp("object", "Array of 3D [x, y, z] hole center coordinate arrays on the target face.") },
                    { "depth", JsonHelper.Prop("number", "Optional override depth.") },
                    { "operations", JsonHelper.ArrayProp("object", "Batch mode: array of hole_preset spec objects executed inside one Undo transaction.") },
                    { "units", JsonHelper.Prop("string", "Units for locations/depth: mm or in.") },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }),
                Handler = args => HandleHolePreset(swMgr, catalog, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_create_gridfinity",
                Description = "Generate a standard Zach Freedman Gridfinity bin or baseplate in MMGS (42mm pitch, 7mm U height, stepped 45deg foot/lip, optional 6.5x2.4mm magnet & M3 screw holes, front scoop, and embossed label).",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "kind", JsonHelper.Prop("string", "bin or baseplate.", "bin", new[] { "bin", "baseplate" }) },
                    { "units_x", JsonHelper.Prop("integer", "Grid units along X.", 1) },
                    { "units_y", JsonHelper.Prop("integer", "Grid units along Y.", 2) },
                    { "height_u", JsonHelper.Prop("integer", "Bin height in 7mm U units (for bins).", 4) },
                    { "include_stacking_lip", JsonHelper.Prop("boolean", "Include 4.4mm top stacking lip.", true) },
                    { "magnet_pockets", JsonHelper.Prop("boolean", "Cut 6.5x2.4mm magnet pockets in cell corners.", true) },
                    { "m3_screw_holes", JsonHelper.Prop("boolean", "Cut 3.0mm M3 screw holes inside magnet pockets.", false) },
                    { "wall_thickness_mm", JsonHelper.Prop("number", "Wall thickness in mm (multiple of 0.4mm).", 1.2) },
                    { "floor_thickness_mm", JsonHelper.Prop("number", "Solid floor thickness in mm above base foot.", 2.0) },
                    { "hollow_interior", JsonHelper.Prop("boolean", "Cut hollow interior cavity (set false for solid shadow toolholders).", true) },
                    { "embossed_label", JsonHelper.Prop("string", "Optional front/rim label text.") },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path (creates MMGS part if omitted).") }
                }),
                Handler = args => HandleCreateGridfinity(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_boolean_or_fixture_cavity",
                Description = "Insert a STEP/SLDPRT component body into a fixture part and subtract or indent a form-fitting cavity with configurable clearance offset.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "operation", JsonHelper.Prop("string", "subtract, indent, or common.", "indent", new[] { "subtract", "indent", "common" }) },
                    { "target_body", JsonHelper.Prop("string", "Target fixture solid body name.") },
                    { "tool_step_or_part_path", JsonHelper.Prop("string", "Optional external SLDPRT/STEP file to insert as tool body.") },
                    { "tool_body_name", JsonHelper.Prop("string", "Existing tool body name if already in multibody part.") },
                    { "clearance_offset", JsonHelper.Prop("number", "Cavity clearance offset (e.g. 0.010 in or 0.3 mm).", 0.010) },
                    { "wall_thickness", JsonHelper.Prop("number", "Indent shell thickness if operation=indent.", 0.250) },
                    { "keep_tool_body", JsonHelper.Prop("boolean", "Keep tool body after boolean.", false) },
                    { "units", JsonHelper.Prop("string", "Units: mm or in.") },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }, "operation", "target_body"),
                Handler = args => HandleBooleanOrCavity(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_create_bending_form",
                Description = "Generate an offset curved caul / vacuum-press lamination form from a curved face token accounting for veneer ply stack + breather allowance.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "target_curved_face_token", JsonHelper.Prop("string", "Face token of the laminated furniture curve.") },
                    { "veneer_ply_count", JsonHelper.Prop("integer", "Number of veneer/bending-ply layers.", 5) },
                    { "ply_thickness", JsonHelper.Prop("number", "Single ply thickness (default 0.0625 in).", 0.0625) },
                    { "breather_allowance", JsonHelper.Prop("number", "Vacuum breather/caul allowance.", 0.030) },
                    { "caul_thickness", JsonHelper.Prop("number", "Thickness of generated form/caul shell.", 0.500) },
                    { "units", JsonHelper.Prop("string", "Units: in or mm.") },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }, "target_curved_face_token"),
                Handler = args => HandleCreateBendingForm(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_engrave_or_emboss_label",
                Description = "Engrave or emboss shop text on planar faces (or batch via 'labels' array), automatically recommending the 3/8\" 90deg GrooVee Jenny (wood), 30deg 0.2mm V-bit (Corian/acrylic), or 0.6mm emboss (3D print).",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "face_token", JsonHelper.Prop("string", "Target planar face token or plane name.") },
                    { "text", JsonHelper.Prop("string", "Label text (e.g. '1/4 BIT + 3/8 BUSHING').") },
                    { "origin_xyz", JsonHelper.ArrayProp("number", "Text start point [x,y,z].") },
                    { "font_height", JsonHelper.Prop("number", "Text height in document/specified units.", 0.35) },
                    { "depth", JsonHelper.Prop("number", "Cut/boss depth.", 0.030) },
                    { "material_context", JsonHelper.Prop("string", "wood, corian_acrylic, or 3d_print.", "wood", new[] { "wood", "corian_acrylic", "3d_print" }) },
                    { "emboss", JsonHelper.Prop("boolean", "Extrude raised boss instead of cut (true for 3D prints).", false) },
                    { "labels", JsonHelper.ArrayProp("object", "Batch mode: array of label spec objects [{face_token, text, origin_xyz, font_height, depth, material_context, emboss}] executed inside one Undo transaction.") },
                    { "units", JsonHelper.Prop("string", "Units: in or mm.") },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }),
                Handler = args => HandleEngraveLabel(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_build_part_features",
                Description = "Build or cleanly rebuild an entire parametric part in a SINGLE Undo transaction with UI suppressed: optional reset_first (clears user features/sketches while keeping Origin & standard planes), equations, ref_plane/sketch_2d/sketch_3d/extrude(supports inline sketch)/revolve/hole_preset/fillet/chamfer/shell/pattern/mirror/label/teardrop/crisscross_bridge/joinery steps, and automatic sketch hiding.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "reset_first", JsonHelper.Prop("boolean", "Delete existing user features, sketches, and user ref planes before building (keeps Front/Top/Right Plane & Origin).", false) },
                    { "units", JsonHelper.Prop("string", "Default units for all steps: mm, in, or m (defaults to active doc units).") },
                    { "equations", JsonHelper.Prop("object", "Optional equations args {action, equations_txt_path, variables} applied before building features.") },
                    { "features", JsonHelper.ArrayProp("object", "Ordered list of feature step objects [{op:'ref_plane'|'sketch_2d'|'sketch_3d'|'extrude'|'revolve'|'hole_preset'|'fillet'|'chamfer'|'shell'|'pattern'|'mirror'|'label'|'teardrop'|'crisscross_bridge'|'joinery', ...}]. 'extrude'/'revolve' can include inline sketch:{plane_or_face, sketch_name, entities}.") },
                    { "keep_sketches_visible", JsonHelper.Prop("boolean", "Leave newly created sketches visible (default false auto-hides all newly created sketches).", false) },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }, "features"),
                Handler = args => HandleBuildPartFeatures(swMgr, catalog, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_invoke_repo_addin_tool",
                Description = "Headlessly invoke repository SOLIDWORKS add-ins and macros: ExportFacesToDXF.swp, SolidWorks3DPrintDFMAddin (3dprint_teardrop / 3dprint_bridge MacroFeatures or standalone native cut fallback), and SolidWorksJoineryAddin (joinery MacroFeature or standalone native cut/boss fallback).",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "tool", JsonHelper.Prop("string", "Tool identifier.", "export_faces_dxf", new[] { "export_faces_dxf", "3dprint_teardrop", "3dprint_bridge", "joinery" }) },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") },
                    { "face_tokens", JsonHelper.ArrayProp("string", "Optional face tokens to scope teardrops, crisscross bridges, or joinery (omit to auto-scan qualifying features/contacts).") },
                    { "edge_tokens", JsonHelper.ArrayProp("string", "Optional edge tokens for joinery contact detection.") },
                    { "body_names", JsonHelper.ArrayProp("string", "Optional solid body names in multibody parts.") },
                    { "bed_plane_or_face", JsonHelper.Prop("string", "Optional plane name or face token defining the print bed upward normal (3dprint_*).") },
                    { "build_direction", JsonHelper.ArrayProp("number", "Optional [x,y,z] print bed upward normal vector (3dprint_*, default [0,0,1]).") },
                    { "flip_bed_direction", JsonHelper.Prop("boolean", "Invert resolved print bed direction (3dprint_*).", false) },
                    { "overhang_angle_deg", JsonHelper.Prop("number", "Teardrop overhang roof angle from bed horizontal in degrees (default 40).", 40.0) },
                    { "enable_cap", JsonHelper.Prop("boolean", "Cap teardrop apex height to max_cap_height_mm (default true).", true) },
                    { "max_cap_height_mm", JsonHelper.Prop("number", "Max teardrop cap extension along bed normal in mm (default 0.6).", 0.6) },
                    { "min_circle_angle_deg", JsonHelper.Prop("number", "Min hole-axis to bed-normal angle in degrees for teardrop scan (default 40).", 40.0) },
                    { "min_hole_diameter_mm", JsonHelper.Prop("number", "Min hole diameter in mm for teardrop auto-scan (default 1.5).", 1.5) },
                    { "layer_height_mm", JsonHelper.Prop("number", "Layer height in mm for crisscross bridge tier depth (default 0.20).", 0.20) },
                    { "bridge_angle_offset_deg", JsonHelper.Prop("number", "Crisscross bridge rotation angle offset in degrees (default 0).", 0.0) },
                    { "joint_type", JsonHelper.Prop("string", "Joinery type for tool='joinery'.", "tab_and_slot", new[] { "tab_and_slot", "finger_joint", "through_dado", "blind_dado", "rabbet", "dado_tab_combo" }) },
                    { "tab_count", JsonHelper.Prop("integer", "Tabs/fingers per joint (joinery, default 3).", 3) },
                    { "tab_width", JsonHelper.Prop("number", "Nominal tab width in specified/doc units (joinery).") },
                    { "tool_bit_diameter", JsonHelper.Prop("number", "CNC router bit diameter for dogbone corner reliefs in specified/doc units (joinery).") },
                    { "clearance", JsonHelper.Prop("number", "Signed fit clearance (+gap / -press) in specified/doc units (joinery).") },
                    { "enable_dogbones", JsonHelper.Prop("boolean", "Enable CNC corner reliefs on joinery slots/dados (default true).", true) },
                    { "relief_type", JsonHelper.Prop("string", "Corner relief style (joinery).", "diagonal_dogbone", new[] { "diagonal_dogbone", "t_bone", "none" }) },
                    { "enable_keying", JsonHelper.Prop("boolean", "Enable poka-yoke directional keying on confusable joints (default true).", true) },
                    { "key_delta", JsonHelper.Prop("number", "Directional key offset magnitude in specified/doc units (joinery).") },
                    { "use_macro_feature", JsonHelper.Prop("boolean", "Attempt native editable Add-in MacroFeature via GetAddInObject first with automatic standalone native fallback (default true).", true) },
                    { "units", JsonHelper.Prop("string", "Units for joinery numeric parameters: mm, in, or m (defaults to active doc units).") },
                    { "output_folder", JsonHelper.Prop("string", "Output folder for ExportFacesToDXF.") },
                    { "mode", JsonHelper.Prop("integer", "ExportFacesToDXF mode (1=Smart CAM Grouping, 3=Per Face).", 1) },
                    { "overcut", JsonHelper.Prop("string", "Pocket overcut string e.g. '0.25in' or '6mm'.", "0.25in") }
                }, "tool"),
                Handler = args => HandleInvokeRepoTool(swMgr, args)
            });
        }

        private static McpToolResult HandleExtrude(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            ISldWorks app = swMgr.GetApp();
            List<object> ops = JsonHelper.GetList(args, "operations");
            if (ops.Count > 0)
            {
                string defaultUnits = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
                return swMgr.ExecuteInUndoTransaction(doc, "Extrude Batch", true, () =>
                {
                    using (IDisposable uiScope = SwHelper.SuppressUiUpdates(doc))
                    {
                        List<object> created = new List<object>();
                        foreach (object opObj in ops)
                        {
                            Dictionary<string, object> opArgs = opObj as Dictionary<string, object>
                                ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(opObj));
                            if (opArgs == null) continue;
                            if (!opArgs.ContainsKey("units")) opArgs["units"] = defaultUnits;
                            created.Add(ExtrudeInternal(app, doc, opArgs));
                        }
                        return McpToolResult.JsonText(new
                        {
                            batch_count = created.Count,
                            features = created
                        });
                    }
                });
            }

            string mode = JsonHelper.GetBool(args, "is_cut", false) ? "cut" : JsonHelper.GetString(args, "mode", "boss").ToLowerInvariant();
            return swMgr.ExecuteInUndoTransaction(doc, "Extrude " + mode, true, () =>
                McpToolResult.JsonText(ExtrudeInternal(app, doc, args)));
        }

        internal static Dictionary<string, object> ExtrudeInternal(ISldWorks app, IModelDoc2 doc, Dictionary<string, object> args)
        {
            string defaultUnits = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            Dictionary<string, object> inlineSketch = JsonHelper.GetDict(args, "sketch");
            string sketchName = JsonHelper.GetString(args, "sketch_name");
            if (inlineSketch != null)
            {
                if (!inlineSketch.ContainsKey("units")) inlineSketch["units"] = defaultUnits;
                Dictionary<string, object> skRes = SketchAndEquationTools.Create2DSketchInternal(app, doc, inlineSketch);
                if (string.IsNullOrWhiteSpace(sketchName))
                    sketchName = skRes.TryGetValue("sketch_name", out object snObj) ? (snObj as string) : sketchName;
            }
            if (string.IsNullOrWhiteSpace(sketchName))
                throw new InvalidOperationException("extrude requires 'sketch_name' or inline 'sketch'.");

            string mode = args.ContainsKey("is_cut")
                ? (JsonHelper.GetBool(args, "is_cut", false) ? "cut" : "boss")
                : JsonHelper.GetString(args, "mode", "boss").ToLowerInvariant();
            double depth = JsonHelper.GetDouble(args, "depth", 1.0);
            double startOffset = JsonHelper.GetDouble(args, "start_offset", 0.0);
            string startOffsetMode = JsonHelper.GetString(args, "start_offset_mode", "model_positive").ToLowerInvariant();
            bool throughAll = JsonHelper.GetBool(args, "through_all", false);
            bool midPlane = JsonHelper.GetBool(args, "mid_plane", false);
            bool flip = JsonHelper.GetBool(args, "flip_direction", false);
            double draftDeg = JsonHelper.GetDouble(args, "draft_deg", 0.0);
            bool merge = JsonHelper.GetBool(args, "merge", true);
            string featName = JsonHelper.GetString(args, "feature_name");
            bool hideSketch = JsonHelper.GetBool(args, "hide_sketch", false);

            double depthM = ShopCatalogService.ToMeters(depth, defaultUnits);
            double startOffM = ShopCatalogService.ToMeters(startOffset, defaultUnits);
            double draftRad = ShopCatalogService.ToRadians(draftDeg);
            int endCond = throughAll
                ? (int)swEndConditions_e.swEndCondThroughAll
                : (midPlane ? (int)swEndConditions_e.swEndCondMidPlane : (int)swEndConditions_e.swEndCondBlind);

            int startCond = (int)swStartConditions_e.swStartSketchPlane;
            double startOffAbsM = 0.0;
            bool flipStartOff = false;

            if (Math.Abs(startOffM) > 1e-9)
            {
                startCond = (int)swStartConditions_e.swStartOffset;
                double signedAlongSketchNorm = ResolveSketchStartOffsetAlongNormal(doc, sketchName, startOffM, startOffsetMode);
                startOffAbsM = Math.Abs(signedAlongSketchNorm);
                flipStartOff = signedAlongSketchNorm < 0.0;
            }

            doc.ClearSelection2(true);
            if (!SwConnectionManager.TrySelectByToken(doc, sketchName, false, 0))
                throw new InvalidOperationException("Could not select sketch: " + sketchName);

            IFeature feat;
            if (mode == "cut")
            {
                feat = doc.FeatureManager.FeatureCut4(
                    !midPlane, false, flip, endCond, (int)swEndConditions_e.swEndCondBlind,
                    depthM, 0.0, Math.Abs(draftRad) > 1e-6, false, false, false, draftRad, 0.0,
                    false, false, false, false, false, true, true, true, true, false,
                    startCond, startOffAbsM, flipStartOff, false);

                if (feat == null && !midPlane)
                {
                    doc.ClearSelection2(true);
                    SwConnectionManager.TrySelectByToken(doc, sketchName, false, 0);
                    feat = doc.FeatureManager.FeatureCut4(
                        true, false, !flip, endCond, (int)swEndConditions_e.swEndCondBlind,
                        depthM, 0.0, Math.Abs(draftRad) > 1e-6, false, false, false, draftRad, 0.0,
                        false, false, false, false, false, true, true, true, true, false,
                        startCond, startOffAbsM, flipStartOff, false);
                }
            }
            else
            {
                feat = doc.FeatureManager.FeatureExtrusion3(
                    !midPlane, false, flip, endCond, (int)swEndConditions_e.swEndCondBlind,
                    depthM, 0.0, Math.Abs(draftRad) > 1e-6, false, false, false, draftRad, 0.0,
                    false, false, false, false, merge, true, true,
                    startCond, startOffAbsM, flipStartOff);
            }

            if (feat == null)
                throw new InvalidOperationException("Extrude " + mode + " failed for sketch: " + sketchName);

            if (!string.IsNullOrWhiteSpace(featName))
                feat.Name = featName;

            if (hideSketch)
                SwHelper.HideSketches(doc, new[] { sketchName });

            return new Dictionary<string, object>
            {
                { "feature_name", feat.Name },
                { "sketch_name", sketchName },
                { "mode", mode },
                { "start_offset", Math.Abs(startOffset) > 1e-9 ? (object)startOffset : null },
                { "units", defaultUnits }
            };
        }

        private static double ResolveSketchStartOffsetAlongNormal(IModelDoc2 doc, string sketchName, double offsetM, string startOffsetMode)
        {
            if (!string.Equals(startOffsetMode, "model_positive", StringComparison.OrdinalIgnoreCase))
                return offsetM;

            IFeature skFeat = FindFeatureByName(doc, sketchName);
            ISketch sk = skFeat != null ? skFeat.GetSpecificFeature2() as ISketch : null;
            if (sk == null || sk.ModelToSketchTransform == null)
                return offsetM;

            double[] m2s = ((IMathTransform)sk.ModelToSketchTransform).ArrayData as double[];
            if (m2s == null || m2s.Length < 12) return offsetM;
            double[] s2m = AssemblyGeometryHelper.InvertTransform16(m2s);
            double[] skNorm = AssemblyGeometryHelper.TransformVectorFast(s2m, 0.0, 0.0, 1.0);

            if (Math.Abs(skNorm[0]) > 0.999) return offsetM * (skNorm[0] >= 0 ? 1.0 : -1.0);
            if (Math.Abs(skNorm[1]) > 0.999) return offsetM * (skNorm[1] >= 0 ? 1.0 : -1.0);
            if (Math.Abs(skNorm[2]) > 0.999) return offsetM * (skNorm[2] >= 0 ? 1.0 : -1.0);
            return offsetM;
        }

        internal static IFeature FindFeatureByName(IModelDoc2 doc, string name)
        {
            if (doc == null || string.IsNullOrWhiteSpace(name)) return null;
            IFeature f = doc.FirstFeature() as IFeature;
            while (f != null)
            {
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                    return f;
                f = f.GetNextFeature() as IFeature;
            }
            return null;
        }

        internal class SweepPathValidation
        {
            public bool Valid { get; set; }
            public int SegmentCount { get; set; }
            public double TotalLengthM { get; set; }
            public double MinBendRadiusM { get; set; }
            public double[] MinBendLocationM { get; set; }
            public string Issue { get; set; }
        }

        private class SweepSamplePt
        {
            public int SegIndex;
            public double CumSegLenM;
            public double[] PointM;
        }

        private class SegEndpointInfo
        {
            public int SegIndex;
            public double[] PointM;
            public double[] InwardTangent;
        }

        internal static SweepPathValidation ValidateSweepPathCurvature(IModelDoc2 doc, string pathSketchName, double outerDiameterM, string units)
        {
            IFeature skFeat = FindFeatureByName(doc, pathSketchName);
            ISketch sk = skFeat != null ? skFeat.GetSpecificFeature2() as ISketch : null;
            if (sk == null)
                return new SweepPathValidation { Valid = true, MinBendRadiusM = double.PositiveInfinity };

            object[] rawSegs = sk.GetSketchSegments() as object[];
            if (rawSegs == null || rawSegs.Length == 0)
                return new SweepPathValidation { Valid = false, Issue = "Sweep path sketch '" + pathSketchName + "' contains no segments." };

            bool is3D = sk.Is3D();
            double[] s2m = AssemblyGeometryHelper.IdentityTransform16();
            if (!is3D && sk.ModelToSketchTransform != null)
            {
                double[] m2s = ((IMathTransform)sk.ModelToSketchTransform).ArrayData as double[];
                s2m = AssemblyGeometryHelper.InvertTransform16(m2s);
            }

            double minR = double.PositiveInfinity;
            double[] minLoc = null;
            double totalLen = 0.0;
            int activeSegs = 0;

            List<SweepSamplePt> centerSamples = new List<SweepSamplePt>();
            List<SegEndpointInfo> endpoints = new List<SegEndpointInfo>();
            int segIdx = 0;

            foreach (object sObj in rawSegs)
            {
                if (!(sObj is ISketchSegment seg) || seg.ConstructionGeometry) continue;
                int thisSegIdx = segIdx++;
                activeSegs++;
                int segType = seg.GetType();
                ICurve curve = seg.GetCurve() as ICurve;
                if (curve == null) continue;

                double t0, t1;
                bool isClosed, isPeriodic;
                if (!curve.GetEndParams(out t0, out t1, out isClosed, out isPeriodic)) continue;

                double[] segStartPt = null, segEndPt = null, segStartTan = null, segEndTan = null;
                double[] prevPt = null;
                double segLen = 0.0;
                int samples = (segType == (int)swSketchSegments_e.swSketchLINE) ? 8 : 64;

                for (int i = 0; i <= samples; i++)
                {
                    double frac = (double)i / samples;
                    double t = t0 + frac * (t1 - t0);
                    double[] ev = curve.Evaluate2(t, 2) as double[];
                    if (ev == null || ev.Length < 3) continue;

                    double[] pModel = is3D
                        ? new[] { ev[0], ev[1], ev[2] }
                        : AssemblyGeometryHelper.TransformPointFast(s2m, ev[0], ev[1], ev[2]);

                    if (prevPt != null)
                    {
                        double dx = pModel[0] - prevPt[0], dy = pModel[1] - prevPt[1], dz = pModel[2] - prevPt[2];
                        double stepLen = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        segLen += stepLen;
                        totalLen += stepLen;
                    }
                    prevPt = pModel;
                    centerSamples.Add(new SweepSamplePt { SegIndex = thisSegIdx, CumSegLenM = segLen, PointM = pModel });

                    if (ev.Length >= 6)
                    {
                        double[] d1 = is3D
                            ? new[] { ev[3], ev[4], ev[5] }
                            : AssemblyGeometryHelper.TransformVectorFast(s2m, ev[3], ev[4], ev[5]);
                        if (i == 0) { segStartPt = pModel; segStartTan = AssemblyGeometryHelper.Normalize(d1); }
                        if (i == samples) { segEndPt = pModel; segEndTan = AssemblyGeometryHelper.Normalize(d1); }
                    }

                    if (segType == (int)swSketchSegments_e.swSketchARC && seg is ISketchArc arc)
                    {
                        double arcR = arc.GetRadius();
                        if (arcR > 1e-7 && arcR < minR)
                        {
                            minR = arcR;
                            minLoc = pModel;
                        }
                    }
                    else if (segType != (int)swSketchSegments_e.swSketchLINE && ev.Length >= 9)
                    {
                        double[] r1 = new[] { ev[3], ev[4], ev[5] };
                        double[] r2 = new[] { ev[6], ev[7], ev[8] };
                        double spd = Math.Sqrt(r1[0] * r1[0] + r1[1] * r1[1] + r1[2] * r1[2]);
                        double[] cr = AssemblyGeometryHelper.Cross(r1, r2);
                        double crNorm = Math.Sqrt(cr[0] * cr[0] + cr[1] * cr[1] + cr[2] * cr[2]);
                        if (spd > 1e-7 && crNorm > 1e-9)
                        {
                            double rad = (spd * spd * spd) / crNorm;
                            if (rad < minR)
                            {
                                minR = rad;
                                minLoc = pModel;
                            }
                        }
                    }
                }

                // Store inward-pointing tangents at both segment endpoints (independent of curve parameterization direction)
                if (segStartPt != null && segStartTan != null)
                {
                    endpoints.Add(new SegEndpointInfo
                    {
                        SegIndex = thisSegIdx,
                        PointM = segStartPt,
                        InwardTangent = segStartTan
                    });
                }
                if (segEndPt != null && segEndTan != null)
                {
                    endpoints.Add(new SegEndpointInfo
                    {
                        SegIndex = thisSegIdx,
                        PointM = segEndPt,
                        InwardTangent = new[] { -segEndTan[0], -segEndTan[1], -segEndTan[2] }
                    });
                }
            }

            // Evaluate G1 tangency across touching segment endpoints (smooth G1 join has inwardA . inwardB <= -0.85)
            Dictionary<string, List<double[]>> sharedJunctions = new Dictionary<string, List<double[]>>();
            for (int i = 0; i < endpoints.Count; i++)
            {
                for (int j = i + 1; j < endpoints.Count; j++)
                {
                    SegEndpointInfo a = endpoints[i];
                    SegEndpointInfo b = endpoints[j];
                    if (a.SegIndex == b.SegIndex) continue;

                    double jDist = Math.Sqrt(
                        Math.Pow(a.PointM[0] - b.PointM[0], 2) +
                        Math.Pow(a.PointM[1] - b.PointM[1], 2) +
                        Math.Pow(a.PointM[2] - b.PointM[2], 2));
                    if (jDist >= 0.0005) continue;

                    int lo = Math.Min(a.SegIndex, b.SegIndex);
                    int hi = Math.Max(a.SegIndex, b.SegIndex);
                    string pKey = lo + "|" + hi;
                    if (!sharedJunctions.TryGetValue(pKey, out List<double[]> jPts))
                    {
                        jPts = new List<double[]>();
                        sharedJunctions[pKey] = jPts;
                    }
                    jPts.Add(new[]
                    {
                        0.5 * (a.PointM[0] + b.PointM[0]),
                        0.5 * (a.PointM[1] + b.PointM[1]),
                        0.5 * (a.PointM[2] + b.PointM[2])
                    });

                    double inwardDot = AssemblyGeometryHelper.Dot(a.InwardTangent, b.InwardTangent);
                    if (inwardDot > -0.85)
                    {
                        minR = 0.0;
                        minLoc = a.PointM;
                    }
                }
            }

            if (outerDiameterM > 0.0)
            {
                double minAllowedM = 0.5 * outerDiameterM;
                if (!double.IsInfinity(minR) && minR <= minAllowedM + 1e-6)
                {
                    string locStr = minLoc != null
                        ? string.Format(CultureInfo.InvariantCulture, "[{0:F2}, {1:F2}, {2:F2}] {3}",
                            ShopCatalogService.FromMeters(minLoc[0], units),
                            ShopCatalogService.FromMeters(minLoc[1], units),
                            ShopCatalogService.FromMeters(minLoc[2], units), units)
                        : "unknown";
                    return new SweepPathValidation
                    {
                        Valid = false,
                        SegmentCount = activeSegs,
                        TotalLengthM = totalLen,
                        MinBendRadiusM = minR,
                        MinBendLocationM = minLoc,
                        Issue = string.Format(CultureInfo.InvariantCulture,
                            "Sweep path self-intersections: min bend radius {0:F2} {1} <= D/2 ({2:F2} {1}) at {3}. Increase corner/spline bend radius above D/2.",
                            ShopCatalogService.FromMeters(minR, units), units,
                            ShopCatalogService.FromMeters(minAllowedM, units), locStr)
                    };
                }

                // Check non-adjacent centerline foldback
                double minArcSep = Math.Max(outerDiameterM * 1.6, 0.002);
                double jClearM = 1.2 * outerDiameterM;
                for (int i = 0; i < centerSamples.Count; i++)
                {
                    SweepSamplePt sa = centerSamples[i];
                    for (int j = i + 4; j < centerSamples.Count; j++)
                    {
                        SweepSamplePt sb = centerSamples[j];
                        if (sa.SegIndex == sb.SegIndex)
                        {
                            if (Math.Abs(sb.CumSegLenM - sa.CumSegLenM) < minArcSep) continue;
                        }
                        else
                        {
                            int lo = Math.Min(sa.SegIndex, sb.SegIndex);
                            int hi = Math.Max(sa.SegIndex, sb.SegIndex);
                            if (sharedJunctions.TryGetValue(lo + "|" + hi, out List<double[]> jPts))
                            {
                                bool nearShared = false;
                                foreach (double[] jp in jPts)
                                {
                                    double da = Math.Sqrt(Math.Pow(sa.PointM[0] - jp[0], 2) + Math.Pow(sa.PointM[1] - jp[1], 2) + Math.Pow(sa.PointM[2] - jp[2], 2));
                                    double db = Math.Sqrt(Math.Pow(sb.PointM[0] - jp[0], 2) + Math.Pow(sb.PointM[1] - jp[1], 2) + Math.Pow(sb.PointM[2] - jp[2], 2));
                                    if (da < jClearM || db < jClearM) { nearShared = true; break; }
                                }
                                if (nearShared) continue;
                            }
                        }

                        double[] a = sa.PointM, b = sb.PointM;
                        double d = Math.Sqrt(Math.Pow(a[0] - b[0], 2) + Math.Pow(a[1] - b[1], 2) + Math.Pow(a[2] - b[2], 2));
                        if (d < 0.85 * outerDiameterM)
                        {
                            string locStr = string.Format(CultureInfo.InvariantCulture, "[{0:F2}, {1:F2}, {2:F2}] {3}",
                                ShopCatalogService.FromMeters(a[0], units),
                                ShopCatalogService.FromMeters(a[1], units),
                                ShopCatalogService.FromMeters(a[2], units), units);
                            return new SweepPathValidation
                            {
                                Valid = false,
                                SegmentCount = activeSegs,
                                TotalLengthM = totalLen,
                                MinBendRadiusM = minR,
                                MinBendLocationM = a,
                                Issue = string.Format(CultureInfo.InvariantCulture,
                                    "Sweep path folds back within {0:F2} {1} (< 0.85*D = {2:F2} {1}) near {3}.",
                                    ShopCatalogService.FromMeters(d, units), units,
                                    ShopCatalogService.FromMeters(0.85 * outerDiameterM, units), locStr)
                            };
                        }
                    }
                }
            }

            return new SweepPathValidation
            {
                Valid = true,
                SegmentCount = activeSegs,
                TotalLengthM = totalLen,
                MinBendRadiusM = minR,
                MinBendLocationM = minLoc
            };
        }

        private static McpToolResult HandleRevolve(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            ISldWorks app = swMgr.GetApp();
            List<object> ops = JsonHelper.GetList(args, "operations");
            if (ops.Count > 0)
            {
                string defaultUnits = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
                return swMgr.ExecuteInUndoTransaction(doc, "Revolve Batch", true, () =>
                {
                    using (IDisposable uiScope = SwHelper.SuppressUiUpdates(doc))
                    {
                        List<object> created = new List<object>();
                        foreach (object opObj in ops)
                        {
                            Dictionary<string, object> opArgs = opObj as Dictionary<string, object>
                                ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(opObj));
                            if (opArgs == null) continue;
                            if (!opArgs.ContainsKey("units")) opArgs["units"] = defaultUnits;
                            created.Add(RevolveInternal(app, doc, opArgs));
                        }
                        return McpToolResult.JsonText(new
                        {
                            batch_count = created.Count,
                            features = created
                        });
                    }
                });
            }

            string mode = JsonHelper.GetBool(args, "is_cut", false) ? "cut" : JsonHelper.GetString(args, "mode", "boss").ToLowerInvariant();
            return swMgr.ExecuteInUndoTransaction(doc, "Revolve " + mode, true, () =>
                McpToolResult.JsonText(RevolveInternal(app, doc, args)));
        }

        internal static Dictionary<string, object> RevolveInternal(ISldWorks app, IModelDoc2 doc, Dictionary<string, object> args)
        {
            string defaultUnits = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            Dictionary<string, object> inlineSketch = JsonHelper.GetDict(args, "sketch");
            string sketchName = JsonHelper.GetString(args, "sketch_name");
            if (inlineSketch != null)
            {
                if (!inlineSketch.ContainsKey("units")) inlineSketch["units"] = defaultUnits;
                Dictionary<string, object> skRes = SketchAndEquationTools.Create2DSketchInternal(app, doc, inlineSketch);
                if (string.IsNullOrWhiteSpace(sketchName))
                    sketchName = skRes.TryGetValue("sketch_name", out object snObj) ? (snObj as string) : sketchName;
            }
            if (string.IsNullOrWhiteSpace(sketchName))
                throw new InvalidOperationException("revolve requires 'sketch_name' or inline 'sketch'.");

            string mode = args.ContainsKey("is_cut")
                ? (JsonHelper.GetBool(args, "is_cut", false) ? "cut" : "boss")
                : JsonHelper.GetString(args, "mode", "boss").ToLowerInvariant();
            double angleRad = ShopCatalogService.ToRadians(JsonHelper.GetDouble(args, "angle_deg", 360.0));
            bool merge = JsonHelper.GetBool(args, "merge", true);
            string featName = JsonHelper.GetString(args, "feature_name");
            bool hideSketch = JsonHelper.GetBool(args, "hide_sketch", false);

            doc.ClearSelection2(true);
            if (!SwConnectionManager.TrySelectByToken(doc, sketchName, false, 0))
                throw new InvalidOperationException("Could not select sketch: " + sketchName);

            IFeature feat = doc.FeatureManager.FeatureRevolve2(
                true, true, false, mode == "cut", false, false,
                (int)swEndConditions_e.swEndCondBlind, 0, angleRad, 0.0,
                false, false, 0.0, 0.0, (int)swThinWallType_e.swThinWallOneDirection, 0.0, 0.0,
                merge, true, true);

            if (feat == null)
                throw new InvalidOperationException("FeatureRevolve2 returned null.");

            if (!string.IsNullOrWhiteSpace(featName))
                feat.Name = featName;

            if (hideSketch)
                SwHelper.HideSketches(doc, new[] { sketchName });

            return new Dictionary<string, object>
            {
                { "feature_name", feat.Name },
                { "sketch_name", sketchName },
                { "mode", mode }
            };
        }

        private static McpToolResult HandleAirflowLoftOrSweep(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            string mode = JsonHelper.GetString(args, "mode", "loft").ToLowerInvariant();
            List<string> profiles = JsonHelper.GetStringList(args, "profiles");
            string pathSketch = JsonHelper.GetString(args, "path_sketch");
            double circDia = JsonHelper.GetDouble(args, "circular_diameter", 0.0);
            List<string> guides = JsonHelper.GetStringList(args, "guide_curves");
            bool tangentNormal = JsonHelper.GetBool(args, "start_tangent_normal", true);
            double wallMm = JsonHelper.GetDouble(args, "wall_thickness_mm", 0.0);
            bool merge = JsonHelper.GetBool(args, "merge", true);
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            string featName = JsonHelper.GetString(args, "feature_name");
            double circDiaM = circDia > 0.0 ? ShopCatalogService.ToMeters(circDia, units) : 0.0;

            return swMgr.ExecuteInUndoTransaction(doc, "Airflow " + mode, true, () =>
            {
                doc.ClearSelection2(true);
                IFeature feat;
                SweepPathValidation pathCheck = null;

                if (mode == "sweep")
                {
                    bool useCircularProfile = profiles.Count == 0 && circDiaM > 1e-6;
                    if (string.IsNullOrWhiteSpace(pathSketch))
                        throw new InvalidOperationException("Sweep requires 'path_sketch'.");
                    if (!useCircularProfile && profiles.Count < 1)
                        throw new InvalidOperationException("Sweep requires at least 1 profile sketch in 'profiles' OR 'circular_diameter' > 0.");

                    double checkDiaM = useCircularProfile ? circDiaM : 0.0;
                    pathCheck = ValidateSweepPathCurvature(doc, pathSketch, checkDiaM, units);
                    if (!pathCheck.Valid)
                        throw new InvalidOperationException(pathCheck.Issue);

                    if (!useCircularProfile)
                    {
                        if (!SwConnectionManager.TrySelectByToken(doc, profiles[0], false, 1))
                            throw new InvalidOperationException("Could not select profile: " + profiles[0]);
                        if (!SwConnectionManager.TrySelectByToken(doc, pathSketch, true, 4))
                            throw new InvalidOperationException("Could not select path sketch: " + pathSketch);
                    }
                    else
                    {
                        if (!SwConnectionManager.TrySelectByToken(doc, pathSketch, false, 4))
                            throw new InvalidOperationException("Could not select path sketch: " + pathSketch);
                    }

                    for (int i = 0; i < guides.Count; i++)
                        SwConnectionManager.TrySelectByToken(doc, guides[i], true, 2);

                    bool thin = wallMm > 0.01;
                    feat = doc.FeatureManager.InsertProtrusionSwept4(
                        false, false, (int)swTwistControlType_e.swTwistControlFollowPath,
                        false, false, (int)swTangencyType_e.swTangencyNone, (int)swTangencyType_e.swTangencyNone,
                        thin, thin ? wallMm * 0.001 : 0.0, 0.0,
                        (int)swThinWallType_e.swThinWallOneDirection, 0,
                        merge, true, true, 0.0, true, useCircularProfile, circDiaM, 0);
                }
                else
                {
                    if (profiles.Count < 2)
                        throw new InvalidOperationException("Loft requires at least 2 profile sketches.");
                    for (int i = 0; i < profiles.Count; i++)
                    {
                        if (!SwConnectionManager.TrySelectByToken(doc, profiles[i], i > 0, 1))
                            throw new InvalidOperationException("Could not select profile sketch: " + profiles[i]);
                    }
                    for (int i = 0; i < guides.Count; i++)
                        SwConnectionManager.TrySelectByToken(doc, guides[i], true, 2);

                    short tanType = tangentNormal ? (short)3 : (short)0; // 3 = Normal to profile
                    bool thin = wallMm > 0.01;
                    feat = doc.FeatureManager.InsertProtrusionBlend2(
                        false, false, false, 1.0, tanType, tanType, 1.0, 1.0,
                        true, true, thin, thin ? wallMm * 0.001 : 0.0, 0.0,
                        (short)swThinWallType_e.swThinWallOneDirection, merge, true, true, 0);
                }

                if (feat == null)
                    throw new InvalidOperationException("Airflow " + mode + " failed. Check profile sketch closure, path curvature (R > D/2), and guide curve endpoints.");

                if (!string.IsNullOrWhiteSpace(featName))
                    feat.Name = featName;

                double? minBendOut = (pathCheck != null && !double.IsInfinity(pathCheck.MinBendRadiusM))
                    ? (double?)Math.Round(ShopCatalogService.FromMeters(pathCheck.MinBendRadiusM, units), 3)
                    : null;
                double[] minBendLocOut = (pathCheck != null && pathCheck.MinBendLocationM != null)
                    ? new[]
                    {
                        Math.Round(ShopCatalogService.FromMeters(pathCheck.MinBendLocationM[0], units), 2),
                        Math.Round(ShopCatalogService.FromMeters(pathCheck.MinBendLocationM[1], units), 2),
                        Math.Round(ShopCatalogService.FromMeters(pathCheck.MinBendLocationM[2], units), 2)
                    }
                    : null;

                return McpToolResult.JsonText(new
                {
                    feature_name = feat.Name,
                    mode = mode,
                    circular_profile = profiles.Count == 0 && circDiaM > 0,
                    circular_diameter = circDia > 0 ? (object)circDia : null,
                    units = units,
                    min_path_bend_radius = minBendOut,
                    min_bend_location = minBendLocOut,
                    thin_wall_mm = wallMm,
                    nozzle_0_4mm_perimeters = wallMm > 0 ? Math.Round(wallMm / 0.4, 1) : 0,
                    dfm_note = "Run sw_capture_viewport_quad (or section view) and sw_audit_dfm(process='3d_printing') to verify support-free internal roof angle <= 45 deg."
                });
            });
        }

        private static McpToolResult HandleShell(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            return swMgr.ExecuteInUndoTransaction(doc, "Shell Duct/Body", true, () =>
                McpToolResult.JsonText(ShellInternal(doc, args)));
        }

        internal static Dictionary<string, object> ShellInternal(IModelDoc2 doc, Dictionary<string, object> args)
        {
            double thickness = JsonHelper.GetDouble(args, "thickness", 2.0);
            List<string> openFaces = JsonHelper.GetStringList(args, "open_face_tokens");
            bool outward = JsonHelper.GetBool(args, "outward", false);
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            double thickM = ShopCatalogService.ToMeters(thickness, units);

            doc.ClearSelection2(true);
            for (int i = 0; i < openFaces.Count; i++)
            {
                if (!SwConnectionManager.TrySelectByToken(doc, openFaces[i], i > 0, 1))
                    throw new InvalidOperationException("Could not select open face: " + openFaces[i]);
            }
            doc.InsertFeatureShell(thickM, outward);
            doc.EditRebuild3();
            return new Dictionary<string, object>
            {
                { "shelled", true },
                { "wall_thickness", thickness },
                { "units", units },
                { "open_faces_count", openFaces.Count }
            };
        }

        private static McpToolResult HandleFilletOrChamfer(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            List<object> ops = JsonHelper.GetList(args, "operations");
            if (ops.Count > 0)
            {
                string defaultUnits = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
                return swMgr.ExecuteInUndoTransaction(doc, "Fillet/Chamfer Batch", true, () =>
                {
                    using (IDisposable uiScope = SwHelper.SuppressUiUpdates(doc))
                    {
                        List<object> created = new List<object>();
                        foreach (object opObj in ops)
                        {
                            Dictionary<string, object> opArgs = opObj as Dictionary<string, object>
                                ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(opObj));
                            if (opArgs == null) continue;
                            if (!opArgs.ContainsKey("units")) opArgs["units"] = defaultUnits;
                            created.Add(FilletOrChamferInternal(doc, opArgs));
                        }
                        return McpToolResult.JsonText(new
                        {
                            batch_count = created.Count,
                            features = created
                        });
                    }
                });
            }

            string kind = JsonHelper.GetString(args, "kind", "fillet").ToLowerInvariant();
            return swMgr.ExecuteInUndoTransaction(doc, kind, true, () =>
                McpToolResult.JsonText(FilletOrChamferInternal(doc, args)));
        }

        internal static Dictionary<string, object> FilletOrChamferInternal(IModelDoc2 doc, Dictionary<string, object> args)
        {
            string kind = JsonHelper.GetString(args, "kind", "fillet").ToLowerInvariant();
            List<string> tokens = JsonHelper.GetStringList(args, "edge_or_face_tokens");
            double size = JsonHelper.GetDouble(args, "size", 0.125);
            double angleRad = ShopCatalogService.ToRadians(JsonHelper.GetDouble(args, "angle_deg", 45.0));
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            string featName = JsonHelper.GetString(args, "feature_name");
            double sizeM = ShopCatalogService.ToMeters(size, units);

            doc.ClearSelection2(true);
            for (int i = 0; i < tokens.Count; i++)
            {
                if (!SwConnectionManager.TrySelectByToken(doc, tokens[i], i > 0, 1))
                    throw new InvalidOperationException("Could not select edge/face: " + tokens[i]);
            }

            IFeature feat;
            if (kind == "chamfer")
            {
                feat = doc.FeatureManager.InsertFeatureChamfer(
                    (int)swFeatureChamferOption_e.swFeatureChamferTangentPropagation,
                    (int)swChamferType_e.swChamferAngleDistance,
                    sizeM, angleRad, 0.0, 0.0, 0.0, 0.0);
            }
            else
            {
                feat = doc.FeatureManager.FeatureFillet3(
                    (int)swFeatureFilletOptions_e.swFeatureFilletUniformRadius | (int)swFeatureFilletOptions_e.swFeatureFilletPropagate,
                    sizeM, 0.0, 0.0, 0, 0, 0, null, null, null, null, null, null, null) as IFeature;
            }

            if (feat == null)
                throw new InvalidOperationException(kind + " operation returned null.");
            if (!string.IsNullOrWhiteSpace(featName))
                feat.Name = featName;

            return new Dictionary<string, object>
            {
                { "feature_name", feat.Name },
                { "kind", kind },
                { "size", size },
                { "units", units }
            };
        }

        private static McpToolResult HandlePatternOrMirror(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            List<object> ops = JsonHelper.GetList(args, "operations");
            if (ops.Count > 0)
            {
                string defaultUnits = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
                return swMgr.ExecuteInUndoTransaction(doc, "Pattern/Mirror Batch", true, () =>
                {
                    using (IDisposable uiScope = SwHelper.SuppressUiUpdates(doc))
                    {
                        List<object> created = new List<object>();
                        foreach (object opObj in ops)
                        {
                            Dictionary<string, object> opArgs = opObj as Dictionary<string, object>
                                ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(opObj));
                            if (opArgs == null) continue;
                            if (!opArgs.ContainsKey("units")) opArgs["units"] = defaultUnits;
                            created.Add(PatternOrMirrorInternal(doc, opArgs));
                        }
                        return McpToolResult.JsonText(new
                        {
                            batch_count = created.Count,
                            features = created
                        });
                    }
                });
            }

            return swMgr.ExecuteInUndoTransaction(doc, "Pattern/Mirror", true, () =>
                McpToolResult.JsonText(PatternOrMirrorInternal(doc, args)));
        }

        internal static Dictionary<string, object> PatternOrMirrorInternal(IModelDoc2 doc, Dictionary<string, object> args)
        {
            string kind = JsonHelper.GetString(args, "kind", "linear").ToLowerInvariant();
            List<string> feats = JsonHelper.GetStringList(args, "feature_names");
            string dirRef = JsonHelper.GetString(args, "direction_ref");
            int count = Math.Max(2, JsonHelper.GetInt(args, "count", 2));
            double spacing = JsonHelper.GetDouble(args, "spacing", 1.0);
            bool flip = JsonHelper.GetBool(args, "flip", false);
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));

            doc.ClearSelection2(true);
            for (int i = 0; i < feats.Count; i++)
            {
                if (!SwConnectionManager.TrySelectByToken(doc, feats[i], i > 0, 4))
                    throw new InvalidOperationException("Failed to select feature: " + feats[i]);
            }

            IFeature feat;
            if (kind == "mirror")
            {
                if (!SwConnectionManager.TrySelectByToken(doc, dirRef, true, 2))
                    throw new InvalidOperationException("Failed to select mirror plane: " + dirRef);
                feat = doc.FeatureManager.InsertMirrorFeature2(false, false, false, false, (int)swFeatureError_e.swFeatureErrorNone);
            }
            else if (kind == "circular")
            {
                if (!SwConnectionManager.TrySelectByToken(doc, dirRef, true, 1))
                    throw new InvalidOperationException("Failed to select axis/edge: " + dirRef);
                double angRad = ShopCatalogService.ToRadians(spacing);
                feat = doc.FeatureManager.FeatureCircularPattern4(count, angRad, flip, "NULL", true, true, false);
            }
            else
            {
                if (!SwConnectionManager.TrySelectByToken(doc, dirRef, true, 1))
                    throw new InvalidOperationException("Failed to select direction edge/axis: " + dirRef);
                double spaceM = ShopCatalogService.ToMeters(spacing, units);
                feat = doc.FeatureManager.FeatureLinearPattern4(
                    count, spaceM, 1, 0.0, flip, false, "NULL", "NULL",
                    false, false, false, false, false, false, false, false, false, false, 0.0, 0.0);
            }

            if (feat == null)
                throw new InvalidOperationException("Pattern/mirror feature failed.");

            return new Dictionary<string, object>
            {
                { "feature_name", feat.Name },
                { "kind", kind },
                { "count", count }
            };
        }

        private static McpToolResult HandleHolePreset(SwConnectionManager swMgr, ShopCatalogService catalog, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            ISldWorks app = swMgr.GetApp();
            List<object> ops = JsonHelper.GetList(args, "operations");
            if (ops.Count > 0)
            {
                string defaultUnits = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
                return swMgr.ExecuteInUndoTransaction(doc, "Hole Preset Batch", true, () =>
                {
                    using (IDisposable uiScope = SwHelper.SuppressUiUpdates(doc))
                    {
                        List<object> created = new List<object>();
                        foreach (object opObj in ops)
                        {
                            Dictionary<string, object> opArgs = opObj as Dictionary<string, object>
                                ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(opObj));
                            if (opArgs == null) continue;
                            if (!opArgs.ContainsKey("units")) opArgs["units"] = defaultUnits;
                            created.Add(HolePresetInternal(app, catalog, doc, opArgs));
                        }
                        return McpToolResult.JsonText(new
                        {
                            batch_count = created.Count,
                            features = created
                        });
                    }
                });
            }

            string presetType = JsonHelper.GetString(args, "preset_type", "mcmaster_insert").ToLowerInvariant();
            return swMgr.ExecuteInUndoTransaction(doc, "Hole Preset " + presetType, true, () =>
                McpToolResult.JsonText(HolePresetInternal(app, catalog, doc, args)));
        }

        internal static Dictionary<string, object> HolePresetInternal(
            ISldWorks app,
            ShopCatalogService catalog,
            IModelDoc2 doc,
            Dictionary<string, object> args)
        {
            string presetType = JsonHelper.GetString(args, "preset_type", "mcmaster_insert").ToLowerInvariant();
            string presetId = JsonHelper.GetString(args, "preset_id", string.Empty);
            string faceToken = JsonHelper.GetString(args, "face_token");
            List<object> locations = JsonHelper.GetList(args, "locations");
            double? overrideDepth = JsonHelper.GetNullableDouble(args, "depth");
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));

            // Determine hole parameters in meters
            double diaM = 0.005;
            double depthM = 0.010;
            bool isRect = false;
            double rectW = 0.005, rectL = 0.020;
            string recommendedPn = null;
            string semanticName = presetType;

            if (presetType == "mcmaster_insert")
            {
                Dictionary<string, object> ins = catalog.FindMcMasterInsert(string.IsNullOrWhiteSpace(presetId) ? "94180A333" : presetId);
                if (ins == null)
                    throw new InvalidOperationException("Unknown McMaster insert ID: " + presetId + ". Owned: 94459A110, 93365A112, 94180A333, 94180A353.");
                diaM = JsonHelper.GetDouble(ins, "hole_top_dia_mm", 5.23) * 0.001;
                depthM = JsonHelper.GetDouble(ins, "recommended_hole_depth_mm", 7.6) * 0.001;
                recommendedPn = JsonHelper.GetString(ins, "mcmaster_pn");
                semanticName = "HeatSet_" + recommendedPn;
            }
            else if (presetType == "forstner_metric")
            {
                double mm = 20.0;
                double.TryParse(presetId.Replace("mm", "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out mm);
                mm = Math.Max(10.0, Math.Min(30.0, mm));
                diaM = mm * 0.001;
                depthM = overrideDepth.HasValue ? ShopCatalogService.ToMeters(overrideDepth.Value, units) : 0.0127;
                semanticName = string.Format(CultureInfo.InvariantCulture, "Forstner_{0:F0}mm", mm);
            }
            else if (presetType == "qcw_dowel_grid")
            {
                diaM = 0.375 * 0.0254;
                depthM = overrideDepth.HasValue ? ShopCatalogService.ToMeters(overrideDepth.Value, units) : 0.500 * 0.0254;
                semanticName = "QCW_3_8_Dowel_Pin";
            }
            else if (presetType == "npt_1_8_vac_tap")
            {
                diaM = 0.332 * 0.0254; // Letter Q / 0.332" tap drill for 1/8" NPT
                depthM = overrideDepth.HasValue ? ShopCatalogService.ToMeters(overrideDepth.Value, units) : 0.450 * 0.0254;
                semanticName = "VacPort_1_8_NPT_TapDrill";
            }
            else if (presetType == "blum_hinge_cup")
            {
                diaM = 0.035;
                depthM = 0.013;
                semanticName = "Blum_35mm_CupBore";
            }
            else if (presetType == "domino_mortise")
            {
                isRect = true;
                string pid = presetId.ToLowerInvariant();
                if (pid.Contains("4")) { rectW = 0.004; rectL = 0.017; depthM = 0.012; }
                else if (pid.Contains("8")) { rectW = 0.008; rectL = 0.023; depthM = 0.022; }
                else { rectW = 0.005; rectL = 0.019; depthM = 0.017; }
                if (overrideDepth.HasValue) depthM = ShopCatalogService.ToMeters(overrideDepth.Value, units);
                semanticName = "Domino_Mortise_" + (string.IsNullOrWhiteSpace(presetId) ? "5mm" : presetId);
            }
            else // fastener_thru
            {
                string pid = presetId.ToUpperInvariant();
                if (pid.Contains("M2")) diaM = 0.0024;
                else if (pid.Contains("M3")) diaM = 0.0034;
                else if (pid.Contains("M4")) diaM = 0.0045;
                else if (pid.Contains("M6")) diaM = 0.0066;
                else if (pid.Contains("1/4")) diaM = 0.266 * 0.0254;
                depthM = overrideDepth.HasValue ? ShopCatalogService.ToMeters(overrideDepth.Value, units) : 0.050;
                semanticName = "ThruHole_" + pid;
            }

            doc.ClearSelection2(true);
            if (!SwConnectionManager.TrySelectByToken(doc, faceToken, false, 0))
                throw new InvalidOperationException("Failed to select face/plane: " + faceToken);

            ISketchManager sm = doc.SketchManager;
            sm.InsertSketch(true);
            ISketch sk = sm.ActiveSketch;
            IMathUtility mathUtil = app.GetMathUtility() as IMathUtility;
            IMathTransform xform = sk.ModelToSketchTransform;

            sm.AddToDB = true;
            foreach (object ptObj in locations)
            {
                List<object> arr = JsonHelper.AsObjectList(ptObj);
                double mx = ShopCatalogService.ToMeters(Convert.ToDouble(arr[0], CultureInfo.InvariantCulture), units);
                double my = ShopCatalogService.ToMeters(Convert.ToDouble(arr[1], CultureInfo.InvariantCulture), units);
                double mz = arr.Count > 2 ? ShopCatalogService.ToMeters(Convert.ToDouble(arr[2], CultureInfo.InvariantCulture), units) : 0.0;

                SwConnectionManager.MapPointToSketch2D(mathUtil, xform, mx, my, mz, out double sx, out double sy);
                if (isRect)
                {
                    double halfSpan = 0.5 * Math.Max(0.0, rectL - rectW);
                    SwHelper.CreateSlot2D(sm, sx - halfSpan, sy, sx + halfSpan, sy, rectW, false, false);
                }
                else
                {
                    sm.CreateCircleByRadius(sx, sy, 0, 0.5 * diaM);
                }
            }
            sm.AddToDB = false;
            IFeature skFeat = sk as IFeature;
            if (skFeat != null) skFeat.Name = semanticName + "_Sk";
            sm.InsertSketch(true);

            doc.ClearSelection2(true);
            skFeat.Select2(false, 0);
            IFeature cut = doc.FeatureManager.FeatureCut4(
                true, false, false, (int)swEndConditions_e.swEndCondBlind, 0,
                depthM, 0.0, false, false, false, false, 0, 0,
                false, false, false, false, false, true, true, true, true, false,
                (int)swStartConditions_e.swStartSketchPlane, 0, false, false);
            if (cut == null)
            {
                doc.ClearSelection2(true);
                skFeat.Select2(false, 0);
                cut = doc.FeatureManager.FeatureCut4(
                    true, false, true, (int)swEndConditions_e.swEndCondBlind, 0,
                    depthM, 0.0, false, false, false, false, 0, 0,
                    false, false, false, false, false, true, true, true, true, false,
                    (int)swStartConditions_e.swStartSketchPlane, 0, false, false);
            }

            if (cut == null)
                throw new InvalidOperationException("Cut failed for preset: " + semanticName);
            cut.Name = semanticName;

            return new Dictionary<string, object>
            {
                { "feature_name", cut.Name },
                { "sketch_name", skFeat?.Name },
                { "count", locations.Count },
                { "recommended_mcmaster_pn", recommendedPn }
            };
        }

        private static McpToolResult HandleCreateGridfinity(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            string kind = JsonHelper.GetString(args, "kind", "bin").ToLowerInvariant();
            int ux = Math.Max(1, JsonHelper.GetInt(args, "units_x", 1));
            int uy = Math.Max(1, JsonHelper.GetInt(args, "units_y", 2));
            int hu = Math.Max(1, JsonHelper.GetInt(args, "height_u", 4));
            bool lip = JsonHelper.GetBool(args, "include_stacking_lip", true);
            bool magnets = JsonHelper.GetBool(args, "magnet_pockets", true);
            bool m3Holes = JsonHelper.GetBool(args, "m3_screw_holes", false);
            double wallMm = JsonHelper.GetDouble(args, "wall_thickness_mm", 1.2);
            double floorMm = JsonHelper.GetDouble(args, "floor_thickness_mm", 2.0);
            bool hollow = JsonHelper.GetBool(args, "hollow_interior", true);
            string label = JsonHelper.GetString(args, "embossed_label");
            string targetDoc = JsonHelper.GetString(args, "target_doc");

            ISldWorks app = swMgr.GetApp(true);
            IModelDoc2 doc;
            if (string.IsNullOrWhiteSpace(targetDoc) && app.ActiveDoc == null)
            {
                string tpl = SwConnectionManager.ResolveDefaultTemplate(app, "part");
                doc = app.NewDocument(tpl, (int)swDwgPaperSizes_e.swDwgPaperBsize, 0, 0) as IModelDoc2;
            }
            else
            {
                doc = swMgr.RequireModel(targetDoc, true);
            }

            SwConnectionManager.SetDocumentUnitSystem(doc, "gridfinity");

            // Standard Zach Freedman Gridfinity dimensions (mm -> m)
            double pitch = 42.0;
            double outerW = (pitch * ux) - 0.5;
            double outerD = (pitch * uy) - 0.5;
            double bodyHeight = hu * 7.0;
            double footH = 4.75;
            double upperH = Math.Max(2.25, bodyHeight - footH);

            return swMgr.ExecuteInUndoTransaction(doc, "Create Gridfinity " + kind, true, () =>
            {
                ISketchManager sm = doc.SketchManager;
                IFeatureManager fm = doc.FeatureManager;
                bool prevAddToDb = sm.AddToDB;
                bool prevFeatureTree = fm.EnableFeatureTree;
                fm.EnableFeatureTree = false;
                try
                {
                    // Create main upper body block on Top Plane (starting at y_up = footH via offset or mid-extrude)
                    doc.ClearSelection2(true);
                    SwConnectionManager.TrySelectByToken(doc, "Top Plane", false, 0);

                    if (kind == "baseplate")
                    {
                        double bpW = pitch * ux;
                        double bpD = pitch * uy;
                        sm.AddToDB = true;
                        sm.InsertSketch(true);
                        doc.ClearSelection2(true);
                        sm.CreateCenterRectangle(0, 0, 0, 0.5 * bpW * 0.001, 0.5 * bpD * 0.001, 0);
                        sm.AddToDB = false;
                        IFeature sk = sm.ActiveSketch as IFeature;
                        sm.InsertSketch(true);

                        doc.ClearSelection2(true);
                        sk.Select2(false, 0);
                        IFeature baseBlk = fm.FeatureExtrusion3(
                            true, false, false, (int)swEndConditions_e.swEndCondBlind, 0, 0.00475, 0,
                            false, false, false, false, 0, 0, false, false, false, false, true, true, true, 0, 0, false);
                        if (baseBlk != null) baseBlk.Name = string.Format("Gridfinity_Baseplate_{0}x{1}", ux, uy);

                        // Cut 42mm stepped cell openings
                        for (int ix = 0; ix < ux; ix++)
                        {
                            for (int iy = 0; iy < uy; iy++)
                            {
                                double cx = (-0.5 * bpW + (ix + 0.5) * pitch) * 0.001;
                                double cy = (-0.5 * bpD + (iy + 0.5) * pitch) * 0.001;
                                doc.ClearSelection2(true);
                                SwConnectionManager.TrySelectByToken(doc, "Top Plane", false, 0);
                                sm.AddToDB = true;
                                sm.InsertSketch(true);
                                doc.ClearSelection2(true);
                                sm.CreateCenterRectangle(cx, cy, 0, cx + 0.0185, cy + 0.0185, 0);
                                sm.AddToDB = false;
                                IFeature cSk = sm.ActiveSketch as IFeature;
                                sm.InsertSketch(true);
                                doc.ClearSelection2(true);
                                cSk.Select2(false, 0);
                                fm.FeatureCut4(true, false, true, (int)swEndConditions_e.swEndCondThroughAll, 0, 0.01, 0,
                                    false, false, false, false, 0, 0, false, false, false, false, false, true, true, true, true, false, 0, 0, false, false);
                            }
                        }

                        return McpToolResult.JsonText(new
                        {
                            kind = "baseplate",
                            units = "MMGS",
                            size_mm = new { x = bpW, y = bpD, z = 4.75 },
                            fits_prusa_mk35s = (bpW <= 250.0 && bpD <= 210.0) || (bpD <= 250.0 && bpW <= 210.0)
                        });
                    }

                    // Bin: extrude main body starting at 4.75mm up to bodyHeight, then add per-cell stepped feet below
                    doc.ClearSelection2(true);
                    SwConnectionManager.TrySelectByToken(doc, "Top Plane", false, 0);
                    sm.AddToDB = true;
                    sm.InsertSketch(true);
                    doc.ClearSelection2(true);
                    sm.CreateCenterRectangle(0, 0, 0, 0.5 * outerW * 0.001, 0.5 * outerD * 0.001, 0);
                    sm.AddToDB = false;
                    IFeature binSk = sm.ActiveSketch as IFeature;
                    if (binSk != null) binSk.Name = "Gridfinity_Body_Sk";
                    sm.InsertSketch(true);

                    doc.ClearSelection2(true);
                    binSk.Select2(false, 0);
                    IFeature binBody = fm.FeatureExtrusion3(
                        true, false, false, (int)swEndConditions_e.swEndCondBlind, 0,
                        upperH * 0.001, 0, false, false, false, false, 0, 0,
                        false, false, false, false, true, true, true,
                        (int)swStartConditions_e.swStartOffset, footH * 0.001, false);
                    if (binBody != null) binBody.Name = string.Format("Gridfinity_Bin_{0}x{1}x{2}U", ux, uy, hu);

                    // Create stepped Gridfinity base foot per cell (extrude downward from Y=4.75mm with 45deg draft)
                    for (int ix = 0; ix < ux; ix++)
                    {
                        for (int iy = 0; iy < uy; iy++)
                        {
                            double cx = (-0.5 * (pitch * ux) + (ix + 0.5) * pitch) * 0.001;
                            double cy = (-0.5 * (pitch * uy) + (iy + 0.5) * pitch) * 0.001;
                            double footTopHalf = 0.5 * 41.5 * 0.001;
                            doc.ClearSelection2(true);
                            SwConnectionManager.TrySelectByToken(doc, "Top Plane", false, 0);
                            sm.AddToDB = true;
                            sm.InsertSketch(true);
                            doc.ClearSelection2(true);
                            sm.CreateCenterRectangle(cx, cy, 0, cx + footTopHalf, cy + footTopHalf, 0);
                            sm.AddToDB = false;
                            IFeature fSk = sm.ActiveSketch as IFeature;
                            sm.InsertSketch(true);

                            doc.ClearSelection2(true);
                            fSk.Select2(false, 0);
                            IFeature fExt = fm.FeatureExtrusion3(
                                true, false, false, (int)swEndConditions_e.swEndCondBlind, 0,
                                footH * 0.001, 0, true, false, false, false, ShopCatalogService.ToRadians(32.0), 0,
                                false, false, false, false, true, true, true, 0, 0, false);
                            if (fExt != null) fExt.Name = string.Format("Gridfinity_Foot_{0}_{1}", ix + 1, iy + 1);

                            if (magnets)
                            {
                                double off = 0.013; // 26mm / 2
                                double[][] mPts = new[]
                                {
                                    new[] { cx - off, cy - off },
                                    new[] { cx + off, cy - off },
                                    new[] { cx + off, cy + off },
                                    new[] { cx - off, cy + off }
                                };
                                doc.ClearSelection2(true);
                                SwConnectionManager.TrySelectByToken(doc, "Top Plane", false, 0);
                                sm.AddToDB = true;
                                sm.InsertSketch(true);
                                doc.ClearSelection2(true);
                                foreach (double[] mp in mPts)
                                {
                                    sm.CreateCircleByRadius(mp[0], mp[1], 0, m3Holes ? 0.0015 : 0.00325);
                                }
                                sm.AddToDB = false;
                                IFeature mSk = sm.ActiveSketch as IFeature;
                                sm.InsertSketch(true);
                                doc.ClearSelection2(true);
                                mSk.Select2(false, 0);
                                fm.FeatureCut4(true, false, false, (int)swEndConditions_e.swEndCondBlind, 0,
                                    0.0024, 0, false, false, false, false, 0, 0, false, false, false, false, false, true, true, true, true, false, 0, 0, false, false);
                            }
                        }
                    }

                    if (hollow)
                    {
                        double cavW = outerW - 2.0 * wallMm;
                        double cavD = outerD - 2.0 * wallMm;
                        double cutDepth = Math.Max(1.0, upperH - floorMm);
                        doc.ClearSelection2(true);
                        SwConnectionManager.TrySelectByToken(doc, "Top Plane", false, 0);
                        sm.AddToDB = true;
                        sm.InsertSketch(true);
                        doc.ClearSelection2(true);
                        sm.CreateCenterRectangle(0, 0, 0, 0.5 * cavW * 0.001, 0.5 * cavD * 0.001, 0);
                        sm.AddToDB = false;
                        IFeature hSk = sm.ActiveSketch as IFeature;
                        sm.InsertSketch(true);

                        doc.ClearSelection2(true);
                        hSk.Select2(false, 0);
                        IFeature hCut = fm.FeatureCut4(
                            true, false, true, (int)swEndConditions_e.swEndCondBlind, 0,
                            cutDepth * 0.001, 0, false, false, false, false, 0, 0,
                            false, false, false, false, false, true, true, true, true, false,
                            (int)swStartConditions_e.swStartOffset, bodyHeight * 0.001, false, false);
                        if (hCut != null) hCut.Name = "Gridfinity_Interior_Cavity";
                    }

                    double totalZ = bodyHeight + (lip ? 4.4 : 0.0);
                    return McpToolResult.JsonText(new
                    {
                        kind = "bin",
                        units = "MMGS",
                        grid = string.Format("{0}U x {1}U x {2}U", ux, uy, hu),
                        outer_mm = new { x = outerW, y = outerD, z = Math.Round(totalZ, 2) },
                        wall_mm = wallMm,
                        magnet_pockets = magnets,
                        stacking_lip_requested = lip,
                        label = label,
                        fits_prusa_mk35s = (outerW <= 250.0 && outerD <= 210.0) || (outerD <= 250.0 && outerW <= 210.0)
                    });
                }
                finally
                {
                    try { sm.AddToDB = prevAddToDb; } catch { }
                    try { fm.EnableFeatureTree = prevFeatureTree; } catch { }
                }
            });
        }

        private static McpToolResult HandleBooleanOrCavity(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            if (!(doc is IPartDoc part))
                return McpToolResult.Error("sw_boolean_or_fixture_cavity operates on an active Part document.");

            string op = JsonHelper.GetString(args, "operation", "indent").ToLowerInvariant();
            string targetBodyName = JsonHelper.GetString(args, "target_body");
            string toolPath = JsonHelper.GetString(args, "tool_step_or_part_path");
            string toolBodyName = JsonHelper.GetString(args, "tool_body_name");
            double clearance = JsonHelper.GetDouble(args, "clearance_offset", 0.010);
            double wallThick = JsonHelper.GetDouble(args, "wall_thickness", 0.250);
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));

            double clearM = ShopCatalogService.ToMeters(clearance, units);
            double wallM = ShopCatalogService.ToMeters(wallThick, units);

            return swMgr.ExecuteInUndoTransaction(doc, "Fixture Cavity " + op, true, () =>
            {
                if (!string.IsNullOrWhiteSpace(toolPath) && File.Exists(toolPath))
                {
                    IFeature inserted = part.InsertPart2(toolPath, (int)swInsertPartOptions_e.swInsertPartImportSolids);
                    if (inserted != null && string.IsNullOrWhiteSpace(toolBodyName))
                        toolBodyName = inserted.Name;
                }

                doc.ClearSelection2(true);
                if (!SwConnectionManager.TrySelectByToken(doc, targetBodyName, false, 1))
                    throw new InvalidOperationException("Failed to select target_body: " + targetBodyName);
                if (!SwConnectionManager.TrySelectByToken(doc, toolBodyName, true, 2))
                    throw new InvalidOperationException("Failed to select tool_body: " + toolBodyName);

                IFeature feat;
                if (op == "indent")
                {
                    feat = doc.FeatureManager.InsertIndent(wallM, clearM, true, false, false, false);
                }
                else
                {
                    int combType = op == "common" ? (int)swBodyOperationType_e.SWBODYINTERSECT : (int)swBodyOperationType_e.SWBODYCUT;
                    feat = doc.FeatureManager.InsertCombineFeature(combType, null, null);
                }

                if (feat == null)
                    throw new InvalidOperationException("Boolean/indent cavity operation returned null.");

                return McpToolResult.JsonText(new
                {
                    feature_name = feat.Name,
                    operation = op,
                    clearance = clearance,
                    units = units
                });
            });
        }

        private static McpToolResult HandleCreateBendingForm(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            string faceToken = JsonHelper.GetString(args, "target_curved_face_token");
            int plyCount = Math.Max(1, JsonHelper.GetInt(args, "veneer_ply_count", 5));
            double plyThick = JsonHelper.GetDouble(args, "ply_thickness", 0.0625);
            double breather = JsonHelper.GetDouble(args, "breather_allowance", 0.030);
            double caulThick = JsonHelper.GetDouble(args, "caul_thickness", 0.500);
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));

            double totalOffset = (plyCount * plyThick) + breather;
            double offsetM = ShopCatalogService.ToMeters(totalOffset, units);
            double caulM = ShopCatalogService.ToMeters(caulThick, units);

            return swMgr.ExecuteInUndoTransaction(doc, "Create Vacuum Bending Form", true, () =>
            {
                doc.ClearSelection2(true);
                if (!SwConnectionManager.TrySelectByToken(doc, faceToken, false, 0))
                    throw new InvalidOperationException("Could not select curved face: " + faceToken);

                doc.InsertOffsetSurface(offsetM, false);
                doc.FeatureManager.FeatureBossThicken(caulM, 0, 0, false, false, true, true);
                return McpToolResult.JsonText(new
                {
                    bending_caul_created = true,
                    total_ply_and_breather_offset = Math.Round(totalOffset, 4),
                    caul_thickness = caulThick,
                    units = units,
                    shop_note = "Add 3/8\" dowel alignment pins to lock male/female forms inside the vacuum press bag."
                });
            });
        }

        private static McpToolResult HandleEngraveLabel(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            ISldWorks app = swMgr.GetApp();
            List<object> labels = JsonHelper.GetList(args, "labels");
            if (labels.Count > 0)
            {
                string defaultUnits = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
                return swMgr.ExecuteInUndoTransaction(doc, "Engrave/Emboss Labels Batch", true, () =>
                {
                    using (IDisposable uiScope = SwHelper.SuppressUiUpdates(doc))
                    {
                        List<object> created = new List<object>();
                        foreach (object lObj in labels)
                        {
                            Dictionary<string, object> lArgs = lObj as Dictionary<string, object>
                                ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(lObj));
                            if (lArgs == null) continue;
                            if (!lArgs.ContainsKey("units")) lArgs["units"] = defaultUnits;
                            created.Add(EngraveLabelInternal(app, doc, lArgs));
                        }
                        return McpToolResult.JsonText(new
                        {
                            batch_count = created.Count,
                            labels = created
                        });
                    }
                });
            }

            return swMgr.ExecuteInUndoTransaction(doc, "Engrave/Emboss Label", true, () =>
                McpToolResult.JsonText(EngraveLabelInternal(app, doc, args)));
        }

        internal static Dictionary<string, object> EngraveLabelInternal(ISldWorks app, IModelDoc2 doc, Dictionary<string, object> args)
        {
            string faceToken = JsonHelper.GetString(args, "face_token");
            string text = JsonHelper.GetString(args, "text", "JIG");
            double[] origin = JsonHelper.GetDoubleArray(args, "origin_xyz") ?? new[] { 0.0, 0.0, 0.0 };
            double height = JsonHelper.GetDouble(args, "font_height", 0.35);
            double depth = JsonHelper.GetDouble(args, "depth", 0.030);
            string matCtx = JsonHelper.GetString(args, "material_context", "wood").ToLowerInvariant();
            bool emboss = JsonHelper.GetBool(args, "emboss", matCtx == "3d_print");
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));

            double heightM = ShopCatalogService.ToMeters(height, units);
            double depthM = ShopCatalogService.ToMeters(depth, units);

            string recBit = matCtx == "wood"
                ? "3/8\" Cadence Mfg \"GrooVee Jenny\" 90-deg Downshear"
                : (matCtx == "corian_acrylic" ? "30-deg 0.2mm Tip V-Engraving Bit" : "0.4mm Prusa MK3.5S+ Nozzle (0.6-0.8mm emboss)");

            doc.ClearSelection2(true);
            if (!SwConnectionManager.TrySelectByToken(doc, faceToken, false, 0))
                throw new InvalidOperationException("Failed to select face: " + faceToken);

            ISketchManager sm = doc.SketchManager;
            sm.InsertSketch(true);
            ISketch sk = sm.ActiveSketch;
            IMathUtility mathUtil = app.GetMathUtility() as IMathUtility;
            double ox = origin.Length > 0 ? ShopCatalogService.ToMeters(origin[0], units) : 0.0;
            double oy = origin.Length > 1 ? ShopCatalogService.ToMeters(origin[1], units) : 0.0;
            double oz = origin.Length > 2 ? ShopCatalogService.ToMeters(origin[2], units) : 0.0;
            SwConnectionManager.MapPointToSketch2D(mathUtil, sk.ModelToSketchTransform, ox, oy, oz, out double sx, out double sy);

            ISketchText skText = doc.InsertSketchText(sx, sy, 0, text, 0, 0, 0, 100, 100) as ISketchText;
            if (skText != null)
            {
                ITextFormat tf = skText.GetTextFormat() as ITextFormat;
                if (tf != null)
                {
                    tf.CharHeight = heightM;
                    skText.SetTextFormat(false, tf);
                }
            }
            IFeature skFeat = sk as IFeature;
            if (skFeat != null) skFeat.Name = "Label_Sk";
            sm.InsertSketch(true);

            doc.ClearSelection2(true);
            skFeat.Select2(false, 0);
            IFeature feat = emboss
                ? doc.FeatureManager.FeatureExtrusion3(true, false, false, (int)swEndConditions_e.swEndCondBlind, 0, depthM, 0,
                    false, false, false, false, 0, 0, false, false, false, false, true, true, true, 0, 0, false)
                : doc.FeatureManager.FeatureCut4(true, false, false, (int)swEndConditions_e.swEndCondBlind, 0, depthM, 0,
                    false, false, false, false, 0, 0, false, false, false, false, false, true, true, true, true, false, 0, 0, false, false);

            if (feat != null) feat.Name = "Label_" + (emboss ? "Emboss" : "Engrave");

            return new Dictionary<string, object>
            {
                { "feature_name", feat?.Name },
                { "sketch_name", skFeat?.Name },
                { "text", text },
                { "recommended_tool", recBit }
            };
        }

        private static McpToolResult HandleBuildPartFeatures(SwConnectionManager swMgr, ShopCatalogService catalog, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            if (!(doc is IPartDoc partDoc))
                return McpToolResult.Error("sw_build_part_features requires an active Part (.SLDPRT) document.");

            ISldWorks app = swMgr.GetApp();
            bool resetFirst = JsonHelper.GetBool(args, "reset_first", false);
            bool keepSketchesVisible = JsonHelper.GetBool(args, "keep_sketches_visible", false);
            string defaultUnits = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            Dictionary<string, object> equationsArg = JsonHelper.GetDict(args, "equations");
            List<object> steps = JsonHelper.GetList(args, "features");

            return swMgr.ExecuteInUndoTransaction(doc, "Build Part Features Batch", true, () =>
            {
                using (IDisposable uiScope = SwHelper.SuppressUiUpdates(doc))
                {
                    HashSet<string> preVisible = resetFirst
                        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        : SwHelper.GetVisibleSketchNames(doc);

                    int resetDeleted = resetFirst ? ResetPartFeaturesClean(doc) : 0;

                    object eqResult = null;
                    if (equationsArg != null && equationsArg.Count > 0)
                        eqResult = SketchAndEquationTools.ManageEquationsInternal(doc, equationsArg);

                    List<object> stepResults = new List<object>();
                    foreach (object stepObj in steps)
                    {
                        Dictionary<string, object> step = stepObj as Dictionary<string, object>
                            ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(stepObj));
                        if (step == null) continue;
                        if (!step.ContainsKey("units")) step["units"] = defaultUnits;

                        string op = JsonHelper.GetString(step, "op", "").Trim().ToLowerInvariant();
                        if (string.IsNullOrEmpty(op))
                        {
                            if (step.ContainsKey("depth") || step.ContainsKey("sketch") || step.ContainsKey("through_all"))
                                op = "extrude";
                            else if (step.ContainsKey("entities"))
                                op = "sketch_2d";
                            else if (step.ContainsKey("curves"))
                                op = "sketch_3d";
                        }

                        object outData;
                        if (op == "equations")
                        {
                            outData = SketchAndEquationTools.ManageEquationsInternal(doc, step);
                        }
                        else if (op == "ref_plane" || op == "ref_geometry" || op == "axis" || op == "coord_sys")
                        {
                            if (!step.ContainsKey("kind"))
                                step["kind"] = (op == "axis" || op == "coord_sys") ? op : "plane";
                            outData = SketchAndEquationTools.CreateRefGeometryInternal(doc, step);
                        }
                        else if (op == "sketch_2d" || op == "sketch")
                        {
                            outData = SketchAndEquationTools.Create2DSketchInternal(app, doc, step);
                        }
                        else if (op == "sketch_3d" || op == "guide_sketch")
                        {
                            outData = SketchAndEquationTools.Create3DGuideSketchInternal(doc, step);
                        }
                        else if (op == "extrude" || op == "boss" || op == "cut")
                        {
                            if (op == "cut" && !step.ContainsKey("mode") && !step.ContainsKey("is_cut"))
                                step["mode"] = "cut";
                            outData = ExtrudeInternal(app, doc, step);
                        }
                        else if (op == "revolve")
                        {
                            outData = RevolveInternal(app, doc, step);
                        }
                        else if (op == "hole_preset" || op == "holes")
                        {
                            outData = HolePresetInternal(app, catalog, doc, step);
                        }
                        else if (op == "fillet" || op == "chamfer" || op == "fillet_or_chamfer")
                        {
                            if ((op == "fillet" || op == "chamfer") && !step.ContainsKey("kind"))
                                step["kind"] = op;
                            outData = FilletOrChamferInternal(doc, step);
                        }
                        else if (op == "shell")
                        {
                            outData = ShellInternal(doc, step);
                        }
                        else if (op == "pattern" || op == "mirror" || op == "pattern_or_mirror")
                        {
                            if (op == "mirror" && !step.ContainsKey("kind"))
                                step["kind"] = "mirror";
                            outData = PatternOrMirrorInternal(doc, step);
                        }
                        else if (op == "label" || op == "engrave" || op == "emboss")
                        {
                            if (op == "emboss" && !step.ContainsKey("emboss"))
                                step["emboss"] = true;
                            outData = EngraveLabelInternal(app, doc, step);
                        }
                        else if (op == "teardrop" || op == "3dprint_teardrop")
                        {
                            outData = InvokeAddinFeatureHeadlessInternal(app, doc, "3dprint_teardrop", step);
                        }
                        else if (op == "crisscross_bridge" || op == "3dprint_bridge" || op == "bridge")
                        {
                            outData = InvokeAddinFeatureHeadlessInternal(app, doc, "3dprint_bridge", step);
                        }
                        else if (op == "joinery")
                        {
                            outData = InvokeAddinFeatureHeadlessInternal(app, doc, "joinery", step);
                        }
                        else
                        {
                            throw new InvalidOperationException("Unsupported op in sw_build_part_features: " + op);
                        }

                        stepResults.Add(new { op = op, result = outData });
                    }

                    int hiddenCount = keepSketchesVisible ? 0 : SwHelper.HideSketches(doc, null, preVisible);
                    uiScope.Dispose();
                    doc.EditRebuild3();

                    object[] bodies = partDoc.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                    int bodyCount = bodies != null ? bodies.Length : 0;
                    double[] partBox = partDoc.GetPartBox(true) as double[];
                    object bboxOut = (partBox != null && partBox.Length >= 6)
                        ? new
                        {
                            min = new[]
                            {
                                Math.Round(ShopCatalogService.FromMeters(partBox[0], defaultUnits), 4),
                                Math.Round(ShopCatalogService.FromMeters(partBox[1], defaultUnits), 4),
                                Math.Round(ShopCatalogService.FromMeters(partBox[2], defaultUnits), 4)
                            },
                            max = new[]
                            {
                                Math.Round(ShopCatalogService.FromMeters(partBox[3], defaultUnits), 4),
                                Math.Round(ShopCatalogService.FromMeters(partBox[4], defaultUnits), 4),
                                Math.Round(ShopCatalogService.FromMeters(partBox[5], defaultUnits), 4)
                            },
                            units = defaultUnits
                        }
                        : null;

                    return McpToolResult.JsonText(new
                    {
                        part_title = doc.GetTitle(),
                        reset_deleted_count = resetDeleted,
                        equations = eqResult,
                        steps_executed = stepResults.Count,
                        results = stepResults,
                        hidden_sketches_count = hiddenCount,
                        body_count = bodyCount,
                        bounding_box = bboxOut,
                        units = defaultUnits
                    });
                }
            });
        }

        private static int ResetPartFeaturesClean(IModelDoc2 doc)
        {
            if (doc == null) return 0;

            HashSet<string> preservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Front Plane", "Top Plane", "Right Plane", "Origin",
                "History", "Sensors", "Annotations", "Design Binder", "Equations",
                "Solid Bodies", "Surface Bodies", "Lights, Cameras and Scene",
                "Ambient", "Directional1", "Directional2", "Directional3",
                "Comments", "Favorites", "Selection Sets", "Material <not specified>"
            };
            HashSet<string> preservedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "HistoryFolder", "SensorFolder", "DetailCabinet", "DocsFolder", "EqnFolder",
                "SolidBodyFolder", "SurfaceBodyFolder", "MaterialFolder", "CommentsFolder",
                "FavorFolder", "SelectionSetFolder", "LiveSectionFolder", "BlockDef",
                "OriginProfileFeature", "Lighting", "Camera", "AmbientLight",
                "DirectionLight", "SpotLight", "EnvFolder", "MateGroup"
            };

            List<IFeature> candidates = new List<IFeature>();
            bool seenOrigin = false;
            IFeature f = doc.FirstFeature() as IFeature;
            while (f != null)
            {
                string fType = f.GetTypeName2() ?? "";
                string fName = f.Name ?? "";
                if (string.Equals(fType, "OriginProfileFeature", StringComparison.OrdinalIgnoreCase))
                {
                    seenOrigin = true;
                }
                else if (seenOrigin && !preservedTypes.Contains(fType) && !preservedNames.Contains(fName))
                {
                    if (!fType.EndsWith("Folder", StringComparison.OrdinalIgnoreCase))
                        candidates.Add(f);
                }
                f = f.GetNextFeature() as IFeature;
            }

            if (candidates.Count == 0)
                return 0;

            int deletedCount = 0;
            int delOpts = (int)(swDeleteSelectionOptions_e.swDelete_Absorbed | swDeleteSelectionOptions_e.swDelete_Children);
            // Delete in reverse tree order (children first)
            for (int pass = 0; pass < 2 && candidates.Count > 0; pass++)
            {
                doc.ClearSelection2(true);
                int sel = 0;
                for (int i = candidates.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        if (candidates[i].Select2(sel > 0, 0))
                            sel++;
                    }
                    catch { }
                }
                if (sel > 0 && doc.Extension.DeleteSelection2(delOpts))
                    deletedCount += sel;
                doc.ClearSelection2(true);

                // Re-scan to see if any user features survived (e.g. consumed sketch order edge cases)
                List<IFeature> remaining = new List<IFeature>();
                seenOrigin = false;
                f = doc.FirstFeature() as IFeature;
                while (f != null)
                {
                    string fType = f.GetTypeName2() ?? "";
                    string fName = f.Name ?? "";
                    if (string.Equals(fType, "OriginProfileFeature", StringComparison.OrdinalIgnoreCase))
                        seenOrigin = true;
                    else if (seenOrigin && !preservedTypes.Contains(fType) && !preservedNames.Contains(fName) && !fType.EndsWith("Folder", StringComparison.OrdinalIgnoreCase))
                        remaining.Add(f);
                    f = f.GetNextFeature() as IFeature;
                }
                candidates = remaining;
            }
            doc.EditRebuild3();
            return deletedCount;
        }

        private static McpToolResult HandleInvokeRepoTool(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            string tool = (JsonHelper.GetString(args, "tool", "export_faces_dxf") ?? "").Trim().ToLowerInvariant();
            if (tool == "export_faces_dxf")
            {
                ISldWorks app = swMgr.GetApp(false);
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string swpPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "ExportFacesToDXF.swp"));
                if (!File.Exists(swpPath))
                    return McpToolResult.Error("ExportFacesToDXF.swp not found at: " + swpPath);

                string folder = JsonHelper.GetString(args, "output_folder", Path.Combine(Path.GetTempPath(), "sw_mcp_dxf_export"));
                Directory.CreateDirectory(folder);
                int mode = JsonHelper.GetInt(args, "mode", 1);
                string overcut = JsonHelper.GetString(args, "overcut", "0.25in");
                string resultFile = Path.Combine(Path.GetTempPath(), "ExportFacesToDXF_test_result.txt");
                string cfgFile = Path.Combine(Path.GetTempPath(), "ExportFacesToDXF_test.cfg");
                File.WriteAllText(cfgFile, string.Format("mode={0}\novercut={1}\nfolder={2}\nresult={3}\n", mode, overcut, folder, resultFile));

                int err = 0;
                bool ok = app.RunMacro2(swpPath, "ExportFacesToDXF", "RunAutomatedFromTempConfig", 2, out err); // 2 = swRunMacroUnloadAfterRun
                string status = File.Exists(resultFile) ? File.ReadAllText(resultFile) : "No result file written";
                return McpToolResult.JsonText(new { macro_ok = ok, err_code = err, status = status, output_folder = folder });
            }

            if (tool == "3dprint_teardrop" || tool == "teardrop" ||
                tool == "3dprint_bridge" || tool == "crisscross_bridge" || tool == "bridge" ||
                tool == "joinery")
            {
                IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
                if (!(doc is IPartDoc))
                    return McpToolResult.Error("Headless 3DPrintDFM / Joinery tools operate on an active Part (.SLDPRT) document.");

                ISldWorks app = swMgr.GetApp();
                return swMgr.ExecuteInUndoTransaction(doc, "Headless Add-in " + tool, true, () =>
                {
                    using (IDisposable uiScope = SwHelper.SuppressUiUpdates(doc))
                    {
                        HashSet<string> preVisible = SwHelper.GetVisibleSketchNames(doc);
                        Dictionary<string, object> result = InvokeAddinFeatureHeadlessInternal(app, doc, tool, args);
                        int hiddenCount = SwHelper.HideSketches(doc, null, preVisible);
                        result["hidden_sketches_count"] = hiddenCount;
                        return McpToolResult.JsonText(result, isError: !JsonHelper.GetBool(result, "ok", true));
                    }
                });
            }

            return McpToolResult.Error("Unsupported tool in sw_invoke_repo_addin_tool: " + tool);
        }

        internal static Dictionary<string, object> InvokeAddinFeatureHeadlessInternal(
            ISldWorks app,
            IModelDoc2 doc,
            string toolKind,
            Dictionary<string, object> args)
        {
            string normTool = (toolKind ?? "3dprint_teardrop").Trim().ToLowerInvariant();
            bool isJoinery = normTool == "joinery";
            bool isBridge = normTool == "3dprint_bridge" || normTool == "crisscross_bridge" || normTool == "bridge";
            string canonicalTool = isJoinery ? "joinery" : (isBridge ? "3dprint_bridge" : "3dprint_teardrop");

            Dictionary<string, object> payload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { "tool", canonicalTool },
                { "mode", canonicalTool },
                { "target_doc", doc != null ? doc.GetTitle() : JsonHelper.GetString(args, "target_doc") }
            };

            List<string> faceB64 = new List<string>();
            foreach (string tok in JsonHelper.GetStringList(args, "face_tokens"))
            {
                string b64 = ResolveTokenToPersistBase64(doc, tok, "face");
                if (!string.IsNullOrEmpty(b64)) faceB64.Add(b64);
            }
            if (faceB64.Count > 0) payload["face_persist_ids_b64"] = faceB64;

            List<string> edgeB64 = new List<string>();
            foreach (string tok in JsonHelper.GetStringList(args, "edge_tokens"))
            {
                string b64 = ResolveTokenToPersistBase64(doc, tok, "edge");
                if (!string.IsNullOrEmpty(b64)) edgeB64.Add(b64);
            }
            if (edgeB64.Count > 0) payload["edge_persist_ids_b64"] = edgeB64;

            List<string> bodyNames = JsonHelper.GetStringList(args, "body_names");
            if (bodyNames.Count > 0) payload["body_names"] = bodyNames;

            string bedRef = JsonHelper.GetString(args, "bed_plane_or_face");
            if (!string.IsNullOrWhiteSpace(bedRef))
            {
                string bedB64 = ResolveTokenToPersistBase64(doc, bedRef, "");
                if (!string.IsNullOrEmpty(bedB64))
                    payload["bed_persist_id_b64"] = bedB64;
            }

            foreach (string key in new[]
            {
                "build_direction", "flip_bed_direction", "overhang_angle_deg", "enable_cap",
                "max_cap_height_mm", "min_circle_angle_deg", "min_hole_diameter_mm",
                "layer_height_mm", "bridge_angle_offset_deg", "joint_type", "tab_count",
                "tab_width", "tool_bit_diameter", "tool_bit_radius", "clearance",
                "enable_dogbones", "relief_type", "enable_keying", "key_delta",
                "use_macro_feature", "units"
            })
            {
                if (args != null && args.TryGetValue(key, out object val) && val != null)
                    payload[key] = val;
            }

            string reqJson = JsonHelper.Serialize(payload);
            bool useMacroFeature = JsonHelper.GetBool(args, "use_macro_feature", true);
            string fallbackReason = null;
            string rawResp = null;

            if (useMacroFeature && app != null)
            {
                string guid = isJoinery ? SolidWorksJoineryAddin.SwAddin.ADDIN_GUID : SolidWorks3DPrintDFMAddin.SwAddin.ADDIN_GUID;
                string methodName = isJoinery ? "InvokeJoineryHeadless" : "InvokeDfmHeadless";
                object addinObj = null;
                try { addinObj = app.GetAddInObject(guid); }
                catch (Exception ex) { fallbackReason = "GetAddInObject exception: " + ex.Message; }

                if (addinObj == null)
                {
                    if (fallbackReason == null)
                        fallbackReason = string.Format("Add-in {0} is not loaded in active SOLIDWORKS session.", guid);
                }
                else
                {
                    try
                    {
                        MethodInfo mi = addinObj.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
                        if (mi != null)
                        {
                            rawResp = mi.Invoke(addinObj, new object[] { reqJson }) as string;
                        }
                        else
                        {
                            rawResp = addinObj.GetType().InvokeMember(
                                methodName,
                                BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                                null,
                                addinObj,
                                new object[] { reqJson }) as string;
                        }
                    }
                    catch (Exception ex)
                    {
                        fallbackReason = string.Format("{0} not exposed on loaded add-in (restart SOLIDWORKS after add-in update): {1}",
                            methodName, ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                        rawResp = null;
                    }
                }
            }
            else if (!useMacroFeature)
            {
                fallbackReason = "use_macro_feature=false requested standalone native features.";
            }

            if (string.IsNullOrWhiteSpace(rawResp))
            {
                rawResp = isJoinery
                    ? SolidWorksJoineryAddin.MacroFeatures.JoineryHeadlessBridge.ExecuteJson(app, reqJson, allowMacroFeature: false)
                    : SolidWorks3DPrintDFMAddin.Builders.DfmHeadlessBridge.ExecuteJson(app, reqJson, allowMacroFeature: false);
            }

            Dictionary<string, object> parsed = JsonHelper.DeserializeDict(rawResp);
            if (parsed == null)
            {
                return new Dictionary<string, object>
                {
                    { "ok", false },
                    { "tool", canonicalTool },
                    { "error", "Empty response from headless bridge." }
                };
            }

            if (!string.IsNullOrEmpty(fallbackReason))
                parsed["fallback_reason"] = fallbackReason;
            return parsed;
        }

        private static string ResolveTokenToPersistBase64(IModelDoc2 doc, string token, string kindHint)
        {
            if (doc == null || string.IsNullOrWhiteSpace(token)) return null;
            string clean = token.Trim();
            if (clean.StartsWith("pers:", StringComparison.OrdinalIgnoreCase))
                return clean.Substring(5).Trim();

            object targetObj = null;
            if (kindHint == "face" || clean.StartsWith("face:", StringComparison.OrdinalIgnoreCase))
                targetObj = SwConnectionManager.FindFaceByToken(doc, clean);
            else if (kindHint == "edge" || clean.StartsWith("edge:", StringComparison.OrdinalIgnoreCase))
                targetObj = SwConnectionManager.FindEdgeByToken(doc, clean);

            if (targetObj == null)
            {
                try
                {
                    doc.ClearSelection2(true);
                    if (SwConnectionManager.TrySelectByToken(doc, clean, false, 0))
                    {
                        ISelectionMgr selMgr = doc.SelectionManager as ISelectionMgr;
                        targetObj = selMgr != null ? selMgr.GetSelectedObject6(1, -1) : null;
                    }
                }
                catch { }
                finally
                {
                    try { doc.ClearSelection2(true); } catch { }
                }
            }

            if (targetObj == null) return null;
            try
            {
                byte[] bytes = doc.Extension.GetPersistReference3(targetObj) as byte[];
                return (bytes != null && bytes.Length > 0) ? Convert.ToBase64String(bytes) : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
