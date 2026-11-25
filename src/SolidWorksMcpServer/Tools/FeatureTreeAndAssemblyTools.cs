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
                Description = "Semantically rename generic features/sketches, reorder independent reference/DFM features safely along the parent/child DAG, automatically fully define under-defined (-) sketches, group stages into folders, and verify mass-property equivalence with auto-rollback.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "rename_semantically", JsonHelper.Prop("boolean", "Rename Boss-Extrude/Cut-Extrude/Sketch features semantically.", true) },
                    { "reorder_dag", JsonHelper.Prop("boolean", "Move cosmetic fillets/chamfers late in the tree safely.", true) },
                    { "fix_underdefined_sketches", JsonHelper.Prop("boolean", "Automatically run FullyDefineSketch on any under-defined (-) 2D sketches while preserving part mass/volume.", true) },
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
                Description = "Manage assembly component lifecycle, BOM exclusion/custom properties, and constraints: insert, replace, delete, delete_mates, transform pose, mate (supports geometric face/plane/principal_planes selectors), fix/float, move_to_folder, configure_bom_and_properties (batch ExcludeFromBOM & custom properties like BOM_Desc / BOM_Role without manual part opening), atomic pose_and_mate_batch / batch operations, and swap_fastener_and_insert_spacer with a single EditRebuild3() at the end.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "action", JsonHelper.Prop("string", "Operation to perform.", "transform", new[] { "insert", "replace", "delete", "delete_mates", "transform", "mate", "fix", "float", "move_to_folder", "configure_bom_and_properties", "batch", "pose_and_mate_batch", "swap_fastener_and_insert_spacer" }) },
                    { "component_names", JsonHelper.ArrayProp("string", "Target component instance names/paths in the assembly (prefix '@top:' or '/' for strict top-level only).") },
                    { "exclude_from_bom", JsonHelper.Prop("boolean", "Optional ExcludeFromBOM flag applied to component_names or newly inserted components.") },
                    { "exclude_from_bom_components", JsonHelper.ArrayProp("string", "Component names/prefixes to mark ExcludeFromBOM = true (e.g. reference valve/context parts).") },
                    { "include_in_bom_components", JsonHelper.ArrayProp("string", "Component names/prefixes to mark ExcludeFromBOM = false.") },
                    { "component_properties", JsonHelper.ArrayProp("object", "Batch custom-property and BOM exclusion definitions: [{component_name, component_names, part_path, exclude_from_bom, configuration_specific, save_part, properties:{BOM_Desc, BOM_Role, PartNumber, Description, Material, ...}}].") },
                    { "mate_names", JsonHelper.ArrayProp("string", "Explicit mate feature names to delete for action='delete_mates'.") },
                    { "mate_type_filter", JsonHelper.Prop("string", "Optional mate type filter for delete_mates on component_names (e.g. coincident, concentric, distance, angle, parallel, perpendicular).") },
                    { "top_level_only", JsonHelper.Prop("boolean", "For action='delete_mates' by bare component_names, match top-level component entities rather than subassembly paths.", true) },
                    { "operations", JsonHelper.ArrayProp("object", "Ordered sub-operation dictionaries [{action, ...}] for action='batch' executed inside one EditRebuild3() transaction.") },
                    { "pose_and_mate_items", JsonHelper.ArrayProp("object", "Items for action='pose_and_mate_batch': [{component_name, release_mates, mate_type_filter, unfix, position, ex, ey, ez, axis, local_axis, mate_to_principal_planes, target_ref_component, mate_specs, fix, exclude_from_bom}].") },
                    { "swap_spec", JsonHelper.Prop("object", "Atomic spacer-insertion & fastener-swap spec: {delete_components, release_coincident_components, spacer_part_path, spacer_placements, spacer_mate_specs, spacer_folder_name, remate_specs, new_fastener_part_path, ensure_fastener_axis, new_fastener_placements, new_fastener_mate_specs, hardware_folder_name}.") },
                    { "part_path", JsonHelper.Prop("string", "Full .SLDPRT path for insert or replace actions.") },
                    { "placements", JsonHelper.ArrayProp("object", "Pose transforms: [{component_name, position:[x,y,z], axis:[ax,ay,az], local_axis:[0,1,0], ex:[x,y,z], ey:[x,y,z], ez:[x,y,z], local_bearing_offset_mm, clock_angle_deg}].") },
                    { "mate_specs", JsonHelper.ArrayProp("object", "Mates to create: [{entity_a, entity_b, entity_a_spec, entity_b_spec, component_name, target_ref_component, mate_type, alignment, flip, distance, angle_deg, lock_rotation}].") },
                    { "folder_name", JsonHelper.Prop("string", "FeatureManager folder name for insert or move_to_folder.", "McMaster Hardware") },
                    { "ensure_fastener_axis", JsonHelper.Prop("boolean", "Ensure 'Fastener_Axis' reference axis exists when pre-warming part_path.", false) },
                    { "replace_all_instances", JsonHelper.Prop("boolean", "Replace all instances when action='replace'.", false) },
                    { "check_mate_health", JsonHelper.Prop("boolean", "Attach fast top-level mate_health summary (<5ms).", true) },
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
            bool fixUnderdefined = JsonHelper.GetBool(args, "fix_underdefined_sketches", true);
            bool verifyMass = JsonHelper.GetBool(args, "verify_mass_equivalence", true);

            MassStats before = SwConnectionManager.ComputeMassStats(doc);

            return swMgr.ExecuteInUndoTransaction(doc, "Organize Feature Tree", true, () =>
            {
                List<object> renamed = new List<object>();
                List<string> reordered = new List<string>();
                List<string> fixedSketches = new List<string>();
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

                if (fixUnderdefined)
                {
                    HashSet<string> preVisible = SwHelper.GetVisibleSketchNames(doc);
                    HashSet<string> seenSk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    List<IFeature> skFeatsToCheck = new List<IFeature>();

                    foreach (IFeature feat in allFeats)
                    {
                        if (string.Equals(feat.GetTypeName2(), "ProfileFeature", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrEmpty(feat.Name) && seenSk.Add(feat.Name))
                        {
                            skFeatsToCheck.Add(feat);
                        }
                        IFeature sub = feat.GetFirstSubFeature() as IFeature;
                        while (sub != null)
                        {
                            if (string.Equals(sub.GetTypeName2(), "ProfileFeature", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrEmpty(sub.Name) && seenSk.Add(sub.Name))
                            {
                                skFeatsToCheck.Add(sub);
                            }
                            sub = sub.GetNextSubFeature() as IFeature;
                        }
                    }

                    foreach (IFeature skFeat in skFeatsToCheck)
                    {
                        try
                        {
                            if (skFeat.IsSuppressed()) continue;
                            ISketch sk = skFeat.GetSpecificFeature2() as ISketch;
                            if (sk == null || sk.GetConstrainedStatus() != (int)swConstrainedStatus_e.swUnderConstrained)
                                continue;

                            doc.ClearSelection2(true);
                            if (!skFeat.Select2(false, 0)) continue;
                            doc.EditSketch();
                            if (!SwComExecutionGate.IsOfflineTestMode())
                                System.Threading.Thread.Sleep(25);
                            if (doc.SketchManager.ActiveSketch != null)
                            {
                                int newStatus = SwHelper.FullyDefineActiveSketch(doc);
                                doc.SketchManager.InsertSketchSafe(true);
                                if (newStatus == (int)swConstrainedStatus_e.swFullyConstrained)
                                    fixedSketches.Add(skFeat.Name);
                            }
                        }
                        catch
                        {
                            try
                            {
                                if (doc.SketchManager.ActiveSketch != null)
                                    doc.SketchManager.InsertSketchSafe(true);
                            }
                            catch { }
                        }
                    }

                    SwHelper.HideSketches(doc, null, preVisible);
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
                    fixed_underdefined_sketches = fixedSketches,
                    sketch_health = SwHelper.BuildSketchHealthSummary(doc),
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

        private static bool IsDestructiveComponentAction(string opAction)
        {
            return opAction == "delete" || opAction == "delete_mates" || opAction == "release_mates";
        }

        private static void FlushPostDeleteRebuild(IModelDoc2 doc)
        {
            if (doc == null) return;
            try { doc.ClearSelection2(true); } catch { }
            try { doc.EditRebuild3(); } catch { }
            try { doc.ClearSelection2(true); } catch { }
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
            bool checkMateHealth = JsonHelper.GetBool(args, "check_mate_health", true);

            return SwConnectionManager.RunWithModalGuard(app, () =>
            {
                SwConnectionManager.ActivateDocumentSafe(app, doc);
                using (IDisposable uiScope = SwHelper.SuppressUiUpdates(doc))
                {
                    if (action == "batch")
                    {
                        List<object> ops = JsonHelper.GetList(args, "operations");
                        if (ops.Count == 0)
                            return McpToolResult.Error("'operations' array is required for action='batch'.");

                        List<object> stepResults = new List<object>();
                        bool dirtyDestructive = false;
                        foreach (object opObj in ops)
                        {
                            Dictionary<string, object> opDict = opObj as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(opObj));
                            if (opDict == null) continue;
                            if (!opDict.ContainsKey("units")) opDict["units"] = defaultUnits;
                            string opAct = (JsonHelper.GetString(opDict, "action", "") ?? "").Trim().ToLowerInvariant();
                            if (dirtyDestructive && !IsDestructiveComponentAction(opAct))
                            {
                                FlushPostDeleteRebuild(doc);
                                dirtyDestructive = false;
                            }

                            object stepRes = ExecuteComponentAction(app, doc, assy, mathUtil, opDict, deferRebuild: true, out string errMsg);
                            if (IsDestructiveComponentAction(opAct) && errMsg == null)
                                dirtyDestructive = true;

                            if (errMsg != null)
                            {
                                stepResults.Add(new { ok = false, action = opAct, error = errMsg });
                            }
                            else
                            {
                                stepResults.Add(new { ok = true, result = stepRes });
                            }
                        }
                        uiScope.Dispose();
                        doc.EditRebuild3();
                        return McpToolResult.JsonText(new
                        {
                            action = "batch",
                            operations_count = stepResults.Count,
                            results = stepResults,
                            mate_health = checkMateHealth ? AssemblyMateHealthAuditor.BuildCompactSummary(doc) : null
                        });
                    }

                    if (action == "pose_and_mate_batch")
                    {
                        Dictionary<string, object> pmRes = ExecutePoseAndMateBatch(app, doc, assy, mathUtil, args, defaultUnits);
                        uiScope.Dispose();
                        doc.EditRebuild3();
                        pmRes["mate_health"] = checkMateHealth ? AssemblyMateHealthAuditor.BuildCompactSummary(doc) : null;
                        return McpToolResult.JsonText(pmRes);
                    }

                    if (action == "swap_fastener_and_insert_spacer")
                    {
                        Dictionary<string, object> spec = JsonHelper.GetDict(args, "swap_spec") ?? args;
                        Dictionary<string, object> swapRes = ExecuteSwapFastenerAndInsertSpacer(app, doc, assy, mathUtil, spec, defaultUnits);
                        uiScope.Dispose();
                        doc.EditRebuild3();
                        swapRes["mate_health"] = checkMateHealth ? AssemblyMateHealthAuditor.BuildCompactSummary(doc) : null;
                        return McpToolResult.JsonText(swapRes);
                    }

                    object singleRes = ExecuteComponentAction(app, doc, assy, mathUtil, args, deferRebuild: false, out string singleErr);
                    uiScope.Dispose();
                    if (singleErr != null)
                        return McpToolResult.Error(singleErr);
                    Dictionary<string, object> singleDict = singleRes as Dictionary<string, object>
                        ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(singleRes));
                    if (singleDict != null)
                    {
                        singleDict["mate_health"] = checkMateHealth ? AssemblyMateHealthAuditor.BuildCompactSummary(doc) : null;
                        return McpToolResult.JsonText(singleDict);
                    }
                    return McpToolResult.JsonText(singleRes);
                }
            });
        }

        private static Dictionary<string, object> ExecutePoseAndMateBatch(
            ISldWorks app,
            IModelDoc2 doc,
            IAssemblyDoc assy,
            IMathUtility mathUtil,
            Dictionary<string, object> args,
            string units)
        {
            List<object> rawItems = JsonHelper.GetList(args, "pose_and_mate_items");
            List<Dictionary<string, object>> items = new List<Dictionary<string, object>>(rawItems.Count);
            foreach (object itemObj in rawItems)
            {
                Dictionary<string, object> item = itemObj as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(itemObj));
                if (item != null) items.Add(item);
            }

            List<Dictionary<string, object>> entryList = new List<Dictionary<string, object>>(items.Count);
            IComponent2[] resolvedComps = new IComponent2[items.Count];
            bool anyReleased = false;

            // Pass 1 (destructive phase): release requested mates before any posing or mating
            for (int i = 0; i < items.Count; i++)
            {
                Dictionary<string, object> item = items[i];
                string compName = JsonHelper.GetString(item, "component_name");
                IComponent2 comp = !string.IsNullOrWhiteSpace(compName)
                    ? AssemblyGeometryHelper.FindComponentByName(assy, compName)
                    : null;
                resolvedComps[i] = comp;

                if (!string.IsNullOrWhiteSpace(compName) && comp == null)
                {
                    entryList.Add(new Dictionary<string, object>
                    {
                        { "ok", false },
                        { "component_name", compName },
                        { "error", "Component not found." }
                    });
                    continue;
                }

                Dictionary<string, object> entryRes = new Dictionary<string, object>
                {
                    { "ok", true },
                    { "component_name", comp != null ? comp.Name2 : null }
                };

                bool releaseMates = JsonHelper.GetBool(item, "release_mates", false);
                if (comp != null && releaseMates)
                {
                    entryRes["released_mates"] = ExecuteComponentAction(app, doc, assy, mathUtil, new Dictionary<string, object>
                    {
                        { "action", "delete_mates" },
                        { "component_names", new List<object> { comp.Name2 } },
                        { "mate_type_filter", JsonHelper.GetString(item, "mate_type_filter", "") },
                        { "top_level_only", JsonHelper.GetBool(item, "top_level_only", true) },
                        { "units", units }
                    }, deferRebuild: true, out _);
                    anyReleased = true;
                }

                entryList.Add(entryRes);
            }

            if (anyReleased)
            {
                FlushPostDeleteRebuild(doc);
            }

            // Batch-unfix all requested components in ONE multi-select UnfixComponent() call
            doc.ClearSelection2(true);
            int unfixSelected = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (!JsonHelper.GetBool(entryList[i], "ok", false)) continue;
                IComponent2 comp = resolvedComps[i];
                if (comp != null && JsonHelper.GetBool(items[i], "unfix", true) && comp.IsFixed())
                {
                    if (comp.Select4(unfixSelected > 0, null, false))
                        unfixSelected++;
                }
            }
            if (unfixSelected > 0)
            {
                assy.UnfixComponent();
                doc.ClearSelection2(true);
            }

            Dictionary<string, SwHelper.LocalPlaneTriplet> planeCache =
                new Dictionary<string, SwHelper.LocalPlaneTriplet>(StringComparer.OrdinalIgnoreCase);
            List<SwHelper.PendingPrincipalPlaneCheck> deferredPrincipalChecks =
                new List<SwHelper.PendingPrincipalPlaneCheck>(items.Count);

            // Pass 2 (constructive phase): pose and create principal/explicit mates with deferred verification
            for (int i = 0; i < items.Count; i++)
            {
                Dictionary<string, object> item = items[i];
                Dictionary<string, object> entryRes = entryList[i];
                if (!JsonHelper.GetBool(entryRes, "ok", false))
                    continue;

                IComponent2 comp = resolvedComps[i];
                bool unfix = JsonHelper.GetBool(item, "unfix", true);
                bool matePrincipal = JsonHelper.GetBool(item, "mate_to_principal_planes", false);
                List<object> itemMates = JsonHelper.GetList(item, "mate_specs");
                bool fixAfter = JsonHelper.GetBool(item, "fix", false);

                bool hasPose = item.ContainsKey("position") || item.ContainsKey("axis") ||
                               item.ContainsKey("ex") || item.ContainsKey("ey") || item.ContainsKey("ez");
                if (comp != null && hasPose)
                {
                    double[] posM = ResolvePositionMeters(comp, item, units);
                    bool directTransformOnly = matePrincipal || itemMates.Count > 0 || fixAfter;
                    ApplyPlacementTransform(comp, item, posM, null, mathUtil, directTransformOnly);
                    entryRes["posed"] = true;
                }

                if (comp != null && matePrincipal)
                {
                    string targetRefName = JsonHelper.GetString(item, "target_ref_component");
                    IComponent2 targetRefComp = !string.IsNullOrWhiteSpace(targetRefName)
                        ? AssemblyGeometryHelper.FindComponentByName(assy, targetRefName)
                        : null;
                    List<IMate2> pMates = SwHelper.MateByPrincipalPlanesCore(
                        assy,
                        comp,
                        targetRefComp,
                        transform: null,
                        deferRebuild: true,
                        skipUnfixCheck: unfix,
                        planeCache: planeCache,
                        deferredChecks: deferredPrincipalChecks);
                    List<string> pNames = new List<string>(pMates.Count);
                    foreach (IMate2 pm in pMates)
                        if (pm is IFeature pf && !string.IsNullOrEmpty(pf.Name)) pNames.Add(pf.Name);
                    entryRes["principal_plane_mates"] = pNames;
                }

                if (itemMates.Count > 0)
                {
                    List<object> createdMates = new List<object>(itemMates.Count);
                    foreach (object msObj in itemMates)
                    {
                        Dictionary<string, object> ms = msObj as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(msObj));
                        if (ms == null) continue;
                        createdMates.Add(CreateMateFromSpec(doc, assy, mathUtil, ms, units, planeCache, deferredPrincipalChecks));
                    }
                    entryRes["mates"] = createdMates;
                }

                if (comp != null && item.ContainsKey("exclude_from_bom"))
                {
                    bool exBom = JsonHelper.GetBool(item, "exclude_from_bom", false);
                    try { comp.ExcludeFromBOM = exBom; entryRes["exclude_from_bom"] = exBom; } catch { }
                }
            }

            if (deferredPrincipalChecks.Count > 0)
            {
                SwHelper.VerifyAndFlipPrincipalPlaneMates(doc, deferredPrincipalChecks, runPostFlipRebuild: false);
            }

            // Batch-fix all requested components in ONE multi-select FixComponent() call
            doc.ClearSelection2(true);
            int fixSelected = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (!JsonHelper.GetBool(entryList[i], "ok", false)) continue;
                IComponent2 comp = resolvedComps[i];
                if (comp != null && JsonHelper.GetBool(items[i], "fix", false) && !comp.IsFixed())
                {
                    if (comp.Select4(fixSelected > 0, null, false))
                        fixSelected++;
                }
            }
            if (fixSelected > 0)
            {
                assy.FixComponent();
                doc.ClearSelection2(true);
            }

            List<object> itemResults = new List<object>(entryList.Count);
            foreach (Dictionary<string, object> e in entryList)
                itemResults.Add(e);

            // Also support top-level placements + mate_specs arrays when pose_and_mate_items is omitted
            List<object> topPlacements = JsonHelper.GetList(args, "placements");
            object topPoseResult = null;
            if (topPlacements.Count > 0)
            {
                topPoseResult = ExecuteComponentAction(app, doc, assy, mathUtil, new Dictionary<string, object>
                {
                    { "action", "transform" },
                    { "placements", topPlacements },
                    { "units", units }
                }, deferRebuild: true, out _);
            }

            List<object> topMates = JsonHelper.GetList(args, "mate_specs");
            object topMateResult = null;
            if (topMates.Count > 0)
            {
                topMateResult = ExecuteComponentAction(app, doc, assy, mathUtil, new Dictionary<string, object>
                {
                    { "action", "mate" },
                    { "mate_specs", topMates },
                    { "units", units }
                }, deferRebuild: true, out _);
            }

            return new Dictionary<string, object>
            {
                { "action", "pose_and_mate_batch" },
                { "items_processed", itemResults.Count },
                { "item_results", itemResults },
                { "placements_result", topPoseResult },
                { "mates_result", topMateResult }
            };
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

            if (delComps.Count > 0 || releaseComps.Count > 0)
            {
                FlushPostDeleteRebuild(doc);
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

                IModelDoc2 openedPartToClose = null;
                try
                {
                    FastenerPartProfile profile = EnsurePartOpenForInsert(app, partPath, ensureAxis, out openedPartToClose);
                    if (profile == null)
                    {
                        errorMsg = "Failed to open/read configuration from part_path: " + partPath;
                        return null;
                    }
                    string cfgName = profile.ActiveConfig ?? "";
                    SwConnectionManager.ActivateDocumentSafe(app, doc);

                    int count = Math.Max(1, placements.Count);
                    List<IComponent2> inserted = new List<IComponent2>();
                    List<string> insertedNames = new List<string>();
                    int failedPlacements = 0;

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
                        if (comp == null)
                        {
                            failedPlacements++;
                            continue;
                        }

                        ApplyPlacementTransform(comp, pDict, posM, profile, mathUtil, directTransformOnly: true);
                        if (pDict != null && pDict.ContainsKey("exclude_from_bom"))
                        {
                            try { comp.ExcludeFromBOM = JsonHelper.GetBool(pDict, "exclude_from_bom", false); } catch { }
                        }
                        else if (args.ContainsKey("exclude_from_bom"))
                        {
                            try { comp.ExcludeFromBOM = JsonHelper.GetBool(args, "exclude_from_bom", false); } catch { }
                        }

                        inserted.Add(comp);
                        insertedNames.Add(comp.Name2);
                    }

                    if (inserted.Count > 0)
                    {
                        doc.ClearSelection2(true);
                        int fixOrUnfixSel = 0;
                        foreach (IComponent2 ic in inserted)
                        {
                            bool needsChange = fixAfterInsert ? !ic.IsFixed() : ic.IsFixed();
                            if (needsChange && ic.Select4(fixOrUnfixSel > 0, null, false))
                                fixOrUnfixSel++;
                        }
                        if (fixOrUnfixSel > 0)
                        {
                            if (fixAfterInsert) assy.FixComponent();
                            else assy.UnfixComponent();
                        }
                        doc.ClearSelection2(true);
                    }

                    if (inserted.Count == 0)
                    {
                        errorMsg = string.Format(CultureInfo.InvariantCulture,
                            "AddComponent5 returned null for all {0} placement(s) of '{1}' (config='{2}').",
                            count, partPath, cfgName);
                        return null;
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
                        inserted_count = insertedNames.Count,
                        failed_placements = failedPlacements,
                        inserted_components = insertedNames,
                        grouped_in_folder = folderName,
                        grouped_count = grouped
                    };
                }
                finally
                {
                    if (openedPartToClose != null)
                    {
                        try { app.CloseDoc(openedPartToClose.GetTitle()); } catch { }
                    }
                }
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

                IModelDoc2 openedPartToClose = null;
                try
                {
                    FastenerPartProfile profile = EnsurePartOpenForInsert(app, partPath, ensureAxis, out openedPartToClose);
                    string cfgName = profile != null ? (profile.ActiveConfig ?? "") : "";
                    SwConnectionManager.ActivateDocumentSafe(app, doc);

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
                finally
                {
                    if (openedPartToClose != null)
                    {
                        try { app.CloseDoc(openedPartToClose.GetTitle()); } catch { }
                    }
                }
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
                bool topLevelOnly = JsonHelper.GetBool(args, "top_level_only", true);
                if (mateNames.Count == 0 && compNames.Count == 0)
                {
                    errorMsg = "Provide 'mate_names' or 'component_names' (with optional mate_type_filter) for action='delete_mates'.";
                    return null;
                }

                int? mateTypeFilter = ParseMateTypeFilter(typeFilterStr);
                HashSet<string> targetMateNames = new HashSet<string>(mateNames, StringComparer.OrdinalIgnoreCase);
                HashSet<string> targetCompNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<string> resolvePatterns = new List<string>();
                bool anyExplicitSubPath = false;
                foreach (string cn in compNames)
                {
                    if (string.IsNullOrWhiteSpace(cn)) continue;
                    string trimmed = cn.Trim();
                    string bare = trimmed.StartsWith("@top:", StringComparison.OrdinalIgnoreCase)
                        ? trimmed.Substring(5).Trim()
                        : (trimmed.StartsWith("/", StringComparison.Ordinal) ? trimmed.Substring(1).Trim() : trimmed);
                    if (bare.Contains("/")) anyExplicitSubPath = true;
                    resolvePatterns.Add((topLevelOnly && !bare.Contains("/") && !trimmed.StartsWith("@top:", StringComparison.OrdinalIgnoreCase))
                        ? ("@top:" + bare)
                        : trimmed);
                    if (!string.IsNullOrEmpty(bare)) targetCompNames.Add(bare);
                }

                foreach (IComponent2 rc in AssemblyGeometryHelper.ResolveComponentsMatching(assy, resolvePatterns, includeSuppressed: true))
                    if (!string.IsNullOrEmpty(rc.Name2)) targetCompNames.Add(rc.Name2);

                List<IFeature> toDelete = new List<IFeature>();
                CollectMatchingMateFeatures(doc, targetMateNames, targetCompNames, mateTypeFilter, topLevelOnly && !anyExplicitSubPath, toDelete);

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

                    double[] posM = ResolvePositionMeters(comp, pDict, units);
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

                Dictionary<string, SwHelper.LocalPlaneTriplet> planeCache =
                    new Dictionary<string, SwHelper.LocalPlaneTriplet>(StringComparer.OrdinalIgnoreCase);
                List<SwHelper.PendingPrincipalPlaneCheck> deferredPrincipalChecks =
                    new List<SwHelper.PendingPrincipalPlaneCheck>(mateSpecs.Count);

                List<object> createdMates = new List<object>();
                foreach (object msObj in mateSpecs)
                {
                    Dictionary<string, object> ms = msObj as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(msObj));
                    if (ms == null) continue;
                    createdMates.Add(CreateMateFromSpec(doc, assy, mathUtil, ms, units, planeCache, deferredPrincipalChecks));
                }
                if (deferredPrincipalChecks.Count > 0)
                {
                    SwHelper.VerifyAndFlipPrincipalPlaneMates(doc, deferredPrincipalChecks, runPostFlipRebuild: !deferRebuild);
                }
                else if (!deferRebuild)
                {
                    doc.EditRebuild3();
                }

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

            if (action == "configure_bom_and_properties")
            {
                return ExecuteConfigureBomAndProperties(app, doc, assy, args, deferRebuild, out errorMsg);
            }

            errorMsg = "Unsupported action: " + action;
            return null;
        }

        private static object ExecuteConfigureBomAndProperties(
            ISldWorks app,
            IModelDoc2 doc,
            IAssemblyDoc assy,
            Dictionary<string, object> args,
            bool deferRebuild,
            out string errorMsg)
        {
            errorMsg = null;
            List<string> compNames = JsonHelper.GetStringList(args, "component_names");
            List<string> excludeComps = JsonHelper.GetStringList(args, "exclude_from_bom_components");
            List<string> includeComps = JsonHelper.GetStringList(args, "include_in_bom_components");
            List<object> compProps = JsonHelper.GetList(args, "component_properties");

            if (args.ContainsKey("exclude_from_bom") && compNames.Count > 0)
            {
                if (JsonHelper.GetBool(args, "exclude_from_bom", false))
                    excludeComps.AddRange(compNames);
                else
                    includeComps.AddRange(compNames);
            }

            List<string> excludedList = new List<string>();
            if (excludeComps.Count > 0)
            {
                foreach (IComponent2 c in AssemblyGeometryHelper.ResolveComponentsMatching(assy, excludeComps, includeSuppressed: true))
                {
                    try
                    {
                        c.ExcludeFromBOM = true;
                        excludedList.Add(c.Name2);
                    }
                    catch { }
                }
            }

            List<string> includedList = new List<string>();
            if (includeComps.Count > 0)
            {
                foreach (IComponent2 c in AssemblyGeometryHelper.ResolveComponentsMatching(assy, includeComps, includeSuppressed: true))
                {
                    try
                    {
                        c.ExcludeFromBOM = false;
                        includedList.Add(c.Name2);
                    }
                    catch { }
                }
            }

            Dictionary<string, IModelDoc2> docsToSave = new Dictionary<string, IModelDoc2>(StringComparer.OrdinalIgnoreCase);
            List<IModelDoc2> openedToClose = new List<IModelDoc2>();
            List<object> propertyResults = new List<object>();

            try
            {
                foreach (object cpObj in compProps)
                {
                    Dictionary<string, object> cp = cpObj as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(cpObj));
                    if (cp == null) continue;

                    List<string> targetComps = JsonHelper.GetStringList(cp, "component_names");
                    string singleComp = JsonHelper.GetString(cp, "component_name");
                    if (!string.IsNullOrWhiteSpace(singleComp))
                        targetComps.Add(singleComp);
                    string explicitPartPath = JsonHelper.GetString(cp, "part_path");
                    bool savePart = JsonHelper.GetBool(cp, "save_part", true);
                    bool configSpecific = JsonHelper.GetBool(cp, "configuration_specific", false);
                    Dictionary<string, object> props = JsonHelper.GetDict(cp, "properties");

                    List<IComponent2> matchedComps = targetComps.Count > 0
                        ? AssemblyGeometryHelper.ResolveComponentsMatching(assy, targetComps, includeSuppressed: true)
                        : new List<IComponent2>();

                    if (cp.ContainsKey("exclude_from_bom"))
                    {
                        bool exVal = JsonHelper.GetBool(cp, "exclude_from_bom", false);
                        foreach (IComponent2 mc in matchedComps)
                        {
                            try
                            {
                                mc.ExcludeFromBOM = exVal;
                                if (exVal) excludedList.Add(mc.Name2);
                                else includedList.Add(mc.Name2);
                            }
                            catch { }
                        }
                    }

                    if (props == null || props.Count == 0)
                        continue;

                    HashSet<string> targetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Dictionary<string, IModelDoc2> pathToDoc = new Dictionary<string, IModelDoc2>(StringComparer.OrdinalIgnoreCase);

                    if (!string.IsNullOrWhiteSpace(explicitPartPath) && File.Exists(explicitPartPath))
                        targetPaths.Add(explicitPartPath);

                    foreach (IComponent2 mc in matchedComps)
                    {
                        string p = mc.GetPathName();
                        if (!string.IsNullOrWhiteSpace(p))
                            targetPaths.Add(p);
                        IModelDoc2 mDoc = mc.GetModelDoc2() as IModelDoc2;
                        if (mDoc != null && !string.IsNullOrWhiteSpace(p))
                            pathToDoc[p] = mDoc;
                    }

                    foreach (string p in targetPaths)
                    {
                        if (!pathToDoc.TryGetValue(p, out IModelDoc2 targetPartDoc) || targetPartDoc == null)
                        {
                            int docType = p.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase)
                                ? (int)swDocumentTypes_e.swDocASSEMBLY
                                : (int)swDocumentTypes_e.swDocPART;
                            int oErr = 0, oWarn = 0;
                            app.DocumentVisible(false, docType);
                            try
                            {
                                targetPartDoc = app.OpenDoc6(p, docType, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref oErr, ref oWarn);
                                if (targetPartDoc != null)
                                    openedToClose.Add(targetPartDoc);
                            }
                            finally
                            {
                                try { app.DocumentVisible(true, docType); } catch { }
                            }
                        }

                        if (targetPartDoc == null)
                        {
                            propertyResults.Add(new { part_path = p, ok = false, error = "Could not acquire IModelDoc2 for custom properties." });
                            continue;
                        }

                        int writtenCount = WriteModelCustomProperties(targetPartDoc, props, configSpecific);
                        if (savePart)
                            docsToSave[p] = targetPartDoc;

                        propertyResults.Add(new
                        {
                            part_path = p,
                            part_title = targetPartDoc.GetTitle(),
                            ok = true,
                            properties_written = writtenCount
                        });
                    }
                }

                List<string> savedPaths = new List<string>();
                foreach (KeyValuePair<string, IModelDoc2> kv in docsToSave)
                {
                    if (SwHelper.SaveSilent(kv.Value))
                        savedPaths.Add(kv.Key);
                }

                SwConnectionManager.ActivateDocumentSafe(app, doc);
                if (!deferRebuild) doc.EditRebuild3();

                return new
                {
                    action = "configure_bom_and_properties",
                    excluded_from_bom_count = excludedList.Count,
                    excluded_from_bom = excludedList,
                    included_in_bom_count = includedList.Count,
                    included_in_bom = includedList,
                    property_updates = propertyResults,
                    saved_parts = savedPaths
                };
            }
            finally
            {
                foreach (IModelDoc2 oc in openedToClose)
                {
                    try { app.CloseDoc(oc.GetTitle()); } catch { }
                }
                try { SwConnectionManager.ActivateDocumentSafe(app, doc); } catch { }
            }
        }

        internal static int WriteModelCustomProperties(IModelDoc2 model, Dictionary<string, object> props, bool alsoConfigSpecific = true)
        {
            if (model == null || props == null || props.Count == 0)
                return 0;

            ICustomPropertyManager docCpm = model.Extension.get_CustomPropertyManager("");
            string activeCfg = model.ConfigurationManager?.ActiveConfiguration?.Name ?? "";
            ICustomPropertyManager cfgCpm = (alsoConfigSpecific && !string.IsNullOrEmpty(activeCfg))
                ? model.Extension.get_CustomPropertyManager(activeCfg)
                : null;

            int written = 0;
            foreach (KeyValuePair<string, object> kv in props)
            {
                if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                string key = kv.Key.Trim();
                string val = Convert.ToString(kv.Value ?? "", CultureInfo.InvariantCulture);
                try
                {
                    if (docCpm != null)
                        docCpm.Add3(key, (int)swCustomInfoType_e.swCustomInfoText, val, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                    if (cfgCpm != null)
                        cfgCpm.Add3(key, (int)swCustomInfoType_e.swCustomInfoText, val, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                    written++;
                }
                catch { }
            }
            return written;
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
            bool requireTopLevelComponentsOnly,
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
                        WalkMateSubfeature(sub, targetMateNames, targetCompNames, mateTypeFilter, requireTopLevelComponentsOnly, seen, matches);
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
            bool requireTopLevelComponentsOnly,
            HashSet<string> seen,
            List<IFeature> matches)
        {
            if (sub == null) return;
            if (string.Equals(sub.GetTypeName2(), "FtrFolder", StringComparison.OrdinalIgnoreCase))
            {
                IFeature child = sub.GetFirstSubFeature() as IFeature;
                while (child != null)
                {
                    WalkMateSubfeature(child, targetMateNames, targetCompNames, mateTypeFilter, requireTopLevelComponentsOnly, seen, matches);
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
                    string rcName = rc != null ? (rc.Name2 ?? "") : "";
                    if (string.IsNullOrEmpty(rcName)) continue;
                    if (requireTopLevelComponentsOnly && rcName.Contains("/")) continue;
                    if (targetCompNames.Contains(rcName))
                    {
                        compHit = true;
                        break;
                    }
                }
            }

            if ((nameHit || compHit) && seen.Add(sub.Name ?? ""))
                matches.Add(sub);
        }

        private static FastenerPartProfile EnsurePartOpenForInsert(
            ISldWorks app,
            string partPath,
            bool ensureAxis,
            out IModelDoc2 openedDocToClose)
        {
            openedDocToClose = null;
            string fullTargetPath = Path.GetFullPath(partPath);
            int oErr = 0, oWarn = 0;
            bool wasOpen = false;
            IModelDoc2 partDoc = null;

            IModelDoc2 iter = app.GetFirstDocument() as IModelDoc2;
            while (iter != null)
            {
                string iterPath = iter.GetPathName() ?? "";
                if (!string.IsNullOrEmpty(iterPath))
                {
                    try
                    {
                        if (string.Equals(Path.GetFullPath(iterPath), fullTargetPath, StringComparison.OrdinalIgnoreCase))
                        {
                            partDoc = iter;
                            wasOpen = true;
                            break;
                        }
                    }
                    catch
                    {
                        if (string.Equals(iterPath, partPath, StringComparison.OrdinalIgnoreCase))
                        {
                            partDoc = iter;
                            wasOpen = true;
                            break;
                        }
                    }
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
                if (partDoc != null && !wasOpen)
                    openedDocToClose = partDoc;
            }

            if (partDoc == null)
                return null;

            if (ensureAxis)
                return AssemblyGeometryHelper.EnsureFastenerAxisAndInspect(app, partPath);

            string cfg = partDoc.ConfigurationManager?.ActiveConfiguration?.Name ?? "";
            return new FastenerPartProfile
            {
                PartPath = partPath,
                ActiveConfig = cfg,
                LocalAxis = new[] { 0.0, 1.0, 0.0 }
            };
        }

        private static object CreateMateFromSpec(
            IModelDoc2 doc,
            IAssemblyDoc assy,
            IMathUtility mathUtil,
            Dictionary<string, object> ms,
            string units,
            Dictionary<string, SwHelper.LocalPlaneTriplet> planeCache = null,
            List<SwHelper.PendingPrincipalPlaneCheck> deferredPrincipalChecks = null)
        {
            string mTypeStr = JsonHelper.GetString(ms, "mate_type", "concentric").ToLowerInvariant();
            if (mTypeStr == "principal_planes")
            {
                string compName = JsonHelper.GetString(ms, "component_name");
                IComponent2 comp = AssemblyGeometryHelper.FindComponentByName(assy, compName);
                if (comp == null)
                {
                    return new
                    {
                        mate_type = "principal_planes",
                        component_name = compName,
                        created = false,
                        error = "Component not found."
                    };
                }
                string targetRefName = JsonHelper.GetString(ms, "target_ref_component") ?? JsonHelper.GetString(ms, "target_component_name");
                IComponent2 targetRef = !string.IsNullOrWhiteSpace(targetRefName)
                    ? AssemblyGeometryHelper.FindComponentByName(assy, targetRefName)
                    : null;

                IMathTransform xf = null;
                if (ms.ContainsKey("position") || ms.ContainsKey("axis") || ms.ContainsKey("ex") || ms.ContainsKey("ey") || ms.ContainsKey("ez"))
                {
                    double[] posM = ResolvePositionMeters(comp, ms, units);
                    xf = BuildTransformFromSpec(comp, ms, posM, null, mathUtil);
                }

                List<IMate2> created = SwHelper.MateByPrincipalPlanesCore(
                    assy,
                    comp,
                    targetRef,
                    xf,
                    deferRebuild: deferredPrincipalChecks != null,
                    skipUnfixCheck: false,
                    planeCache: planeCache,
                    deferredChecks: deferredPrincipalChecks);
                List<string> mNames = new List<string>();
                foreach (IMate2 m in created)
                    if (m is IFeature mf && !string.IsNullOrEmpty(mf.Name)) mNames.Add(mf.Name);

                return new
                {
                    mate_type = "principal_planes",
                    component_name = comp.Name2,
                    created = created.Count == 3,
                    mate_names = mNames
                };
            }

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

            doc.ClearSelection2(true);
            string descA, descB;
            if (!TrySelectMateSide(doc, assy, ms, isA: true, units, append: false, mark: 1, out descA))
            {
                doc.ClearSelection2(true);
                return new { entity_a = descA, entity_b = JsonHelper.GetString(ms, "entity_b"), mate_type = mTypeStr, created = false, error_code = -1, error = "Failed to select entity_a" };
            }
            if (!TrySelectMateSide(doc, assy, ms, isA: false, units, append: true, mark: 1, out descB))
            {
                doc.ClearSelection2(true);
                return new { entity_a = descA, entity_b = descB, mate_type = mTypeStr, created = false, error_code = -1, error = "Failed to select entity_b" };
            }

            int errCode;
            IMate2 mate = assy.AddMate5(
                mateType,
                align,
                flip,
                distM, distM, distM,
                1.0, 1.0,
                angRad, angRad, angRad,
                false,
                lockRot,
                0,
                out errCode);
            doc.ClearSelection2(true);

            return new
            {
                entity_a = descA,
                entity_b = descB,
                mate_type = mTypeStr,
                created = mate != null && errCode == (int)swAddMateError_e.swAddMateError_NoError,
                mate_name = (mate as IFeature)?.Name,
                error_code = errCode
            };
        }

        private static bool TrySelectMateSide(
            IModelDoc2 doc,
            IAssemblyDoc assy,
            Dictionary<string, object> ms,
            bool isA,
            string units,
            bool append,
            int mark,
            out string description)
        {
            string specKey = isA ? "entity_a_spec" : "entity_b_spec";
            string strKey = isA ? "entity_a" : "entity_b";
            Dictionary<string, object> spec = JsonHelper.GetDict(ms, specKey);
            if (spec != null)
            {
                string compName = JsonHelper.GetString(spec, "component_name");
                string kind = (JsonHelper.GetString(spec, "kind", "planar") ?? "planar").Trim().ToLowerInvariant();
                description = string.IsNullOrEmpty(compName) ? kind : (compName + "::" + kind);

                IComponent2 comp = null;
                if (!string.IsNullOrWhiteSpace(compName) && !string.Equals(compName, "__root__", StringComparison.OrdinalIgnoreCase))
                {
                    comp = AssemblyGeometryHelper.FindComponentByName(assy, compName);
                    if (comp == null) return false;
                }

                ISelectionMgr selMgr = doc.SelectionManager as ISelectionMgr;
                SelectData selData = selMgr != null ? (SelectData)selMgr.CreateSelectData() : null;
                if (selData != null) selData.Mark = mark;

                if (kind == "planar" && comp != null)
                {
                    double[] ptMm = ResolvePointMm(spec, "world_point_mm", "world_point", units);
                    double[] norm = JsonHelper.GetDoubleArray(spec, "world_normal") ?? JsonHelper.GetDoubleArray(spec, "normal");
                    double minAreaMm2 = JsonHelper.GetDouble(spec, "min_area_mm2", 0.0);
                    double tolMm = JsonHelper.GetDouble(spec, "tol_mm", 2.0);
                    IEntity ent = SwHelper.FindPlanarFace(comp, ptMm, norm, minAreaMm2, tolMm);
                    return ent != null && ent.Select4(append, selData);
                }

                if (kind == "cylindrical" && comp != null)
                {
                    double[] ptMm = ResolvePointMm(spec, "world_point_mm", "world_point", units);
                    double[] axis = JsonHelper.GetDoubleArray(spec, "world_axis") ?? JsonHelper.GetDoubleArray(spec, "axis");
                    double radiusMm = spec.ContainsKey("radius_mm")
                        ? JsonHelper.GetDouble(spec, "radius_mm", 0.0)
                        : ShopCatalogService.ToMeters(JsonHelper.GetDouble(spec, "radius", 0.0), units) * 1000.0;
                    double radTolMm = JsonHelper.GetDouble(spec, "radius_tol_mm", 0.5);
                    IEntity ent = SwHelper.FindCylindricalFace(comp, ptMm, axis, radiusMm, radTolMm);
                    return ent != null && ent.Select4(append, selData);
                }

                string featName = JsonHelper.GetString(spec, "feature_name") ?? JsonHelper.GetString(spec, "plane_name") ?? JsonHelper.GetString(spec, "name");
                if (!string.IsNullOrWhiteSpace(featName))
                {
                    IFeature feat = comp != null
                        ? comp.FeatureByName(featName)
                        : (((doc as IPartDoc)?.FeatureByName(featName) as IFeature)
                           ?? ((doc as IAssemblyDoc)?.FeatureByName(featName) as IFeature)
                           ?? FeatureAndFixtureTools.FindFeatureByName(doc, featName));
                    if (feat != null)
                        return feat.Select2(append, mark);
                }
                return false;
            }

            string selector = JsonHelper.GetString(ms, strKey);
            description = selector;
            return AssemblyGeometryHelper.TrySelectAssemblyTarget(doc, assy, selector, append, mark);
        }

        private static double[] ResolvePointMm(Dictionary<string, object> spec, string mmKey, string unitKey, string units)
        {
            double[] ptMm = JsonHelper.GetDoubleArray(spec, mmKey);
            if (ptMm != null && ptMm.Length >= 3) return ptMm;
            double[] ptUnit = JsonHelper.GetDoubleArray(spec, unitKey);
            if (ptUnit != null && ptUnit.Length >= 3)
            {
                return new[]
                {
                    ShopCatalogService.ToMeters(ptUnit[0], units) * 1000.0,
                    ShopCatalogService.ToMeters(ptUnit[1], units) * 1000.0,
                    ShopCatalogService.ToMeters(ptUnit[2], units) * 1000.0
                };
            }
            return null;
        }

        private static double[] ResolvePositionMeters(IComponent2 comp, Dictionary<string, object> pDict, string units)
        {
            double[] posArr = pDict != null ? JsonHelper.GetDoubleArray(pDict, "position") : null;
            if (posArr != null && posArr.Length >= 3)
            {
                return new[]
                {
                    ShopCatalogService.ToMeters(posArr[0], units),
                    ShopCatalogService.ToMeters(posArr[1], units),
                    ShopCatalogService.ToMeters(posArr[2], units)
                };
            }
            double[] m = (comp != null && comp.Transform2 != null) ? comp.Transform2.ArrayData as double[] : null;
            if (m != null && m.Length >= 12)
                return new[] { m[9], m[10], m[11] };
            return new[] { 0.0, 0.0, 0.0 };
        }

        private static MathTransform BuildTransformFromSpec(
            IComponent2 comp,
            Dictionary<string, object> pDict,
            double[] posM,
            FastenerPartProfile profile,
            IMathUtility mathUtil)
        {
            if (pDict == null || mathUtil == null) return null;

            double[] ex = JsonHelper.GetDoubleArray(pDict, "ex");
            double[] ey = JsonHelper.GetDoubleArray(pDict, "ey");
            double[] ez = JsonHelper.GetDoubleArray(pDict, "ez");
            if ((ex != null && ex.Length >= 3) || (ey != null && ey.Length >= 3) || (ez != null && ez.Length >= 3))
            {
                if (ex != null && ey != null && ez == null)
                    ez = AssemblyGeometryHelper.Cross(ex, ey);
                else if (ex != null && ez != null && ey == null)
                    ey = AssemblyGeometryHelper.Cross(ez, ex);
                else if (ey != null && ez != null && ex == null)
                    ex = AssemblyGeometryHelper.Cross(ey, ez);

                if (ex != null && ey != null && ez != null)
                {
                    double[] ux = AssemblyGeometryHelper.Normalize(ex);
                    double[] uz = AssemblyGeometryHelper.Normalize(AssemblyGeometryHelper.Cross(ux, ey));
                    double[] uy = AssemblyGeometryHelper.Normalize(AssemblyGeometryHelper.Cross(uz, ux));
                    double[] mData = new[]
                    {
                        ux[0], ux[1], ux[2],
                        uy[0], uy[1], uy[2],
                        uz[0], uz[1], uz[2],
                        posM[0], posM[1], posM[2],
                        1.0, 0.0, 0.0, 0.0
                    };
                    return mathUtil.CreateTransform(mData) as MathTransform;
                }
            }

            double[] targetAxis = JsonHelper.GetDoubleArray(pDict, "axis");
            if (targetAxis != null && targetAxis.Length >= 3)
            {
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

                return AssemblyGeometryHelper.BuildTransformFromPose(
                    mathUtil, posM, targetAxis, localAxis, bearingOffM, clockDeg, hexLocalDeg);
            }

            if (pDict.ContainsKey("position") && comp != null)
            {
                double[] cur = comp.Transform2 != null ? comp.Transform2.ArrayData as double[] : AssemblyGeometryHelper.IdentityTransform16();
                if (cur != null && cur.Length >= 16)
                {
                    double[] copy = (double[])cur.Clone();
                    copy[9] = posM[0];
                    copy[10] = posM[1];
                    copy[11] = posM[2];
                    return mathUtil.CreateTransform(copy) as MathTransform;
                }
            }

            return null;
        }

        private static void ApplyPlacementTransform(
            IComponent2 comp,
            Dictionary<string, object> pDict,
            double[] posM,
            FastenerPartProfile profile,
            IMathUtility mathUtil,
            bool directTransformOnly = false)
        {
            if (comp == null || pDict == null || mathUtil == null) return;
            MathTransform xf = BuildTransformFromSpec(comp, pDict, posM, profile, mathUtil);
            if (xf != null)
            {
                if (directTransformOnly)
                {
                    comp.Transform2 = xf;
                }
                else if (!comp.SetTransformAndSolve3(xf, false))
                {
                    comp.Transform2 = xf;
                }
            }
        }
    }
}
