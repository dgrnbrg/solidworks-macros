using System;
using System.Collections.Generic;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcpServer.Core;
using SolidWorksMcpServer.Protocol;

namespace SolidWorksMcpServer.Tools
{
    public static class DocumentAndSessionTools
    {
        public static void Register(McpStdioHost host, SwConnectionManager swMgr, ShopCatalogService catalog)
        {
            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_connect_status",
                Description = "Attach to a running SOLIDWORKS session (or launch/restart it via 3DEXPERIENCE in Microsoft Edge) and inspect open documents and active document units.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "launch_if_closed", JsonHelper.Prop("boolean", "Launch SOLIDWORKS via 3DEXPERIENCE in Microsoft Edge if not already running.", false) },
                    { "restart_if_running", JsonHelper.Prop("boolean", "Close any running SOLIDWORKS session and restart via 3DEXPERIENCE in Microsoft Edge.", false) },
                    { "force_restart", JsonHelper.Prop("boolean", "Force restart even if open documents have unsaved changes.", false) },
                    { "wait_timeout_seconds", JsonHelper.Prop("integer", "Max seconds to wait for SOLIDWORKS COM registration after 3DEXPERIENCE launch.", 90) }
                }),
                Handler = args => HandleConnectStatus(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_open_or_import_document",
                Description = "Open a SOLIDWORKS file (.SLDPRT, .SLDASM, .SLDDRW) or import neutral CAD (.STEP, .STP, .IGES, .X_T), optionally enforcing MMGS or IPS units.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "file_path", JsonHelper.Prop("string", "Absolute path to the CAD file.") },
                    { "unit_override", JsonHelper.Prop("string", "Optional unit system override: MMGS or IPS.") }
                }, "file_path"),
                Handler = args => HandleOpenOrImport(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_create_document",
                Description = "Create a new SOLIDWORKS part, assembly, or drawing and enforce domain-appropriate units (MMGS for 3D printing/Gridfinity/KiCad; IPS for woodworking/CNC/electrical/SendCutSend).",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "doc_type", JsonHelper.Prop("string", "Document type: part, assembly, or drawing.", "part", new[] { "part", "assembly", "drawing" }) },
                    { "domain", JsonHelper.Prop("string", "Shop domain determining unit system.", "3d_printing", new[] { "3d_printing", "gridfinity", "electronics_kicad", "woodworking", "cnc_furniture_and_jigs", "residential_electrical", "sheet_metal_sendcutsend" }) },
                    { "save_path", JsonHelper.Prop("string", "Optional file path to save immediately.") },
                    { "template_path", JsonHelper.Prop("string", "Optional custom .PRTDOT/.ASMDOT/.DRWDOT template path.") }
                }),
                Handler = args => HandleCreateDocument(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_save_document",
                Description = "Save the active (or specified) SOLIDWORKS document.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path; defaults to active document.") },
                    { "save_path", JsonHelper.Prop("string", "Optional SaveAs path.") },
                    { "save_all_components", JsonHelper.Prop("boolean", "Save referenced assembly components as well.", false) }
                }),
                Handler = args => HandleSaveDocument(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_export_document",
                Description = "Export active SOLIDWORKS model to STEP, STL, 3MF, DXF, DWG, or PDF.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "format", JsonHelper.Prop("string", "Output format.", "step", new[] { "step", "stl", "3mf", "dxf", "dwg", "pdf" }) },
                    { "output_path", JsonHelper.Prop("string", "Target output file path.") },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }, "format", "output_path"),
                Handler = args => HandleExportDocument(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_get_shop_profile",
                Description = "Query the shop equipment profile, manufacturing preference hierarchy, McMaster inserts, CNC bits, woodworking limits, Gridfinity specs, or SendCutSend material/bend catalog.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "section", JsonHelper.Prop("string", "Profile section to return.", "all", new[] { "all", "hierarchy", "printing_3d", "gridfinity", "electronics_and_electrical", "airflow_and_pneumatics", "cnc_carving", "woodworking", "sheet_metal_sendcutsend", "sendcutsend_catalog" }) },
                    { "material", JsonHelper.Prop("string", "Optional SendCutSend material key (e.g. 5052_aluminum, mild_steel_1008).") },
                    { "thickness_in", JsonHelper.Prop("number", "Optional thickness in inches to look up exact SendCutSend bend/flange rules.") }
                }),
                Handler = args => HandleGetShopProfile(catalog, args)
            });
        }

        private static McpToolResult HandleConnectStatus(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            bool launch = JsonHelper.GetBool(args, "launch_if_closed", false);
            bool restartIfRunning = JsonHelper.GetBool(args, "restart_if_running", false);
            bool forceRestart = JsonHelper.GetBool(args, "force_restart", false);
            int waitTimeoutSeconds = Math.Max(30, JsonHelper.GetInt(args, "wait_timeout_seconds", 90));

            ThreeDExperienceLaunchResult launchResult = null;
            if (restartIfRunning)
            {
                try
                {
                    launchResult = swMgr.LaunchOrRestartVia3DExperience(true, forceRestart, waitTimeoutSeconds);
                }
                catch (Exception ex)
                {
                    return McpToolResult.JsonText(new
                    {
                        connected = false,
                        message = ex.Message
                    });
                }
            }
            else if (!launch && !swMgr.TryAttachOnly(out ISldWorks _))
            {
                System.Diagnostics.Process[] running = System.Diagnostics.Process.GetProcessesByName("SLDWORKS");
                if (running.Length > 0)
                {
                    return McpToolResult.JsonText(new
                    {
                        connected = false,
                        running_pids = Array.ConvertAll(running, p => p.Id),
                        message = "SLDWORKS.exe is running but its COM interface is not registered in the ROT yet (still loading add-ins, showing a modal/recovery dialog, or running as Administrator)."
                    });
                }

                return McpToolResult.JsonText(new
                {
                    connected = false,
                    message = "SOLIDWORKS is not running in this desktop session. Call sw_connect_status(launch_if_closed=true) to log into 3DEXPERIENCE in Edge and launch SOLIDWORKS Design."
                });
            }
            else if (launch && !swMgr.TryAttachOnly(out ISldWorks _) &&
                     System.Diagnostics.Process.GetProcessesByName("SLDWORKS").Length == 0)
            {
                try
                {
                    launchResult = swMgr.LaunchOrRestartVia3DExperience(false, forceRestart, waitTimeoutSeconds);
                }
                catch (Exception ex)
                {
                    return McpToolResult.JsonText(new
                    {
                        connected = false,
                        message = ex.Message
                    });
                }
            }

            if (launchResult != null && !launchResult.Connected)
            {
                return McpToolResult.JsonText(new
                {
                    connected = false,
                    launched_process = launchResult.LaunchedProcess,
                    restarted = launchResult.Restarted,
                    edge_window_title = launchResult.EdgeWindowTitle,
                    current_url = launchResult.CurrentUrl,
                    clicked_target = launchResult.ClickedTarget,
                    message = launchResult.StatusMessage,
                    launch_logs = launchResult.Logs
                });
            }

            ISldWorks app;
            try
            {
                app = swMgr.GetApp(launch);
            }
            catch (InvalidOperationException ex)
            {
                return McpToolResult.JsonText(new
                {
                    connected = false,
                    message = ex.Message,
                    launch_logs = launchResult != null ? launchResult.Logs : null
                });
            }
            List<object> openDocs = new List<object>();
            object[] docs = app.GetDocuments() as object[];
            if (docs != null)
            {
                foreach (object dObj in docs)
                {
                    if (dObj is IModelDoc2 d)
                    {
                        openDocs.Add(new
                        {
                            title = d.GetTitle(),
                            path = d.GetPathName(),
                            doc_type = d.GetType(),
                            units = SwConnectionManager.GetDocumentUnits(d)
                        });
                    }
                }
            }

            IModelDoc2 active = app.ActiveDoc as IModelDoc2;
            return McpToolResult.JsonText(new
            {
                connected = true,
                revision = app.RevisionNumber(),
                active_doc = active != null ? new
                {
                    title = active.GetTitle(),
                    path = active.GetPathName(),
                    doc_type = active.GetType(),
                    units = SwConnectionManager.GetDocumentUnits(active)
                } : null,
                open_documents = openDocs,
                launch_logs = launchResult != null ? launchResult.Logs : null
            });
        }

        private static McpToolResult HandleOpenOrImport(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            string filePath = JsonHelper.GetString(args, "file_path");
            string unitOverride = JsonHelper.GetString(args, "unit_override");

            ISldWorks app = swMgr.GetApp(true);
            IModelDoc2 doc = swMgr.OpenOrImport(app, filePath, out string warnings);

            string enforced = null;
            if (!string.IsNullOrWhiteSpace(unitOverride))
                enforced = SwConnectionManager.SetDocumentUnitSystem(doc, unitOverride);

            int bodyCount = 0;
            if (doc is IPartDoc part)
            {
                object[] bodies = part.GetBodies2((int)swBodyType_e.swAllBodies, false) as object[];
                bodyCount = bodies != null ? bodies.Length : 0;
            }

            return McpToolResult.JsonText(new
            {
                title = doc.GetTitle(),
                path = doc.GetPathName(),
                doc_type = doc.GetType(),
                units = SwConnectionManager.GetDocumentUnits(doc),
                enforced_unit_system = enforced,
                body_count = bodyCount,
                warnings = warnings
            });
        }

        private static McpToolResult HandleCreateDocument(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            string docType = JsonHelper.GetString(args, "doc_type", "part").ToLowerInvariant();
            string domain = JsonHelper.GetString(args, "domain", "3d_printing");
            string savePath = JsonHelper.GetString(args, "save_path");
            string template = JsonHelper.GetString(args, "template_path");

            ISldWorks app = swMgr.GetApp(true);
            if (string.IsNullOrWhiteSpace(template))
                template = SwConnectionManager.ResolveDefaultTemplate(app, docType);

            IModelDoc2 doc = app.NewDocument(template, (int)swDwgPaperSizes_e.swDwgPaperBsize, 0, 0) as IModelDoc2;
            if (doc == null)
                return McpToolResult.Error("NewDocument returned null. Check template path: " + template);

            string unitSys = SwConnectionManager.SetDocumentUnitSystem(doc, domain);

            if (!string.IsNullOrWhiteSpace(savePath))
            {
                string dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                int errs = 0, warns = 0;
                doc.Extension.SaveAs3(savePath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errs, ref warns);
            }

            return McpToolResult.JsonText(new
            {
                title = doc.GetTitle(),
                path = doc.GetPathName(),
                doc_type = doc.GetType(),
                domain = domain,
                unit_system = unitSys,
                units = SwConnectionManager.GetDocumentUnits(doc),
                template_used = template
            });
        }

        private static McpToolResult HandleSaveDocument(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            string savePath = JsonHelper.GetString(args, "save_path");
            bool saveAll = JsonHelper.GetBool(args, "save_all_components", false);

            int errs = 0, warns = 0;
            bool ok;
            if (!string.IsNullOrWhiteSpace(savePath))
            {
                string dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                ok = doc.Extension.SaveAs3(savePath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errs, ref warns);
            }
            else
            {
                int opts = (int)swSaveAsOptions_e.swSaveAsOptions_Silent;
                if (saveAll) opts |= (int)swSaveAsOptions_e.swSaveAsOptions_SaveReferenced;
                ok = doc.Save3(opts, ref errs, ref warns);
            }

            object mateHealth = doc is IAssemblyDoc ? AssemblyMateHealthAuditor.BuildCompactSummary(doc) : null;
            return McpToolResult.JsonText(new
            {
                saved = ok,
                path = doc.GetPathName(),
                errors = errs,
                warnings = warns,
                mate_health = mateHealth
            }, isError: !ok);
        }

        private static McpToolResult HandleExportDocument(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            string format = JsonHelper.GetString(args, "format", "step").ToLowerInvariant();
            string outputPath = JsonHelper.GetString(args, "output_path");

            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            int errs = 0, warns = 0;
            bool ok = doc.Extension.SaveAs3(
                outputPath,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                null, null, ref errs, ref warns);

            return McpToolResult.JsonText(new
            {
                exported = ok,
                format = format,
                output_path = outputPath,
                errors = errs,
                warnings = warns
            }, isError: !ok);
        }

        private static McpToolResult HandleGetShopProfile(ShopCatalogService catalog, Dictionary<string, object> args)
        {
            string section = JsonHelper.GetString(args, "section", "all").ToLowerInvariant();
            string material = JsonHelper.GetString(args, "material");
            double? thicknessIn = JsonHelper.GetNullableDouble(args, "thickness_in");

            if (!string.IsNullOrWhiteSpace(material) && thicknessIn.HasValue)
            {
                if (catalog.TryGetSendCutSendGauge(material, thicknessIn.Value, out var matInfo, out var gaugeInfo))
                {
                    return McpToolResult.JsonText(new
                    {
                        vendor = "SendCutSend",
                        priority_warning = JsonHelper.GetString(catalog.SendCutSendCatalog, "priority_warning"),
                        material = JsonHelper.GetString(matInfo, "display_name"),
                        bendable = JsonHelper.GetBool(matInfo, "bendable"),
                        gauge = gaugeInfo
                    });
                }
                return McpToolResult.Error("No SendCutSend gauge match for material '" + material + "' at thickness " + thicknessIn.Value + " in.");
            }

            if (section == "sendcutsend_catalog")
                return McpToolResult.JsonText(catalog.SendCutSendCatalog);

            if (section == "hierarchy")
                return McpToolResult.JsonText(new
                {
                    unit_system_preferences = JsonHelper.GetDict(catalog.ShopProfile, "unit_system_preferences"),
                    manufacturing_preference_hierarchy = JsonHelper.GetList(catalog.ShopProfile, "manufacturing_preference_hierarchy")
                });

            if (section != "all" && catalog.ShopProfile.TryGetValue(section, out object secObj))
                return McpToolResult.JsonText(secObj);

            return McpToolResult.JsonText(catalog.ShopProfile);
        }
    }
}
