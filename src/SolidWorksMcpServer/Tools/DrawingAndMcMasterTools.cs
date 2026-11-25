using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcpServer.Core;
using SolidWorksMcpServer.Protocol;

namespace SolidWorksMcpServer.Tools
{
    public static class DrawingAndMcMasterTools
    {
        private static readonly Regex McMasterRegex = AssemblyGeometryHelper.McMasterRegex;

        public static void Register(McpStdioHost host, SwConnectionManager swMgr)
        {
            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_search_and_download_mcmaster",
                Description = "Search McMaster-Carr fasteners (default 18-8 Stainless Steel, or any specified material and nut style) or download genuine .SLDPRT files (single or batch) via the installed McMaster-Carr SOLIDWORKS Add-in, inspect part geometry/origin with automatic shank-length/kind self-verification, and create a 'Fastener_Axis' reference axis for mating.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "mcmaster_pn", JsonHelper.Prop("string", "Optional explicit McMaster-Carr part number (e.g. 93625A100, 91292A137).") },
                    { "mcmaster_pns", JsonHelper.ArrayProp("string", "Optional batch list of McMaster-Carr part numbers to resolve/download/verify in one call.") },
                    { "specs", JsonHelper.ArrayProp("object", "Optional batch list of [{mcmaster_pn, expected_length_mm, expected_kind}] for download + geometry verification.") },
                    { "expected_length_mm", JsonHelper.Prop("number", "Optional expected shank length in mm for single-PN geometry verification.") },
                    { "query", JsonHelper.Prop("string", "Optional spec search query (e.g. 'M6x20mm SHCS', '1/4-20 jam nut', 'Brass M6 hex nut').") },
                    { "material", JsonHelper.Prop("string", "Desired fastener material (defaults to '18-8 Stainless Steel'; e.g. '316 Stainless Steel', 'Black-Oxide Alloy Steel', 'Brass', 'Zinc-Plated Steel').", "18-8 Stainless Steel") },
                    { "nut_style", JsonHelper.Prop("string", "Optional nut style filter for query resolution.", null, new[] { "nylock", "hex_nut", "jam_nut" }) },
                    { "library_dir", JsonHelper.Prop("string", "Target folder for downloaded .SLDPRT files (defaults to <active_assembly_dir>/Hardware).") },
                    { "download_if_missing", JsonHelper.Prop("boolean", "Attempt live download via the McMaster SOLIDWORKS Add-in when not already cached on disk.", true) },
                    { "prepare_fastener_axis", JsonHelper.Prop("boolean", "Silently open the downloaded .SLDPRT, inspect its bearing plane/axis, and ensure 'Fastener_Axis' exists.", true) },
                    { "target_doc", JsonHelper.Prop("string", "Optional assembly title/path used to resolve the default Hardware folder.") }
                }),
                Handler = args => HandleSearchAndDownloadMcMaster(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_install_fastener_stack",
                Description = "One-shot batched installer for complete fastener stacks (bolt + optional head washer + optional nut washer + optional primary/jam nuts) across one or more joint groups. Auto-reads each McMaster part's local axis, bearing plane, and thickness, unfixes all components, mates Concentric ('Fastener_Axis') + Coincident, and groups into 'McMaster Hardware'.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "stacks", JsonHelper.ArrayProp("object", "List of joint stack specs: [{label, hole_component, holes:[[x,y,z],...], axis:[ax,ay,az] (head->tip), head_face_component, head_face_components, head_face_coord, nut_face_component, nut_face_components, nut_face_coord, bolt_pn, head_washer_pn, nut_washer_pn, nut_pn, jam_nut_pn, clock_angle_deg, lock_rotation}].") },
                    { "units", JsonHelper.Prop("string", "Coordinate units: mm or in (defaults to active doc units).") },
                    { "library_dir", JsonHelper.Prop("string", "McMaster hardware library folder (defaults to <assy_dir>/Hardware).") },
                    { "download_if_missing", JsonHelper.Prop("boolean", "Auto-download missing genuine McMaster .SLDPRT files via McMaster Add-in bridge (never generates placeholders).", true) },
                    { "folder_name", JsonHelper.Prop("string", "FeatureManager folder to group inserted hardware.", "McMaster Hardware") },
                    { "create_mates", JsonHelper.Prop("boolean", "Create Concentric ('Fastener_Axis') + Coincident mates.", true) },
                    { "target_doc", JsonHelper.Prop("string", "Optional assembly title/path.") }
                }, "stacks"),
                Handler = args => FastenerStackInstaller.HandleInstallFastenerStack(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_populate_mcmaster_hardware",
                Description = "Populate genuine McMaster-Carr fasteners/inserts/washers/nuts into an active assembly (single SKU or batch via 'items' array), ensure 'Fastener_Axis' exists, apply 3D poses, create Concentric+Coincident mates (unlocked rotation unless hex-flat clocking is required), and group into 'McMaster Hardware'. Never generates placeholder geometry unless allow_placeholder_generation=true is explicitly passed.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "mcmaster_pn", JsonHelper.Prop("string", "McMaster-Carr part number (e.g. 94180A333, 93625A100, 91292A027).") },
                    { "description", JsonHelper.Prop("string", "Short description for BOM/search context.", "Fastener") },
                    { "part_path", JsonHelper.Prop("string", "Optional explicit path to a genuine downloaded McMaster .SLDPRT file.") },
                    { "hole_face_or_edge_tokens", JsonHelper.ArrayProp("string", "Target cylindrical hole faces or circular edge tokens in the assembly (used when placements is not supplied).") },
                    { "placements", JsonHelper.ArrayProp("object", "Optional 3D poses/mate targets: [{position:[x,y,z], axis:[ax,ay,az], clock_angle_deg, hole_token, bearing_face_token, flip_mate, lock_rotation}].") },
                    { "items", JsonHelper.ArrayProp("object", "Batch mode: array of hardware item specs [{mcmaster_pn, description, part_path, placements, hole_face_or_edge_tokens, create_mates, folder_name, ...}] inserted inside one UI-suppressed transaction & single EditRebuild3().") },
                    { "units", JsonHelper.Prop("string", "Units for placement coordinates: mm or in (defaults to active doc units).") },
                    { "create_mates", JsonHelper.Prop("boolean", "Create Concentric ('Fastener_Axis') + Coincident mates when tokens are provided; leaves Concentric rotation unlocked unless hex clocking/lock is set.", true) },
                    { "lock_rotation_or_clock_flat", JsonHelper.Prop("boolean", "Lock Concentric rotation (auto-enabled per placement when clock_angle_deg != 0).", false) },
                    { "folder_name", JsonHelper.Prop("string", "FeatureManager folder name to group inserted hardware.", "McMaster Hardware") },
                    { "allow_placeholder_generation", JsonHelper.Prop("boolean", "EXPLICIT USER APPROVAL ONLY: allow generating IModeler placeholder geometry if McMaster download is unavailable.", false) },
                    { "nominal_diameter_mm", JsonHelper.Prop("number", "Nominal diameter in mm only when allow_placeholder_generation=true.", 5.0) },
                    { "nominal_length_mm", JsonHelper.Prop("number", "Nominal length in mm only when allow_placeholder_generation=true.", 10.0) },
                    { "library_dir", JsonHelper.Prop("string", "Optional folder to search/cache McMaster .SLDPRT files (defaults to <assy_dir>/Hardware).") },
                    { "target_doc", JsonHelper.Prop("string", "Optional assembly title/path.") }
                }),
                Handler = args => HandlePopulateMcMaster(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_extract_mcmaster_and_shop_bom",
                Description = "Extract a unified BOM listing McMaster-Carr SKUs, component quantities, custom properties (BOM_Desc, BOM_Role, PartNumber, Description, Material), and body dimensions from an assembly or multibody part.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") },
                    { "units", JsonHelper.Prop("string", "Units for part dimensions: in or mm.") },
                    { "custom_property_names", JsonHelper.ArrayProp("string", "Optional custom property names to extract per component (defaults to [PartNumber, Description, BOM_Desc, BOM_Role, Material, Vendor]).") },
                    { "include_excluded_from_bom", JsonHelper.Prop("boolean", "Also list components flagged ExcludeFromBOM = true.", false) }
                }),
                Handler = args => HandleExtractBom(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_create_exploded_assembly_view",
                Description = "Create or update an exploded view on a SOLIDWORKS assembly configuration using explicit directional or compound multi-axis steps (SelectData.Mark = 1, automatic Chain1 AutoExplode cleanup, and automatic 3D explode-line route sketch generation).",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "configuration", JsonHelper.Prop("string", "Optional target assembly configuration name to activate (or create if create_configuration_if_missing is true).") },
                    { "create_configuration_if_missing", JsonHelper.Prop("boolean", "Create 'configuration' on the assembly if it does not exist yet.", true) },
                    { "explode_view_name", JsonHelper.Prop("string", "Optional custom name for the exploded view feature.") },
                    { "clear_existing_steps", JsonHelper.Prop("boolean", "Clear any pre-existing or auto-populated Chain1 explode steps on the configuration before adding custom steps.", true) },
                    { "auto_explode", JsonHelper.Prop("boolean", "Run AutoExplode only on small assemblies (<=20 components) when no manual steps are specified.", false) },
                    { "show_exploded", JsonHelper.Prop("boolean", "Display the assembly in exploded state.", true) },
                    { "steps", JsonHelper.ArrayProp("object", "Steps: [{component_name, component_names, distance, direction_index (0=X,1=Y,2=Z), axis ('+X'|'-Y'|'+Z'), reverse, vector:[dx,dy,dz], motions:[{axis,direction_index,distance,reverse}], explode_line, units}]. Supports compound multi-axis moves on the same component.") },
                    { "create_explode_lines", JsonHelper.Prop("boolean", "Automatically generate 3D explode-line trajectories (InsertExplodeLineSketch -> CreateLine) along each step vector.", true) },
                    { "explode_lines", JsonHelper.ArrayProp("object", "Optional explicit 3D explode-line segments: [{from:[x1,y1,z1], to:[x2,y2,z2], units}].") },
                    { "target_doc", JsonHelper.Prop("string", "Optional assembly title/path.") }
                }),
                Handler = args => HandleExplodedView(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_create_fabrication_drawing",
                Description = "Generate a single-sheet or multi-sheet 2D .SLDDRW drawing / step-by-step assembly instruction guide (loads .slddrt sheet format via SetupSheet5/NewSheet4, cleans up title block in EditTemplate, supports per-view configurations, custom camera views, Shaded With Edges via SetDisplayMode3, GetOutline auto-fit boxes, declarative BOM tables, selective/delta ballooning with InsertBOMBalloon2 fallback, and automatic .PDF export).",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "source_model_path", JsonHelper.Prop("string", "Path to source .SLDPRT or .SLDASM (defaults to active doc path).") },
                    { "mode", JsonHelper.Prop("string", "workshop_sheet, assembly_guide, sendcutsend_vendor, or machine_shop_vendor.", "workshop_sheet", new[] { "workshop_sheet", "assembly_guide", "sendcutsend_vendor", "machine_shop_vendor" }) },
                    { "sheet_size", JsonHelper.Prop("string", "Paper size & default .slddrt format.", "tabloid_11x17", new[] { "letter_8_5x11", "letter_portrait", "tabloid_11x17", "c_landscape", "d_landscape" }) },
                    { "sheet_format_path", JsonHelper.Prop("string", "Optional explicit .slddrt sheet format path (defaults to resolving 'b - landscape.slddrt' / 'a - landscape.slddrt' automatically).") },
                    { "configuration", JsonHelper.Prop("string", "Optional default model configuration name for Sheet 1 views.") },
                    { "display_mode", JsonHelper.Prop("string", "Default view display mode (shaded_with_edges uses SetDisplayMode3(false, swSHADED=3, false, true)).", "shaded_with_edges", new[] { "shaded_with_edges", "shaded", "hlr", "hlg", "wireframe" }) },
                    { "include_exploded_view", JsonHelper.Prop("boolean", "Add exploded isometric view if assembly.", true) },
                    { "include_flat_pattern", JsonHelper.Prop("boolean", "Add flat pattern view if sheet metal part.", false) },
                    { "sheets", JsonHelper.ArrayProp("object", "Optional multi-sheet step-by-step instruction specs: [{name, title, configuration, exploded, display_mode, views:[{view_name, source_model_path, configuration, exploded, display_mode, box:[xMin,yMin,xMax,yMax], box_units, position:[x,y], scale:[num,den], caption, balloon, only_balloon_components, exclude_balloon_components, link_to_bom}], bom:{...}, notes:[...], note_specs:[{text,x,y,font_height_mm,bold}], title_block:{...}}].") },
                    { "bom", JsonHelper.Prop("object", "Optional declarative BOM table spec for Sheet 1: {view_name, configuration, bom_type, anchor_type, position:[x,y], columns:[{type:'item'|'custom_property'|'part_number'|'quantity', property_name, title, width_mm}], link_all_views}.") },
                    { "only_balloon_components", JsonHelper.ArrayProp("string", "Optional selective/delta component list to balloon on the default sheet.") },
                    { "exclude_balloon_components", JsonHelper.ArrayProp("string", "Optional component list to exclude from ballooning on the default sheet.") },
                    { "title_block", JsonHelper.Prop("object", "Optional title-block fields applied during automatic EditTemplate() cleanup: {dwg_no, title, rev, material, finish, company, units_note, shop_note}.") },
                    { "save_drw_path", JsonHelper.Prop("string", "Optional .SLDDRW output path (defaults to <source_model_stem>.SLDDRW).") },
                    { "export_pdf", JsonHelper.Prop("boolean", "Automatically export a multi-sheet .PDF alongside the .SLDDRW (PDF documentation is exempt from the manufacturing artifact gate).", true) },
                    { "export_pdf_path", JsonHelper.Prop("string", "Optional .PDF output path (defaults to <save_drw_or_source_stem>.pdf when export_pdf is true).") }
                }),
                Handler = args => HandleCreateDrawing(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_insert_bom_table",
                Description = "Insert, customize, and canonically sort a SOLIDWORKS drawing BOM table declaratively (supports custom-property columns via SetColumnType3 + SetColumnCustomProperty, canonical sorting by OEM -> Manufactured [by technique, then size] -> Fasteners [Screws -> Bolts -> Washers -> Nuts], and cross-sheet view linking via SetKeepLinkedToBOM).",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "target_doc", JsonHelper.Prop("string", "Optional drawing (.SLDDRW) title/path.") },
                    { "sheet_name", JsonHelper.Prop("string", "Optional target sheet name (defaults to current sheet).") },
                    { "view_name", JsonHelper.Prop("string", "Optional drawing view name to attach the BOM table to (defaults to first model view on the sheet).") },
                    { "configuration", JsonHelper.Prop("string", "Optional model configuration name (defaults to anchor view's ReferencedConfiguration).") },
                    { "bom_type", JsonHelper.Prop("string", "BOM hierarchy type.", "top_level_only", new[] { "top_level_only", "parts_only", "indented" }) },
                    { "anchor_type", JsonHelper.Prop("string", "Corner anchor type.", "top_right", new[] { "top_right", "top_left", "bottom_right", "bottom_left" }) },
                    { "position", JsonHelper.ArrayProp("number", "Optional [x, y] placement coordinates (normalized 0..1 sheet fraction if <=1.0, or in meters).") },
                    { "table_template_path", JsonHelper.Prop("string", "Optional .sldbomtbt path (defaults to resolving bom-standard.sldbomtbt).") },
                    { "columns", JsonHelper.ArrayProp("object", "Declarative BOM column specs: [{type:'item'|'custom_property'|'part_number'|'quantity', property_name:'BOM_Desc', title:'COMPONENT / DESCRIPTION', width_mm:75}].") },
                    { "sort_canonical", JsonHelper.Prop("boolean", "Automatically sort BOM rows by OEM parts -> Manufactured parts (by manufacturing technique, then size) -> Fasteners (Threaded Inserts -> Screws -> Bolts -> Washers -> Nuts) and renumber Item 1..N.", true) },
                    { "link_all_views", JsonHelper.Prop("boolean", "Link every drawing view across all sheets to this BOM via IView.SetKeepLinkedToBOM.", true) },
                    { "link_views", JsonHelper.ArrayProp("string", "Optional specific view names to link to this BOM via IView.SetKeepLinkedToBOM.") },
                    { "export_pdf", JsonHelper.Prop("boolean", "Automatically re-export the drawing .PDF after inserting/updating the BOM.", true) },
                    { "export_pdf_path", JsonHelper.Prop("string", "Optional .PDF output path.") }
                }),
                Handler = args => HandleInsertBomTable(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_add_drawing_callouts_and_bom",
                Description = "Add shop notes, positioned callouts, declarative BOM tables, selective/delta balloons (with InsertBOMBalloon2 fallback), bend tables, or title-block cleanup onto a SOLIDWORKS drawing and auto-export PDF.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "sheet_name", JsonHelper.Prop("string", "Optional sheet name to activate first.") },
                    { "shop_notes", JsonHelper.ArrayProp("string", "Text notes to place on the drawing sheet (e.g. Domino/Forstner/clamp/material/step specs).") },
                    { "note_specs", JsonHelper.ArrayProp("object", "Positioned text notes: [{text, x, y, units, font_height_mm, bold}].") },
                    { "bom", JsonHelper.Prop("object", "Optional declarative BOM table spec (same schema as sw_insert_bom_table).") },
                    { "balloon_views", JsonHelper.ArrayProp("object", "Optional selective/auto balloon specs: [{view_name, only_balloon_components:[...], exclude_balloon_components:[...], layout:'square'|'circular'|'top'|'bottom'|'left'|'right', insert_magnetic_line:false}].") },
                    { "title_block", JsonHelper.Prop("object", "Optional title-block cleanup & fields: {dwg_no, title, rev, material, finish, company, units_note, shop_note}.") },
                    { "insert_bend_table", JsonHelper.Prop("boolean", "Insert bend table on flat-pattern view.", false) },
                    { "export_pdf", JsonHelper.Prop("boolean", "Automatically export/update the drawing .PDF (exempt from manufacturing gate).", true) },
                    { "export_pdf_path", JsonHelper.Prop("string", "Optional .PDF output path.") },
                    { "target_doc", JsonHelper.Prop("string", "Optional drawing title/path.") }
                }),
                Handler = args => HandleAddDrawingCallouts(swMgr, args)
            });
        }

        internal static string ResolveHardwareLibraryDir(IModelDoc2 doc, string requestedDir)
        {
            if (!string.IsNullOrWhiteSpace(requestedDir))
                return requestedDir;

            string docDir = doc != null ? Path.GetDirectoryName(doc.GetPathName() ?? string.Empty) : string.Empty;
            if (!string.IsNullOrWhiteSpace(docDir))
                return Path.Combine(docDir, "Hardware");

            return Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), "SolidWorks_McMaster_Hardware");
        }

        private static List<McMasterCatalogItem> QueryLocalCatalog(string query, string nutStyleHint = null)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<McMasterCatalogItem>();

            string q = query.ToUpperInvariant();
            string thread = null;
            foreach (string t in new[] { "1/4-20", "1/4", "M2.5", "M3", "M4", "M5", "M6", "M8" })
            {
                if (q.Contains(t))
                {
                    thread = t;
                    break;
                }
            }
            string kind = null;
            if (!string.IsNullOrWhiteSpace(nutStyleHint))
            {
                string ns = nutStyleHint.Trim().ToLowerInvariant();
                if (ns == "jam_nut" || ns == "jam_nut_stack") kind = "jam_nut";
                else if (ns == "hex_nut") kind = "hex_nut";
                else if (ns == "nylock") kind = "nylock_nut";
            }
            if (kind == null)
            {
                if (q.Contains("JAM")) kind = "jam_nut";
                else if (q.Contains("NYLOCK") || q.Contains("NYLON-INSERT") || q.Contains("LOCKNUT")) kind = "nylock_nut";
                else if (q.Contains("WASHER")) kind = "flat_washer";
                else if (q.Contains("HEX") && q.Contains("NUT")) kind = "hex_nut";
                else if (q.Contains("NUT")) kind = "nylock_nut";
                else if (q.Contains("BHCS") || q.Contains("BUTTON")) kind = "bhcs";
                else if (q.Contains("CSK") || q.Contains("FLAT HEAD") || q.Contains("COUNTERSUNK")) kind = "csk";
                else if (q.Contains("SHCS") || q.Contains("SOCKET")) kind = "shcs";
                else if (q.Contains("HEX")) kind = "hex_bolt";
            }

            double lenMm = 0;
            Match mMm = Regex.Match(q, @"[X\s](\d+(?:\.\d+)?)\s*MM");
            if (mMm.Success)
                double.TryParse(mMm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out lenMm);

            return AssemblyGeometryHelper.Search18_8Catalog(thread, kind, lenMm);
        }

        private static bool QueryMentionsOtherMaterial(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return false;
            string q = query.ToUpperInvariant();
            foreach (string kw in new[] { "316", "BLACK", "OXIDE", "ALLOY", "ZINC", "BRASS", "BRONZE", "NYLON", "PEEK", "TITANIUM", "ALUMINUM", "CHROME" })
            {
                if (q.Contains(kw)) return true;
            }
            return false;
        }

        private static bool IsExpectedKindMatch(string expectedKind, string actualPartKind)
        {
            if (string.IsNullOrWhiteSpace(expectedKind) || string.IsNullOrWhiteSpace(actualPartKind))
                return true;
            string e = expectedKind.Trim().ToLowerInvariant();
            string a = actualPartKind.Trim().ToLowerInvariant();
            if (e == "csk" || e == "csk_screw" || e == "flat_head")
                return a == "csk_screw";
            if (e == "washer" || e == "flat_washer")
                return a == "washer";
            if (e == "nut" || e == "nylock" || e == "nylock_nut" || e == "hex_nut" || e == "jam" || e == "jam_nut")
                return a == "nut";
            if (e == "shcs" || e == "bhcs" || e == "hex_bolt" || e == "bolt" || e == "bolt_or_screw" || e == "screw")
                return a == "bolt_or_screw" || a == "csk_screw";
            return string.Equals(e, a, StringComparison.OrdinalIgnoreCase);
        }

        internal static Dictionary<string, object> ResolveDownloadAndVerifySingle(
            ISldWorks app,
            string pn,
            string query,
            string libDir,
            bool downloadIfMissing,
            bool prepareAxis,
            double expectedLengthMm,
            string expectedKind,
            out bool ok,
            out Dictionary<string, object> bridgeResult)
        {
            ok = false;
            bridgeResult = null;
            string cleanPn = (pn ?? string.Empty).Trim().ToUpperInvariant();
            string partPath = AssemblyGeometryHelper.FindLocalMcMasterPart(cleanPn, libDir);
            string source = "local_cache";

            if (string.IsNullOrEmpty(partPath) && downloadIfMissing)
            {
                bridgeResult = AssemblyGeometryHelper.InvokeMcMasterBridge(app, "download", cleanPn, query, libDir, 25000);
                if (JsonHelper.GetBool(bridgeResult, "ok", false))
                {
                    partPath = JsonHelper.GetString(bridgeResult, "part_path");
                    source = JsonHelper.GetString(bridgeResult, "source", "mcmaster_addin_download");
                }
            }

            if (string.IsNullOrEmpty(partPath) || !File.Exists(partPath))
            {
                return new Dictionary<string, object>
                {
                    { "mcmaster_pn", cleanPn },
                    { "ok", false },
                    { "part_path", null },
                    { "error", string.Format("Genuine McMaster-Carr .SLDPRT for '{0}' was not found in '{1}' and could not be auto-downloaded via the McMaster SOLIDWORKS Add-in.", cleanPn, libDir) },
                    { "mcmaster_addin_response", bridgeResult }
                };
            }

            FastenerPartProfile profile = prepareAxis ? AssemblyGeometryHelper.EnsureFastenerAxisAndInspect(app, partPath) : null;
            McMasterCatalogItem catItem = AssemblyGeometryHelper.FindCatalogItemByPartNumber(cleanPn);
            double targetLen = expectedLengthMm > 0.0 ? expectedLengthMm : (catItem != null ? catItem.NominalLengthMm : 0.0);

            bool verified = true;
            List<string> warnings = new List<string>();
            if (profile != null && targetLen > 0.0 && profile.MeasuredShankLengthMm > 0.0)
            {
                if (Math.Abs(profile.MeasuredShankLengthMm - targetLen) > 1.0)
                {
                    verified = false;
                    warnings.Add(string.Format(CultureInfo.InvariantCulture,
                        "Measured shank length {0:F2}mm differs from expected {1:F2}mm — verify McMaster SKU '{2}'!",
                        profile.MeasuredShankLengthMm, targetLen, cleanPn));
                }
            }
            if (profile != null && !string.IsNullOrWhiteSpace(expectedKind) && !IsExpectedKindMatch(expectedKind, profile.PartKind))
            {
                verified = false;
                warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "Measured part kind '{0}' differs from expected '{1}' for SKU '{2}'.",
                    profile.PartKind, expectedKind, cleanPn));
            }

            string warningText = warnings.Count > 0 ? string.Join(" ", warnings) : null;
            Dictionary<string, object> verification = new Dictionary<string, object>
            {
                { "verified", verified },
                { "expected_length_mm", targetLen > 0 ? (object)Math.Round(targetLen, 2) : null },
                { "measured_shank_length_mm", profile != null ? (object)profile.MeasuredShankLengthMm : null },
                { "measured_head_height_mm", profile != null ? (object)profile.MeasuredHeadHeightMm : null },
                { "measured_thickness_mm", profile != null ? (object)profile.MeasuredThicknessMm : null },
                { "expected_kind", !string.IsNullOrWhiteSpace(expectedKind) ? expectedKind : null },
                { "measured_part_kind", profile != null ? profile.PartKind : null },
                { "warning", warningText }
            };

            ok = true;
            return new Dictionary<string, object>
            {
                { "mcmaster_pn", cleanPn },
                { "ok", true },
                { "part_path", partPath },
                { "source", source },
                { "bom_regex_compatible", McMasterRegex.IsMatch(Path.GetFileNameWithoutExtension(partPath)) },
                { "fastener_profile", profile },
                { "geometry_verification", verification }
            };
        }

        private static McpToolResult HandleSearchAndDownloadMcMaster(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            ISldWorks app = swMgr.GetApp();
            IModelDoc2 doc = null;
            try { doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc")); } catch { }

            string pn = (JsonHelper.GetString(args, "mcmaster_pn") ?? string.Empty).Trim().ToUpperInvariant();
            List<string> batchPns = JsonHelper.GetStringList(args, "mcmaster_pns");
            List<object> batchSpecs = JsonHelper.GetList(args, "specs");
            double singleExpectedLenMm = JsonHelper.GetDouble(args, "expected_length_mm", 0.0);
            string query = JsonHelper.GetString(args, "query", string.Empty);
            string material = JsonHelper.GetString(args, "material", "18-8 Stainless Steel");
            string nutStyle = JsonHelper.GetString(args, "nut_style", string.Empty);
            string libDir = ResolveHardwareLibraryDir(doc, JsonHelper.GetString(args, "library_dir"));
            bool downloadIfMissing = JsonHelper.GetBool(args, "download_if_missing", true);
            bool prepareAxis = JsonHelper.GetBool(args, "prepare_fastener_axis", true);
            Directory.CreateDirectory(libDir);

            // Batch mode: specs or mcmaster_pns
            if (batchSpecs.Count > 0 || batchPns.Count > 0)
            {
                List<Dictionary<string, object>> workItems = new List<Dictionary<string, object>>();
                HashSet<string> seenPns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (object specObj in batchSpecs)
                {
                    Dictionary<string, object> sd = specObj as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(specObj));
                    string sPn = (JsonHelper.GetString(sd, "mcmaster_pn") ?? string.Empty).Trim().ToUpperInvariant();
                    if (string.IsNullOrEmpty(sPn) || !seenPns.Add(sPn)) continue;
                    workItems.Add(new Dictionary<string, object>
                    {
                        { "pn", sPn },
                        { "len", JsonHelper.GetDouble(sd, "expected_length_mm", 0.0) },
                        { "kind", JsonHelper.GetString(sd, "expected_kind", "") }
                    });
                }
                foreach (string bPn in batchPns)
                {
                    string sPn = (bPn ?? string.Empty).Trim().ToUpperInvariant();
                    if (string.IsNullOrEmpty(sPn) || !seenPns.Add(sPn)) continue;
                    workItems.Add(new Dictionary<string, object>
                    {
                        { "pn", sPn },
                        { "len", 0.0 },
                        { "kind", "" }
                    });
                }

                List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
                int resolvedCount = 0;
                int failedCount = 0;
                int warningCount = 0;
                foreach (Dictionary<string, object> w in workItems)
                {
                    string wPn = JsonHelper.GetString(w, "pn");
                    double wLen = JsonHelper.GetDouble(w, "len", 0.0);
                    string wKind = JsonHelper.GetString(w, "kind", "");
                    Dictionary<string, object> itemRes = ResolveDownloadAndVerifySingle(
                        app, wPn, query, libDir, downloadIfMissing, prepareAxis, wLen, wKind, out bool itemOk, out _);
                    results.Add(itemRes);
                    if (itemOk)
                    {
                        resolvedCount++;
                        Dictionary<string, object> gv = JsonHelper.GetDict(itemRes, "geometry_verification");
                        if (gv != null && !JsonHelper.GetBool(gv, "verified", true))
                            warningCount++;
                    }
                    else
                    {
                        failedCount++;
                    }
                }

                if (resolvedCount == 0 && workItems.Count > 0)
                {
                    return McpToolResult.Error(
                        string.Format("All {0} requested McMaster-Carr downloads failed in '{1}'.", workItems.Count, libDir),
                        new
                        {
                            library_dir = libDir,
                            total_requested = workItems.Count,
                            resolved_count = resolvedCount,
                            failed_count = failedCount,
                            warning_count = warningCount,
                            results = results
                        });
                }

                return McpToolResult.JsonText(new
                {
                    library_dir = libDir,
                    total_requested = workItems.Count,
                    resolved_count = resolvedCount,
                    failed_count = failedCount,
                    warning_count = warningCount,
                    results = results
                });
            }

            bool use18_8Catalog = AssemblyGeometryHelper.IsMaterial18_8(material) && !QueryMentionsOtherMaterial(query);
            List<McMasterCatalogItem> catalogMatches = use18_8Catalog
                ? QueryLocalCatalog(string.IsNullOrWhiteSpace(query) ? pn : query, nutStyle)
                : new List<McMasterCatalogItem>();

            if (string.IsNullOrEmpty(pn) && use18_8Catalog && catalogMatches.Count == 1)
                pn = catalogMatches[0].PartNumber;

            // Search mode (no explicit PN resolved yet)
            if (string.IsNullOrEmpty(pn))
            {
                string searchTerm = query ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(material) &&
                    searchTerm.IndexOf(material, StringComparison.OrdinalIgnoreCase) < 0 &&
                    !QueryMentionsOtherMaterial(searchTerm))
                {
                    searchTerm = (material + " " + searchTerm).Trim();
                }

                Dictionary<string, object> bridgeSearch = AssemblyGeometryHelper.InvokeMcMasterBridge(app, "search", "", searchTerm, libDir, 12000);
                List<object> cachedCatalog = new List<object>();
                foreach (McMasterCatalogItem c in catalogMatches)
                {
                    string cached = AssemblyGeometryHelper.FindLocalMcMasterPart(c.PartNumber, libDir);
                    cachedCatalog.Add(new
                    {
                        part_number = c.PartNumber,
                        material = c.Material,
                        thread_spec = c.ThreadSpec,
                        kind = c.Kind,
                        length_mm = c.NominalLengthMm,
                        description = c.Description,
                        cached_path = cached
                    });
                }
                return McpToolResult.JsonText(new
                {
                    query = searchTerm,
                    material = material,
                    library_dir = libDir,
                    catalog_matches_18_8 = cachedCatalog,
                    mcmaster_addin_search = bridgeSearch
                });
            }

            // Explicit single PN: check local cache first, then invoke McMaster SOLIDWORKS Add-in bridge
            Dictionary<string, object> singleRes = ResolveDownloadAndVerifySingle(
                app, pn, query, libDir, downloadIfMissing, prepareAxis, singleExpectedLenMm, "", out bool singleOk, out Dictionary<string, object> bridgeResult);

            if (!singleOk)
            {
                return McpToolResult.Error(
                    string.Format("Genuine McMaster-Carr .SLDPRT for '{0}' was not found in '{1}' and could not be auto-downloaded via the McMaster SOLIDWORKS Add-in.", pn, libDir),
                    new
                    {
                        mcmaster_pn = pn,
                        library_dir = libDir,
                        mcmaster_addin_response = bridgeResult,
                        catalog_matches_18_8 = catalogMatches
                    });
            }

            return McpToolResult.JsonText(new
            {
                mcmaster_pn = singleRes["mcmaster_pn"],
                part_path = singleRes["part_path"],
                source = singleRes["source"],
                bom_regex_compatible = singleRes["bom_regex_compatible"],
                fastener_profile = singleRes["fastener_profile"],
                geometry_verification = singleRes["geometry_verification"],
                catalog_matches_18_8 = catalogMatches
            });
        }

        private class ResolvedMcMasterItem
        {
            public Dictionary<string, object> Args;
            public string Pn;
            public string PartPath;
            public bool Generated;
            public FastenerPartProfile Profile;
            public string Error;
            public Dictionary<string, object> AddinAttempt;
            public string LibDir;
        }

        private static ResolvedMcMasterItem ResolveMcMasterPartItem(
            ISldWorks app,
            IModelDoc2 doc,
            Dictionary<string, object> args,
            string defaultLibDir)
        {
            string pn = JsonHelper.GetString(args, "mcmaster_pn", "").Trim().ToUpperInvariant();
            string desc = Regex.Replace(JsonHelper.GetString(args, "description", "Hardware"), @"[^\w\-]+", "_");
            string explicitPartPath = JsonHelper.GetString(args, "part_path");
            bool allowPlaceholder = JsonHelper.GetBool(args, "allow_placeholder_generation", false);
            double diaMm = JsonHelper.GetDouble(args, "nominal_diameter_mm", 5.0);
            double lenMm = JsonHelper.GetDouble(args, "nominal_length_mm", 10.0);
            string libDir = args.ContainsKey("library_dir")
                ? ResolveHardwareLibraryDir(doc, JsonHelper.GetString(args, "library_dir"))
                : defaultLibDir;
            Directory.CreateDirectory(libDir);

            ResolvedMcMasterItem res = new ResolvedMcMasterItem
            {
                Args = args,
                Pn = pn,
                LibDir = libDir
            };

            if (string.IsNullOrWhiteSpace(pn) && (string.IsNullOrWhiteSpace(explicitPartPath) || !File.Exists(explicitPartPath)))
            {
                res.Error = "mcmaster_pn (or an existing part_path) is required.";
                return res;
            }

            string partPath = (!string.IsNullOrWhiteSpace(explicitPartPath) && File.Exists(explicitPartPath))
                ? explicitPartPath
                : AssemblyGeometryHelper.FindLocalMcMasterPart(pn, libDir);

            Dictionary<string, object> addinAttempt = null;
            if (string.IsNullOrWhiteSpace(partPath) && !string.IsNullOrWhiteSpace(pn))
            {
                addinAttempt = AssemblyGeometryHelper.InvokeMcMasterBridge(app, "download", pn, desc, libDir, 25000);
                if (JsonHelper.GetBool(addinAttempt, "ok", false))
                    partPath = JsonHelper.GetString(addinAttempt, "part_path");
            }
            res.AddinAttempt = addinAttempt;

            if (string.IsNullOrWhiteSpace(partPath))
            {
                if (!allowPlaceholder)
                {
                    res.Error = string.Format("Genuine McMaster-Carr file for {0} was not found locally or via the McMaster SOLIDWORKS Add-in. Placeholder generation is disabled (allow_placeholder_generation=false); download '{0}' via the McMaster Add-in into '{1}' or obtain user approval before generating placeholder shapes.", pn, libDir);
                    return res;
                }

                partPath = Path.Combine(libDir, string.Format("{0}_{1}.SLDPRT", pn, desc));
                string tpl = SwConnectionManager.ResolveDefaultTemplate(app, "part");
                app.DocumentVisible(false, (int)swDocumentTypes_e.swDocPART);
                try
                {
                    IModelDoc2 hwDoc = app.NewDocument(tpl, (int)swDwgPaperSizes_e.swDwgPaperBsize, 0, 0) as IModelDoc2;
                    if (hwDoc == null)
                        throw new InvalidOperationException("NewDocument returned null for template: " + tpl);
                    try
                    {
                        SwConnectionManager.SetDocumentUnitSystem(hwDoc, "MMGS");
                        IModeler modeler = app.GetModeler() as IModeler;
                        if (modeler == null)
                            throw new InvalidOperationException("Failed to obtain SOLIDWORKS IModeler.");
                        IBody2 cyl = modeler.CreateBodyFromCyl(new double[]
                        {
                            0.0, 0.0, 0.0,
                            0.0, 1.0, 0.0,
                            0.5 * diaMm * 0.001,
                            lenMm * 0.001
                        }) as IBody2;
                        if (cyl == null)
                            throw new InvalidOperationException("IModeler.CreateBodyFromCyl returned null.");
                        ((IPartDoc)hwDoc).CreateFeatureFromBody3(cyl, false, (int)swCreateFeatureBodyOpts_e.swCreateFeatureBodyCheck);

                        ICustomPropertyManager cpm = hwDoc.Extension.get_CustomPropertyManager("");
                        cpm.Add3("PartNumber", (int)swCustomInfoType_e.swCustomInfoText, pn, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                        cpm.Add3("Description", (int)swCustomInfoType_e.swCustomInfoText, desc.Replace('_', ' '), (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                        cpm.Add3("Vendor", (int)swCustomInfoType_e.swCustomInfoText, "McMaster-Carr", (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);

                        int errs = 0, warns = 0;
                        hwDoc.Extension.SaveAs3(partPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errs, ref warns);
                    }
                    finally
                    {
                        app.CloseDoc(hwDoc.GetTitle());
                    }
                }
                finally
                {
                    try { app.DocumentVisible(true, (int)swDocumentTypes_e.swDocPART); } catch { }
                }
                res.Generated = true;
            }

            res.PartPath = partPath;
            res.Profile = AssemblyGeometryHelper.EnsureFastenerAxisAndInspect(app, partPath);
            return res;
        }

        private static Dictionary<string, object> PopulateResolvedMcMasterItem(
            IModelDoc2 doc,
            IAssemblyDoc assy,
            IMathUtility mathUtil,
            ResolvedMcMasterItem item,
            string defaultUnits,
            IList<AssemblyGeometryHelper.CachedCandidateComponent> candidateCache)
        {
            Dictionary<string, object> args = item.Args;
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units", defaultUnits));
            List<string> holeTokens = JsonHelper.GetStringList(args, "hole_face_or_edge_tokens");
            List<object> placements = JsonHelper.GetList(args, "placements");
            bool createMates = JsonHelper.GetBool(args, "create_mates", true);
            bool lockRotGlobal = JsonHelper.GetBool(args, "lock_rotation_or_clock_flat", false);
            string folderName = JsonHelper.GetString(args, "folder_name", "McMaster Hardware");

            FastenerPartProfile profile = item.Profile;
            string cfgName = profile != null && !string.IsNullOrEmpty(profile.ActiveConfig) ? profile.ActiveConfig : "";
            string axisFeatName = profile != null && !string.IsNullOrEmpty(profile.AxisFeatureName) ? profile.AxisFeatureName : "Fastener_Axis";

            int insertCount = placements.Count > 0 ? placements.Count : Math.Max(1, holeTokens.Count);
            List<IComponent2> insertedComps = new List<IComponent2>();
            List<string> insertedNames = new List<string>();
            List<object> mateResults = new List<object>();

            for (int i = 0; i < insertCount; i++)
            {
                Dictionary<string, object> pDict = placements.Count > i
                    ? (placements[i] as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(placements[i])))
                    : null;

                double[] posArr = pDict != null ? JsonHelper.GetDoubleArray(pDict, "position") : null;
                double[] targetPosM = posArr != null && posArr.Length >= 3
                    ? new[]
                    {
                        ShopCatalogService.ToMeters(posArr[0], units),
                        ShopCatalogService.ToMeters(posArr[1], units),
                        ShopCatalogService.ToMeters(posArr[2], units)
                    }
                    : new[] { i * 0.02, 0.0, 0.0 };

                IComponent2 comp = assy.AddComponent5(
                    item.PartPath,
                    (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                    "",
                    !string.IsNullOrEmpty(cfgName),
                    cfgName,
                    targetPosM[0],
                    targetPosM[1],
                    targetPosM[2]);

                if (comp == null) continue;
                insertedComps.Add(comp);
                insertedNames.Add(comp.Name2);

                double clockDeg = pDict != null ? JsonHelper.GetDouble(pDict, "clock_angle_deg", 0.0) : 0.0;
                double[] targetAxis = pDict != null ? JsonHelper.GetDoubleArray(pDict, "axis") : null;
                if (targetAxis != null && targetAxis.Length >= 3 && profile != null)
                {
                    double[] localAxis = JsonHelper.GetDoubleArray(pDict, "local_axis");
                    if (localAxis == null || localAxis.Length < 3) localAxis = profile.LocalAxis;
                    double bearingOffM = pDict.ContainsKey("local_bearing_offset_mm")
                        ? JsonHelper.GetDouble(pDict, "local_bearing_offset_mm", profile.LocalBearingOffsetMm) * 0.001
                        : profile.LocalBearingOffsetMm * 0.001;

                    MathTransform xf = AssemblyGeometryHelper.BuildTransformFromPose(
                        mathUtil, targetPosM, targetAxis, localAxis, bearingOffM, clockDeg, profile.HexFlatLocalAngleDeg);
                    if (xf != null)
                        comp.Transform2 = xf;
                }

                int matesForComp = 0;
                if (createMates)
                {
                    string holeToken = pDict != null ? JsonHelper.GetString(pDict, "hole_token") : (holeTokens.Count > i ? holeTokens[i] : null);
                    string bearingTarget = pDict != null ? JsonHelper.GetString(pDict, "bearing_face_token") : null;
                    bool flipMate = pDict != null && JsonHelper.GetBool(pDict, "flip_mate", false);
                    bool lockRot = lockRotGlobal || (pDict != null && JsonHelper.GetBool(pDict, "lock_rotation", false)) || Math.Abs(clockDeg) > 0.01;

                    bool hasConc = false, hasCoin = false;
                    if (!string.IsNullOrWhiteSpace(holeToken))
                    {
                        string compAxisSel = "comp:" + comp.Name2 + "::" + axisFeatName;
                        int mErr;
                        IMate2 mConc = AssemblyGeometryHelper.CreateMateBySelectors(
                            doc, assy, compAxisSel, holeToken,
                            (int)swMateType_e.swMateCONCENTRIC,
                            (int)swMateAlign_e.swMateAlignCLOSEST,
                            flipMate, 0, 0, lockRot, out mErr);
                        if (mConc != null)
                        {
                            hasConc = true;
                            matesForComp++;
                            mateResults.Add(new { component = comp.Name2, type = "concentric", lock_rotation = lockRot, target = holeToken });
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(bearingTarget) && profile != null && !string.IsNullOrEmpty(profile.BearingFaceToken))
                    {
                        string compFaceSel = "comp:" + comp.Name2 + "::" + profile.BearingFaceToken;
                        int mErr;
                        IMate2 mCoin = AssemblyGeometryHelper.CreateMateBySelectors(
                            doc, assy, compFaceSel, bearingTarget,
                            (int)swMateType_e.swMateCOINCIDENT,
                            (int)swMateAlign_e.swMateAlignCLOSEST,
                            flipMate, 0, 0, false, out mErr);
                        if (mCoin != null)
                        {
                            hasCoin = true;
                            matesForComp++;
                            mateResults.Add(new { component = comp.Name2, type = "coincident", target = bearingTarget });
                        }
                    }

                    if (!hasConc || !hasCoin)
                    {
                        double[] tAxis = pDict != null ? JsonHelper.GetDoubleArray(pDict, "axis") : null;
                        double bOffM = profile != null ? profile.LocalBearingOffsetMm * 0.001 : 0.0;
                        if (pDict != null && pDict.ContainsKey("local_bearing_offset_mm"))
                            bOffM = JsonHelper.GetDouble(pDict, "local_bearing_offset_mm", 0.0) * 0.001;

                        int cErr, coErr;
                        AssemblyGeometryHelper.AutoMateFastenerAtPose(
                            doc, assy, mathUtil, comp, targetPosM, tAxis, bOffM, lockRot, out cErr, out coErr, !hasConc, !hasCoin, candidateCache);
                        if (cErr == (int)swAddMateError_e.swAddMateError_NoError)
                        {
                            matesForComp++;
                            mateResults.Add(new { component = comp.Name2, type = "concentric_auto", lock_rotation = lockRot });
                        }
                        if (coErr == (int)swAddMateError_e.swAddMateError_NoError)
                        {
                            matesForComp++;
                            mateResults.Add(new { component = comp.Name2, type = "coincident_auto" });
                        }
                    }
                }

                if (matesForComp == 0)
                {
                    doc.ClearSelection2(true);
                    comp.Select4(false, null, false);
                    assy.FixComponent();
                    doc.ClearSelection2(true);
                }
            }

            int grouped = !string.IsNullOrWhiteSpace(folderName)
                ? AssemblyGeometryHelper.MoveComponentsToFolder(doc, assy, insertedComps, folderName)
                : 0;

            return new Dictionary<string, object>
            {
                { "mcmaster_pn", item.Pn },
                { "part_path", item.PartPath },
                { "active_config", cfgName },
                { "fastener_axis_feature", axisFeatName },
                { "axis_created", profile != null && profile.AxisCreated },
                { "generated_placeholder", item.Generated },
                { "inserted_components", insertedNames },
                { "mates_created", mateResults },
                { "grouped_in_folder", folderName },
                { "grouped_count", grouped },
                { "bom_regex_compatible", McMasterRegex.IsMatch(Path.GetFileNameWithoutExtension(item.PartPath)) }
            };
        }

        private static McpToolResult HandlePopulateMcMaster(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            if (!(doc is IAssemblyDoc assy))
                return McpToolResult.Error("sw_populate_mcmaster_hardware requires an active Assembly (.SLDASM) document.");

            ISldWorks app = swMgr.GetApp();
            IMathUtility mathUtil = app.GetMathUtility() as IMathUtility;
            string defaultUnits = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            string defaultLibDir = ResolveHardwareLibraryDir(doc, JsonHelper.GetString(args, "library_dir"));
            List<object> batchItems = JsonHelper.GetList(args, "items");

            if (batchItems.Count > 0)
            {
                List<ResolvedMcMasterItem> resolvedItems = new List<ResolvedMcMasterItem>();
                foreach (object itemObj in batchItems)
                {
                    Dictionary<string, object> itemArgs = itemObj as Dictionary<string, object>
                        ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(itemObj));
                    if (itemArgs == null) continue;
                    if (!itemArgs.ContainsKey("folder_name") && args.ContainsKey("folder_name"))
                        itemArgs["folder_name"] = args["folder_name"];
                    if (!itemArgs.ContainsKey("create_mates") && args.ContainsKey("create_mates"))
                        itemArgs["create_mates"] = args["create_mates"];
                    if (!itemArgs.ContainsKey("allow_placeholder_generation") && args.ContainsKey("allow_placeholder_generation"))
                        itemArgs["allow_placeholder_generation"] = args["allow_placeholder_generation"];
                    resolvedItems.Add(ResolveMcMasterPartItem(app, doc, itemArgs, defaultLibDir));
                }

                SwConnectionManager.ActivateDocumentSafe(app, doc);
                IDisposable uiScope = SwHelper.SuppressUiUpdates(doc);
                try
                {
                    IList<AssemblyGeometryHelper.CachedCandidateComponent> sharedCache = AssemblyGeometryHelper.BuildCandidateComponentCache(assy);
                    List<object> itemOut = new List<object>();
                    bool anyError = false;
                    foreach (ResolvedMcMasterItem ri in resolvedItems)
                    {
                        if (!string.IsNullOrEmpty(ri.Error))
                        {
                            anyError = true;
                            itemOut.Add(new { ok = false, mcmaster_pn = ri.Pn, error = ri.Error, mcmaster_addin_response = ri.AddinAttempt });
                            continue;
                        }
                        Dictionary<string, object> pop = PopulateResolvedMcMasterItem(doc, assy, mathUtil, ri, defaultUnits, sharedCache);
                        pop["ok"] = true;
                        itemOut.Add(pop);
                    }

                    uiScope.Dispose();
                    doc.EditRebuild3();

                    return McpToolResult.JsonText(new
                    {
                        batch_count = itemOut.Count,
                        items = itemOut,
                        mate_health = AssemblyMateHealthAuditor.BuildCompactSummary(doc)
                    }, isError: anyError);
                }
                finally
                {
                    uiScope.Dispose();
                }
            }

            ResolvedMcMasterItem singleItem = ResolveMcMasterPartItem(app, doc, args, defaultLibDir);
            if (!string.IsNullOrEmpty(singleItem.Error))
            {
                return McpToolResult.Error(
                    singleItem.Error,
                    new { mcmaster_pn = singleItem.Pn, library_dir = singleItem.LibDir, mcmaster_addin_response = singleItem.AddinAttempt });
            }

            SwConnectionManager.ActivateDocumentSafe(app, doc);
            IDisposable singleScope = SwHelper.SuppressUiUpdates(doc);
            try
            {
                IList<AssemblyGeometryHelper.CachedCandidateComponent> cache = AssemblyGeometryHelper.BuildCandidateComponentCache(assy);
                Dictionary<string, object> res = PopulateResolvedMcMasterItem(doc, assy, mathUtil, singleItem, defaultUnits, cache);
                singleScope.Dispose();
                doc.EditRebuild3();
                res["mate_health"] = AssemblyMateHealthAuditor.BuildCompactSummary(doc);
                return McpToolResult.JsonText(res);
            }
            finally
            {
                singleScope.Dispose();
            }
        }

        public class BomItemClassification
        {
            public int MajorGroup; // 1 = OEM, 2 = Manufactured, 3 = Fastener
            public string Category; // "oem", "manufactured", "fastener"
            public int TechniqueRank; // For manufactured: 1=woodworking, 2=3d_printing, 3=cnc_carving, 4=sheet_metal_sendcutsend, 5=custom_fabricated
            public string ManufacturingTechnique;
            public int FastenerSubRank; // For fastener: 1=screw, 2=bolt, 3=washer, 4=nut
            public string FastenerSubType;
            public double ThreadDiameterMm;
            public double FastenerLengthMm;
            public double VolumeMm3;
            public double MaxDimensionMm;
            public string SortKey;
        }

        private static string NormalizeWordBoundaryText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return " ";
            char[] buf = new char[raw.Length + 2];
            buf[0] = ' ';
            for (int i = 0; i < raw.Length; i++)
            {
                char c = char.ToUpperInvariant(raw[i]);
                buf[i + 1] = char.IsLetterOrDigit(c) ? c : ' ';
            }
            buf[raw.Length + 1] = ' ';
            return Regex.Replace(new string(buf), @"\s+", " ");
        }

        private static bool ContainsAnyKeyword(string haystackUpper, params string[] keywords)
        {
            if (string.IsNullOrEmpty(haystackUpper) || keywords == null)
                return false;
            string normHay = NormalizeWordBoundaryText(haystackUpper);
            foreach (string kw in keywords)
            {
                if (string.IsNullOrWhiteSpace(kw)) continue;
                string normKw = NormalizeWordBoundaryText(kw).Trim();
                if (string.IsNullOrEmpty(normKw)) continue;
                if (normHay.IndexOf(" " + normKw + " ", StringComparison.Ordinal) >= 0)
                    return true;
                // Allow prefix match for 6+ char McMaster series prefixes like "91292A"
                if (normKw.Length >= 6 && char.IsDigit(normKw[0]) && normHay.IndexOf(" " + normKw, StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        public static void ParseFastenerThreadAndLengthMm(string text, out double threadDiaMm, out double lengthMm)
        {
            threadDiaMm = 999.0;
            lengthMm = 9999.0;
            if (string.IsNullOrWhiteSpace(text))
                return;

            string u = text.ToUpperInvariant();

            // Metric thread: e.g. M2, M2.5, M3, M4, M5, M6, M8, M10, M12 (optionally followed by x16 or x 16mm)
            Match mMetric = Regex.Match(u, @"\bM(\d+(?:\.\d+)?)(?:\s*[X×\-]\s*(\d+(?:\.\d+)?))?");
            if (mMetric.Success)
            {
                if (double.TryParse(mMetric.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d) && d >= 1.0 && d <= 64.0)
                    threadDiaMm = d;
                if (mMetric.Groups[2].Success && double.TryParse(mMetric.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double l) && l > 0.5)
                    lengthMm = l;
            }
            else
            {
                // Imperial number threads (#2-56, #4-40, #6-32, #8-32, #10-24, #10-32) or fractional (1/4-20, 5/16-18, 3/8-16, 1/2-13)
                if (u.Contains("#2") || u.Contains("NO. 2")) threadDiaMm = 2.18;
                else if (u.Contains("#4") || u.Contains("NO. 4")) threadDiaMm = 2.84;
                else if (u.Contains("#6") || u.Contains("NO. 6")) threadDiaMm = 3.51;
                else if (u.Contains("#8") || u.Contains("NO. 8")) threadDiaMm = 4.17;
                else if (u.Contains("#10") || u.Contains("NO. 10")) threadDiaMm = 4.83;
                else if (u.Contains("1/4-20") || u.Contains("1/4-28") || u.Contains("1/4\"")) threadDiaMm = 6.35;
                else if (u.Contains("5/16-18") || u.Contains("5/16\"")) threadDiaMm = 7.94;
                else if (u.Contains("3/8-16") || u.Contains("3/8\"")) threadDiaMm = 9.525;
                else if (u.Contains("1/2-13") || u.Contains("1/2\"")) threadDiaMm = 12.7;
            }

            if (lengthMm >= 9998.0)
            {
                Match mLenMm = Regex.Match(u, @"(\d+(?:\.\d+)?)\s*MM\s*(?:LONG|LG|LENGTH)?");
                if (mLenMm.Success && double.TryParse(mLenMm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double lMm) && lMm > 0.5)
                {
                    lengthMm = lMm;
                }
            }
        }

        private static bool TryParseDimensionsFromTextMm(string text, out double volMm3, out double maxDimMm)
        {
            volMm3 = 0.0;
            maxDimMm = 0.0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            Match m3 = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*[xX×]\s*(\d+(?:\.\d+)?)\s*[xX×]\s*(\d+(?:\.\d+)?)\s*(mm|in|\x22)?");
            if (m3.Success)
            {
                double.TryParse(m3.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d1);
                double.TryParse(m3.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d2);
                double.TryParse(m3.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d3);
                string u = m3.Groups[4].Success ? m3.Groups[4].Value.ToLowerInvariant() : "mm";
                double scale = (u == "in" || u == "\"") ? 25.4 : 1.0;
                d1 *= scale; d2 *= scale; d3 *= scale;
                volMm3 = d1 * d2 * d3;
                maxDimMm = Math.Max(d1, Math.Max(d2, d3));
                return volMm3 > 0.0;
            }
            return false;
        }

        public static BomItemClassification ClassifyBomEntry(
            string itemName,
            string partPath,
            string extraRowText,
            Dictionary<string, string> customProps,
            bool isSheetMetal = false,
            double volumeMm3 = 0.0,
            double maxDimMm = 0.0)
        {
            string cleanName = (itemName ?? "").Trim();
            if (string.IsNullOrEmpty(cleanName) && !string.IsNullOrWhiteSpace(partPath))
                cleanName = Path.GetFileNameWithoutExtension(partPath);

            string propCat = "";
            string propTech = "";
            string propDesc = "";
            string propPartNo = "";
            string propMat = "";
            string propVendor = "";

            if (customProps != null)
            {
                if (!customProps.TryGetValue("BOM_Category", out propCat))
                    customProps.TryGetValue("Category", out propCat);
                if (!customProps.TryGetValue("BOM_Technique", out propTech))
                    customProps.TryGetValue("Manufacturing_Technique", out propTech);
                if (!customProps.TryGetValue("BOM_Desc", out propDesc) || string.IsNullOrWhiteSpace(propDesc))
                    customProps.TryGetValue("Description", out propDesc);
                customProps.TryGetValue("PartNumber", out propPartNo);
                customProps.TryGetValue("Material", out propMat);
                customProps.TryGetValue("Vendor", out propVendor);
            }

            // Combine primary identity fields (excluding BOM_Role so "Mount Hennkwell actuator to valve" doesn't misclassify a bracket as an actuator)
            string identityText = string.Join(" | ", new[]
            {
                cleanName,
                propPartNo ?? "",
                propDesc ?? "",
                propMat ?? "",
                propTech ?? "",
                extraRowText ?? ""
            });
            string idUpper = identityText.ToUpperInvariant();
            string nameAndDescUpper = ((cleanName ?? "") + " | " + (propDesc ?? "") + " | " + (extraRowText ?? "")).ToUpperInvariant();

            if (volumeMm3 <= 0.0 && maxDimMm <= 0.0)
                TryParseDimensionsFromTextMm(identityText, out volumeMm3, out maxDimMm);

            bool isMcMaster = McMasterRegex.IsMatch(cleanName) ||
                              (!string.IsNullOrEmpty(propPartNo) && McMasterRegex.IsMatch(propPartNo)) ||
                              (!string.IsNullOrEmpty(extraRowText) && McMasterRegex.IsMatch(extraRowText));
            McMasterCatalogItem catItem = null;
            if (isMcMaster)
            {
                Match mPn = McMasterRegex.Match(identityText);
                if (mPn.Success)
                    catItem = AssemblyGeometryHelper.FindCatalogItemByPartNumber(mPn.Value.ToUpperInvariant());
            }

            // Custom manufactured structural part nouns that should never be misclassified as fasteners or OEM parts (unless explicitly tagged OEM)
            bool hasCustomManufacturedNoun = !isMcMaster && ContainsAnyKeyword(nameAndDescUpper,
                "PLATE", "BRACKET", "HOUSING", "ADAPTER", "FRAME", "RETAINER", "SADDLE",
                "SHROUD", "COLLAR", "SPACER", "SHIM", "JIG", "FIXTURE", "POD", "PANEL",
                "TRAY", "LID", "COVER", "QUADRANT", "MANIFOLD", "NOZZLE", "DUCT",
                "CARRIAGE", "CRADLE", "MOUNT", "HOLDER", "CLAMP_BODY", "BLOCK", "RIB", "GUSSET");

            bool hasInHouseProcessKeyword = !isMcMaster && (isSheetMetal || ContainsAnyKeyword(idUpper,
                "3D PRINT", "3D_PRINT", "3DP", "PRINTED", "TOUGH PLA", "TOUGH_PLA", "PLA", "PETG", "ASA", "PRUSAMENT", "FDM",
                "LASER CUT", "LASER_CUT", "LASER-CUT", "SENDCUTSEND", "SHEET METAL", "BENT",
                "CNC", "ONEFINITY", "CORIAN", "PLYWOOD", "BALTIC BIRCH", "HARDWOOD", "MDF", "DOMINO"));

            int majorGroup = 0; // 1 = OEM, 2 = Manufactured, 3 = Fastener
            int fastenerSubRank = 0; // 1 = threaded_insert, 2 = screw, 3 = bolt, 4 = washer, 5 = nut
            string fastenerSubType = null;

            string explicitCat = (propCat ?? "").Trim().ToLowerInvariant();
            if (explicitCat == "oem" || explicitCat == "cots" || explicitCat == "purchased")
                majorGroup = 1;
            else if (explicitCat == "manufactured" || explicitCat == "custom" || explicitCat == "in_house" || explicitCat == "in-house" || explicitCat == "shop")
                majorGroup = 2;
            else if (explicitCat == "fastener" || explicitCat == "hardware" || explicitCat == "threaded_insert" || explicitCat == "insert" ||
                     explicitCat == "screw" || explicitCat == "bolt" || explicitCat == "washer" || explicitCat == "nut")
                majorGroup = 3;

            // Detect Fastener sub-type: 1 = Threaded Insert, 2 = Screw, 3 = Bolt, 4 = Washer, 5 = Nut
            if (!hasCustomManufacturedNoun && !hasInHouseProcessKeyword)
            {
                string catKind = catItem != null ? (catItem.Kind ?? "").ToLowerInvariant() : "";
                if (catKind == "insert" || catKind == "heat_set_insert" || catKind == "threaded_insert" ||
                    ContainsAnyKeyword(nameAndDescUpper, "HEAT-SET INSERT", "HEAT_SET_INSERT", "HEAT SET INSERT", "THREADED INSERT", "THREADED_INSERT", "HELICOIL", "WOOD INSERT", "KNURLED INSERT", "TAPERED INSERT", "ULTRASONIC INSERT") ||
                    (Regex.IsMatch(nameAndDescUpper, @"\bINSERT\b") && !ContainsAnyKeyword(nameAndDescUpper, "NYLON INSERT", "NYLON-INSERT", "LOCKNUT", "NYLOCK")) ||
                    (isMcMaster && ContainsAnyKeyword(nameAndDescUpper, "94180A", "94459A", "93365A", "90016A", "92395A")))
                {
                    fastenerSubRank = 1;
                    fastenerSubType = "threaded_insert";
                }
                else if (catKind == "washer" || catKind == "flat_washer" ||
                         ContainsAnyKeyword(nameAndDescUpper, "WASHER", "FENDER WASHER", "LOCK WASHER", "SEALING WASHER"))
                {
                    fastenerSubRank = 4;
                    fastenerSubType = "washer";
                }
                else if (catKind == "nut" || catKind == "hex_nut" || catKind == "nylock_nut" || catKind == "jam_nut" ||
                         ContainsAnyKeyword(nameAndDescUpper, "HEX NUT", "HEX_NUT", "NYLOCK", "LOCKNUT", "NYLON-INSERT", "JAM NUT", "JAM_NUT", "FLANGE NUT", "T-NUT", "WING NUT", "COUPLING NUT") ||
                         Regex.IsMatch(nameAndDescUpper, @"\bNUT\b"))
                {
                    fastenerSubRank = 5;
                    fastenerSubType = "nut";
                }
                else if (catKind == "hex_bolt" ||
                         ContainsAnyKeyword(nameAndDescUpper, "HEX BOLT", "HEX_BOLT", "HEX HEAD BOLT", "CARRIAGE BOLT", "SHOULDER BOLT", "EYE BOLT", "U-BOLT", "FLANGE BOLT", "CLEVIS PIN", "CLEVIS_PIN", "DOWEL PIN", "THREADED ROD", "ACME ROD") ||
                         (Regex.IsMatch(nameAndDescUpper, @"\bBOLT\b") && !ContainsAnyKeyword(nameAndDescUpper, "SHCS", "BHCS", "SOCKET HEAD", "BUTTON HEAD", "FLAT HEAD", "COUNTERSUNK")))
                {
                    fastenerSubRank = 3;
                    fastenerSubType = "bolt";
                }
                else if (catKind == "shcs" || catKind == "bhcs" || catKind == "csk" || catKind == "csk_screw" ||
                         ContainsAnyKeyword(nameAndDescUpper, "SCREW", "SHCS", "BHCS", "FHCS", "CSK", "SOCKET HEAD", "BUTTON HEAD", "FLAT HEAD", "COUNTERSUNK", "PAN HEAD") ||
                         (isMcMaster && ContainsAnyKeyword(nameAndDescUpper, "91292A", "91290A", "92196A", "92095A", "92949A", "92125A", "91294A")))
                {
                    fastenerSubRank = 2;
                    fastenerSubType = "screw";
                }
            }

            if (majorGroup == 3 && fastenerSubRank == 0)
            {
                if (explicitCat == "threaded_insert" || explicitCat == "insert") { fastenerSubRank = 1; fastenerSubType = "threaded_insert"; }
                else if (explicitCat == "bolt") { fastenerSubRank = 3; fastenerSubType = "bolt"; }
                else if (explicitCat == "washer") { fastenerSubRank = 4; fastenerSubType = "washer"; }
                else if (explicitCat == "nut") { fastenerSubRank = 5; fastenerSubType = "nut"; }
                else { fastenerSubRank = 2; fastenerSubType = "screw"; }
            }

            if (majorGroup == 0)
            {
                if (fastenerSubRank > 0)
                {
                    majorGroup = 3;
                }
                else if (!hasCustomManufacturedNoun && !hasInHouseProcessKeyword &&
                         (isMcMaster ||
                          (!string.IsNullOrWhiteSpace(propVendor) &&
                           propVendor.IndexOf("In-House", StringComparison.OrdinalIgnoreCase) < 0 &&
                           propVendor.IndexOf("Shop", StringComparison.OrdinalIgnoreCase) < 0 &&
                           propVendor.IndexOf("SendCutSend", StringComparison.OrdinalIgnoreCase) < 0) ||
                          ContainsAnyKeyword(nameAndDescUpper,
                              "HENNKWELL", "ACTUATOR", "MOTOR", "STEPPER", "NEMA17", "NEMA23", "SERVO", "SPINDLE", "MAKITA", "RF1101",
                              "LINEAR RAIL", "LINEAR_RAIL", "MGN12", "MGN15", "HIWIN", "BALL BEARING", "BEARING", "BUSHING", "LEAD SCREW", "BALLSCREW",
                              "VALVE", "BALL VALVE", "SOLENOID", "CYLINDER", "CLEVIS YOKE", "PUSH-TO-CONNECT",
                              "PCB", "ARDUINO", "ESP32", "MICROSWITCH", "LIMIT SWITCH", "OMRON", "SENSOR", "POWER SUPPLY", "MEANWELL",
                              "BLUM", "TANDEM", "MOVENTO", "CLIP TOP", "TOGGLE CLAMP", "CASTER", "OEM", "COTS")))
                {
                    majorGroup = 1;
                }
                else
                {
                    majorGroup = 2;
                }
            }

            int techRank = 0;
            string techName = null;
            if (majorGroup == 2)
            {
                string techSource = ((propTech ?? "") + " | " + idUpper).ToUpperInvariant();
                if (ContainsAnyKeyword(techSource, "WOOD", "PLYWOOD", "BALTIC BIRCH", "HARDWOOD", "MAPLE", "WALNUT", "OAK", "CHERRY", "MDF", "DOMINO", "CABINET"))
                {
                    techRank = 1;
                    techName = "woodworking";
                }
                else if (ContainsAnyKeyword(techSource, "3D PRINT", "3D_PRINT", "3DP", "PRINTED", "TOUGH PLA", "TOUGH_PLA", "PLA", "PETG", "ASA", "ABS", "TPU", "PVB", "CPE", "PRUSAMENT", "PRUSA", "FDM", "FILAMENT", "PERIMETERS"))
                {
                    techRank = 2;
                    techName = "3d_printing";
                }
                else if (ContainsAnyKeyword(techSource, "CNC", "ONEFINITY", "CARVED", "CORIAN", "ACRYLIC", "POLYCARBONATE", "DELRIN", "ACETAL", "UHMW", "HDPE", "VACUUM POD"))
                {
                    techRank = 3;
                    techName = "cnc_carving";
                }
                else if (isSheetMetal || ContainsAnyKeyword(techSource, "SENDCUTSEND", "SCS", "LASER CUT", "LASER_CUT", "LASER-CUT", "LASER", "SHEET METAL", "SHEET_METAL", "BENT", "304 SS", "304_SS", "316 SS", "5052", "6061", "A36", "1075", "C260", "STAINLESS PLATE", "ALUMINUM PLATE", "STEEL PLATE", "BRASS PLATE"))
                {
                    techRank = 4;
                    techName = "sheet_metal_sendcutsend";
                }
                else
                {
                    techRank = 5;
                    techName = "custom_fabricated";
                }
            }

            double threadDiaMm = 999.0;
            double fastLenMm = 9999.0;
            if (majorGroup == 3)
            {
                ParseFastenerThreadAndLengthMm(identityText, out threadDiaMm, out fastLenMm);
                if (catItem != null && catItem.NominalLengthMm > 0 && fastLenMm >= 9998.0)
                    fastLenMm = catItem.NominalLengthMm;
                if (fastLenMm >= 9998.0 && maxDimMm > 0.5)
                    fastLenMm = maxDimMm;
            }

            string displayLabel = (!string.IsNullOrWhiteSpace(propDesc) ? propDesc : cleanName).ToUpperInvariant();
            string sortKey;
            if (majorGroup == 1)
            {
                sortKey = string.Format(CultureInfo.InvariantCulture, "1_OEM_{0}_{1}", displayLabel, cleanName.ToUpperInvariant());
            }
            else if (majorGroup == 2)
            {
                // Within each manufacturing technique, sort by size (largest volume first, then largest max dimension, then name)
                long clampedVol = (long)Math.Max(0.0, Math.Min(999999999999.0, Math.Round(volumeMm3 * 10.0)));
                long invVol = 999999999999L - clampedVol;
                long clampedMax = (long)Math.Max(0.0, Math.Min(999999999.0, Math.Round(maxDimMm * 100.0)));
                long invMax = 999999999L - clampedMax;
                sortKey = string.Format(CultureInfo.InvariantCulture, "2_MFG_{0:D2}_{1:D12}_{2:D9}_{3}_{4}",
                    techRank, invVol, invMax, displayLabel, cleanName.ToUpperInvariant());
            }
            else
            {
                long diaKey = (long)Math.Max(0.0, Math.Min(999999.0, Math.Round(threadDiaMm * 100.0)));
                long lenKey = (long)Math.Max(0.0, Math.Min(9999999.0, Math.Round(fastLenMm * 100.0)));
                sortKey = string.Format(CultureInfo.InvariantCulture, "3_FST_{0:D2}_{1:D6}_{2:D7}_{3}_{4}",
                    fastenerSubRank, diaKey, lenKey, displayLabel, cleanName.ToUpperInvariant());
            }

            return new BomItemClassification
            {
                MajorGroup = majorGroup,
                Category = majorGroup == 1 ? "oem" : (majorGroup == 2 ? "manufactured" : "fastener"),
                TechniqueRank = techRank,
                ManufacturingTechnique = techName,
                FastenerSubRank = fastenerSubRank,
                FastenerSubType = fastenerSubType,
                ThreadDiameterMm = majorGroup == 3 && threadDiaMm < 900.0 ? Math.Round(threadDiaMm, 2) : 0.0,
                FastenerLengthMm = majorGroup == 3 && fastLenMm < 9000.0 ? Math.Round(fastLenMm, 2) : 0.0,
                VolumeMm3 = Math.Round(volumeMm3, 2),
                MaxDimensionMm = Math.Round(maxDimMm, 2),
                SortKey = sortKey
            };
        }

        private static void GetComponentBoundingSizeMm(IComponent2 comp, out double dxMm, out double dyMm, out double dzMm, out double volMm3, out double maxDimMm, out bool isSheetMetal)
        {
            dxMm = 0; dyMm = 0; dzMm = 0; volMm3 = 0; maxDimMm = 0; isSheetMetal = false;
            if (comp == null) return;
            try
            {
                IModelDoc2 cDoc = comp.GetModelDoc2() as IModelDoc2;
                if (cDoc is IPartDoc pDoc)
                {
                    object[] bodies = pDoc.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                    if (bodies != null && bodies.Length > 0 && bodies[0] is IBody2 b0)
                    {
                        try { isSheetMetal = b0.IsSheetMetal(); } catch { }
                        double[] bBox = b0.GetBodyBox() as double[];
                        if (bBox != null && bBox.Length >= 6)
                        {
                            dxMm = Math.Abs(bBox[3] - bBox[0]) * 1000.0;
                            dyMm = Math.Abs(bBox[4] - bBox[1]) * 1000.0;
                            dzMm = Math.Abs(bBox[5] - bBox[2]) * 1000.0;
                            volMm3 = dxMm * dyMm * dzMm;
                            maxDimMm = Math.Max(dxMm, Math.Max(dyMm, dzMm));
                            return;
                        }
                    }
                }
                double[] box = comp.GetBox(false, false) as double[];
                if (box != null && box.Length >= 6)
                {
                    dxMm = Math.Abs(box[3] - box[0]) * 1000.0;
                    dyMm = Math.Abs(box[4] - box[1]) * 1000.0;
                    dzMm = Math.Abs(box[5] - box[2]) * 1000.0;
                    volMm3 = dxMm * dyMm * dzMm;
                    maxDimMm = Math.Max(dxMm, Math.Max(dyMm, dzMm));
                }
            }
            catch { }
        }

        private static McpToolResult HandleExtractBom(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            bool includeExcluded = JsonHelper.GetBool(args, "include_excluded_from_bom", false);
            List<string> propNames = JsonHelper.GetStringList(args, "custom_property_names");
            if (propNames.Count == 0)
            {
                propNames.AddRange(new[] { "PartNumber", "Description", "BOM_Desc", "BOM_Role", "BOM_Category", "BOM_Technique", "Material", "Vendor" });
            }

            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> metadata = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> sortKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<string> excludedComponents = new List<string>();

            if (doc is IAssemblyDoc assy)
            {
                object[] comps = assy.GetComponents(false) as object[];
                if (comps != null)
                {
                    foreach (object cObj in comps)
                    {
                        if (!(cObj is IComponent2 c)) continue;
                        if (c.IsSuppressed()) continue;
                        if (c.ExcludeFromBOM)
                        {
                            excludedComponents.Add(c.Name2);
                            if (!includeExcluded) continue;
                        }

                        string path = c.GetPathName() ?? c.Name2;
                        string key = Path.GetFileNameWithoutExtension(path);
                        counts[key] = counts.TryGetValue(key, out int prev) ? prev + 1 : 1;

                        if (!metadata.ContainsKey(key))
                        {
                            Match m = McMasterRegex.Match(key);
                            Dictionary<string, string> customProps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            try
                            {
                                IModelDoc2 compDoc = c.GetModelDoc2() as IModelDoc2;
                                if (compDoc != null)
                                {
                                    ICustomPropertyManager docCpm = compDoc.Extension.get_CustomPropertyManager("");
                                    string cfgName = c.ReferencedConfiguration ?? "";
                                    ICustomPropertyManager cfgCpm = !string.IsNullOrEmpty(cfgName)
                                        ? compDoc.Extension.get_CustomPropertyManager(cfgName)
                                        : null;
                                    foreach (string pn in propNames)
                                    {
                                        if (string.IsNullOrWhiteSpace(pn)) continue;
                                        string valOut = "", resOut = "";
                                        bool wasRes = false;
                                        if (cfgCpm != null)
                                            cfgCpm.Get5(pn, false, out valOut, out resOut, out wasRes);
                                        if (string.IsNullOrWhiteSpace(resOut) && docCpm != null)
                                            docCpm.Get5(pn, false, out valOut, out resOut, out wasRes);
                                        string finalVal = !string.IsNullOrWhiteSpace(resOut) ? resOut : valOut;
                                        if (!string.IsNullOrWhiteSpace(finalVal))
                                            customProps[pn] = finalVal;
                                    }
                                }
                            }
                            catch { }

                            GetComponentBoundingSizeMm(c, out double dxMm, out double dyMm, out double dzMm, out double volMm3, out double maxDimMm, out bool isSm);
                            BomItemClassification cls = ClassifyBomEntry(key, path, null, customProps, isSm, volMm3, maxDimMm);
                            sortKeys[key] = cls.SortKey;

                            metadata[key] = new
                            {
                                item_name = key,
                                path = path,
                                excluded_from_bom = c.ExcludeFromBOM,
                                bom_category = cls.Category,
                                manufacturing_technique = cls.ManufacturingTechnique,
                                fastener_subtype = cls.FastenerSubType,
                                thread_diameter_mm = cls.ThreadDiameterMm > 0 ? (object)cls.ThreadDiameterMm : null,
                                fastener_length_mm = cls.FastenerLengthMm > 0 ? (object)cls.FastenerLengthMm : null,
                                is_mcmaster = m.Success,
                                mcmaster_pn = m.Success ? m.Value.ToUpperInvariant() : null,
                                size_mm = volMm3 > 0 ? new
                                {
                                    x = Math.Round(dxMm, 2),
                                    y = Math.Round(dyMm, 2),
                                    z = Math.Round(dzMm, 2),
                                    volume_mm3 = cls.VolumeMm3,
                                    max_dim_mm = cls.MaxDimensionMm
                                } : null,
                                custom_properties = customProps,
                                sort_key = cls.SortKey
                            };
                        }
                    }
                }
            }
            else if (doc is IPartDoc part)
            {
                object[] bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                if (bodies != null)
                {
                    foreach (object bObj in bodies)
                    {
                        if (!(bObj is IBody2 b)) continue;
                        double[] bb = b.GetBodyBox() as double[];
                        string key = b.Name;
                        counts[key] = 1;
                        double dxMm = bb != null && bb.Length >= 6 ? Math.Abs(bb[3] - bb[0]) * 1000.0 : 0.0;
                        double dyMm = bb != null && bb.Length >= 6 ? Math.Abs(bb[4] - bb[1]) * 1000.0 : 0.0;
                        double dzMm = bb != null && bb.Length >= 6 ? Math.Abs(bb[5] - bb[2]) * 1000.0 : 0.0;
                        double volMm3 = dxMm * dyMm * dzMm;
                        double maxDimMm = Math.Max(dxMm, Math.Max(dyMm, dzMm));
                        bool isSm = b.IsSheetMetal();
                        BomItemClassification cls = ClassifyBomEntry(key, doc.GetPathName(), null, null, isSm, volMm3, maxDimMm);
                        sortKeys[key] = cls.SortKey;
                        metadata[key] = new
                        {
                            item_name = key,
                            bom_category = cls.Category,
                            manufacturing_technique = cls.ManufacturingTechnique,
                            is_sheet_metal = isSm,
                            units = units,
                            size = bb != null && bb.Length >= 6 ? new
                            {
                                x = Math.Round(ShopCatalogService.FromMeters(bb[3] - bb[0], units), 4),
                                y = Math.Round(ShopCatalogService.FromMeters(bb[4] - bb[1], units), 4),
                                z = Math.Round(ShopCatalogService.FromMeters(bb[5] - bb[2], units), 4)
                            } : null,
                            sort_key = cls.SortKey
                        };
                    }
                }
            }

            List<string> orderedKeys = new List<string>(counts.Keys);
            orderedKeys.Sort((a, b) =>
            {
                string ka = sortKeys.TryGetValue(a, out string sa) ? sa : a;
                string kb = sortKeys.TryGetValue(b, out string sb) ? sb : b;
                return string.Compare(ka, kb, StringComparison.Ordinal);
            });

            List<object> bomLines = new List<object>(orderedKeys.Count);
            for (int idx = 0; idx < orderedKeys.Count; idx++)
            {
                string k = orderedKeys[idx];
                bomLines.Add(new
                {
                    item_number = idx + 1,
                    qty = counts[k],
                    info = metadata[k]
                });
            }

            return McpToolResult.JsonText(new
            {
                document = doc.GetTitle(),
                sort_order = "oem -> manufactured (by technique, then size) -> fasteners (threaded_inserts -> screws -> bolts -> washers -> nuts)",
                unique_items = bomLines.Count,
                excluded_from_bom_count = excludedComponents.Count,
                excluded_from_bom_components = excludedComponents,
                bom = bomLines
            });
        }

        public static void ParseAxisDirection(string axisStr, int fallbackDirIdx, bool fallbackReverse, out int dirIdx, out bool reverse)
        {
            dirIdx = fallbackDirIdx >= 0 && fallbackDirIdx <= 2 ? fallbackDirIdx : 2;
            reverse = fallbackReverse;
            if (string.IsNullOrWhiteSpace(axisStr))
                return;

            string s = axisStr.Trim().ToUpperInvariant();
            if (s.StartsWith("-"))
            {
                reverse = true;
                s = s.Substring(1).Trim();
            }
            else if (s.StartsWith("+"))
            {
                reverse = false;
                s = s.Substring(1).Trim();
            }

            if (s == "X" || s == "0") dirIdx = 0;
            else if (s == "Y" || s == "1") dirIdx = 1;
            else if (s == "Z" || s == "2") dirIdx = 2;
        }

        private static double[] GetComponentCenterMeters(IComponent2 comp)
        {
            if (comp == null) return new[] { 0.0, 0.0, 0.0 };
            try
            {
                double[] box = comp.GetBox(false, false) as double[];
                if (box != null && box.Length >= 6)
                {
                    return new[]
                    {
                        0.5 * (box[0] + box[3]),
                        0.5 * (box[1] + box[4]),
                        0.5 * (box[2] + box[5])
                    };
                }
            }
            catch { }
            try
            {
                double[] m = comp.Transform2 != null ? comp.Transform2.ArrayData as double[] : null;
                if (m != null && m.Length >= 12)
                    return new[] { m[9], m[10], m[11] };
            }
            catch { }
            return new[] { 0.0, 0.0, 0.0 };
        }

        private static McpToolResult HandleExplodedView(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            if (!(doc is IAssemblyDoc assy))
                return McpToolResult.Error("Exploded views require an active Assembly (.SLDASM).");

            ISldWorks app = swMgr.GetApp(false);
            SwConnectionManager.ActivateDocumentSafe(app, doc);

            string reqConfig = JsonHelper.GetString(args, "configuration");
            bool createCfgIfMissing = JsonHelper.GetBool(args, "create_configuration_if_missing", true);
            string explodeViewName = JsonHelper.GetString(args, "explode_view_name");
            bool clearExistingSteps = JsonHelper.GetBool(args, "clear_existing_steps", true);
            bool autoExplode = JsonHelper.GetBool(args, "auto_explode", false);
            bool showExploded = JsonHelper.GetBool(args, "show_exploded", true);
            bool createExplodeLines = JsonHelper.GetBool(args, "create_explode_lines", true);
            List<object> steps = JsonHelper.GetList(args, "steps");
            List<object> explicitLines = JsonHelper.GetList(args, "explode_lines");
            string defaultUnits = SwConnectionManager.GetDocumentUnits(doc);

            if (!string.IsNullOrWhiteSpace(reqConfig))
            {
                string curCfgName = doc.ConfigurationManager?.ActiveConfiguration?.Name ?? "";
                if (!string.Equals(curCfgName, reqConfig, StringComparison.OrdinalIgnoreCase))
                {
                    bool shown = doc.ShowConfiguration2(reqConfig);
                    if (!shown && createCfgIfMissing && doc.ConfigurationManager != null)
                    {
                        IConfiguration addedCfg = doc.ConfigurationManager.AddConfiguration2(
                            reqConfig, reqConfig, "", (int)swConfigurationOptions2_e.swConfigOption_DontActivate, "", "", true);
                        if (addedCfg != null)
                            doc.ShowConfiguration2(reqConfig);
                    }
                }
            }

            if (steps.Count == 0 && autoExplode)
            {
                int compCount = assy.GetComponentCount(false);
                if (compCount > 20)
                {
                    return McpToolResult.Error(string.Format(
                        "AutoExplode is disabled on assemblies with >20 components ({0} found) because it can stall SOLIDWORKS. Pass explicit directional 'steps' instead.",
                        compCount));
                }
            }

            // Capture unexploded component centers before creating/showing exploded view so 3D explode lines connect home -> exploded trajectories
            try { assy.ShowExploded(false); } catch { }
            Dictionary<string, double[]> compCentersM = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            object[] allComps = assy.GetComponents(false) as object[];
            if (allComps != null)
            {
                foreach (object co in allComps)
                {
                    if (co is IComponent2 c && !string.IsNullOrEmpty(c.Name2) && !compCentersM.ContainsKey(c.Name2))
                        compCentersM[c.Name2] = GetComponentCenterMeters(c);
                }
            }

            bool createdNew = assy.CreateExplodedView();
            try { assy.ShowExploded(true); } catch { }

            IConfiguration cfg = doc.ConfigurationManager?.ActiveConfiguration;
            if (cfg == null)
                return McpToolResult.Error("Could not acquire active configuration for explode steps.");

            // Clear auto-generated Chain1 AutoExplode steps (CreateExplodedView() often auto-populates Chain1 along +Z)
            int clearedStepsCount = 0;
            if (clearExistingSteps)
            {
                try
                {
                    int existingCount = cfg.GetNumberOfExplodeSteps();
                    for (int i = existingCount - 1; i >= 0; i--)
                    {
                        IExplodeStep exStep = cfg.GetExplodeStep(i) as IExplodeStep;
                        if (exStep != null && !string.IsNullOrEmpty(exStep.Name))
                        {
                            if (cfg.DeleteExplodeStep(exStep.Name))
                                clearedStepsCount++;
                        }
                    }
                }
                catch { }
            }

            int addedSteps = 0;
            List<object> stepReports = new List<object>();
            List<double[][]> trajectorySegmentsM = new List<double[][]>();

            // IConfiguration.AddExplodeStep2 requires components selected with SelectData.Mark = 1 (Mark = 0 fails with err = 3 / NoComponents)
            ISelectionMgr selMgr = doc.SelectionManager as ISelectionMgr;

            if (steps.Count > 0)
            {
                foreach (object sObj in steps)
                {
                    Dictionary<string, object> sd = sObj as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(sObj));
                    if (sd == null) continue;

                    List<string> targetComps = JsonHelper.GetStringList(sd, "component_names");
                    string singleComp = JsonHelper.GetString(sd, "component_name");
                    if (!string.IsNullOrWhiteSpace(singleComp)) targetComps.Add(singleComp);
                    if (targetComps.Count == 0) continue;

                    string stepUnits = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(sd, "units", defaultUnits));
                    bool emitStepLine = JsonHelper.GetBool(sd, "explode_line", true);
                    List<IComponent2> resolved = AssemblyGeometryHelper.ResolveComponentsMatching(assy, targetComps);
                    if (resolved.Count == 0)
                    {
                        stepReports.Add(new { components = targetComps, added = false, error = "No matching components resolved." });
                        continue;
                    }

                    // Decompose step into 1 or more single-axis legs (supports compound vector:[dx,dy,dz], motions:[...], or single direction_index/axis)
                    List<Tuple<int, bool, double>> axisLegs = new List<Tuple<int, bool, double>>();
                    double[] vec = JsonHelper.GetDoubleArray(sd, "vector");
                    List<object> motions = JsonHelper.GetList(sd, "motions");
                    if (vec != null && vec.Length >= 3)
                    {
                        for (int ax = 0; ax < 3; ax++)
                        {
                            double dM = ShopCatalogService.ToMeters(vec[ax], stepUnits);
                            if (Math.Abs(dM) > 1e-6)
                                axisLegs.Add(Tuple.Create(ax, dM < 0.0, Math.Abs(dM)));
                        }
                    }
                    else if (motions != null && motions.Count > 0)
                    {
                        foreach (object mObj in motions)
                        {
                            Dictionary<string, object> md = mObj as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(mObj));
                            if (md == null) continue;
                            string mUnits = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(md, "units", stepUnits));
                            double mVal = JsonHelper.GetDouble(md, "distance", 0.0);
                            double mDistM = Math.Abs(ShopCatalogService.ToMeters(mVal, mUnits));
                            if (mDistM <= 1e-6) continue;
                            bool mRev = JsonHelper.GetBool(md, "reverse", mVal < 0.0);
                            int mDir = JsonHelper.GetInt(md, "direction_index", 2);
                            ParseAxisDirection(JsonHelper.GetString(md, "axis"), mDir, mRev, out mDir, out mRev);
                            axisLegs.Add(Tuple.Create(mDir, mRev, mDistM));
                        }
                    }
                    else
                    {
                        double distVal = JsonHelper.GetDouble(sd, "distance", 50.0);
                        double distM = Math.Abs(ShopCatalogService.ToMeters(distVal, stepUnits));
                        bool reverse = JsonHelper.GetBool(sd, "reverse", distVal < 0.0);
                        int dirIdx = JsonHelper.GetInt(sd, "direction_index", 2);
                        ParseAxisDirection(JsonHelper.GetString(sd, "axis"), dirIdx, reverse, out dirIdx, out reverse);
                        if (distM > 1e-6)
                            axisLegs.Add(Tuple.Create(dirIdx, reverse, distM));
                    }

                    foreach (Tuple<int, bool, double> leg in axisLegs)
                    {
                        int dirIdx = leg.Item1;
                        bool reverse = leg.Item2;
                        double distM = leg.Item3;

                        doc.ClearSelection2(true);
                        SelectData selData = selMgr != null ? (SelectData)selMgr.CreateSelectData() : null;
                        if (selData != null) selData.Mark = 1;

                        int sel = 0;
                        List<string> selNames = new List<string>();
                        foreach (IComponent2 c in resolved)
                        {
                            if (c == null) continue;
                            if (c.Select4(sel > 0, selData, false))
                            {
                                sel++;
                                selNames.Add(c.Name2);
                            }
                        }

                        if (sel > 0)
                        {
                            int err = 0;
                            IExplodeStep step = cfg.AddExplodeStep2(
                                distM,
                                dirIdx,
                                reverse,
                                0.0, 0, false, false, false, out err) as IExplodeStep;
                            doc.ClearSelection2(true);

                            bool stepAdded = step != null;
                            if (stepAdded)
                            {
                                addedSteps++;
                                double signedDeltaM = reverse ? -distM : distM;
                                double dxM = dirIdx == 0 ? signedDeltaM : 0.0;
                                double dyM = dirIdx == 1 ? signedDeltaM : 0.0;
                                double dzM = dirIdx == 2 ? signedDeltaM : 0.0;

                                foreach (IComponent2 c in resolved)
                                {
                                    if (c == null || string.IsNullOrEmpty(c.Name2)) continue;
                                    if (!compCentersM.TryGetValue(c.Name2, out double[] p0))
                                        p0 = GetComponentCenterMeters(c);
                                    double[] p1 = new[] { p0[0] + dxM, p0[1] + dyM, p0[2] + dzM };
                                    compCentersM[c.Name2] = p1;
                                    if (emitStepLine)
                                        trajectorySegmentsM.Add(new[] { p0, p1 });
                                }
                            }

                            stepReports.Add(new
                            {
                                components = selNames,
                                direction_index = dirIdx,
                                reverse = reverse,
                                distance_mm = Math.Round(distM * 1000.0, 3),
                                added = stepAdded,
                                step_name = step != null ? step.Name : null,
                                error_code = err
                            });
                        }
                        else
                        {
                            doc.ClearSelection2(true);
                        }
                    }
                }
            }
            else if (autoExplode)
            {
                assy.AutoExplode();
            }

            foreach (object lineObj in explicitLines)
            {
                Dictionary<string, object> ld = lineObj as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(lineObj));
                if (ld == null) continue;
                string lUnits = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(ld, "units", defaultUnits));
                double[] pFrom = JsonHelper.GetDoubleArray(ld, "from");
                double[] pTo = JsonHelper.GetDoubleArray(ld, "to");
                if (pFrom != null && pFrom.Length >= 3 && pTo != null && pTo.Length >= 3)
                {
                    trajectorySegmentsM.Add(new[]
                    {
                        new[]
                        {
                            ShopCatalogService.ToMeters(pFrom[0], lUnits),
                            ShopCatalogService.ToMeters(pFrom[1], lUnits),
                            ShopCatalogService.ToMeters(pFrom[2], lUnits)
                        },
                        new[]
                        {
                            ShopCatalogService.ToMeters(pTo[0], lUnits),
                            ShopCatalogService.ToMeters(pTo[1], lUnits),
                            ShopCatalogService.ToMeters(pTo[2], lUnits)
                        }
                    });
                }
            }

            if (showExploded)
            {
                try { assy.ShowExploded(true); } catch { }
            }

            string currentExplodeName = null;
            try
            {
                string[] exNames = assy.GetExplodedViewNames2(cfg.Name) as string[];
                if (exNames != null && exNames.Length > 0)
                    currentExplodeName = exNames[exNames.Length - 1];
            }
            catch { }
            if (!string.IsNullOrWhiteSpace(explodeViewName) && !string.IsNullOrWhiteSpace(currentExplodeName) &&
                !string.Equals(currentExplodeName, explodeViewName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    IFeature f = doc.FirstFeature() as IFeature;
                    while (f != null)
                    {
                        if (string.Equals(f.Name, currentExplodeName, StringComparison.OrdinalIgnoreCase))
                        {
                            f.Name = explodeViewName;
                            currentExplodeName = f.Name;
                            break;
                        }
                        f = f.GetNextFeature() as IFeature;
                    }
                }
                catch { }
            }

            int explodeLinesDrawn = 0;
            if (createExplodeLines && trajectorySegmentsM.Count > 0)
            {
                ISketchManager sm = doc.SketchManager;
                try
                {
                    doc.ClearSelection2(true);
                    try { SwConnectionManager.MaskNonMdiControlBarsVisible(app); } catch { }
                    sm.InsertExplodeLineSketch();
                    bool usedFallback3D = false;
                    if (sm.ActiveSketch == null)
                    {
                        sm.Insert3DSketch(true);
                        usedFallback3D = true;
                    }
                    if (sm.ActiveSketch != null)
                    {
                        sm.AddToDB = true;
                        foreach (double[][] seg in trajectorySegmentsM)
                        {
                            double[] p0 = seg[0];
                            double[] p1 = seg[1];
                            double dSq = (p1[0] - p0[0]) * (p1[0] - p0[0]) + (p1[1] - p0[1]) * (p1[1] - p0[1]) + (p1[2] - p0[2]) * (p1[2] - p0[2]);
                            if (dSq < 1e-10) continue;
                            ISketchSegment lineSeg = sm.CreateLine(p0[0], p0[1], p0[2], p1[0], p1[1], p1[2]);
                            if (lineSeg != null) explodeLinesDrawn++;
                        }
                        sm.AddToDB = false;
                        if (usedFallback3D)
                            sm.Insert3DSketch(true);
                        else
                            sm.InsertExplodeLineSketch();
                        if (sm.ActiveSketch != null)
                            sm.Insert3DSketch(true);
                    }
                }
                catch
                {
                    try
                    {
                        sm.AddToDB = false;
                        if (sm.ActiveSketch != null) sm.Insert3DSketch(true);
                    }
                    catch { }
                }
                finally
                {
                    try { doc.ClearSelection2(true); } catch { }
                    try { SwConnectionManager.RestoreNonMdiControlBarsVisible(app); } catch { }
                }
            }

            if (!showExploded)
            {
                try { assy.ShowExploded(false); } catch { }
            }
            doc.EditRebuild3();

            return McpToolResult.JsonText(new
            {
                configuration = cfg.Name,
                explode_view_name = currentExplodeName ?? explodeViewName,
                exploded_view_created = createdNew || addedSteps > 0,
                cleared_existing_steps = clearedStepsCount,
                auto_exploded = steps.Count == 0 && autoExplode,
                manual_steps_added = addedSteps,
                explode_lines_created = explodeLinesDrawn,
                showing_exploded = showExploded,
                steps = stepReports
            });
        }

        public static void ApplyViewDisplayMode(IView view, string displayMode)
        {
            if (view == null || string.IsNullOrWhiteSpace(displayMode))
                return;

            string mode = displayMode.Trim().ToLowerInvariant();
            try
            {
                // Note: IView.SetDisplayMode4 ignores swDisplayMode_e.swSHADED_EDGES (7);
                // IView.SetDisplayMode3(false, swSHADED = 3, false, edges: true) reliably sets Shaded With Edges.
                if (mode == "shaded_with_edges" || mode == "shaded_edges")
                    view.SetDisplayMode3(false, (int)swDisplayMode_e.swSHADED, false, true);
                else if (mode == "shaded")
                    view.SetDisplayMode3(false, (int)swDisplayMode_e.swSHADED, false, false);
                else if (mode == "hlr" || mode == "hidden_lines_removed")
                    view.SetDisplayMode3(false, (int)swDisplayMode_e.swHIDDEN, false, false);
                else if (mode == "hlg" || mode == "hidden_lines_visible" || mode == "hidden_greyed")
                    view.SetDisplayMode3(false, (int)swDisplayMode_e.swHIDDEN_GREYED, false, false);
                else if (mode == "wireframe")
                    view.SetDisplayMode3(false, (int)swDisplayMode_e.swWIREFRAME, false, false);
            }
            catch { }
        }

        public static double[] NormalizeLayoutBoxMeters(double[] rawBox, string boxUnits, double sheetWidthM, double sheetHeightM)
        {
            if (rawBox == null || rawBox.Length < 4)
                return null;

            string u = (boxUnits ?? "").Trim().ToLowerInvariant();
            bool isNormalized = string.IsNullOrEmpty(u) &&
                                Math.Abs(rawBox[0]) <= 1.0 && Math.Abs(rawBox[1]) <= 1.0 &&
                                Math.Abs(rawBox[2]) <= 1.0 && Math.Abs(rawBox[3]) <= 1.0 &&
                                (rawBox[2] > 0.01 || rawBox[3] > 0.01);

            if (isNormalized || u == "fraction" || u == "normalized")
            {
                return new[]
                {
                    rawBox[0] * sheetWidthM,
                    rawBox[1] * sheetHeightM,
                    rawBox[2] * sheetWidthM,
                    rawBox[3] * sheetHeightM
                };
            }

            if (u == "mm" || u == "in" || u == "ft")
            {
                return new[]
                {
                    ShopCatalogService.ToMeters(rawBox[0], u),
                    ShopCatalogService.ToMeters(rawBox[1], u),
                    ShopCatalogService.ToMeters(rawBox[2], u),
                    ShopCatalogService.ToMeters(rawBox[3], u)
                };
            }

            // Default: if values > 1.5, treat as mm; otherwise meters
            if (Math.Abs(rawBox[2]) > 1.5 || Math.Abs(rawBox[3]) > 1.5)
            {
                return new[]
                {
                    rawBox[0] * 0.001,
                    rawBox[1] * 0.001,
                    rawBox[2] * 0.001,
                    rawBox[3] * 0.001
                };
            }

            return new[] { rawBox[0], rawBox[1], rawBox[2], rawBox[3] };
        }

        public static void AutoFitViewToBox(IModelDoc2 drwModel, IView view, double[] boxM, double fillFactor = 0.86)
        {
            if (view == null || boxM == null || boxM.Length < 4)
                return;

            double boxW = Math.Abs(boxM[2] - boxM[0]);
            double boxH = Math.Abs(boxM[3] - boxM[1]);
            double targetCx = 0.5 * (boxM[0] + boxM[2]);
            double targetCy = 0.5 * (boxM[1] + boxM[3]);
            if (boxW < 1e-4 || boxH < 1e-4)
                return;

            try
            {
                double[] outline = view.GetOutline() as double[];
                if (outline != null && outline.Length >= 4)
                {
                    double curW = Math.Max(1e-5, outline[2] - outline[0]);
                    double curH = Math.Max(1e-5, outline[3] - outline[1]);
                    double ratio = Math.Min((boxW * fillFactor) / curW, (boxH * fillFactor) / curH);
                    if (ratio > 0.02 && ratio < 50.0)
                    {
                        double curScale = view.ScaleDecimal;
                        if (curScale <= 0) curScale = 1.0;
                        view.ScaleDecimal = curScale * ratio;
                        try { drwModel?.EditRebuild3(); } catch { }
                    }
                }

                outline = view.GetOutline() as double[];
                double[] pos = view.Position as double[];
                if (outline != null && outline.Length >= 4 && pos != null && pos.Length >= 2)
                {
                    double outCx = 0.5 * (outline[0] + outline[2]);
                    double outCy = 0.5 * (outline[1] + outline[3]);
                    view.Position = new[]
                    {
                        pos[0] + (targetCx - outCx),
                        pos[1] + (targetCy - outCy)
                    };
                }
                else
                {
                    view.Position = new[] { targetCx, targetCy };
                }
            }
            catch { }
        }

        private static bool ComponentMatchesAnyFilter(IComponent2 comp, List<string> patterns)
        {
            if (comp == null || patterns == null || patterns.Count == 0)
                return false;

            string name2 = comp.Name2 ?? "";
            string leaf = name2.Contains("/") ? name2.Substring(name2.LastIndexOf('/') + 1) : name2;
            string stem = Regex.Replace(leaf, @"-\d+$", "");
            string pathStem = "";
            try
            {
                string p = comp.GetPathName();
                if (!string.IsNullOrWhiteSpace(p))
                    pathStem = Path.GetFileNameWithoutExtension(p);
            }
            catch { }

            foreach (string rawPat in patterns)
            {
                if (string.IsNullOrWhiteSpace(rawPat)) continue;
                string pat = rawPat.Trim();
                string patStem = Regex.Replace(pat, @"-\d+$", "");
                if (string.Equals(name2, pat, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(leaf, pat, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stem, pat, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stem, patStem, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(pathStem) && string.Equals(pathStem, patStem, StringComparison.OrdinalIgnoreCase)) ||
                    leaf.StartsWith(patStem, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(pathStem) && pathStem.StartsWith(patStem, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            return false;
        }

        internal static Dictionary<string, object> BalloonViewReliable(
            IModelDoc2 drwModel,
            IDrawingDoc drw,
            IView view,
            List<string> onlyComponents,
            List<string> excludeComponents,
            string layoutStr = "square",
            bool insertMagneticLine = false)
        {
            if (drwModel == null || drw == null || view == null)
                return new Dictionary<string, object> { { "balloon_count", 0 }, { "method", "none" } };

            bool selectiveMode = (onlyComponents != null && onlyComponents.Count > 0) ||
                                 (excludeComponents != null && excludeComponents.Count > 0);
            int placedCount = 0;
            string methodUsed = "AutoBalloon5";

            if (!selectiveMode)
            {
                try
                {
                    drwModel.ClearSelection2(true);
                    string vName = view.GetName2();
                    drw.ActivateView(vName);
                    drwModel.Extension.SelectByID2(vName, "DRAWINGVIEW", 0, 0, 0, false, 0, null, 0);

                    AutoBalloonOptions balOpts = drw.CreateAutoBalloonOptions();
                    if (balOpts != null)
                    {
                        // Critical fix: EditBalloonOption must be explicitly swEditBalloonOption_Replace (0)
                        // and InsertMagneticLine = false by default so tight view layouts do not silently return 0 balloons.
                        balOpts.EditBalloonOption = (int)swEditBalloonOption_e.swEditBalloonOption_Replace;
                        balOpts.InsertMagneticLine = insertMagneticLine;
                        balOpts.IgnoreMultiple = true;
                        balOpts.LeaderAttachmentToFaces = true;
                        balOpts.Style = (int)swBalloonStyle_e.swBS_Circular;
                        balOpts.Size = (int)swBalloonFit_e.swBF_2Chars;
                        balOpts.ItemNumberStart = 1;
                        balOpts.ItemNumberIncrement = 1;

                        string lay = (layoutStr ?? "square").Trim().ToLowerInvariant();
                        if (lay == "circular") balOpts.Layout = (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Circle;
                        else if (lay == "top") balOpts.Layout = (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Top;
                        else if (lay == "bottom") balOpts.Layout = (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Bottom;
                        else if (lay == "left") balOpts.Layout = (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Left;
                        else if (lay == "right") balOpts.Layout = (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Right;
                        else balOpts.Layout = (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Square;

                        object[] notes = drw.AutoBalloon5(balOpts) as object[];
                        if (notes != null)
                            placedCount = notes.Length;
                    }
                    drwModel.ClearSelection2(true);
                }
                catch { }
            }

            // Fallback to or execute selective per-component InsertBOMBalloon2 on IView.GetVisibleEntities2(comp, Edge)
            if (selectiveMode || placedCount == 0)
            {
                methodUsed = selectiveMode ? "InsertBOMBalloon2_selective" : "InsertBOMBalloon2_fallback";
                try
                {
                    object[] visComps = view.GetVisibleComponents() as object[];
                    if (visComps != null && visComps.Length > 0)
                    {
                        List<IComponent2> targets = new List<IComponent2>();
                        HashSet<string> seenPartKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        foreach (object vcObj in visComps)
                        {
                            if (!(vcObj is IComponent2 comp)) continue;
                            try { if (comp.IsSuppressed() || comp.ExcludeFromBOM) continue; } catch { }

                            if (onlyComponents != null && onlyComponents.Count > 0 && !ComponentMatchesAnyFilter(comp, onlyComponents))
                                continue;
                            if (excludeComponents != null && excludeComponents.Count > 0 && ComponentMatchesAnyFilter(comp, excludeComponents))
                                continue;

                            string partKey = comp.GetPathName();
                            if (string.IsNullOrWhiteSpace(partKey))
                                partKey = Regex.Replace(comp.Name2 ?? "", @"-\d+$", "");
                            if (!seenPartKeys.Add(partKey))
                                continue;

                            targets.Add(comp);
                        }

                        double[] outline = view.GetOutline() as double[];
                        double[] vPos = view.Position as double[];
                        double cx = outline != null && outline.Length >= 4 ? 0.5 * (outline[0] + outline[2]) : (vPos != null ? vPos[0] : 0.2);
                        double cy = outline != null && outline.Length >= 4 ? 0.5 * (outline[1] + outline[3]) : (vPos != null ? vPos[1] : 0.14);
                        double rx = outline != null && outline.Length >= 4 ? 0.55 * Math.Max(0.03, outline[2] - outline[0]) + 0.012 : 0.06;
                        double ry = outline != null && outline.Length >= 4 ? 0.55 * Math.Max(0.03, outline[3] - outline[1]) + 0.012 : 0.05;

                        drw.ActivateView(view.GetName2());
                        int nTargets = targets.Count;
                        for (int idx = 0; idx < nTargets; idx++)
                        {
                            IComponent2 comp = targets[idx];
                            IEntity bestEnt = null;
                            try
                            {
                                object[] edges = view.GetVisibleEntities2((Component2)comp, (int)swViewEntityType_e.swViewEntityType_Edge) as object[];
                                if (edges != null && edges.Length > 0)
                                    bestEnt = edges[edges.Length / 2] as IEntity ?? edges[0] as IEntity;
                                if (bestEnt == null)
                                {
                                    object[] faces = view.GetVisibleEntities2((Component2)comp, (int)swViewEntityType_e.swViewEntityType_Face) as object[];
                                    if (faces != null && faces.Length > 0)
                                        bestEnt = faces[0] as IEntity;
                                }
                            }
                            catch { }

                            if (bestEnt == null) continue;

                            drwModel.ClearSelection2(true);
                            if (!bestEnt.Select4(false, null)) continue;

                            BalloonOptions bOpt = drwModel.Extension.CreateBalloonOptions();
                            if (bOpt != null)
                            {
                                bOpt.Style = (int)swBalloonStyle_e.swBS_Circular;
                                bOpt.Size = (int)swBalloonFit_e.swBF_2Chars;
                                bOpt.UpperTextContent = (int)swBalloonTextContent_e.swBalloonTextItemNumber;
                            }
                            INote balNote = drwModel.Extension.InsertBOMBalloon2(bOpt) as INote;
                            if (balNote != null)
                            {
                                double angle = (2.0 * Math.PI * idx) / Math.Max(1, nTargets);
                                double bx = cx + rx * Math.Cos(angle);
                                double by = cy + ry * Math.Sin(angle);
                                IAnnotation bAnn = balNote.GetAnnotation() as IAnnotation;
                                try { bAnn?.SetPosition2(bx, by, 0.0); } catch { try { bAnn?.SetPosition(bx, by, 0.0); } catch { } }
                                placedCount++;
                            }
                        }
                        drwModel.ClearSelection2(true);
                    }
                }
                catch { }
            }

            return new Dictionary<string, object>
            {
                { "view_name", view.GetName2() },
                { "balloon_count", placedCount },
                { "method", methodUsed },
                { "selective", selectiveMode }
            };
        }

        internal static Dictionary<string, object> InsertAndConfigureBomTable(
            ISldWorks app,
            IModelDoc2 drwModel,
            IDrawingDoc drw,
            Dictionary<string, object> bomSpec,
            double sheetWidthM,
            double sheetHeightM,
            out string bomFeatureName)
        {
            bomFeatureName = null;
            if (drwModel == null || drw == null)
                return null;

            Dictionary<string, object> spec = bomSpec ?? new Dictionary<string, object>();
            string reqSheet = JsonHelper.GetString(spec, "sheet_name");
            if (!string.IsNullOrWhiteSpace(reqSheet))
            {
                try { drw.ActivateSheet(reqSheet); } catch { }
            }

            string reqViewName = JsonHelper.GetString(spec, "view_name");
            IView anchorView = null;
            IView iterView = drw.GetFirstView() as IView; // Sheet format view
            if (iterView != null) iterView = iterView.GetNextView() as IView;
            while (iterView != null)
            {
                if (anchorView == null) anchorView = iterView;
                if (!string.IsNullOrWhiteSpace(reqViewName) &&
                    string.Equals(iterView.GetName2(), reqViewName, StringComparison.OrdinalIgnoreCase))
                {
                    anchorView = iterView;
                    break;
                }
                iterView = iterView.GetNextView() as IView;
            }

            if (anchorView == null)
            {
                return new Dictionary<string, object>
                {
                    { "ok", false },
                    { "error", "No drawing view found on sheet to attach BOM table." }
                };
            }

            string bomTypeStr = (JsonHelper.GetString(spec, "bom_type", "top_level_only") ?? "top_level_only").Trim().ToLowerInvariant();
            int bomType = (int)swBomType_e.swBomType_TopLevelOnly;
            if (bomTypeStr == "parts_only") bomType = (int)swBomType_e.swBomType_PartsOnly;
            else if (bomTypeStr == "indented") bomType = (int)swBomType_e.swBomType_Indented;

            string anchorStr = (JsonHelper.GetString(spec, "anchor_type", "top_right") ?? "top_right").Trim().ToLowerInvariant();
            int anchorType = (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_TopRight;
            if (anchorStr == "top_left") anchorType = (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_TopLeft;
            else if (anchorStr == "bottom_right") anchorType = (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_BottomRight;
            else if (anchorStr == "bottom_left") anchorType = (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_BottomLeft;

            double[] posArr = JsonHelper.GetDoubleArray(spec, "position");
            double posX = sheetWidthM * 0.96;
            double posY = sheetHeightM * 0.94;
            bool useAnchorPoint = posArr == null || posArr.Length < 2;
            if (posArr != null && posArr.Length >= 2)
            {
                if (Math.Abs(posArr[0]) <= 1.0 && Math.Abs(posArr[1]) <= 1.0)
                {
                    posX = posArr[0] * sheetWidthM;
                    posY = posArr[1] * sheetHeightM;
                }
                else if (Math.Abs(posArr[0]) > 1.5 || Math.Abs(posArr[1]) > 1.5)
                {
                    posX = posArr[0] * 0.001;
                    posY = posArr[1] * 0.001;
                }
                else
                {
                    posX = posArr[0];
                    posY = posArr[1];
                }
            }

            string cfgName = JsonHelper.GetString(spec, "configuration");
            if (string.IsNullOrWhiteSpace(cfgName))
                cfgName = anchorView.ReferencedConfiguration ?? "";

            string tplPath = SwConnectionManager.ResolveDefaultBomTemplate(app, JsonHelper.GetString(spec, "table_template_path"));
            IBomTableAnnotation bomAnn = anchorView.InsertBomTable4(
                useAnchorPoint,
                posX,
                posY,
                anchorType,
                bomType,
                cfgName,
                tplPath,
                false,
                (int)swNumberingType_e.swNumberingType_Detailed,
                false);

            if (bomAnn == null)
            {
                return new Dictionary<string, object>
                {
                    { "ok", false },
                    { "view_name", anchorView.GetName2() },
                    { "table_template_path", tplPath },
                    { "error", "InsertBomTable4 returned null." }
                };
            }

            ITableAnnotation tbl = bomAnn as ITableAnnotation;
            try
            {
                IBomFeature bf = bomAnn.BomFeature;
                if (bf != null)
                {
                    IFeature bFeat = bf.GetFeature() as IFeature;
                    bomFeatureName = bFeat != null ? bFeat.Name : bf.Name;
                }
            }
            catch { }
            if (string.IsNullOrWhiteSpace(bomFeatureName))
                bomFeatureName = "Bill of Materials1";

            // Declarative column customization via SetColumnType3 + SetColumnCustomProperty (avoiding SetColumnType2 and MoveColumn bugs)
            List<object> colSpecs = JsonHelper.GetList(spec, "columns");
            if (tbl != null && colSpecs != null && colSpecs.Count > 0)
            {
                try
                {
                    while (tbl.ColumnCount < colSpecs.Count)
                    {
                        tbl.InsertColumn2(
                            (int)swTableItemInsertPosition_e.swTableItemInsertPosition_Last,
                            Math.Max(0, tbl.ColumnCount - 1),
                            "",
                            (int)swInsertTableColumnWidthStyle_e.swInsertColumn_DefaultWidth);
                    }
                    while (tbl.ColumnCount > colSpecs.Count)
                    {
                        tbl.DeleteColumn2(tbl.ColumnCount - 1, true);
                    }

                    for (int cIdx = 0; cIdx < colSpecs.Count; cIdx++)
                    {
                        Dictionary<string, object> cd = colSpecs[cIdx] as Dictionary<string, object>
                            ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(colSpecs[cIdx]));
                        if (cd == null) continue;

                        string colType = (JsonHelper.GetString(cd, "type", "") ?? "").Trim().ToLowerInvariant();
                        string propName = JsonHelper.GetString(cd, "property_name") ?? JsonHelper.GetString(cd, "custom_property");
                        string colTitle = JsonHelper.GetString(cd, "title");
                        double widthMm = JsonHelper.GetDouble(cd, "width_mm", 0.0);

                        if (colType == "item" || colType == "item_no" || colType == "item_number")
                        {
                            tbl.SetColumnType3(cIdx, (int)swTableColumnTypes_e.swBomTableColumnType_ItemNumber, false, "");
                        }
                        else if (colType == "quantity" || colType == "qty")
                        {
                            tbl.SetColumnType3(cIdx, (int)swTableColumnTypes_e.swBomTableColumnType_Quantity, false, "");
                        }
                        else if (colType == "part_number" || colType == "part_no")
                        {
                            tbl.SetColumnType3(cIdx, (int)swTableColumnTypes_e.swBomTableColumnType_PartNumber, false, "");
                        }
                        else if (!string.IsNullOrWhiteSpace(propName) || colType == "custom_property" || colType == "property")
                        {
                            string pName = !string.IsNullOrWhiteSpace(propName) ? propName : "Description";
                            tbl.SetColumnType3(cIdx, (int)swTableColumnTypes_e.swBomTableColumnType_CustomProperty, false, pName);
                            bomAnn.SetColumnCustomProperty(cIdx, pName);
                        }

                        if (!string.IsNullOrWhiteSpace(colTitle))
                            tbl.SetColumnTitle2(cIdx, colTitle, true);

                        if (widthMm > 1.0)
                        {
                            tbl.SetColumnWidth(
                                cIdx,
                                widthMm * 0.001,
                                (int)swTableRowColSizeChangeBehavior_e.swTableRowColChange_TableSizeCanChange);
                        }
                    }
                }
                catch { }
            }

            if (!useAnchorPoint && tbl != null)
            {
                try
                {
                    IAnnotation tAnn = ((IBomTableAnnotation)tbl) as IAnnotation;
                    tAnn?.SetPosition2(posX, posY, 0.0);
                }
                catch { }
            }

            // Canonical BOM sorting: OEM parts -> Manufactured parts (by manufacturing technique, then size) -> Fasteners (Screws -> Bolts -> Washers -> Nuts)
            bool sortCanonical = JsonHelper.GetBool(spec, "sort_canonical", true);
            List<object> sortedRowClassifications = null;
            if (sortCanonical && tbl != null)
            {
                sortedRowClassifications = SortDrawingBomTableCanonical(bomAnn, tbl, cfgName);
            }

            // Link views to this BOM across one or all sheets via IView.SetKeepLinkedToBOM
            bool linkAllViews = JsonHelper.GetBool(spec, "link_all_views", true);
            List<string> explicitLinkViews = JsonHelper.GetStringList(spec, "link_views");
            HashSet<string> linkFilter = new HashSet<string>(explicitLinkViews, StringComparer.OrdinalIgnoreCase);
            List<string> linkedViews = new List<string>();
            if (!string.IsNullOrWhiteSpace(bomFeatureName) && (linkAllViews || linkFilter.Count > 0))
            {
                LinkDrawingViewsToBom(drw, bomFeatureName, linkFilter, linkedViews);
            }

            List<string> headers = new List<string>();
            List<List<string>> rows = new List<List<string>>();
            if (tbl != null)
            {
                try
                {
                    int nCols = tbl.ColumnCount;
                    int nRows = tbl.RowCount;
                    for (int c = 0; c < nCols; c++)
                        headers.Add(tbl.GetColumnTitle2(c, true) ?? "");

                    for (int r = 0; r < nRows; r++)
                    {
                        List<string> rowCells = new List<string>(nCols);
                        for (int c = 0; c < nCols; c++)
                            rowCells.Add(tbl.get_Text(r, c) ?? "");
                        rows.Add(rowCells);
                    }
                }
                catch { }
            }

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "bom_feature_name", bomFeatureName },
                { "anchor_view", anchorView.GetName2() },
                { "configuration", cfgName },
                { "table_template_path", tplPath },
                { "sort_canonical", sortCanonical },
                { "sort_order", "oem -> manufactured (by technique, then size) -> fasteners (threaded_inserts -> screws -> bolts -> washers -> nuts)" },
                { "sorted_items", sortedRowClassifications },
                { "column_headers", headers },
                { "row_count", rows.Count },
                { "rows", rows },
                { "linked_views", linkedViews }
            };
        }

        private static BomItemClassification ClassifyDrawingBomRow(IBomTableAnnotation bomAnn, ITableAnnotation tbl, int rowIdx, string cfgName, out IModelDoc2 rowCompDoc, out string rowItemName)
        {
            rowCompDoc = null;
            rowItemName = "";
            string partPath = "";
            bool isSm = false;
            double volMm3 = 0.0, maxDimMm = 0.0;
            Dictionary<string, string> customProps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                object[] rowComps = (!string.IsNullOrEmpty(cfgName) ? bomAnn.GetComponents2(rowIdx, cfgName) as object[] : null)
                                    ?? bomAnn.GetComponents(rowIdx) as object[];
                IComponent2 firstComp = (rowComps != null && rowComps.Length > 0) ? rowComps[0] as IComponent2 : null;
                if (firstComp != null)
                {
                    partPath = firstComp.GetPathName() ?? "";
                    rowItemName = !string.IsNullOrWhiteSpace(partPath)
                        ? Path.GetFileNameWithoutExtension(partPath)
                        : Regex.Replace(firstComp.Name2 ?? "", @"-\d+$", "");
                    rowCompDoc = firstComp.GetModelDoc2() as IModelDoc2;
                    GetComponentBoundingSizeMm(firstComp, out _, out _, out _, out volMm3, out maxDimMm, out isSm);

                    if (rowCompDoc != null)
                    {
                        ICustomPropertyManager docCpm = rowCompDoc.Extension.get_CustomPropertyManager("");
                        string compCfg = firstComp.ReferencedConfiguration ?? "";
                        ICustomPropertyManager cfgCpm = !string.IsNullOrEmpty(compCfg)
                            ? rowCompDoc.Extension.get_CustomPropertyManager(compCfg)
                            : null;
                        foreach (string pn in new[] { "PartNumber", "Description", "BOM_Desc", "BOM_Role", "BOM_Category", "BOM_Technique", "Manufacturing_Technique", "Material", "Vendor" })
                        {
                            string valOut = "", resOut = "";
                            bool wasRes = false;
                            if (cfgCpm != null) cfgCpm.Get5(pn, false, out valOut, out resOut, out wasRes);
                            if (string.IsNullOrWhiteSpace(resOut) && docCpm != null) docCpm.Get5(pn, false, out valOut, out resOut, out wasRes);
                            string finalVal = !string.IsNullOrWhiteSpace(resOut) ? resOut : valOut;
                            if (!string.IsNullOrWhiteSpace(finalVal))
                                customProps[pn] = finalVal;
                        }
                    }
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(partPath))
            {
                try
                {
                    string itemNo = "", partNo = "";
                    object[] paths = bomAnn.GetModelPathNames(rowIdx, out itemNo, out partNo) as object[];
                    if (paths != null && paths.Length > 0 && paths[0] is string p0 && !string.IsNullOrWhiteSpace(p0))
                    {
                        partPath = p0;
                        if (string.IsNullOrWhiteSpace(rowItemName))
                            rowItemName = Path.GetFileNameWithoutExtension(p0);
                    }
                    else if (!string.IsNullOrWhiteSpace(partNo) && string.IsNullOrWhiteSpace(rowItemName))
                    {
                        rowItemName = partNo;
                    }
                }
                catch { }
            }

            List<string> cellTexts = new List<string>();
            try
            {
                int nCols = tbl.ColumnCount;
                for (int c = 0; c < nCols; c++)
                {
                    string ct = tbl.get_Text(rowIdx, c);
                    if (!string.IsNullOrWhiteSpace(ct))
                        cellTexts.Add(ct.Trim());
                }
            }
            catch { }
            string joinedCells = string.Join(" | ", cellTexts);
            if (string.IsNullOrWhiteSpace(rowItemName) && cellTexts.Count > 1)
                rowItemName = cellTexts[1];

            return ClassifyBomEntry(rowItemName, partPath, joinedCells, customProps, isSm, volMm3, maxDimMm);
        }

        internal static List<object> SortDrawingBomTableCanonical(IBomTableAnnotation bomAnn, ITableAnnotation tbl, string cfgName)
        {
            List<object> summary = new List<object>();
            if (bomAnn == null || tbl == null)
                return summary;

            try
            {
                int totalRows = tbl.RowCount;
                if (totalRows <= 2)
                {
                    if (totalRows == 2)
                    {
                        BomItemClassification c1 = ClassifyDrawingBomRow(bomAnn, tbl, 1, cfgName, out _, out string n1);
                        summary.Add(new
                        {
                            item_number = 1,
                            item_name = n1,
                            category = c1.Category,
                            manufacturing_technique = c1.ManufacturingTechnique,
                            fastener_subtype = c1.FastenerSubType,
                            sort_key = c1.SortKey
                        });
                    }
                    return summary;
                }

                // Step A: Write BOM_Sort_Key custom property on loaded component models and invoke native IBomTableAnnotation.Sort
                // with DoNotChangeItemNumber = false so SOLIDWORKS updates linked drawing balloons natively to 1..N.
                for (int r = 1; r < totalRows; r++)
                {
                    BomItemClassification cls = ClassifyDrawingBomRow(bomAnn, tbl, r, cfgName, out IModelDoc2 compDoc, out _);
                    if (compDoc != null)
                    {
                        try
                        {
                            ICustomPropertyManager docCpm = compDoc.Extension.get_CustomPropertyManager("");
                            docCpm?.Add3("BOM_Sort_Key", (int)swCustomInfoType_e.swCustomInfoText, cls.SortKey, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                            string activeCfg = compDoc.ConfigurationManager?.ActiveConfiguration?.Name ?? "";
                            if (!string.IsNullOrEmpty(activeCfg))
                            {
                                ICustomPropertyManager cfgCpm = compDoc.Extension.get_CustomPropertyManager(activeCfg);
                                cfgCpm?.Add3("BOM_Sort_Key", (int)swCustomInfoType_e.swCustomInfoText, cls.SortKey, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                            }
                        }
                        catch { }
                    }
                }

                try
                {
                    int origCols = tbl.ColumnCount;
                    if (tbl.InsertColumn2(
                        (int)swTableItemInsertPosition_e.swTableItemInsertPosition_Last,
                        Math.Max(0, origCols - 1),
                        "BOM_Sort_Key",
                        (int)swInsertTableColumnWidthStyle_e.swInsertColumn_DefaultWidth))
                    {
                        int sortColIdx = tbl.ColumnCount - 1;
                        tbl.SetColumnType3(sortColIdx, (int)swTableColumnTypes_e.swBomTableColumnType_CustomProperty, false, "BOM_Sort_Key");
                        bomAnn.SetColumnCustomProperty(sortColIdx, "BOM_Sort_Key");

                        BomTableSortData sortData = bomAnn.GetBomTableSortData();
                        if (sortData != null)
                        {
                            sortData.set_ColumnIndex(0, sortColIdx);
                            sortData.set_Ascending(0, true);
                            sortData.DoNotChangeItemNumber = false;
                            bomAnn.Sort(sortData);
                        }
                        tbl.DeleteColumn2(sortColIdx, true);
                    }
                }
                catch { }

                // Step B: Verify row order and fallback to ITableAnnotation.MoveRow for any rows whose part doc was read-only/unloaded
                totalRows = tbl.RowCount;
                for (int targetRow = 1; targetRow < totalRows - 1; targetRow++)
                {
                    int bestRow = targetRow;
                    string bestKey = ClassifyDrawingBomRow(bomAnn, tbl, targetRow, cfgName, out _, out _).SortKey;
                    for (int candRow = targetRow + 1; candRow < totalRows; candRow++)
                    {
                        string candKey = ClassifyDrawingBomRow(bomAnn, tbl, candRow, cfgName, out _, out _).SortKey;
                        if (string.Compare(candKey, bestKey, StringComparison.Ordinal) < 0)
                        {
                            bestKey = candKey;
                            bestRow = candRow;
                        }
                    }
                    if (bestRow != targetRow)
                    {
                        try
                        {
                            tbl.MoveRow(bestRow, (int)swTableItemInsertPosition_e.swTableItemInsertPosition_Before, targetRow);
                        }
                        catch { }
                    }
                }

                for (int r = 1; r < tbl.RowCount; r++)
                {
                    BomItemClassification cls = ClassifyDrawingBomRow(bomAnn, tbl, r, cfgName, out _, out string iName);
                    summary.Add(new
                    {
                        item_number = r,
                        item_name = iName,
                        category = cls.Category,
                        manufacturing_technique = cls.ManufacturingTechnique,
                        fastener_subtype = cls.FastenerSubType,
                        sort_key = cls.SortKey
                    });
                }
            }
            catch { }

            return summary;
        }

        private static void LinkDrawingViewsToBom(IDrawingDoc drw, string bomFeatureName, HashSet<string> viewFilter, List<string> linkedOut)
        {
            if (drw == null || string.IsNullOrWhiteSpace(bomFeatureName))
                return;

            string origSheet = (drw.GetCurrentSheet() as ISheet)?.GetName();
            string[] sheetNames = drw.GetSheetNames() as string[] ?? (origSheet != null ? new[] { origSheet } : new string[0]);
            foreach (string sName in sheetNames)
            {
                try
                {
                    drw.ActivateSheet(sName);
                    IView v = drw.GetFirstView() as IView;
                    if (v != null) v = v.GetNextView() as IView;
                    while (v != null)
                    {
                        string vName = v.GetName2();
                        if (viewFilter == null || viewFilter.Count == 0 || viewFilter.Contains(vName))
                        {
                            try
                            {
                                if (v.SetKeepLinkedToBOM(true, bomFeatureName))
                                    linkedOut?.Add(vName);
                            }
                            catch { }
                        }
                        v = v.GetNextView() as IView;
                    }
                }
                catch { }
            }
            if (!string.IsNullOrWhiteSpace(origSheet))
            {
                try { drw.ActivateSheet(origSheet); } catch { }
            }
        }

        internal static Dictionary<string, object> CleanupAndPopulateTitleBlock(
            IModelDoc2 drwModel,
            IDrawingDoc drw,
            string sourcePath,
            string modelUnits,
            string sheetTitle,
            Dictionary<string, object> tbSpec)
        {
            if (drwModel == null || drw == null)
                return null;

            Dictionary<string, object> tb = tbSpec ?? new Dictionary<string, object>();
            string stem = !string.IsNullOrWhiteSpace(sourcePath) ? Path.GetFileNameWithoutExtension(sourcePath) : (drwModel.GetTitle() ?? "DRAWING");
            string title = JsonHelper.GetString(tb, "title");
            if (string.IsNullOrWhiteSpace(title))
                title = !string.IsNullOrWhiteSpace(sheetTitle) ? sheetTitle : stem.Replace('_', ' ');

            string dwgNo = JsonHelper.GetString(tb, "dwg_no");
            if (string.IsNullOrWhiteSpace(dwgNo))
            {
                dwgNo = stem.Length <= 18 ? stem : stem.Substring(0, 18);
            }

            string rev = JsonHelper.GetString(tb, "rev", "A");
            string material = JsonHelper.GetString(tb, "material", "");
            string finish = JsonHelper.GetString(tb, "finish", "");
            string company = JsonHelper.GetString(tb, "company", "SHOP ENGINEERING & FABRICATION");
            string unitsNote = JsonHelper.GetString(tb, "units_note");
            if (string.IsNullOrWhiteSpace(unitsNote))
            {
                unitsNote = string.Equals(modelUnits, "mm", StringComparison.OrdinalIgnoreCase)
                    ? "DIMENSIONS ARE IN MILLIMETERS"
                    : "DIMENSIONS ARE IN INCHES";
            }
            string shopNote = JsonHelper.GetString(tb, "shop_note");

            // Populate drawing custom properties so $PRP / $PRPSHEET links resolve cleanly
            Dictionary<string, object> drwProps = new Dictionary<string, object>
            {
                { "Description", title },
                { "Title", title },
                { "PartNo", dwgNo },
                { "DWG_NO", dwgNo },
                { "Revision", rev },
                { "CompanyName", company }
            };
            if (!string.IsNullOrWhiteSpace(material)) drwProps["Material"] = material;
            if (!string.IsNullOrWhiteSpace(finish)) drwProps["Finish"] = finish;
            FeatureTreeAndAssemblyTools.WriteModelCustomProperties(drwModel, drwProps, alsoConfigSpecific: false);

            int notesCleaned = 0;
            try
            {
                drw.EditTemplate();
                IView tplView = drw.GetFirstView() as IView;
                object[] rawNotes = tplView != null ? tplView.GetNotes() as object[] : null;
                if (rawNotes != null)
                {
                    foreach (object nObj in rawNotes)
                    {
                        if (!(nObj is INote n)) continue;
                        string disp = n.GetText() ?? "";
                        string linked = "";
                        try { linked = n.PropertyLinkedText ?? ""; } catch { }

                        if (disp.IndexOf("<INSERT COMPANY NAME HERE>", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            linked.IndexOf("<INSERT COMPANY NAME HERE>", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            try { n.PropertyLinkedText = company; } catch { }
                            try { n.SetText(company); } catch { }
                            notesCleaned++;
                        }
                        else if (disp.IndexOf("DIMENSIONS ARE IN INCHES", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            string updatedTol = Regex.Replace(disp, @"DIMENSIONS ARE IN INCHES", unitsNote, RegexOptions.IgnoreCase);
                            try { n.PropertyLinkedText = updatedTol; } catch { }
                            try { n.SetText(updatedTol); } catch { }
                            notesCleaned++;
                        }
                        else if (linked.IndexOf("SW-File Name", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // Prevent long descriptive filenames from overflowing DWG. NO. across SIZE and REV
                            try { n.PropertyLinkedText = dwgNo; } catch { }
                            try { n.SetText(dwgNo); } catch { }
                            if (dwgNo.Length > 14)
                            {
                                try
                                {
                                    ITextFormat tf = n.GetTextFormat() as ITextFormat;
                                    if (tf != null)
                                    {
                                        tf.CharHeight = Math.Min(tf.CharHeight > 0 ? tf.CharHeight : 0.003, 0.0026);
                                        n.SetTextFormat(0, tf);
                                    }
                                }
                                catch { }
                            }
                            notesCleaned++;
                        }
                        else if (linked.IndexOf("\"Description\"", StringComparison.OrdinalIgnoreCase) >= 0 && !string.IsNullOrWhiteSpace(title))
                        {
                            try { n.PropertyLinkedText = title; } catch { }
                            try { n.SetText(title); } catch { }
                            notesCleaned++;
                        }
                        else if (linked.IndexOf("\"Material\"", StringComparison.OrdinalIgnoreCase) >= 0 && !string.IsNullOrWhiteSpace(material))
                        {
                            try { n.PropertyLinkedText = material; } catch { }
                            try { n.SetText(material); } catch { }
                            notesCleaned++;
                        }
                        else if (linked.IndexOf("\"Finish\"", StringComparison.OrdinalIgnoreCase) >= 0 && !string.IsNullOrWhiteSpace(finish))
                        {
                            try { n.PropertyLinkedText = finish; } catch { }
                            try { n.SetText(finish); } catch { }
                            notesCleaned++;
                        }
                        else if (linked.IndexOf("\"Revision\"", StringComparison.OrdinalIgnoreCase) >= 0 && !string.IsNullOrWhiteSpace(rev))
                        {
                            try { n.PropertyLinkedText = rev; } catch { }
                            try { n.SetText(rev); } catch { }
                            notesCleaned++;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(shopNote))
                {
                    INote sn = drwModel.InsertNote(shopNote) as INote;
                    if (sn != null)
                    {
                        IAnnotation ann = sn.GetAnnotation() as IAnnotation;
                        try { ann?.SetPosition2(0.02, 0.025, 0.0); } catch { try { ann?.SetPosition(0.02, 0.025, 0.0); } catch { } }
                        notesCleaned++;
                    }
                }
            }
            catch { }
            finally
            {
                try { drw.EditSheet(); } catch { }
            }

            return new Dictionary<string, object>
            {
                { "dwg_no", dwgNo },
                { "title", title },
                { "rev", rev },
                { "company", company },
                { "units_note", unitsNote },
                { "template_notes_updated", notesCleaned }
            };
        }

        internal static bool ExportDrawingPdfSafe(ISldWorks app, IModelDoc2 drwModel, string pdfPath, out int errs, out int warns)
        {
            errs = 0;
            warns = 0;
            if (drwModel == null || string.IsNullOrWhiteSpace(pdfPath))
                return false;

            string dir = Path.GetDirectoryName(pdfPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            IExportPdfData pdfData = null;
            try
            {
                if (app != null)
                {
                    pdfData = app.GetExportFileData((int)swExportDataFileType_e.swExportPdfData) as IExportPdfData;
                    if (pdfData != null)
                    {
                        pdfData.SetSheets((int)swExportDataSheetsToExport_e.swExportData_ExportAllSheets, null);
                        pdfData.ViewPdfAfterSaving = false;
                    }
                }
            }
            catch { pdfData = null; }

            bool ok = drwModel.Extension.SaveAs3(
                pdfPath,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                pdfData,
                null,
                ref errs,
                ref warns);

            if (!ok && pdfData != null)
            {
                ok = drwModel.Extension.SaveAs3(
                    pdfPath,
                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                    null,
                    null,
                    ref errs,
                    ref warns);
            }
            return ok;
        }

        private static int PlaceNotesOnActiveSheet(IModelDoc2 drwModel, List<string> shopNotes, List<object> noteSpecs, double sheetWidthM, double sheetHeightM)
        {
            int added = 0;
            double defaultY = sheetHeightM - 0.025;
            if (shopNotes != null)
            {
                foreach (string noteText in shopNotes)
                {
                    if (string.IsNullOrWhiteSpace(noteText)) continue;
                    INote note = drwModel.InsertNote(noteText) as INote;
                    if (note != null)
                    {
                        IAnnotation ann = note.GetAnnotation() as IAnnotation;
                        try { ann?.SetPosition2(0.02, defaultY, 0.0); } catch { try { ann?.SetPosition(0.02, defaultY, 0.0); } catch { } }
                        defaultY -= 0.014;
                        added++;
                    }
                }
            }

            if (noteSpecs != null)
            {
                foreach (object nsObj in noteSpecs)
                {
                    Dictionary<string, object> ns = nsObj as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(nsObj));
                    if (ns == null) continue;
                    string text = JsonHelper.GetString(ns, "text");
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    double rawX = JsonHelper.GetDouble(ns, "x", 0.02);
                    double rawY = JsonHelper.GetDouble(ns, "y", defaultY);
                    string u = (JsonHelper.GetString(ns, "units", "") ?? "").Trim().ToLowerInvariant();
                    double xM, yM;
                    if (string.IsNullOrEmpty(u) && Math.Abs(rawX) <= 1.0 && Math.Abs(rawY) <= 1.0)
                    {
                        xM = rawX * sheetWidthM;
                        yM = rawY * sheetHeightM;
                    }
                    else if (u == "mm" || Math.Abs(rawX) > 1.5 || Math.Abs(rawY) > 1.5)
                    {
                        xM = rawX * 0.001;
                        yM = rawY * 0.001;
                    }
                    else if (u == "in")
                    {
                        xM = ShopCatalogService.ToMeters(rawX, "in");
                        yM = ShopCatalogService.ToMeters(rawY, "in");
                    }
                    else
                    {
                        xM = rawX;
                        yM = rawY;
                    }

                    INote note = drwModel.InsertNote(text) as INote;
                    if (note != null)
                    {
                        double fontMm = JsonHelper.GetDouble(ns, "font_height_mm", 0.0);
                        bool bold = JsonHelper.GetBool(ns, "bold", false);
                        if (fontMm > 0.5 || bold)
                        {
                            try
                            {
                                ITextFormat tf = note.GetTextFormat() as ITextFormat;
                                if (tf != null)
                                {
                                    if (fontMm > 0.5) tf.CharHeight = fontMm * 0.001;
                                    if (bold) tf.Bold = true;
                                    note.SetTextFormat(0, tf);
                                }
                            }
                            catch { }
                        }
                        IAnnotation ann = note.GetAnnotation() as IAnnotation;
                        try { ann?.SetPosition2(xM, yM, 0.0); } catch { try { ann?.SetPosition(xM, yM, 0.0); } catch { } }
                        added++;
                    }
                }
            }
            return added;
        }

        private static McpToolResult HandleCreateDrawing(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            ISldWorks app = swMgr.GetApp(true);
            string sourcePath = JsonHelper.GetString(args, "source_model_path");
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                IModelDoc2 active = app.ActiveDoc as IModelDoc2;
                sourcePath = active?.GetPathName();
            }
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return McpToolResult.Error("source_model_path must point to a saved .SLDPRT or .SLDASM file.");

            string mode = JsonHelper.GetString(args, "mode", "workshop_sheet").ToLowerInvariant();
            string sheetSize = JsonHelper.GetString(args, "sheet_size", "tabloid_11x17").ToLowerInvariant();
            string defaultConfig = JsonHelper.GetString(args, "configuration");
            string defaultDisplayMode = JsonHelper.GetString(args, "display_mode", "shaded_with_edges");
            bool incExploded = JsonHelper.GetBool(args, "include_exploded_view", true);
            bool incFlat = JsonHelper.GetBool(args, "include_flat_pattern", false);
            List<object> sheetsSpec = JsonHelper.GetList(args, "sheets");
            Dictionary<string, object> topBomSpec = JsonHelper.GetDict(args, "bom");
            Dictionary<string, object> topTitleBlock = JsonHelper.GetDict(args, "title_block");
            List<string> topOnlyBalloons = JsonHelper.GetStringList(args, "only_balloon_components");
            List<string> topExcludeBalloons = JsonHelper.GetStringList(args, "exclude_balloon_components");

            string drwPath = JsonHelper.GetString(args, "save_drw_path");
            if (string.IsNullOrWhiteSpace(drwPath))
            {
                string suffix = mode == "assembly_guide" ? "_Assembly_Instructions.SLDDRW" : ".SLDDRW";
                drwPath = Path.Combine(
                    Path.GetDirectoryName(sourcePath) ?? ".",
                    Path.GetFileNameWithoutExtension(sourcePath) + suffix);
            }

            // PDF documentation is exempt from the manufacturing export gate and auto-exports by default
            bool exportPdf = JsonHelper.GetBool(args, "export_pdf", true);
            string pdfPath = JsonHelper.GetString(args, "export_pdf_path");
            if (exportPdf && string.IsNullOrWhiteSpace(pdfPath))
            {
                pdfPath = Path.ChangeExtension(!string.IsNullOrWhiteSpace(drwPath) ? drwPath : sourcePath, ".pdf");
            }

            string modelUnits = "mm";
            try
            {
                IModelDoc2 srcDoc = app.GetOpenDocumentByName(sourcePath) as IModelDoc2;
                if (srcDoc != null) modelUnits = SwConnectionManager.GetDocumentUnits(srcDoc);
            }
            catch { }

            SwConnectionManager.ResolveSheetPaperGeometry(sheetSize, out int paperEnum, out double widthM, out double heightM, out _);
            string sheetFormatPath = SwConnectionManager.ResolveDefaultSheetFormat(app, sheetSize, JsonHelper.GetString(args, "sheet_format_path"));
            string drwTemplate = SwConnectionManager.ResolveDefaultTemplate(app, "drawing");

            IDrawingDoc drw = app.NewDrawing2(
                (int)swDwgTemplates_e.swDwgTemplateCustom,
                !string.IsNullOrWhiteSpace(sheetFormatPath) ? sheetFormatPath : drwTemplate,
                paperEnum,
                widthM,
                heightM) as IDrawingDoc;

            if (drw == null && !string.IsNullOrWhiteSpace(drwTemplate))
                drw = app.NewDocument(drwTemplate, paperEnum, widthM, heightM) as IDrawingDoc;
            if (drw == null)
                return McpToolResult.Error("NewDrawing2 returned null.");

            IModelDoc2 drwModel = drw as IModelDoc2;
            string masterBomFeatureName = null;
            Dictionary<string, object> masterBomResult = null;
            List<object> sheetReports = new List<object>();

            // Multi-sheet declarative mode when sheets:[...] is provided
            if (sheetsSpec != null && sheetsSpec.Count > 0)
            {
                for (int sIdx = 0; sIdx < sheetsSpec.Count; sIdx++)
                {
                    Dictionary<string, object> sd = sheetsSpec[sIdx] as Dictionary<string, object>
                        ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(sheetsSpec[sIdx]));
                    if (sd == null) continue;

                    string reqSheetName = JsonHelper.GetString(sd, "name", "Sheet" + (sIdx + 1));
                    string sheetTitle = JsonHelper.GetString(sd, "title");
                    string sheetCfg = JsonHelper.GetString(sd, "configuration", defaultConfig);
                    bool sheetExploded = JsonHelper.GetBool(sd, "exploded", incExploded);
                    string sheetDisplayMode = JsonHelper.GetString(sd, "display_mode", defaultDisplayMode);

                    if (sIdx == 0)
                    {
                        ISheet curSheet = drw.GetCurrentSheet() as ISheet;
                        string curName = curSheet != null ? curSheet.GetName() : "Sheet1";
                        if (!string.IsNullOrWhiteSpace(sheetFormatPath))
                        {
                            try
                            {
                                drw.SetupSheet5(curName, paperEnum, (int)swDwgTemplates_e.swDwgTemplateCustom,
                                    1.0, 2.0, false, sheetFormatPath, widthM, heightM, "Default", true);
                            }
                            catch { }
                        }
                        if (curSheet != null && !string.IsNullOrWhiteSpace(reqSheetName) &&
                            !string.Equals(curName, reqSheetName, StringComparison.OrdinalIgnoreCase))
                        {
                            try { curSheet.SetName(reqSheetName); } catch { }
                        }
                    }
                    else
                    {
                        try
                        {
                            drw.NewSheet4(reqSheetName, paperEnum, (int)swDwgTemplates_e.swDwgTemplateCustom,
                                1.0, 2.0, false, sheetFormatPath, widthM, heightM, "Default", 0, 0, 0, 0, 0, 0);
                            drw.ActivateSheet(reqSheetName);
                            if (!string.IsNullOrWhiteSpace(sheetFormatPath))
                            {
                                drw.SetupSheet5(reqSheetName, paperEnum, (int)swDwgTemplates_e.swDwgTemplateCustom,
                                    1.0, 2.0, false, sheetFormatPath, widthM, heightM, "Default", true);
                            }
                        }
                        catch { }
                    }

                    Dictionary<string, object> sheetTb = JsonHelper.GetDict(sd, "title_block") ?? topTitleBlock;
                    Dictionary<string, object> tbReport = CleanupAndPopulateTitleBlock(drwModel, drw, sourcePath, modelUnits, sheetTitle, sheetTb);

                    List<object> viewSpecs = JsonHelper.GetList(sd, "views");
                    List<object> createdViews = new List<object>();
                    List<Tuple<IView, Dictionary<string, object>>> pendingBalloons = new List<Tuple<IView, Dictionary<string, object>>>();

                    for (int vIdx = 0; vIdx < viewSpecs.Count; vIdx++)
                    {
                        Dictionary<string, object> vd = viewSpecs[vIdx] as Dictionary<string, object>
                            ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(viewSpecs[vIdx]));
                        if (vd == null) continue;

                        string vModelPath = JsonHelper.GetString(vd, "source_model_path", sourcePath);
                        string vName = JsonHelper.GetString(vd, "view_name", "*Isometric");
                        string vCfg = JsonHelper.GetString(vd, "configuration", sheetCfg);
                        bool vExploded = JsonHelper.GetBool(vd, "exploded", sheetExploded);
                        string vDisp = JsonHelper.GetString(vd, "display_mode", sheetDisplayMode);
                        string vCaption = JsonHelper.GetString(vd, "caption");

                        double[] rawBox = JsonHelper.GetDoubleArray(vd, "box");
                        double[] boxM = NormalizeLayoutBoxMeters(rawBox, JsonHelper.GetString(vd, "box_units"), widthM, heightM);
                        double[] rawPos = JsonHelper.GetDoubleArray(vd, "position");

                        double initX = widthM * 0.45, initY = heightM * 0.55;
                        if (boxM != null)
                        {
                            initX = 0.5 * (boxM[0] + boxM[2]);
                            initY = 0.5 * (boxM[1] + boxM[3]);
                        }
                        else if (rawPos != null && rawPos.Length >= 2)
                        {
                            if (Math.Abs(rawPos[0]) <= 1.0 && Math.Abs(rawPos[1]) <= 1.0)
                            {
                                initX = rawPos[0] * widthM;
                                initY = rawPos[1] * heightM;
                            }
                            else if (Math.Abs(rawPos[0]) > 1.5 || Math.Abs(rawPos[1]) > 1.5)
                            {
                                initX = rawPos[0] * 0.001;
                                initY = rawPos[1] * 0.001;
                            }
                            else
                            {
                                initX = rawPos[0];
                                initY = rawPos[1];
                            }
                        }

                        IView drawView = drw.CreateDrawViewFromModelView3(vModelPath, vName, initX, initY, 0.0);
                        if (drawView == null)
                        {
                            string normV = ViewportVisionTools.NormalizeStandardViewName(vName);
                            if (!string.IsNullOrEmpty(normV) && !string.Equals(normV, vName, StringComparison.Ordinal))
                                drawView = drw.CreateDrawViewFromModelView3(vModelPath, normV, initX, initY, 0.0);
                        }
                        if (drawView == null)
                        {
                            createdViews.Add(new { view_name = vName, created = false, error = "CreateDrawViewFromModelView3 returned null." });
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(vCfg))
                        {
                            try { drawView.ReferencedConfiguration = vCfg; } catch { }
                        }
                        if (vExploded && vModelPath.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase))
                        {
                            try { drawView.ShowExploded(true); } catch { }
                        }

                        double[] scalePair = JsonHelper.GetDoubleArray(vd, "scale");
                        if (scalePair != null && scalePair.Length >= 2 && scalePair[1] > 0)
                        {
                            try { drawView.ScaleDecimal = scalePair[0] / scalePair[1]; } catch { }
                        }

                        try { drwModel.EditRebuild3(); } catch { }
                        if (boxM != null)
                        {
                            AutoFitViewToBox(drwModel, drawView, boxM);
                        }

                        ApplyViewDisplayMode(drawView, vDisp);

                        if (!string.IsNullOrWhiteSpace(vCaption))
                        {
                            double capX = boxM != null ? boxM[0] + 0.005 : initX - 0.04;
                            double capY = boxM != null ? boxM[1] + 0.006 : initY - 0.06;
                            PlaceNotesOnActiveSheet(drwModel, null, new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    { "text", vCaption },
                                    { "x", capX },
                                    { "y", capY },
                                    { "units", "m" },
                                    { "font_height_mm", 3.5 },
                                    { "bold", true }
                                }
                            }, widthM, heightM);
                        }

                        if (JsonHelper.GetBool(vd, "balloon", false))
                        {
                            pendingBalloons.Add(Tuple.Create(drawView, vd));
                        }

                        createdViews.Add(new
                        {
                            drawing_view_name = drawView.GetName2(),
                            camera_view = vName,
                            configuration = drawView.ReferencedConfiguration,
                            exploded = vExploded,
                            display_mode = vDisp,
                            scale_decimal = Math.Round(drawView.ScaleDecimal, 4),
                            created = true
                        });
                    }

                    Dictionary<string, object> sheetBomSpec = JsonHelper.GetDict(sd, "bom") ?? (sIdx == 0 ? topBomSpec : null);
                    Dictionary<string, object> sheetBomRes = null;
                    if (sheetBomSpec != null || (sIdx == 0 && sourcePath.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase) && (mode == "workshop_sheet" || mode == "assembly_guide")))
                    {
                        sheetBomRes = InsertAndConfigureBomTable(app, drwModel, drw, sheetBomSpec, widthM, heightM, out string bfName);
                        if (!string.IsNullOrWhiteSpace(bfName) && string.IsNullOrWhiteSpace(masterBomFeatureName))
                        {
                            masterBomFeatureName = bfName;
                            masterBomResult = sheetBomRes;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(masterBomFeatureName))
                    {
                        LinkDrawingViewsToBom(drw, masterBomFeatureName, null, null);
                    }

                    List<object> balloonReports = new List<object>();
                    foreach (Tuple<IView, Dictionary<string, object>> pb in pendingBalloons)
                    {
                        List<string> onlyComps = JsonHelper.GetStringList(pb.Item2, "only_balloon_components");
                        List<string> exclComps = JsonHelper.GetStringList(pb.Item2, "exclude_balloon_components");
                        string lay = JsonHelper.GetString(pb.Item2, "balloon_layout", "square");
                        bool mag = JsonHelper.GetBool(pb.Item2, "insert_magnetic_line", false);
                        balloonReports.Add(BalloonViewReliable(drwModel, drw, pb.Item1, onlyComps, exclComps, lay, mag));
                    }

                    int notesAdded = PlaceNotesOnActiveSheet(
                        drwModel,
                        JsonHelper.GetStringList(sd, "notes"),
                        JsonHelper.GetList(sd, "note_specs"),
                        widthM,
                        heightM);

                    sheetReports.Add(new
                    {
                        sheet_name = reqSheetName,
                        views = createdViews,
                        bom = sheetBomRes,
                        balloons = balloonReports,
                        notes_added = notesAdded,
                        title_block = tbReport
                    });
                }
            }
            else
            {
                // Single-sheet turnkey mode (with proper .slddrt sheet format, BOM template, title block cleanup, and auto-fit)
                ISheet curSheet = drw.GetCurrentSheet() as ISheet;
                string curSheetName = curSheet != null ? curSheet.GetName() : "Sheet1";
                if (!string.IsNullOrWhiteSpace(sheetFormatPath))
                {
                    try
                    {
                        drw.SetupSheet5(curSheetName, paperEnum, (int)swDwgTemplates_e.swDwgTemplateCustom,
                            1.0, 2.0, false, sheetFormatPath, widthM, heightM, "Default", true);
                    }
                    catch { }
                }

                Dictionary<string, object> tbReport = CleanupAndPopulateTitleBlock(drwModel, drw, sourcePath, modelUnits, null, topTitleBlock);
                drw.Create3rdAngleViews2(sourcePath);

                IView isoView = drw.CreateDrawViewFromModelView3(sourcePath, "*Isometric", widthM * 0.76, heightM * 0.62, 0.0);
                if (isoView != null)
                {
                    if (!string.IsNullOrWhiteSpace(defaultConfig))
                    {
                        try { isoView.ReferencedConfiguration = defaultConfig; } catch { }
                    }
                    if (incExploded && sourcePath.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase))
                    {
                        try { isoView.ShowExploded(true); } catch { }
                    }
                    AutoFitViewToBox(drwModel, isoView, new[] { widthM * 0.54, heightM * 0.30, widthM * 0.96, heightM * 0.78 });
                    ApplyViewDisplayMode(isoView, defaultDisplayMode);
                }

                if (incFlat && sourcePath.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase))
                {
                    drw.CreateDrawViewFromModelView3(sourcePath, "Flat pattern", widthM * 0.78, heightM * 0.30, 0.0);
                }

                if (isoView != null && sourcePath.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase))
                {
                    masterBomResult = InsertAndConfigureBomTable(app, drwModel, drw, topBomSpec, widthM, heightM, out masterBomFeatureName);
                    if (mode == "workshop_sheet" || mode == "assembly_guide")
                    {
                        BalloonViewReliable(drwModel, drw, isoView, topOnlyBalloons, topExcludeBalloons, "square", false);
                    }
                }

                sheetReports.Add(new
                {
                    sheet_name = curSheetName,
                    bom = masterBomResult,
                    title_block = tbReport
                });
            }

            try { drwModel.EditRebuild3(); } catch { }

            bool drwSaved = false;
            if (!string.IsNullOrWhiteSpace(drwPath))
            {
                string dir = Path.GetDirectoryName(drwPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                int errs = 0, warns = 0;
                drwSaved = drwModel.Extension.SaveAs3(drwPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errs, ref warns);
            }

            bool pdfExported = false;
            if (exportPdf && !string.IsNullOrWhiteSpace(pdfPath))
            {
                pdfExported = ExportDrawingPdfSafe(app, drwModel, pdfPath, out _, out _);
            }

            return McpToolResult.JsonText(new
            {
                drawing_title = drwModel.GetTitle(),
                mode = mode,
                sheet_size = sheetSize,
                sheet_format_path = sheetFormatPath,
                sheet_count = sheetReports.Count,
                sheets = sheetReports,
                master_bom_feature = masterBomFeatureName,
                saved_drw_path = drwPath,
                drw_saved = drwSaved,
                exported_pdf_path = pdfPath,
                pdf_exported = pdfExported
            });
        }

        private static McpToolResult HandleInsertBomTable(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            if (!(doc is IDrawingDoc drw))
                return McpToolResult.Error("sw_insert_bom_table requires an active Drawing (.SLDDRW) document.");

            ISldWorks app = swMgr.GetApp(false);
            SwConnectionManager.ActivateDocumentSafe(app, doc);

            double widthM = 0.4318, heightM = 0.2794;
            try
            {
                ISheet cur = drw.GetCurrentSheet() as ISheet;
                double[] props = cur != null ? cur.GetProperties2() as double[] : null;
                if (props != null && props.Length >= 7 && props[5] > 0.05 && props[6] > 0.05)
                {
                    widthM = props[5];
                    heightM = props[6];
                }
            }
            catch { }

            Dictionary<string, object> res = InsertAndConfigureBomTable(app, doc, drw, args, widthM, heightM, out _);
            try { doc.EditRebuild3(); } catch { }

            bool exportPdf = JsonHelper.GetBool(args, "export_pdf", true);
            string pdfPath = JsonHelper.GetString(args, "export_pdf_path");
            if (exportPdf && string.IsNullOrWhiteSpace(pdfPath))
            {
                string docPath = doc.GetPathName();
                if (!string.IsNullOrWhiteSpace(docPath))
                    pdfPath = Path.ChangeExtension(docPath, ".pdf");
            }
            if (exportPdf && !string.IsNullOrWhiteSpace(pdfPath))
            {
                try { SwHelper.SaveSilent(doc); } catch { }
                res["pdf_exported"] = ExportDrawingPdfSafe(app, doc, pdfPath, out _, out _);
                res["exported_pdf_path"] = pdfPath;
            }

            bool ok = JsonHelper.GetBool(res, "ok", false);
            return McpToolResult.JsonText(res, isError: !ok);
        }

        private static McpToolResult HandleAddDrawingCallouts(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            if (!(doc is IDrawingDoc drw))
                return McpToolResult.Error("Active document must be a Drawing (.SLDDRW).");

            ISldWorks app = swMgr.GetApp(false);
            SwConnectionManager.ActivateDocumentSafe(app, doc);

            string reqSheet = JsonHelper.GetString(args, "sheet_name");
            if (!string.IsNullOrWhiteSpace(reqSheet))
            {
                try { drw.ActivateSheet(reqSheet); } catch { }
            }

            double widthM = 0.4318, heightM = 0.2794;
            try
            {
                ISheet cur = drw.GetCurrentSheet() as ISheet;
                double[] props = cur != null ? cur.GetProperties2() as double[] : null;
                if (props != null && props.Length >= 7 && props[5] > 0.05 && props[6] > 0.05)
                {
                    widthM = props[5];
                    heightM = props[6];
                }
            }
            catch { }

            int added = PlaceNotesOnActiveSheet(
                doc,
                JsonHelper.GetStringList(args, "shop_notes"),
                JsonHelper.GetList(args, "note_specs"),
                widthM,
                heightM);

            Dictionary<string, object> tbSpec = JsonHelper.GetDict(args, "title_block");
            Dictionary<string, object> tbReport = null;
            if (tbSpec != null)
            {
                tbReport = CleanupAndPopulateTitleBlock(doc, drw, doc.GetPathName(), SwConnectionManager.GetDocumentUnits(doc), null, tbSpec);
            }

            Dictionary<string, object> bomSpec = JsonHelper.GetDict(args, "bom");
            Dictionary<string, object> bomReport = null;
            if (bomSpec != null)
            {
                bomReport = InsertAndConfigureBomTable(app, doc, drw, bomSpec, widthM, heightM, out _);
            }

            List<object> balloonSpecs = JsonHelper.GetList(args, "balloon_views");
            List<object> balloonReports = new List<object>();
            if (balloonSpecs != null && balloonSpecs.Count > 0)
            {
                foreach (object bsObj in balloonSpecs)
                {
                    Dictionary<string, object> bs = bsObj as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(bsObj));
                    if (bs == null) continue;
                    string vName = JsonHelper.GetString(bs, "view_name");
                    IView targetView = null;
                    IView vIter = drw.GetFirstView() as IView;
                    if (vIter != null) vIter = vIter.GetNextView() as IView;
                    while (vIter != null)
                    {
                        if (targetView == null) targetView = vIter;
                        if (!string.IsNullOrWhiteSpace(vName) && string.Equals(vIter.GetName2(), vName, StringComparison.OrdinalIgnoreCase))
                        {
                            targetView = vIter;
                            break;
                        }
                        vIter = vIter.GetNextView() as IView;
                    }
                    if (targetView != null)
                    {
                        balloonReports.Add(BalloonViewReliable(
                            doc,
                            drw,
                            targetView,
                            JsonHelper.GetStringList(bs, "only_balloon_components"),
                            JsonHelper.GetStringList(bs, "exclude_balloon_components"),
                            JsonHelper.GetString(bs, "layout", "square"),
                            JsonHelper.GetBool(bs, "insert_magnetic_line", false)));
                    }
                }
            }

            try { doc.EditRebuild3(); } catch { }

            bool exportPdf = JsonHelper.GetBool(args, "export_pdf", true);
            string pdfPath = JsonHelper.GetString(args, "export_pdf_path");
            if (exportPdf && string.IsNullOrWhiteSpace(pdfPath))
            {
                string docPath = doc.GetPathName();
                if (!string.IsNullOrWhiteSpace(docPath))
                    pdfPath = Path.ChangeExtension(docPath, ".pdf");
            }
            bool pdfExported = false;
            if (exportPdf && !string.IsNullOrWhiteSpace(pdfPath))
            {
                try { SwHelper.SaveSilent(doc); } catch { }
                pdfExported = ExportDrawingPdfSafe(app, doc, pdfPath, out _, out _);
            }

            return McpToolResult.JsonText(new
            {
                drawing_title = doc.GetTitle(),
                notes_added = added,
                title_block = tbReport,
                bom = bomReport,
                balloons = balloonReports,
                exported_pdf_path = pdfPath,
                pdf_exported = pdfExported
            });
        }
    }
}
