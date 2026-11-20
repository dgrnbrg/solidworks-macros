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
                Description = "Extract a unified BOM listing McMaster-Carr SKUs, component quantities, custom properties, and body dimensions from an assembly or multibody part.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") },
                    { "units", JsonHelper.Prop("string", "Units for part dimensions: in or mm.") }
                }),
                Handler = args => HandleExtractBom(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_create_exploded_assembly_view",
                Description = "Create an exploded view on the active SOLIDWORKS assembly using explicit directional steps (recommended; auto_explode is disabled by default and guarded on >20 components).",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "auto_explode", JsonHelper.Prop("boolean", "Run AutoExplode only on small assemblies (<=20 components) when no manual steps are specified.", false) },
                    { "show_exploded", JsonHelper.Prop("boolean", "Display the assembly in exploded state.", true) },
                    { "steps", JsonHelper.ArrayProp("object", "Steps: [{component_name, component_names, distance, direction_index (0=X,1=Y,2=Z), reverse, units}].") },
                    { "target_doc", JsonHelper.Prop("string", "Optional assembly title/path.") }
                }),
                Handler = args => HandleExplodedView(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_create_fabrication_drawing",
                Description = "Generate a 2D .SLDDRW drawing (Workshop Printable Sheet w/ exploded view, balloons, & BOM OR SendCutSend/Machine-Shop Vendor sheet w/ 3rd-angle & flat pattern) and export PDF.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "source_model_path", JsonHelper.Prop("string", "Path to source .SLDPRT or .SLDASM (defaults to active doc path).") },
                    { "mode", JsonHelper.Prop("string", "workshop_sheet, sendcutsend_vendor, or machine_shop_vendor.", "workshop_sheet", new[] { "workshop_sheet", "sendcutsend_vendor", "machine_shop_vendor" }) },
                    { "sheet_size", JsonHelper.Prop("string", "letter_8_5x11 or tabloid_11x17.", "tabloid_11x17", new[] { "letter_8_5x11", "tabloid_11x17" }) },
                    { "include_exploded_view", JsonHelper.Prop("boolean", "Add exploded isometric view if assembly.", true) },
                    { "include_flat_pattern", JsonHelper.Prop("boolean", "Add flat pattern view if sheet metal part.", false) },
                    { "save_drw_path", JsonHelper.Prop("string", "Optional .SLDDRW output path.") },
                    { "export_pdf_path", JsonHelper.Prop("string", "Optional .PDF output path for workshop printing/vendor upload.") }
                }),
                Handler = args => HandleCreateDrawing(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_add_drawing_callouts_and_bom",
                Description = "Add hole callouts, bend tables, or shop fabrication notes onto an active SOLIDWORKS drawing sheet.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "shop_notes", JsonHelper.ArrayProp("string", "Text notes to place on the drawing sheet (e.g. Domino/Forstner/clamp/material specs).") },
                    { "insert_bend_table", JsonHelper.Prop("boolean", "Insert bend table on flat-pattern view.", false) },
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

        private static McpToolResult HandleExtractBom(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> metadata = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            if (doc is IAssemblyDoc assy)
            {
                object[] comps = assy.GetComponents(false) as object[];
                if (comps != null)
                {
                    foreach (object cObj in comps)
                    {
                        if (!(cObj is IComponent2 c)) continue;
                        if (c.IsSuppressed() || c.ExcludeFromBOM) continue;

                        string path = c.GetPathName() ?? c.Name2;
                        string key = Path.GetFileNameWithoutExtension(path);
                        counts[key] = counts.TryGetValue(key, out int prev) ? prev + 1 : 1;

                        if (!metadata.ContainsKey(key))
                        {
                            Match m = McMasterRegex.Match(key);
                            metadata[key] = new
                            {
                                item_name = key,
                                path = path,
                                is_mcmaster = m.Success,
                                mcmaster_pn = m.Success ? m.Value.ToUpperInvariant() : null
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
                        metadata[key] = new
                        {
                            item_name = key,
                            is_sheet_metal = b.IsSheetMetal(),
                            units = units,
                            size = bb != null && bb.Length >= 6 ? new
                            {
                                x = Math.Round(ShopCatalogService.FromMeters(bb[3] - bb[0], units), 4),
                                y = Math.Round(ShopCatalogService.FromMeters(bb[4] - bb[1], units), 4),
                                z = Math.Round(ShopCatalogService.FromMeters(bb[5] - bb[2], units), 4)
                            } : null
                        };
                    }
                }
            }

            List<object> bomLines = new List<object>();
            foreach (var kv in counts)
            {
                bomLines.Add(new
                {
                    qty = kv.Value,
                    info = metadata[kv.Key]
                });
            }

            return McpToolResult.JsonText(new
            {
                document = doc.GetTitle(),
                unique_items = bomLines.Count,
                bom = bomLines
            });
        }

        private static McpToolResult HandleExplodedView(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            if (!(doc is IAssemblyDoc assy))
                return McpToolResult.Error("Exploded views require an active Assembly (.SLDASM).");

            bool autoExplode = JsonHelper.GetBool(args, "auto_explode", false);
            bool showExploded = JsonHelper.GetBool(args, "show_exploded", true);
            List<object> steps = JsonHelper.GetList(args, "steps");
            string defaultUnits = SwConnectionManager.GetDocumentUnits(doc);

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

            bool ok = assy.CreateExplodedView();
            int addedSteps = 0;

            if (steps.Count > 0)
            {
                IConfiguration cfg = doc.ConfigurationManager?.ActiveConfiguration;
                if (cfg == null)
                    return McpToolResult.Error("Could not acquire active configuration for explode steps.");

                foreach (object sObj in steps)
                {
                    Dictionary<string, object> sd = sObj as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(sObj));
                    if (sd == null) continue;

                    List<string> targetComps = JsonHelper.GetStringList(sd, "component_names");
                    string singleComp = JsonHelper.GetString(sd, "component_name");
                    if (!string.IsNullOrWhiteSpace(singleComp)) targetComps.Add(singleComp);
                    if (targetComps.Count == 0) continue;

                    string stepUnits = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(sd, "units", defaultUnits));
                    double distVal = JsonHelper.GetDouble(sd, "distance", 50.0);
                    double distM = Math.Abs(ShopCatalogService.ToMeters(distVal, stepUnits));
                    bool reverse = JsonHelper.GetBool(sd, "reverse", distVal < 0.0);
                    int dirIdx = JsonHelper.GetInt(sd, "direction_index", 2);
                    if (dirIdx < 0 || dirIdx > 2) dirIdx = 2;

                    doc.ClearSelection2(true);
                    List<IComponent2> resolved = AssemblyGeometryHelper.ResolveComponentsMatching(assy, targetComps);
                    int sel = 0;
                    foreach (IComponent2 c in resolved)
                    {
                        if (c.Select4(sel > 0, null, false)) sel++;
                    }
                    if (sel > 0)
                    {
                        int err = 0;
                        object step = cfg.AddExplodeStep2(
                            distM,
                            dirIdx,
                            reverse,
                            0.0, 0, false, false, true, out err);
                        if (step != null) addedSteps++;
                    }
                    doc.ClearSelection2(true);
                }
            }
            else if (autoExplode)
            {
                assy.AutoExplode();
            }

            if (showExploded)
                assy.ShowExploded(true);

            return McpToolResult.JsonText(new
            {
                exploded_view_created = ok,
                auto_exploded = steps.Count == 0 && autoExplode,
                manual_steps_added = addedSteps,
                showing_exploded = showExploded
            });
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
            bool incExploded = JsonHelper.GetBool(args, "include_exploded_view", true);
            bool incFlat = JsonHelper.GetBool(args, "include_flat_pattern", false);
            string drwPath = JsonHelper.GetString(args, "save_drw_path");
            string pdfPath = JsonHelper.GetString(args, "export_pdf_path");

            int paperEnum = sheetSize == "letter_8_5x11"
                ? (int)swDwgPaperSizes_e.swDwgPaperAsize
                : (int)swDwgPaperSizes_e.swDwgPaperBsize;
            double widthM = sheetSize == "letter_8_5x11" ? 0.2794 : 0.4318;
            double heightM = sheetSize == "letter_8_5x11" ? 0.2159 : 0.2794;

            string drwTemplate = SwConnectionManager.ResolveDefaultTemplate(app, "drawing");
            IDrawingDoc drw = app.NewDrawing2(
                (int)swDwgTemplates_e.swDwgTemplateCustom,
                drwTemplate,
                paperEnum,
                widthM,
                heightM) as IDrawingDoc;

            if (drw == null)
                return McpToolResult.Error("NewDrawing2 returned null.");

            IModelDoc2 drwModel = drw as IModelDoc2;
            drw.Create3rdAngleViews2(sourcePath);

            IView isoView = drw.CreateDrawViewFromModelView3(sourcePath, "*Isometric", widthM * 0.78, heightM * 0.72, 0.0);
            if (isoView != null && incExploded && sourcePath.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase))
            {
                isoView.ShowExploded(true);
                if (mode == "workshop_sheet")
                {
                    AutoBalloonOptions balOpts = drw.CreateAutoBalloonOptions();
                    drw.AutoBalloon5(balOpts);
                }
            }

            if (incFlat && sourcePath.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase))
            {
                drw.CreateDrawViewFromModelView3(sourcePath, "Flat pattern", widthM * 0.78, heightM * 0.30, 0.0);
            }

            if (isoView != null && sourcePath.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase))
            {
                isoView.InsertBomTable4(false, widthM * 0.95, heightM * 0.95, (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_TopRight,
                    (int)swBomType_e.swBomType_TopLevelOnly, "", "", false, 0, false);
            }

            if (!string.IsNullOrWhiteSpace(drwPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(drwPath));
                int errs = 0, warns = 0;
                drwModel.Extension.SaveAs3(drwPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errs, ref warns);
            }

            if (!string.IsNullOrWhiteSpace(pdfPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pdfPath));
                int errs = 0, warns = 0;
                drwModel.Extension.SaveAs3(pdfPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errs, ref warns);
            }

            return McpToolResult.JsonText(new
            {
                drawing_title = drwModel.GetTitle(),
                mode = mode,
                sheet_size = sheetSize,
                saved_drw_path = drwPath,
                exported_pdf_path = pdfPath
            });
        }

        private static McpToolResult HandleAddDrawingCallouts(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            if (!(doc is IDrawingDoc drw))
                return McpToolResult.Error("Active document must be a Drawing (.SLDDRW).");

            List<string> notes = JsonHelper.GetStringList(args, "shop_notes");
            int added = 0;
            double y = 0.25;
            foreach (string noteText in notes)
            {
                INote note = doc.InsertNote(noteText) as INote;
                if (note != null)
                {
                    IAnnotation ann = note.GetAnnotation() as IAnnotation;
                    ann?.SetPosition(0.02, y, 0.0);
                    y -= 0.015;
                    added++;
                }
            }

            return McpToolResult.JsonText(new
            {
                notes_added = added,
                drawing_title = doc.GetTitle()
            });
        }
    }
}
