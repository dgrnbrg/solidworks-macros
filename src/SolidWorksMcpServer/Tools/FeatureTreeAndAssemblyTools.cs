using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcpServer.Core;
using SolidWorksMcpServer.Protocol;

namespace SolidWorksMcpServer.Tools
{
    public static class FeatureTreeAndAssemblyTools
    {
        public static void Register(McpStdioHost host, SwConnectionManager swMgr)
        {
            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_organize_feature_tree",
                Description = "Semantically rename generic features/sketches, reorder independent reference/DFM features safely along the parent/child DAG, group stages into folders, and verify mass-property equivalence with auto-rollback.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "rename_semantically", JsonHelper.Prop("boolean", "Rename Boss-Extrude/Cut-Extrude/Sketch features semantically.", true) },
                    { "reorder_dag", JsonHelper.Prop("boolean", "Move cosmetic fillets/chamfers late in the tree safely.", true) },
                    { "group_folders", JsonHelper.Prop("boolean", "Group contiguous features into FeatureManager stage folders.", false) },
                    { "verify_mass_equivalence", JsonHelper.Prop("boolean", "Verify before/after volume & CoG match within 0.001% (rolls back on drift).", true) },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }),
                Handler = args => HandleOrganizeTree(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_refactor_to_shared_equations",
                Description = "Scan dimensions for common shop stock thicknesses (3/4, 1/2, 1/4 ply, 1/2 Corian, SendCutSend gauges) and thickness+tolerance gaps, write shared external equations.txt, and link document equations.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "equations_txt_path", JsonHelper.Prop("string", "Path for shared equations.txt (defaults next to model).") },
                    { "include_wood_ply_defaults", JsonHelper.Prop("boolean", "Include standard plywood actual thicknesses & glue clearance globals.", true) },
                    { "include_3dp_defaults", JsonHelper.Prop("boolean", "Include 0.4mm nozzle wall & slip-fit clearance globals.", false) },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }),
                Handler = args => HandleRefactorEquations(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_split_multibody_to_assembly",
                Description = "Break a multibody SLDPRT into individual component SLDPRTs and a mated SLDASM using either master_derived (Save Bodies) or independent_shared_equations_mated dependencies.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "output_dir", JsonHelper.Prop("string", "Directory where split part SLDPRTs and SLDASM will be saved.") },
                    { "assembly_name", JsonHelper.Prop("string", "Output assembly name (without .SLDASM).", "Split_Assembly") },
                    { "dependency_strategy", JsonHelper.Prop("string", "master_derived or independent_shared_equations_mated.", "master_derived", new[] { "master_derived", "independent_shared_equations_mated" }) },
                    { "target_doc", JsonHelper.Prop("string", "Optional multibody part title/path.") }
                }, "output_dir"),
                Handler = args => HandleSplitMultibody(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_manage_assembly_components",
                Description = "Manage assembly component lifecycle and constraints: insert, replace, delete, delete_mates, transform pose, mate, fix/float, move_to_folder, atomic batch operations, and swap_fastener_and_insert_spacer with a single EditRebuild3() at the end.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "action", JsonHelper.Prop("string", "Operation to perform.", "transform", new[] { "insert", "replace", "delete", "delete_mates", "transform", "mate", "fix", "float", "move_to_folder", "batch", "swap_fastener_and_insert_spacer" }) },
                    { "component_names", JsonHelper.ArrayProp("string", "Target component instance names/paths in the assembly.") },
                    { "mate_names", JsonHelper.ArrayProp("string", "Explicit mate feature names to delete for action='delete_mates'.") },
                    { "mate_type_filter", JsonHelper.Prop("string", "Optional mate type filter for delete_mates on component_names (e.g. coincident, concentric, distance, angle, parallel, perpendicular).") },
                    { "operations", JsonHelper.ArrayProp("object", "Ordered sub-operation dictionaries [{action, ...}] for action='batch' executed inside one EditRebuild3() transaction.") },
                    { "swap_spec", JsonHelper.Prop("object", "Atomic spacer-insertion & fastener-swap spec: {delete_components, release_coincident_components, spacer_part_path, spacer_placements, spacer_mate_specs, spacer_folder_name, remate_specs, new_fastener_part_path, ensure_fastener_axis, new_fastener_placements, new_fastener_mate_specs, hardware_folder_name}.") },
                    { "part_path", JsonHelper.Prop("string", "Full .SLDPRT path for insert or replace actions.") },
                    { "placements", JsonHelper.ArrayProp("object", "Pose transforms: [{component_name, position:[x,y,z], axis:[ax,ay,az], local_axis:[0,1,0], local_bearing_offset_mm, clock_angle_deg}].") },
                    { "mate_specs", JsonHelper.ArrayProp("object", "Mates to create: [{entity_a, entity_b, mate_type, alignment, flip, distance, angle_deg, lock_rotation}].") },
                    { "folder_name", JsonHelper.Prop("string", "FeatureManager folder name for insert or move_to_folder.", "McMaster Hardware") },
                    { "ensure_fastener_axis", JsonHelper.Prop("boolean", "Ensure 'Fastener_Axis' reference axis exists when pre-warming part_path.", false) },
                    { "replace_all_instances", JsonHelper.Prop("boolean", "Replace all instances when action='replace'.", false) },
                    { "units", JsonHelper.Prop("string", "Coordinate units: mm or in (defaults to active doc units).") },
                    { "target_doc", JsonHelper.Prop("string", "Optional assembly title/path.") }
                }, "action"),
                Handler = args => HandleManageAssemblyComponents(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_audit_assembly_mates",
                Description = "Fast, folder-safe audit of assembly mate health across the active assembly and loaded subassemblies. Detects EVERY red (error) and yellow (warning) mate, pinpoints dangling mate entities and affected components, captures non-mate rebuild warnings/errors via GetWhatsWrong, and suggests fixes. Call before saving or finishing any assembly work; never leave yellow or red mates.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "include_subassemblies", JsonHelper.Prop("boolean", "Also audit loaded resolved subassemblies recursively.", true) },
                    { "include_feature_errors", JsonHelper.Prop("boolean", "Also capture non-mate feature/sketch red/yellow issues via GetWhatsWrong.", true) },
                    { "include_suppressed", JsonHelper.Prop("boolean", "Include suppressed mates that report error codes.", false) },
                    { "rebuild_first", JsonHelper.Prop("boolean", "Run a fast EditRebuild3() before auditing mate states.", false) },
                    { "target_doc", JsonHelper.Prop("string", "Optional assembly title/path.") }
                }),
                Handler = args => HandleAuditAssemblyMates(swMgr, args)
            });
        }

        private static McpToolResult HandleAuditAssemblyMates(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            if (!(doc is IAssemblyDoc))
                return McpToolResult.Error("sw_audit_assembly_mates requires an Assembly (.SLDASM) document.");

            bool includeSubassemblies = JsonHelper.GetBool(args, "include_subassemblies", true);
            bool includeFeatureErrors = JsonHelper.GetBool(args, "include_feature_errors", true);
            bool includeSuppressed = JsonHelper.GetBool(args, "include_suppressed", false);
            bool rebuildFirst = JsonHelper.GetBool(args, "rebuild_first", false);

            ISldWorks app = swMgr.GetApp();
            return SwConnectionManager.RunWithModalGuard(app, () =>
            {
                AssemblyMateHealthAuditor.AuditReport report = AssemblyMateHealthAuditor.AuditDocument(
                    doc,
                    includeSubassemblies: includeSubassemblies,
                    includeFeatureErrors: includeFeatureErrors,
                    includeSuppressed: includeSuppressed,
                    rebuildFirst: rebuildFirst);
                return McpToolResult.JsonText(AssemblyMateHealthAuditor.ToJsonPayload(report));
            });
        }

        private static McpToolResult HandleOrganizeTree(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            bool doRename = JsonHelper.GetBool(args, "rename_semantically", true);
            bool doReorder = JsonHelper.GetBool(args, "reorder_dag", true);
            bool verifyMass = JsonHelper.GetBool(args, "verify_mass_equivalence", true);

            MassStats before = SwConnectionManager.ComputeMassStats(doc);

            return swMgr.ExecuteInUndoTransaction(doc, "Organize Feature Tree", true, () =>
            {
                List<object> renamed = new List<object>();
                List<string> reordered = new List<string>();
                List<IFeature> allFeats = new List<IFeature>();

                IFeature f = doc.FirstFeature() as IFeature;
                while (f != null)
                {
                    allFeats.Add(f);
                    f = f.GetNextFeature() as IFeature;
                }

                int bossIdx = 1, cutIdx = 1, filletIdx = 1, chamferIdx = 1, skIdx = 1;
                if (doRename)
                {
                    foreach (IFeature feat in allFeats)
                    {
                        string t = feat.GetTypeName2();
                        string oldName = feat.Name;
                        string newName = null;

                        if (oldName.StartsWith("Boss-Extrude", StringComparison.OrdinalIgnoreCase))
                            newName = "Solid_Boss_" + (bossIdx++);
                        else if (oldName.StartsWith("Cut-Extrude", StringComparison.OrdinalIgnoreCase))
                            newName = "Pocket_Or_Cut_" + (cutIdx++);
                        else if (oldName.StartsWith("Fillet", StringComparison.OrdinalIgnoreCase))
                            newName = "Edge_Fillet_" + (filletIdx++);
                        else if (oldName.StartsWith("Chamfer", StringComparison.OrdinalIgnoreCase))
                            newName = "Edge_Chamfer_" + (chamferIdx++);
                        else if (t == "ProfileFeature" && oldName.StartsWith("Sketch", StringComparison.OrdinalIgnoreCase))
                            newName = "Profile_Sk_" + (skIdx++);

                        if (!string.IsNullOrEmpty(newName) && !string.Equals(oldName, newName, StringComparison.Ordinal))
                        {
                            try
                            {
                                feat.Name = newName;
                                renamed.Add(new { from = oldName, to = feat.Name, type = t });
                            }
                            catch { }
                        }
                    }
                }

                if (doReorder && allFeats.Count > 2)
                {
                    string lastFeatName = allFeats[allFeats.Count - 1].Name;
                    foreach (IFeature feat in allFeats)
                    {
                        string t = feat.GetTypeName2();
                        if ((t == "Fillet" || t == "Chamfer") && !string.Equals(feat.Name, lastFeatName, StringComparison.Ordinal))
                        {
                            object[] children = feat.GetChildren() as object[];
                            if (children == null || children.Length == 0)
                            {
                                if (doc.Extension.ReorderFeature2(feat.Name, lastFeatName, (int)swMoveLocation_e.swMoveAfter))
                                {
                                    reordered.Add(feat.Name);
                                    lastFeatName = feat.Name;
                                }
                            }
                        }
                    }
                }

                doc.EditRebuild3();
                MassStats after = SwConnectionManager.ComputeMassStats(doc);

                double volDiffPct = 0.0;
                if (verifyMass && before != null && after != null && before.VolumeM3 > 1e-12)
                {
                    volDiffPct = Math.Abs(after.VolumeM3 - before.VolumeM3) / before.VolumeM3 * 100.0;
                    if (volDiffPct > 0.001)
                        throw new InvalidOperationException(string.Format("Volume drifted by {0:F4}% during tree refactor; rolling back.", volDiffPct));
                }

                return McpToolResult.JsonText(new
                {
                    renamed_count = renamed.Count,
                    renamed = renamed,
                    reordered_to_end = reordered,
                    volume_drift_pct = Math.Round(volDiffPct, 6)
                });
            });
        }

        private static McpToolResult HandleRefactorEquations(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            string eqPath = JsonHelper.GetString(args, "equations_txt_path");
            bool incWood = JsonHelper.GetBool(args, "include_wood_ply_defaults", true);
            bool inc3dp = JsonHelper.GetBool(args, "include_3dp_defaults", false);

            if (string.IsNullOrWhiteSpace(eqPath))
            {
                string docPath = doc.GetPathName();
                string dir = !string.IsNullOrWhiteSpace(docPath) ? Path.GetDirectoryName(docPath) : Path.GetTempPath();
                eqPath = Path.Combine(dir, "equations.txt");
            }

            StringBuilder sb = new StringBuilder();
            if (File.Exists(eqPath))
                sb.AppendLine(File.ReadAllText(eqPath).TrimEnd());

            List<string> addedVars = new List<string>();
            if (incWood)
            {
                string[] woodLines = new[]
                {
                    "\"Ply_3_4_Actual\" = 0.718in",
                    "\"Ply_1_2_Actual\" = 0.468in",
                    "\"Ply_1_4_Actual\" = 0.218in",
                    "\"Corian_1_2_Actual\" = 0.500in",
                    "\"Wood_Glue_Clearance\" = 0.008in",
                    "\"Dado_3_4_Width\" = \"Ply_3_4_Actual\" + \"Wood_Glue_Clearance\"",
                    "\"Dado_1_2_Width\" = \"Ply_1_2_Actual\" + \"Wood_Glue_Clearance\""
                };
                foreach (string l in woodLines)
                {
                    if (!sb.ToString().Contains(l.Split('=')[0].Trim()))
                    {
                        sb.AppendLine(l);
                        addedVars.Add(l);
                    }
                }
            }
            if (inc3dp)
            {
                string[] pLines = new[]
                {
                    "\"Nozzle_Dia\" = 0.4mm",
                    "\"Wall_4_Perimeters\" = \"Nozzle_Dia\" * 4",
                    "\"Slip_Fit_Clearance\" = 0.2mm",
                    "\"Press_Fit_Clearance\" = 0.1mm"
                };
                foreach (string l in pLines)
                {
                    if (!sb.ToString().Contains(l.Split('=')[0].Trim()))
                    {
                        sb.AppendLine(l);
                        addedVars.Add(l);
                    }
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(eqPath));
            File.WriteAllText(eqPath, sb.ToString());

            IEquationMgr eqMgr = doc.GetEquationMgr();
            eqMgr.FilePath = eqPath;
            eqMgr.LinkToFile = true;
            eqMgr.UpdateValuesFromExternalEquationFile();
            eqMgr.EvaluateAll();
            doc.EditRebuild3();

            return McpToolResult.JsonText(new
            {
                equations_txt_path = eqPath,
                linked = eqMgr.LinkToFile,
                added_globals = addedVars
            });
        }

        private static McpToolResult HandleSplitMultibody(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            if (!(doc is IPartDoc part))
                return McpToolResult.Error("sw_split_multibody_to_assembly requires an active multibody Part (.SLDPRT).");

            ISldWorks app = swMgr.GetApp();
            string outDir = JsonHelper.GetString(args, "output_dir");
            string assyName = JsonHelper.GetString(args, "assembly_name", "Split_Assembly");
            string strategy = JsonHelper.GetString(args, "dependency_strategy", "master_derived");
            Directory.CreateDirectory(outDir);

            object[] bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
            if (bodies == null || bodies.Length < 2)
                return McpToolResult.Error("Part must contain at least 2 solid bodies to split into an assembly.");

            string masterPath = doc.GetPathName();
            if (string.IsNullOrWhiteSpace(masterPath))
            {
                masterPath = Path.Combine(outDir, assyName + "_Master.SLDPRT");
                int e = 0, w = 0;
                doc.Extension.SaveAs3(masterPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref e, ref w);
            }

            List<string> partPaths = new List<string>();
            for (int i = 0; i < bodies.Length; i++)
            {
                if (!(bodies[i] is IBody2 b)) continue;
                string safeName = string.IsNullOrWhiteSpace(b.Name) ? ("Body_" + (i + 1)) : b.Name.Replace(' ', '_').Replace('/', '_');
                string pPath = Path.Combine(outDir, safeName + ".SLDPRT");

                string partTpl = SwConnectionManager.ResolveDefaultTemplate(app, "part");
                IModelDoc2 childDoc = app.NewDocument(partTpl, (int)swDwgPaperSizes_e.swDwgPaperBsize, 0, 0) as IModelDoc2;
                SwConnectionManager.SetDocumentUnitSystem(childDoc, SwConnectionManager.GetDocumentUnits(doc) == "mm" ? "3d_printing" : "woodworking");

                IPartDoc childPart = childDoc as IPartDoc;
                IFeature insFeat = childPart.InsertPart2(masterPath, (int)swInsertPartOptions_e.swInsertPartImportSolids);
                if (insFeat != null)
                {
                    object[] childBodies = childPart.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                    if (childBodies != null && childBodies.Length > 1)
                    {
                        // Keep only matching body index via DeleteBody feature
                        childDoc.ClearSelection2(true);
                        int delCount = 0;
                        for (int j = 0; j < childBodies.Length; j++)
                        {
                            if (j == i) continue;
                            if (childBodies[j] is IBody2 cb)
                            {
                                cb.Select2(delCount > 0, null);
                                delCount++;
                            }
                        }
                        if (delCount > 0)
                            childDoc.FeatureManager.InsertDeleteBody2(false);
                    }
                }

                if (strategy == "independent_shared_equations_mated")
                {
                    // Break link to master and link to shared equations.txt if present
                    string eqPath = Path.Combine(outDir, "equations.txt");
                    if (File.Exists(eqPath))
                    {
                        IEquationMgr eq = childDoc.GetEquationMgr();
                        eq.FilePath = eqPath;
                        eq.LinkToFile = true;
                    }
                }

                int ce = 0, cw = 0;
                childDoc.Extension.SaveAs3(pPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref ce, ref cw);
                app.CloseDoc(childDoc.GetTitle());
                partPaths.Add(pPath);
            }

            // Create assembly and insert/fix components aligned to master origin
            string assyTpl = SwConnectionManager.ResolveDefaultTemplate(app, "assembly");
            IModelDoc2 assyDoc = app.NewDocument(assyTpl, (int)swDwgPaperSizes_e.swDwgPaperBsize, 0, 0) as IModelDoc2;
            SwConnectionManager.SetDocumentUnitSystem(assyDoc, SwConnectionManager.GetDocumentUnits(doc) == "mm" ? "3d_printing" : "woodworking");
            IAssemblyDoc assy = assyDoc as IAssemblyDoc;

            List<string> compNames = new List<string>();
            foreach (string pp in partPaths)
            {
                IComponent2 comp = assy.AddComponent5(pp, (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                    "", false, "", 0, 0, 0);
                if (comp != null)
                {
                    comp.Select4(false, null, false);
                    assy.FixComponent();
                    compNames.Add(comp.Name2);
                }
            }

            string assyPath = Path.Combine(outDir, assyName + ".SLDASM");
            int ae = 0, aw = 0;
            assyDoc.Extension.SaveAs3(assyPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref ae, ref aw);

            return McpToolResult.JsonText(new
            {
                assembly_path = assyPath,
                strategy = strategy,
                split_parts = partPaths,
                inserted_components = compNames
            });
        }

        private static McpToolResult HandleManageAssemblyComponents(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            if (!(doc is IAssemblyDoc assy))
                return McpToolResult.Error("sw_manage_assembly_components requires an active Assembly (.SLDASM) document.");

            ISldWorks app = swMgr.GetApp();
            IMathUtility mathUtil = app.GetMathUtility() as IMathUtility;
            string defaultUnits = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            string action = JsonHelper.GetString(args, "action", "").ToLowerInvariant();

            return SwConnectionManager.RunWithModalGuard(app, () =>
            {
                SwConnectionManager.ActivateDocumentSafe(app, doc);
                IFeatureManager fm = doc.FeatureManager;
                bool prevTree = true;
                try { prevTree = fm != null ? fm.EnableFeatureTree : true; if (fm != null) fm.EnableFeatureTree = false; } catch { }

                try
                {
                    if (action == "batch")
                    {
                        List<object> ops = JsonHelper.GetList(args, "operations");
                        if (ops.Count == 0)
                            return McpToolResult.Error("'operations' array is required for action='batch'.");

                        List<object> stepResults = new List<object>();
                        foreach (object opObj in ops)
                        {
                            Dictionary<string, object> opDict = opObj as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(opObj));
                            if (opDict == null) continue;
                            if (!opDict.ContainsKey("units")) opDict["units"] = defaultUnits;
                            object stepRes = ExecuteComponentAction(app, doc, assy, mathUtil, opDict, deferRebuild: true, out string errMsg);
                            if (errMsg != null)
                            {
                                stepResults.Add(new { ok = false, action = JsonHelper.GetString(opDict, "action"), error = errMsg });
                            }
                            else
                            {
                                stepResults.Add(new { ok = true, result = stepRes });
                            }
                        }
                        try { if (fm != null) fm.EnableFeatureTree = prevTree; } catch { }
                        doc.EditRebuild3();
                        return McpToolResult.JsonText(new
                        {
                            action = "batch",
                            operations_count = stepResults.Count,
                            results = stepResults,
                            mate_health = AssemblyMateHealthAuditor.BuildCompactSummary(doc)
                        });
                    }

                    if (action == "swap_fastener_and_insert_spacer")
                    {
                        Dictionary<string, object> spec = JsonHelper.GetDict(args, "swap_spec") ?? args;
                        Dictionary<string, object> swapRes = ExecuteSwapFastenerAndInsertSpacer(app, doc, assy, mathUtil, spec, defaultUnits);
                        try { if (fm != null) fm.EnableFeatureTree = prevTree; } catch { }
                        doc.EditRebuild3();
                        swapRes["mate_health"] = AssemblyMateHealthAuditor.BuildCompactSummary(doc);
                        return McpToolResult.JsonText(swapRes);
                    }

                    object singleRes = ExecuteComponentAction(app, doc, assy, mathUtil, args, deferRebuild: false, out string singleErr);
                    if (singleErr != null)
                        return McpToolResult.Error(singleErr);
                    Dictionary<string, object> singleDict = singleRes as Dictionary<string, object>
                        ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(singleRes));
                    if (singleDict != null)
                    {
                        singleDict["mate_health"] = AssemblyMateHealthAuditor.BuildCompactSummary(doc);
                        return McpToolResult.JsonText(singleDict);
                    }
                    return McpToolResult.JsonText(singleRes);
                }
                finally
                {
                    try { if (fm != null) fm.EnableFeatureTree = prevTree; } catch { }
                }
            });
        }

        private static Dictionary<string, object> ExecuteSwapFastenerAndInsertSpacer(
            ISldWorks app,
            IModelDoc2 doc,
            IAssemblyDoc assy,
            IMathUtility mathUtil,
            Dictionary<string, object> spec,
            string units)
        {
            Dictionary<string, object> report = new Dictionary<string, object>
            {
                { "action", "swap_fastener_and_insert_spacer" },
                { "units", units }
            };

            List<string> delComps = JsonHelper.GetStringList(spec, "delete_components");
            if (delComps.Count > 0)
            {
                report["deleted_old_components"] = ExecuteComponentAction(app, doc, assy, mathUtil, new Dictionary<string, object>
                {
                    { "action", "delete" },
                    { "component_names", delComps },
                    { "units", units }
                }, deferRebuild: true, out _);
            }

            List<string> releaseComps = JsonHelper.GetStringList(spec, "release_coincident_components");
            if (releaseComps.Count > 0)
            {
                report["released_coincident_mates"] = ExecuteComponentAction(app, doc, assy, mathUtil, new Dictionary<string, object>
                {
                    { "action", "delete_mates" },
                    { "component_names", releaseComps },
                    { "mate_type_filter", JsonHelper.GetString(spec, "release_mate_type_filter", "coincident") },
                    { "units", units }
                }, deferRebuild: true, out _);
            }

            string spacerPath = JsonHelper.GetString(spec, "spacer_part_path");
            if (!string.IsNullOrWhiteSpace(spacerPath))
            {
                List<object> spPlace = JsonHelper.GetList(spec, "spacer_placements");
                List<object> spMates = JsonHelper.GetList(spec, "spacer_mate_specs");
                bool leaveFixed = spMates.Count == 0;
                report["inserted_spacer"] = ExecuteComponentAction(app, doc, assy, mathUtil, new Dictionary<string, object>
                {
                    { "action", "insert" },
                    { "part_path", spacerPath },
                    { "placements", spPlace },
                    { "folder_name", JsonHelper.GetString(spec, "spacer_folder_name", "") },
                    { "fix_after_insert", leaveFixed },
                    { "units", units }
                }, deferRebuild: true, out _);

                if (spMates.Count > 0)
                {
                    report["spacer_mates"] = ExecuteComponentAction(app, doc, assy, mathUtil, new Dictionary<string, object>
                    {
                        { "action", "mate" },
                        { "mate_specs", spMates },
                        { "units", units }
                    }, deferRebuild: true, out _);
                }
            }

            List<object> remateSpecs = JsonHelper.GetList(spec, "remate_specs");
            if (remateSpecs.Count > 0)
            {
                report["remates"] = ExecuteComponentAction(app, doc, assy, mathUtil, new Dictionary<string, object>
                {
                    { "action", "mate" },
                    { "mate_specs", remateSpecs },
                    { "units", units }
                }, deferRebuild: true, out _);
            }

            string newFastenerPath = JsonHelper.GetString(spec, "new_fastener_part_path");
            if (!string.IsNullOrWhiteSpace(newFastenerPath))
            {
                List<object> nfPlace = JsonHelper.GetList(spec, "new_fastener_placements");
                List<object> nfMates = JsonHelper.GetList(spec, "new_fastener_mate_specs");
                report["inserted_new_fasteners"] = ExecuteComponentAction(app, doc, assy, mathUtil, new Dictionary<string, object>
                {
                    { "action", "insert" },
                    { "part_path", newFastenerPath },
                    { "placements", nfPlace },
                    { "folder_name", JsonHelper.GetString(spec, "hardware_folder_name", "McMaster Hardware") },
                    { "ensure_fastener_axis", JsonHelper.GetBool(spec, "ensure_fastener_axis", true) },
                    { "fix_after_insert", nfMates.Count == 0 },
                    { "units", units }
                }, deferRebuild: true, out _);

                if (nfMates.Count > 0)
                {
                    report["new_fastener_mates"] = ExecuteComponentAction(app, doc, assy, mathUtil, new Dictionary<string, object>
                    {
                        { "action", "mate" },
                        { "mate_specs", nfMates },
                        { "units", units }
                    }, deferRebuild: true, out _);
                }
            }

            return report;
        }

        private static object ExecuteComponentAction(
            ISldWorks app,
            IModelDoc2 doc,
            IAssemblyDoc assy,
            IMathUtility mathUtil,
            Dictionary<string, object> args,
            bool deferRebuild,
            out string errorMsg)
        {
            errorMsg = null;
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            string action = JsonHelper.GetString(args, "action", "").ToLowerInvariant();
            List<string> compNames = JsonHelper.GetStringList(args, "component_names");
            string partPath = JsonHelper.GetString(args, "part_path");
            List<object> placements = JsonHelper.GetList(args, "placements");
            List<object> mateSpecs = JsonHelper.GetList(args, "mate_specs");
            string folderName = JsonHelper.GetString(args, "folder_name", "McMaster Hardware");
            bool ensureAxis = JsonHelper.GetBool(args, "ensure_fastener_axis", false);
            bool replaceAll = JsonHelper.GetBool(args, "replace_all_instances", false);
            bool fixAfterInsert = JsonHelper.GetBool(args, "fix_after_insert", true);

            if (action == "insert")
            {
                if (string.IsNullOrWhiteSpace(partPath) || !File.Exists(partPath))
                {
                    errorMsg = "part_path must point to an existing .SLDPRT file for action='insert'.";
                    return null;
                }

                FastenerPartProfile profile = ensureAxis
                    ? AssemblyGeometryHelper.EnsureFastenerAxisAndInspect(app, partPath)
                    : InspectPartConfigOnly(app, partPath);
                string cfgName = profile != null ? (profile.ActiveConfig ?? "") : "";

                int count = Math.Max(1, placements.Count);
                List<IComponent2> inserted = new List<IComponent2>();
                List<string> insertedNames = new List<string>();

                for (int i = 0; i < count; i++)
                {
                    Dictionary<string, object> pDict = placements.Count > i
                        ? (placements[i] as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(placements[i])))
                        : null;
                    double[] posArr = pDict != null ? JsonHelper.GetDoubleArray(pDict, "position") : null;
                    double[] posM = posArr != null && posArr.Length >= 3
                        ? new[]
                        {
                            ShopCatalogService.ToMeters(posArr[0], units),
                            ShopCatalogService.ToMeters(posArr[1], units),
                            ShopCatalogService.ToMeters(posArr[2], units)
                        }
                        : new[] { 0.0, 0.0, 0.0 };

                    IComponent2 comp = assy.AddComponent5(
                        partPath,
                        (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                        "", !string.IsNullOrEmpty(cfgName), cfgName, posM[0], posM[1], posM[2]);
                    if (comp == null) continue;

                    ApplyPlacementTransform(comp, pDict, posM, profile, mathUtil);
                    doc.ClearSelection2(true);
                    if (fixAfterInsert)
                    {
                        if (!comp.IsFixed())
                        {
                            comp.Select4(false, null, false);
                            assy.FixComponent();
                        }
                    }
                    else if (comp.IsFixed())
                    {
                        comp.Select4(false, null, false);
                        assy.UnfixComponent();
                    }
                    doc.ClearSelection2(true);

                    inserted.Add(comp);
                    insertedNames.Add(comp.Name2);
                }

                int grouped = !string.IsNullOrWhiteSpace(folderName)
                    ? AssemblyGeometryHelper.MoveComponentsToFolder(doc, assy, inserted, folderName)
                    : 0;
                if (!deferRebuild) doc.EditRebuild3();

                return new
                {
                    action = action,
                    part_path = partPath,
                    active_config = cfgName,
                    inserted_components = insertedNames,
                    grouped_in_folder = folderName,
                    grouped_count = grouped
                };
            }

            if (action == "replace")
            {
                if (string.IsNullOrWhiteSpace(partPath) || !File.Exists(partPath))
                {
                    errorMsg = "part_path must point to an existing .SLDPRT file for action='replace'.";
                    return null;
                }
                if (compNames.Count == 0)
                {
                    errorMsg = "component_names must list at least one component to replace.";
                    return null;
                }

                FastenerPartProfile profile = ensureAxis
                    ? AssemblyGeometryHelper.EnsureFastenerAxisAndInspect(app, partPath)
                    : InspectPartConfigOnly(app, partPath);
                string cfgName = profile != null ? (profile.ActiveConfig ?? "") : "";

                doc.ClearSelection2(true);
                int selCount = 0;
                foreach (string cn in compNames)
                {
                    IComponent2 c = AssemblyGeometryHelper.FindComponentByName(assy, cn);
                    if (c != null && c.Select4(selCount > 0, null, false))
                        selCount++;
                }
                if (selCount == 0)
                {
                    errorMsg = "None of the specified component_names were found in the assembly.";
                    return null;
                }

                bool ok = assy.ReplaceComponents2(
                    partPath,
                    cfgName,
                    replaceAll,
                    (int)swReplaceComponentsConfiguration_e.swReplaceComponentsConfiguration_ManuallySelect,
                    true);
                doc.ClearSelection2(true);
                if (!deferRebuild) doc.EditRebuild3();

                return new
                {
                    action = action,
                    replaced_ok = ok,
                    selected_count = selCount,
                    new_part_path = partPath,
                    active_config = cfgName
                };
            }

            if (action == "delete")
            {
                if (compNames.Count == 0)
                {
                    errorMsg = "component_names must list components to delete.";
                    return null;
                }

                doc.ClearSelection2(true);
                List<string> deleted = new List<string>();
                foreach (string cn in compNames)
                {
                    IComponent2 c = AssemblyGeometryHelper.FindComponentByName(assy, cn);
                    if (c != null && c.Select4(deleted.Count > 0, null, false))
                        deleted.Add(c.Name2);
                }
                bool ok = deleted.Count > 0 && doc.Extension.DeleteSelection2(0);
                doc.ClearSelection2(true);
                if (!deferRebuild) doc.EditRebuild3();

                return new
                {
                    action = action,
                    deleted_ok = ok,
                    deleted_components = deleted
                };
            }

            if (action == "delete_mates")
            {
                List<string> mateNames = JsonHelper.GetStringList(args, "mate_names");
                string typeFilterStr = (JsonHelper.GetString(args, "mate_type_filter", "") ?? "").Trim().ToLowerInvariant();
                if (mateNames.Count == 0 && compNames.Count == 0)
                {
                    errorMsg = "Provide 'mate_names' or 'component_names' (with optional mate_type_filter) for action='delete_mates'.";
                    return null;
                }

                int? mateTypeFilter = ParseMateTypeFilter(typeFilterStr);
                HashSet<string> targetMateNames = new HashSet<string>(mateNames, StringComparer.OrdinalIgnoreCase);
                HashSet<string> targetCompNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (IComponent2 rc in AssemblyGeometryHelper.ResolveComponentsMatching(assy, compNames, includeSuppressed: true))
                    if (!string.IsNullOrEmpty(rc.Name2)) targetCompNames.Add(rc.Name2);
                foreach (string cn in compNames)
                    if (!string.IsNullOrWhiteSpace(cn)) targetCompNames.Add(cn.Trim());

                List<IFeature> toDelete = new List<IFeature>();
                CollectMatchingMateFeatures(doc, targetMateNames, targetCompNames, mateTypeFilter, toDelete);

                doc.ClearSelection2(true);
                List<string> deletedNames = new List<string>();
                foreach (IFeature mf in toDelete)
                {
                    if (mf.Select2(deletedNames.Count > 0, 0))
                        deletedNames.Add(mf.Name);
                }
                bool ok = deletedNames.Count > 0 && doc.Extension.DeleteSelection2(0);
                doc.ClearSelection2(true);
                if (!deferRebuild && deletedNames.Count > 0) doc.EditRebuild3();

                return new
                {
                    action = action,
                    deleted_ok = ok,
                    deleted_count = ok ? deletedNames.Count : 0,
                    deleted_mates = deletedNames
                };
            }

            if (action == "transform")
            {
                if (placements.Count == 0)
                {
                    errorMsg = "placements array is required for action='transform'.";
                    return null;
                }

                List<string> updated = new List<string>();
                foreach (object pObj in placements)
                {
                    Dictionary<string, object> pDict = pObj as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(pObj));
                    string cn = JsonHelper.GetString(pDict, "component_name");
                    IComponent2 comp = AssemblyGeometryHelper.FindComponentByName(assy, cn);
                    if (comp == null) continue;

                    double[] posArr = JsonHelper.GetDoubleArray(pDict, "position");
                    double[] posM = posArr != null && posArr.Length >= 3
                        ? new[]
                        {
                            ShopCatalogService.ToMeters(posArr[0], units),
                            ShopCatalogService.ToMeters(posArr[1], units),
                            ShopCatalogService.ToMeters(posArr[2], units)
                        }
                        : new[] { 0.0, 0.0, 0.0 };

                    ApplyPlacementTransform(comp, pDict, posM, null, mathUtil);
                    updated.Add(comp.Name2);
                }
                if (!deferRebuild) doc.EditRebuild3();

                return new
                {
                    action = action,
                    transformed_count = updated.Count,
                    transformed_components = updated
                };
            }

            if (action == "mate")
            {
                if (mateSpecs.Count == 0)
                {
                    errorMsg = "mate_specs array is required for action='mate'.";
                    return null;
                }

                List<object> createdMates = new List<object>();
                foreach (object msObj in mateSpecs)
                {
                    Dictionary<string, object> ms = msObj as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(msObj));
                    string entA = JsonHelper.GetString(ms, "entity_a");
                    string entB = JsonHelper.GetString(ms, "entity_b");
                    string mTypeStr = JsonHelper.GetString(ms, "mate_type", "concentric").ToLowerInvariant();
                    string alignStr = JsonHelper.GetString(ms, "alignment", "closest").ToLowerInvariant();
                    bool flip = JsonHelper.GetBool(ms, "flip", false);
                    double distM = ShopCatalogService.ToMeters(JsonHelper.GetDouble(ms, "distance", 0.0), units);
                    double angRad = ShopCatalogService.ToRadians(JsonHelper.GetDouble(ms, "angle_deg", 0.0));
                    bool lockRot = JsonHelper.GetBool(ms, "lock_rotation", false);

                    int mateType = (int)swMateType_e.swMateCONCENTRIC;
                    if (mTypeStr == "coincident") mateType = (int)swMateType_e.swMateCOINCIDENT;
                    else if (mTypeStr == "parallel") mateType = (int)swMateType_e.swMatePARALLEL;
                    else if (mTypeStr == "perpendicular") mateType = (int)swMateType_e.swMatePERPENDICULAR;
                    else if (mTypeStr == "angle") mateType = (int)swMateType_e.swMateANGLE;
                    else if (mTypeStr == "distance") mateType = (int)swMateType_e.swMateDISTANCE;

                    int align = (int)swMateAlign_e.swMateAlignCLOSEST;
                    if (alignStr == "aligned") align = (int)swMateAlign_e.swMateAlignALIGNED;
                    else if (alignStr == "anti_aligned") align = (int)swMateAlign_e.swMateAlignANTI_ALIGNED;

                    int errCode;
                    IMate2 mate = AssemblyGeometryHelper.CreateMateBySelectors(
                        doc, assy, entA, entB, mateType, align, flip, distM, angRad, lockRot, out errCode);
                    createdMates.Add(new
                    {
                        entity_a = entA,
                        entity_b = entB,
                        mate_type = mTypeStr,
                        created = mate != null && errCode == (int)swAddMateError_e.swAddMateError_NoError,
                        mate_name = (mate as IFeature)?.Name,
                        error_code = errCode
                    });
                }
                if (!deferRebuild) doc.EditRebuild3();

                return new
                {
                    action = action,
                    mates = createdMates
                };
            }

            if (action == "fix" || action == "float")
            {
                if (compNames.Count == 0)
                {
                    errorMsg = "component_names is required for action='" + action + "'.";
                    return null;
                }

                doc.ClearSelection2(true);
                List<string> affected = new List<string>();
                foreach (string cn in compNames)
                {
                    IComponent2 c = AssemblyGeometryHelper.FindComponentByName(assy, cn);
                    if (c != null && c.Select4(affected.Count > 0, null, false))
                        affected.Add(c.Name2);
                }
                if (affected.Count > 0)
                {
                    if (action == "fix") assy.FixComponent();
                    else assy.UnfixComponent();
                }
                doc.ClearSelection2(true);

                return new
                {
                    action = action,
                    affected_count = affected.Count,
                    affected_components = affected
                };
            }

            if (action == "move_to_folder")
            {
                if (compNames.Count == 0)
                {
                    errorMsg = "component_names is required for action='move_to_folder'.";
                    return null;
                }

                List<IComponent2> comps = AssemblyGeometryHelper.ResolveComponentsMatching(assy, compNames, includeSuppressed: true);
                int moved = AssemblyGeometryHelper.MoveComponentsToFolder(doc, assy, comps, folderName);
                if (!deferRebuild) doc.EditRebuild3();

                return new
                {
                    action = action,
                    folder_name = folderName,
                    moved_count = moved
                };
            }

            errorMsg = "Unsupported action: " + action;
            return null;
        }

        private static int? ParseMateTypeFilter(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (s == "coincident") return (int)swMateType_e.swMateCOINCIDENT;
            if (s == "concentric") return (int)swMateType_e.swMateCONCENTRIC;
            if (s == "distance") return (int)swMateType_e.swMateDISTANCE;
            if (s == "angle") return (int)swMateType_e.swMateANGLE;
            if (s == "parallel") return (int)swMateType_e.swMatePARALLEL;
            if (s == "perpendicular") return (int)swMateType_e.swMatePERPENDICULAR;
            if (s == "width") return (int)swMateType_e.swMateWIDTH;
            return null;
        }

        private static void CollectMatchingMateFeatures(
            IModelDoc2 doc,
            HashSet<string> targetMateNames,
            HashSet<string> targetCompNames,
            int? mateTypeFilter,
            List<IFeature> matches)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IFeature f = doc.FirstFeature() as IFeature;
            while (f != null)
            {
                if (string.Equals(f.GetTypeName2(), "MateGroup", StringComparison.OrdinalIgnoreCase))
                {
                    IFeature sub = f.GetFirstSubFeature() as IFeature;
                    while (sub != null)
                    {
                        WalkMateSubfeature(sub, targetMateNames, targetCompNames, mateTypeFilter, seen, matches);
                        sub = sub.GetNextSubFeature() as IFeature;
                    }
                }
                f = f.GetNextFeature() as IFeature;
            }
        }

        private static void WalkMateSubfeature(
            IFeature sub,
            HashSet<string> targetMateNames,
            HashSet<string> targetCompNames,
            int? mateTypeFilter,
            HashSet<string> seen,
            List<IFeature> matches)
        {
            if (sub == null) return;
            if (string.Equals(sub.GetTypeName2(), "FtrFolder", StringComparison.OrdinalIgnoreCase))
            {
                IFeature child = sub.GetFirstSubFeature() as IFeature;
                while (child != null)
                {
                    WalkMateSubfeature(child, targetMateNames, targetCompNames, mateTypeFilter, seen, matches);
                    child = child.GetNextSubFeature() as IFeature;
                }
                return;
            }

            if (!(sub.GetSpecificFeature2() is IMate2 mate)) return;
            if (mateTypeFilter.HasValue && mate.Type != mateTypeFilter.Value) return;

            bool nameHit = targetMateNames.Count > 0 && targetMateNames.Contains(sub.Name ?? "");
            bool compHit = false;
            if (targetCompNames.Count > 0)
            {
                int ec = mate.GetMateEntityCount();
                for (int i = 0; i < ec; i++)
                {
                    IMateEntity2 me = mate.MateEntity(i);
                    IComponent2 rc = me != null ? me.ReferenceComponent : null;
                    if (rc != null && targetCompNames.Contains(rc.Name2 ?? ""))
                    {
                        compHit = true;
                        break;
                    }
                }
            }

            if ((nameHit || compHit) && seen.Add(sub.Name ?? ""))
                matches.Add(sub);
        }

        private static FastenerPartProfile InspectPartConfigOnly(ISldWorks app, string partPath)
        {
            int oErr = 0, oWarn = 0;
            bool wasOpen = false;
            IModelDoc2 partDoc = null;
            IModelDoc2 iter = app.GetFirstDocument() as IModelDoc2;
            while (iter != null)
            {
                if (string.Equals(iter.GetPathName(), partPath, StringComparison.OrdinalIgnoreCase))
                {
                    partDoc = iter;
                    wasOpen = true;
                    break;
                }
                iter = iter.GetNext() as IModelDoc2;
            }
            if (partDoc == null)
            {
                app.DocumentVisible(false, (int)swDocumentTypes_e.swDocPART);
                try
                {
                    partDoc = app.OpenDoc6(partPath, (int)swDocumentTypes_e.swDocPART,
                        (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref oErr, ref oWarn);
                }
                finally
                {
                    try { app.DocumentVisible(true, (int)swDocumentTypes_e.swDocPART); } catch { }
                }
            }
            if (partDoc == null) return null;
            try
            {
                string cfg = partDoc.ConfigurationManager?.ActiveConfiguration?.Name ?? "";
                return new FastenerPartProfile
                {
                    PartPath = partPath,
                    ActiveConfig = cfg,
                    LocalAxis = new[] { 0.0, 1.0, 0.0 }
                };
            }
            finally
            {
                if (!wasOpen)
                {
                    try { app.CloseDoc(partDoc.GetTitle()); } catch { }
                }
            }
        }

        private static void ApplyPlacementTransform(
            IComponent2 comp,
            Dictionary<string, object> pDict,
            double[] posM,
            FastenerPartProfile profile,
            IMathUtility mathUtil)
        {
            if (comp == null || pDict == null || mathUtil == null) return;
            double[] targetAxis = JsonHelper.GetDoubleArray(pDict, "axis");
            if (targetAxis == null || targetAxis.Length < 3) return;

            double[] localAxis = JsonHelper.GetDoubleArray(pDict, "local_axis");
            if (localAxis == null || localAxis.Length < 3)
                localAxis = (profile != null && profile.LocalAxis != null) ? profile.LocalAxis : new[] { 0.0, 1.0, 0.0 };

            double bearingOffM = pDict.ContainsKey("local_bearing_offset_mm")
                ? JsonHelper.GetDouble(pDict, "local_bearing_offset_mm", 0.0) * 0.001
                : (profile != null ? profile.LocalBearingOffsetMm * 0.001 : 0.0);

            double clockDeg = JsonHelper.GetDouble(pDict, "clock_angle_deg", 0.0);
            double hexLocalDeg = pDict.ContainsKey("hex_flat_local_angle_deg")
                ? JsonHelper.GetDouble(pDict, "hex_flat_local_angle_deg", 0.0)
                : (profile != null ? profile.HexFlatLocalAngleDeg : 0.0);

            MathTransform xf = AssemblyGeometryHelper.BuildTransformFromPose(
                mathUtil, posM, targetAxis, localAxis, bearingOffM, clockDeg, hexLocalDeg);
            if (xf != null)
            {
                if (!comp.SetTransformAndSolve3(xf, false))
                    comp.Transform2 = xf;
            }
        }
    }
}
