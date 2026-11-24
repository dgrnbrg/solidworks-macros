"""Tests for the unified SOLIDWORKS plugin catalogs, skills, and MCP stdio server."""

from __future__ import annotations

import json
import os
import re
import subprocess
import tempfile
import time
import unittest
import uuid
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
PLUGIN_ROOT = REPO_ROOT / ".agents" / "plugins" / "solidworks"
MCP_EXE = Path(
    os.environ.get(
        "SOLIDWORKS_MCP_EXE",
        str(REPO_ROOT / "src" / "SolidWorksMcpServer" / "bin" / "x64" / "Release" / "SolidWorksMcpServer.exe"),
    )
)


class TestPluginArtifactsAndCatalogs(unittest.TestCase):
    def test_plugin_manifests_and_rules_exist(self) -> None:
        plugin_json = json.loads((PLUGIN_ROOT / "plugin.json").read_text(encoding="utf-8"))
        self.assertEqual(plugin_json["name"], "solidworks")
        self.assertEqual(len(plugin_json["suggestedPrompts"]), 3)

        mcp_json = json.loads((PLUGIN_ROOT / "mcp_config.json").read_text(encoding="utf-8"))
        self.assertIn("solidworks-mcp", mcp_json["mcpServers"])

        rules_text = (PLUGIN_ROOT / "rules" / "AGENTS.md").read_text(encoding="utf-8")
        self.assertFalse(rules_text.lstrip().startswith("---"), "rules/AGENTS.md must not have YAML frontmatter")
        self.assertIn("MMGS", rules_text)
        self.assertIn("IPS", rules_text)
        self.assertIn("SendCutSend", rules_text)
        self.assertIn("Export Only on Explicit User Request", rules_text)
        self.assertIn("include_components", rules_text)
        self.assertIn("sw_audit_assembly_mates", rules_text)
        self.assertIn("Zero Yellow/Red Mates Gate", rules_text)
        self.assertIn("HideSketches", rules_text)
        self.assertIn("Manufacturer Datasheet", rules_text)
        self.assertIn("HoleWizard5", rules_text)
        self.assertIn("3dp_self_thread", rules_text)
        self.assertIn("In-Context Assembly Constraints", rules_text)
        self.assertIn("Exclusive SOLIDWORKS Session Lock", rules_text)
        self.assertIn("locked_by_other_agent", rules_text)
        self.assertIn("Stop-Process -Force", rules_text)
        self.assertIn("WM_CLOSE", rules_text)
        self.assertIn("Crash / Timeout Yield & Debug Ticket Protocol", rules_text)

        workflows_text = (
            PLUGIN_ROOT / "skills" / "solidworks-cad-modeling" / "workflows" / "end_to_end_workflows.md"
        ).read_text(encoding="utf-8")
        self.assertNotIn("-> `sw_export_document(", workflows_text)
        self.assertNotIn("-> sw_export_document(", workflows_text)
        self.assertNotIn("-> `sw_export_scs_flat_pattern_dxf", workflows_text)
        self.assertNotIn("-> sw_export_scs_flat_pattern_dxf", workflows_text)

        cad_skill_text = (PLUGIN_ROOT / "skills" / "solidworks-cad-modeling" / "SKILL.md").read_text(encoding="utf-8")
        for expected_tok in (
            "swDontRebuildActiveDoc",
            "EditRebuild3",
            "DeleteSelection2",
            "IFace2.Normal",
            "SwHelper",
            "Z_MIN_POS",
            "Z_MAX_POS",
            "sw_audit_assembly_mates",
            "SuppressUiUpdates",
            "BatchDeleteMatesForComponents",
            "SWCEFComWrapper",
            "HideSketches",
            "FullyDefineActiveSketch",
            "EditHoleWizardLocations",
            "BeginEditPartInAssembly",
            "3dp_self_thread",
            "HoleWizard5",
            "lock_action",
            "locked_by_other_agent",
            "Stop-Process -Force",
            "WM_CLOSE",
            "Crash / Timeout Yield & Debug Ticket Protocol",
        ):
            self.assertIn(expected_tok, cad_skill_text)

        launcher_cs = (REPO_ROOT / "src" / "SolidWorksMcpServer" / "Core" / "ThreeDExperienceEdgeLauncher.cs").read_text(encoding="utf-8")
        self.assertNotIn("proc.Kill()", launcher_cs, "ThreeDExperienceEdgeLauncher must never force-kill SLDWORKS.exe")
        self.assertIn("WM_CLOSE", launcher_cs)

        conn_cs = (REPO_ROOT / "src" / "SolidWorksMcpServer" / "Core" / "SwConnectionManager.cs").read_text(encoding="utf-8")
        self.assertNotIn("prevInputDim", conn_cs, "SwConnectionManager must never restore swInputDimValOnCreate back to prevInputDim")
        self.assertIn("EnsureHeadlessSafePreferences", conn_cs)
        self.assertIn("RunWithModalGuard(_swApp", conn_cs)
        self.assertIn("IsRpcDisconnectOrServerCrash", conn_cs)

        helper_cs = (REPO_ROOT / "src" / "SolidWorksMcpServer" / "Tools" / "SwHelper.cs").read_text(encoding="utf-8")
        self.assertIn("SuppressDimensionInputPopup", helper_cs)
        self.assertIn("DismissActiveSolidWorksModalDialogs", helper_cs)
        self.assertIn("InsertSketchSafe", helper_cs)
        self.assertIn("DeleteFixedRelationsOnLastAttempt", helper_cs)

        dyn_cs = (REPO_ROOT / "src" / "SolidWorksMcpServer" / "Tools" / "DynamicCSharpTool.cs").read_text(encoding="utf-8")
        self.assertIn("swInputDimValOnCreate, false", dyn_cs)
        self.assertIn(".InsertSketchSafe(", dyn_cs)
        self.assertIn("SuppressUiUpdates", dyn_cs)
        self.assertIn("RecordCrashEvent", dyn_cs)

        feat_cs = (REPO_ROOT / "src" / "SolidWorksMcpServer" / "Tools" / "FeatureAndFixtureTools.cs").read_text(encoding="utf-8")
        self.assertIn("TryResolvePlanarSolidFaceForHole", feat_cs)
        self.assertIn("IsPlanarSolidFaceSelected", feat_cs)
        self.assertIn("through_all_both", feat_cs)
        self.assertIn("depth2", feat_cs)

        sketch_cs = (REPO_ROOT / "src" / "SolidWorksMcpServer" / "Tools" / "SketchAndEquationTools.cs").read_text(encoding="utf-8")
        self.assertIn('"polyline"', sketch_cs)

        print_skill_text = (PLUGIN_ROOT / "skills" / "dfm-3d-printing" / "SKILL.md").read_text(encoding="utf-8")
        for expected_3dp in ("42°", "D-loop", "Glass Transition", "Cold Creep", "Abrasion Resistance", "Slipperiness", "3dp_self_thread"):
            self.assertIn(expected_3dp, print_skill_text)

    def test_no_swUserDecision_in_mcp_server_sources(self) -> None:
        src_root = REPO_ROOT / "src" / "SolidWorksMcpServer"
        for cs_file in src_root.rglob("*.cs"):
            txt = cs_file.read_text(encoding="utf-8")
            self.assertNotIn("swRebuildOnActivation_e.swUserDecision", txt, f"Found forbidden swUserDecision in {cs_file}")

    def test_no_CommandInProgress_in_mcp_server_sources(self) -> None:
        src_root = REPO_ROOT / "src" / "SolidWorksMcpServer"
        for cs_file in src_root.rglob("*.cs"):
            txt = cs_file.read_text(encoding="utf-8")
            self.assertNotRegex(
                txt,
                r"\.\s*CommandInProgress\s*=",
                f"Found forbidden CommandInProgress assignment in {cs_file}",
            )

    def test_ray_intersections_uses_dispatch_wrapper(self) -> None:
        src_root = REPO_ROOT / "src" / "SolidWorksMcpServer"
        for cs_file in src_root.rglob("*.cs"):
            txt = cs_file.read_text(encoding="utf-8")
            self.assertNotRegex(
                txt,
                r"RayIntersections\s*\(\s*new\s+object\s*\[",
                f"RayIntersections must pass DispatchWrapper[] (not object[]) in {cs_file}",
            )

    def test_no_crash_prone_sketchslot_or_rayintersections_in_mcp_sources(self) -> None:
        src_root = REPO_ROOT / "src" / "SolidWorksMcpServer"
        for cs_file in src_root.rglob("*.cs"):
            txt = cs_file.read_text(encoding="utf-8")
            if cs_file.name != "DynamicCSharpTool.cs":
                self.assertNotIn(
                    ".CreateSketchSlot(",
                    txt,
                    f"Forbidden crash-prone .CreateSketchSlot( found in {cs_file}; use SwHelper.CreateSlot2D",
                )
        dfm_cs = (src_root / "Tools" / "DfmAuditAndFabricationTools.cs").read_text(encoding="utf-8")
        self.assertNotIn(".RayIntersections(", dfm_cs)
        self.assertNotIn("GetRayIntersectionsPoints", dfm_cs)

    def test_shop_profile_completeness(self) -> None:
        profile = json.loads((PLUGIN_ROOT / "data" / "shop_profile.json").read_text(encoding="utf-8"))
        units = profile["unit_system_preferences"]
        self.assertEqual(units["3d_printing"], "MMGS")
        self.assertEqual(units["gridfinity"], "MMGS")
        self.assertEqual(units["electronics_kicad"], "MMGS")
        self.assertEqual(units["woodworking"], "IPS")
        self.assertEqual(units["cnc_furniture_and_jigs"], "IPS")
        self.assertEqual(units["residential_electrical"], "IPS")

        inserts = {x["mcmaster_pn"] for x in profile["printing_3d"]["heat_set_inserts_mcmaster"]}
        self.assertEqual(inserts, {"94459A110", "93365A112", "94180A333", "94180A353"})

        mat_props = profile["printing_3d"]["filaments"]["material_properties_reference"]
        for mat_key in ("tough_pla_basic", "tough_pla_pro", "petg", "pvb", "cpe", "asa", "tpu", "prusament_pc"):
            self.assertIn(mat_key, mat_props)
            for field in ("tg_c", "max_loaded_temp_c", "cold_creep_resistance", "abrasion_resistance", "slipperiness"):
                self.assertIn(field, mat_props[mat_key])

        self.assertEqual(profile["gridfinity"]["grid_pitch_mm"], 42.0)
        self.assertEqual(profile["gridfinity"]["height_unit_u_mm"], 7.0)
        self.assertEqual(len(profile["cnc_carving"]["cutter_inventory"]), 8)
        self.assertEqual(profile["woodworking"]["clamping_inventory"]["parallel_bar_clamps"], 4)
        self.assertEqual(profile["woodworking"]["clamping_inventory"]["pipe_clamps"], 6)

    def test_sendcutsend_catalog(self) -> None:
        scs = json.loads((PLUGIN_ROOT / "data" / "sendcutsend_catalog.json").read_text(encoding="utf-8"))
        mats = scs["materials"]
        self.assertTrue(mats["5052_aluminum"]["bendable"])
        self.assertFalse(mats["6061_aluminum"]["bendable"])
        self.assertFalse(mats["c260_brass"]["bendable"])
        self.assertFalse(mats["c110_copper"]["bendable"])

    def test_all_nine_skills_present_and_valid(self) -> None:
        expected = {
            "solidworks-cad-modeling",
            "solidworks-fixtures-and-imports",
            "solidworks-feature-tree-hygiene",
            "dfm-3d-printing",
            "dfm-cnc-carving",
            "dfm-sheet-metal-sendcutsend",
            "dfm-woodworking",
            "solidworks-addins",
            "solidworks-macros",
        }
        skills = sorted((PLUGIN_ROOT / "skills").glob("*/SKILL.md"))
        found = {p.parent.name for p in skills}
        self.assertEqual(found, expected)
        for p in skills:
            txt = p.read_text(encoding="utf-8")
            self.assertRegex(txt, re.compile(r"^---\s*\nname:\s*" + re.escape(p.parent.name) + r"\s*\ndescription:", re.M))

    def test_all_2d_sketch_toggles_use_insert_sketch_safe(self) -> None:
        tools_dir = REPO_ROOT / "src" / "SolidWorksMcpServer" / "Tools"
        for cs_file in tools_dir.glob("*.cs"):
            if cs_file.name == "SwHelper.cs":
                continue
            txt = cs_file.read_text(encoding="utf-8")
            self.assertNotIn(
                ".InsertSketch(",
                txt,
                f"Raw .InsertSketch( found in {cs_file}; use .InsertSketchSafe( to avoid uoSelRefGeom_c OnIdle crash",
            )
            self.assertNotIn(
                ".CreateCornerRectangle(",
                txt,
                f"Raw .CreateCornerRectangle( found in {cs_file}; use .CreateCornerRectangleSafe( to avoid uoSelRefGeom_c::getRefGeom crash",
            )
            self.assertNotIn(
                ".CreateCenterRectangle(",
                txt,
                f"Raw .CreateCenterRectangle( found in {cs_file}; use .CreateCenterRectangleSafe( to avoid uoSelRefGeom_c::getRefGeom crash",
            )

        helper_cs = (tools_dir / "SwHelper.cs").read_text(encoding="utf-8")
        m = re.search(
            r"public static void InsertSketchSafe\(this ISketchManager sm.*?if \(!wasInSketch\)\s*\{(?P<enter>.*?)\}\s*else\s*\{",
            helper_cs,
            re.S,
        )
        self.assertIsNotNone(m, "InsertSketchSafe !wasInSketch branch must exist in SwHelper.cs")
        enter_body = m.group("enter")
        self.assertIn("MaskNonMdiControlBarsVisible", enter_body)
        self.assertLess(enter_body.index("MaskNonMdiControlBarsVisible"), enter_body.index("sm.InsertSketch("))
        self.assertGreater(enter_body.rindex("MaskNonMdiControlBarsVisible"), enter_body.index("sm.InsertSketch("))
        self.assertIn("sm.AddToDB = true", enter_body)
        self.assertGreater(enter_body.index("sm.AddToDB = true"), enter_body.index("sm.InsertSketch("))
        self.assertGreater(enter_body.rindex("MaskNonMdiControlBarsVisible"), enter_body.index("sm.AddToDB = true"))
        self.assertIn("ClearResidualDatumPlaneSelection", enter_body)
        self.assertNotIn("Thread.Sleep(", enter_body)
        conn_cs_txt = (REPO_ROOT / "src" / "SolidWorksMcpServer" / "Core" / "SwConnectionManager.cs").read_text(encoding="utf-8")
        self.assertIn("MaskNonMdiControlBarsVisible", conn_cs_txt)
        self.assertIn("RestoreNonMdiControlBarsVisible", conn_cs_txt)
        self.assertIn("ClearDatumPlaneSelectionsIfIdle", conn_cs_txt)
        self.assertIn("RunOnInteractiveDesktop", conn_cs_txt)
        self.assertIn("EnumDesktopWindows", conn_cs_txt)
        self.assertIn('"swMdiClient"', conn_cs_txt)
        self.assertIn("_maskedControlBarHwnds.Add(hWndMainFrame)", conn_cs_txt)
        self.assertIn("EditHoleWizardLocations", helper_cs)
        hw_m = re.search(r"public static int EditHoleWizardLocations\(.*?return status;", helper_cs, re.S)
        self.assertIsNotNone(hw_m)
        self.assertNotIn("Thread.Sleep(", hw_m.group(0))
        self.assertIn("CreateCornerRectangleSafe", helper_cs)
        self.assertIn("CreateCenterRectangleSafe", helper_cs)
        self.assertIn("DeleteSelectionSafe", helper_cs)
        self.assertIn("BatchDeleteFeatures", helper_cs)
        self.assertIn("FlushPendingPostDeleteRebuildIfNeeded", helper_cs)
        dyn_cs_txt = (tools_dir / "DynamicCSharpTool.cs").read_text(encoding="utf-8")
        self.assertIn(".DeleteSelectionSafe(", dyn_cs_txt)
        self.assertIn("model is IAssemblyDoc", dyn_cs_txt)

        for fname in (
            REPO_ROOT / "src" / "SolidWorksMcpServer" / "Core" / "SwConnectionManager.cs",
            tools_dir / "SketchAndEquationTools.cs",
            tools_dir / "FeatureAndFixtureTools.cs",
            tools_dir / "SheetMetalSendCutSendTools.cs",
        ):
            ftxt = fname.read_text(encoding="utf-8")
            self.assertNotIn(
                "mathUtil.CreatePoint",
                ftxt,
                f"Forbidden mathUtil.CreatePoint found in {fname}; use pure-C# ArrayData transforms",
            )
            self.assertNotIn(
                ".MultiplyTransform(",
                ftxt,
                f"Forbidden .MultiplyTransform( found in {fname}; use AssemblyGeometryHelper.TransformPointFast",
            )
        self.assertIn("TransformPointFast", (REPO_ROOT / "src" / "SolidWorksMcpServer" / "Core" / "SwConnectionManager.cs").read_text(encoding="utf-8"))
        self.assertIn("ProjectModelToSketch2D", helper_cs)

        ui_m = re.search(r"internal UiUpdateScope\(IModelDoc2 doc\).*?public void Dispose\(\)", helper_cs, re.S)
        self.assertIsNotNone(ui_m, "UiUpdateScope constructor must exist in SwHelper.cs")
        self.assertIn("MaskNonMdiControlBarsVisible", ui_m.group(0))
        self.assertIn("if (doc is IAssemblyDoc)", ui_m.group(0))

        mop_m = re.search(r"public static IFeature MakeOffsetPlane\(.*?altConstraint", helper_cs, re.S)
        self.assertIsNotNone(mop_m, "MakeOffsetPlane must exist in SwHelper.cs")
        self.assertIn("doc.EditRebuild3()", mop_m.group(0))

        sketch_eq_cs = (tools_dir / "SketchAndEquationTools.cs").read_text(encoding="utf-8")
        crg_m = re.search(
            r"internal static Dictionary<string, object> CreateRefGeometryInternal\(.*?InsertRefPlane.*?\n        \}",
            sketch_eq_cs,
            re.S,
        )
        self.assertIsNotNone(crg_m, "CreateRefGeometryInternal must exist in SketchAndEquationTools.cs")
        self.assertIn("try { doc.ClearSelection2(true); }", crg_m.group(0))

        self.assertIn("MateByPrincipalPlanesCore", helper_cs)
        self.assertIn("VerifyAndFlipPrincipalPlaneMates", helper_cs)
        self.assertIn("GetOrReadLocalPlaneTriplet", helper_cs)

        ft_assy_cs = (tools_dir / "FeatureTreeAndAssemblyTools.cs").read_text(encoding="utf-8")
        self.assertIn("MateByPrincipalPlanesCore", ft_assy_cs)
        self.assertIn("VerifyAndFlipPrincipalPlaneMates", ft_assy_cs)
        self.assertIn("directTransformOnly", ft_assy_cs)

        launcher_cs = (REPO_ROOT / "src" / "SolidWorksMcpServer" / "Core" / "ThreeDExperienceEdgeLauncher.cs").read_text(encoding="utf-8")
        dm_m = re.search(
            r"public static void DismissActiveSolidWorksModalDialogs\(.*?\n        \}",
            launcher_cs,
            re.S,
        )
        self.assertIsNotNone(dm_m, "DismissActiveSolidWorksModalDialogs must exist in ThreeDExperienceEdgeLauncher.cs")
        self.assertIn("SwConnectionManager.RunOnInteractiveDesktop", dm_m.group(0))

        cad_skill_text = (PLUGIN_ROOT / "skills" / "solidworks-cad-modeling" / "SKILL.md").read_text(encoding="utf-8")
        rules_text = (PLUGIN_ROOT / "rules" / "AGENTS.md").read_text(encoding="utf-8")
        hygiene_skill_text = (PLUGIN_ROOT / "skills" / "solidworks-feature-tree-hygiene" / "SKILL.md").read_text(encoding="utf-8")
        for tok in (
            "ProjectModelToSketch2D",
            "DeleteSelectionSafe",
            "BatchDeleteFeatures",
            "start_offset_mode",
            "MakeOffsetPlane",
            "use_undo_transaction: false",
        ):
            self.assertIn(tok, cad_skill_text)
            self.assertIn(tok, rules_text)
        for tok in ("start_offset_mode", "MakeOffsetPlane"):
            self.assertIn(tok, hygiene_skill_text)


class TestSolidWorksMcpStdioServer(unittest.TestCase):
    @staticmethod
    def _run_rpc_sequence(requests: list[dict], extra_env: dict[str, str] | None = None) -> list[dict]:
        if not MCP_EXE.exists():
            raise unittest.SkipTest(f"MCP executable not built yet: {MCP_EXE}")

        env = os.environ.copy()
        if extra_env:
            env.update(extra_env)
        env.setdefault("SOLIDWORKS_MCP_OFFLINE_TEST", "1")

        temp_lock_ctx = None
        if "SOLIDWORKS_MCP_LOCK_DIR" not in env:
            temp_lock_ctx = tempfile.TemporaryDirectory()
            env["SOLIDWORKS_MCP_LOCK_DIR"] = temp_lock_ctx.name

        try:
            stdin_payload = "\n".join(json.dumps(r) for r in requests) + "\n"
            proc = subprocess.run(
                [str(MCP_EXE)],
                input=stdin_payload,
                text=True,
                capture_output=True,
                timeout=30,
                encoding="utf-8",
                env=env,
            )
            if proc.returncode != 0:
                raise AssertionError(f"Server exited with {proc.returncode}. stderr:\n{proc.stderr}")

            responses = []
            for line in proc.stdout.splitlines():
                line = line.strip()
                if line:
                    responses.append(json.loads(line))
            return responses
        finally:
            if temp_lock_ctx is not None:
                temp_lock_ctx.cleanup()

    def test_mcp_initialize_tools_list_and_offline_tools(self) -> None:
        reqs = [
            {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05"}},
            {"jsonrpc": "2.0", "method": "notifications/initialized"},
            {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}},
            {
                "jsonrpc": "2.0",
                "id": 99,
                "method": "tools/call",
                "params": {
                    "name": "sw_connect_status",
                    "arguments": {
                        "lock_action": "claim",
                        "agent_id": "test-agent-1",
                        "task": "Offline MCP test suite",
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 3,
                "method": "tools/call",
                "params": {
                    "name": "sw_get_shop_profile",
                    "arguments": {"material": "5052_aluminum", "thickness_in": 0.063},
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 4,
                "method": "tools/call",
                "params": {
                    "name": "sw_audit_dfm",
                    "arguments": {
                        "agent_id": "test-agent-1",
                        "process": "sheet_metal_sendcutsend",
                        "material": "5052_aluminum",
                        "thickness_in": 0.063,
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 5,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "compile_only": True,
                        "code_body": (
                            "List<string> items = new();\n"
                            "bool ParseInt(string s, out int v) => int.TryParse(s, out v);\n"
                            "if (ParseInt(\"42\", out int num)) items.Add($\"val={num}\");\n"
                            "(int count, string first) tup = (items.Count, items[0]);\n"
                            "Func<string> fn = () => nameof(SwHelper.Activate) + nameof(SwHelper.SuppressUiUpdates) + nameof(SwHelper.CreateSlot2D) + nameof(SwHelper.TopComponents) + nameof(SwHelper.MakeTransform) + nameof(SwHelper.MateEntities) + nameof(SwHelper.SaveSilent) + nameof(SwHelper.BatchDeleteComponents) + nameof(SwHelper.BatchDeleteMates) + nameof(SwHelper.BatchDeleteMatesForComponents) + nameof(SwHelper.GetVisibleSketchNames) + nameof(SwHelper.HideSketches) + nameof(SwHelper.MinimizeCefUiNoise) + nameof(SwHelper.GetConstrainedStatusLabel) + nameof(SwHelper.FullyDefineActiveSketch) + nameof(SwHelper.SelectSketchObject) + nameof(SwHelper.NormalizeSketchRelationName) + nameof(SwHelper.AddSketchDimension) + nameof(SwHelper.UpsertDimensionEquation) + nameof(SwHelper.EditHoleWizardLocations) + nameof(SwHelper.BeginEditPartInAssembly) + nameof(SwHelper.AddInContextRelation) + nameof(SwHelper.ConvertEntitiesInContext) + nameof(SwHelper.EndEditPartInAssembly) + nameof(SwHelper.BuildSketchHealthSummary);\n"
                            "using (SwHelper.SuppressUiUpdates(swModel)) { var vis = SwHelper.GetVisibleSketchNames(swModel); _ = SwHelper.HideSketches(swModel, null, vis); _ = SwHelper.BatchDeleteMates(swModel, new string[0]); }\n"
                            "return swApp != null ? swApp.RevisionNumber() : tup.first + \":\" + fn();"
                        ),
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 6,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "compile_only": True,
                        "code_body": "swApp.ActivateDoc3(\"x\", false, (int)swRebuildOnActivation_e.swUserDecision, ref err);",
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 7,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "compile_only": True,
                        "code_body": "swModel.EditDelete();",
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 8,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "agent_id": "test-agent-1",
                        "allow_disconnected": True,
                        "code_body": (
                            "log(\"step_alpha\");\n"
                            "log(\"step_beta\");\n"
                            "throw new InvalidOperationException(\"intentional_boom\");"
                        ),
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 9,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "compile_only": True,
                        "code_body": "swApp.CommandInProgress = true;",
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 10,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "compile_only": True,
                        "code_body": "var x = feat.DisplayDimension2[0];",
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 11,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "compile_only": True,
                        "code_body": "object a, b, c; swModel.Extension.GetWhatsWrong(out a, out b, out c);",
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 12,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "compile_only": True,
                        "code_body": f"return \"cache_probe_{uuid.uuid4().hex}\";",
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 14,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "compile_only": True,
                        "code_body": "sm.CreateSketchSlot(0, 0, 0.0045, 0, 0, 0, 0.00109, 0, 0, 0, 0, 0, 1, false);",
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 15,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "compile_only": True,
                        "code_body": "swAssy.ToolsCheckInterference2(comps, false, out p1, out p2, out p3);",
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 16,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "compile_only": True,
                        "code_body": "int e = 0, w = 0; swModel.Save3(1, ref e, ref w);",
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 17,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "compile_only": True,
                        "code_body": "swModel.Extension.DeleteSelection2(0); var r = mateEnt.Reference;",
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 18,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "agent_id": "test-agent-1",
                        "allow_disconnected": True,
                        "code_body": (
                            "// Outward-wound 20mm x 20mm x 0.95mm rectangular slab (bottom z=0 normal -Z, top z=0.00095 normal +Z)\n"
                            "float x0 = 0f, x1 = 0.020f, y0 = 0f, y1 = 0.020f, z0 = 0f, z1 = 0.00095f;\n"
                            "float[] tris = new float[] {\n"
                            "    // Top (+Z): (x0,y0,z1)->(x1,y0,z1)->(x1,y1,z1), (x0,y0,z1)->(x1,y1,z1)->(x0,y1,z1)\n"
                            "    x0,y0,z1, x1,y0,z1, x1,y1,z1,  x0,y0,z1, x1,y1,z1, x0,y1,z1,\n"
                            "    // Bottom (-Z): (x0,y0,z0)->(x1,y1,z0)->(x1,y0,z0), (x0,y0,z0)->(x0,y1,z0)->(x1,y1,z0)\n"
                            "    x0,y0,z0, x1,y1,z0, x1,y0,z0,  x0,y0,z0, x0,y1,z0, x1,y1,z0\n"
                            "};\n"
                            "var pts = new List<double[]> { new[] { 0.010, 0.010, 0.00095 } };\n"
                            "var norms = new List<double[]> { new[] { 0.0, 0.0, 1.0 } };\n"
                            "DfmAuditAndFabricationTools.MeasureThinWebsFromTriangles(tris, pts, norms, 0.050, out double[] mins, out double[][] locs);\n"
                            "return new { measured_mm = Math.Round(mins[0], 4), mid_z_mm = locs[0][2] };"
                        ),
                    },
                },
            },
        ]
        # Duplicate id 12 code_body under id 13 to test in-process Roslyn SHA-256 cache hit
        reqs.append(
            {
                "jsonrpc": "2.0",
                "id": 13,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "compile_only": True,
                        "code_body": reqs[-1]["params"]["arguments"]["code_body"],
                    },
                },
            }
        )
        reqs.extend([
            {
                "jsonrpc": "2.0",
                "id": 19,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "compile_only": True,
                        "code_body": (
                            "bool flipDir = true;\n"
                            "swModel.Extension.DeleteSelection2(0);\n"
                            "var sm = swModel.SketchManager;\n"
                            "sm.InsertSketch(true);\n"
                            "sm.CreateCornerRectangle(0.06, -0.04, 0, 0.07, 0.04, 0);\n"
                            "sm.CreateCenterRectangle(0, 0, 0, 0.02, 0.02, 0);\n"
                            "swModel.SketchManager.InsertSketch(false);\n"
                            "var f1 = swModel.FeatureManager.FeatureExtrusion3(true, false, false, 0, 0, 0.01, 0, false, false, false, false, 0, 0, false, false, false, false, true, true, true, 0, 0, false);\n"
                            "var f2 = swModel.FeatureManager.FeatureExtrusion3(true, false, flipDir, 0, 0, 0.01, 0, false, false, false, false, 0, 0, false, false, false, false, true, true, true, 0, 0, false);\n"
                            "return (f1, f2);"
                        ),
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 20,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "compile_only": True,
                        "code_body": (
                            "bool flipDir = true;\n"
                            "var f = swModel.FeatureManager.FeatureExtrusion3(true, flipDir, flipDir, 0, 0, 0.01, 0, false, false, false, false, 0, 0, false, false, false, false, true, true, true, 0, 0, false);\n"
                            "return f;"
                        ),
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 21,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "compile_only": True,
                        "code_body": (
                            "var mathUtil = (IMathUtility)swApp.GetMathUtility();\n"
                            "var pt = (IMathPoint)mathUtil.CreatePoint(new double[] { 0.0, 0.05, 0.02 });\n"
                            "var sp = (IMathPoint)pt.MultiplyTransform(xform);\n"
                            "return sp.ArrayData;"
                        ),
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 22,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "agent_id": "test-agent-1",
                        "allow_disconnected": True,
                        "code_body": (
                            "// Right Plane ModelToSketchTransform ArrayData: u = -z, v = +y, w = +x\n"
                            "double[] rightPlaneM2s = new double[] { 0, 0, 1, 0, 1, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0 };\n"
                            "var (u, v) = SwHelper.ProjectModelMmToSketch2D(rightPlaneM2s, 0.0, 51.13, 22.0);\n"
                            "var p2 = SwHelper.ProjectModelToSketch2D(rightPlaneM2s, 0.0, 0.05113, 0.022);\n"
                            "return new { u = Math.Round(u, 6), v = Math.Round(v, 6), p2u = Math.Round(p2.u, 6), p2v = Math.Round(p2.v, 6) };"
                        ),
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 23,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "compile_only": True,
                        "code_body": "var r = swModel.SketchManager.CreatePolygon(0, 0, 0, 0.01, 0, 0, 6, true); return r;",
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 24,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "agent_id": "test-agent-1",
                        "allow_disconnected": True,
                        "code_body": (
                            "SwConnectionManager.MaskNonMdiControlBarsVisible(swApp);\n"
                            "SwConnectionManager.ClearDatumPlaneSelectionsIfIdle(swApp);\n"
                            "SwConnectionManager.RestoreNonMdiControlBarsVisible(swApp, true);\n"
                            "return \"ui_mask_ok\";"
                        ),
                    },
                },
            },
        ])

        resps = self._run_rpc_sequence(reqs)
        by_id = {r["id"]: r for r in resps if "id" in r}

        self.assertEqual(by_id[1]["result"]["serverInfo"]["name"], "solidworks-mcp")
        self.assertTrue(by_id[1]["result"].get("instructions"), "initialize must return instructions for Antigravity")

        tools = by_id[2]["result"]["tools"]
        self.assertEqual(len(tools), 52, f"Expected 52 MCP tools, found {len(tools)}")
        tool_names = {t["name"] for t in tools}
        for t in tools:
            self.assertLessEqual(
                len(t["name"]),
                33,
                f"Tool name '{t['name']}' exceeds 33 chars (64-char Antigravity prefix limit risk)",
            )
            self.assertIn("properties", t["inputSchema"])
            self.assertIn("agent_id", t["inputSchema"]["properties"])

        for required_tool in (
            "sw_connect_status",
            "sw_create_document",
            "sw_inspect_topology_datums",
            "sw_inspect_assembly_joints",
            "sw_measure_clearance",
            "sw_capture_viewport_quad",
            "sw_insert_sketch_picture",
            "sw_manage_equations",
            "sw_create_2d_sketch",
            "sw_extrude_boss_or_cut",
            "sw_build_part_features",
            "sw_create_airflow_loft_or_sweep",
            "sw_create_gridfinity",
            "sw_search_and_download_mcmaster",
            "sw_populate_mcmaster_hardware",
            "sw_install_fastener_stack",
            "sw_create_fabrication_drawing",
            "sw_create_scs_sheet_metal_base",
            "sw_organize_feature_tree",
            "sw_split_multibody_to_assembly",
            "sw_manage_assembly_components",
            "sw_audit_assembly_mates",
            "sw_audit_dfm",
            "sw_query_assembly_transforms",
            "sw_route_cable_or_loom",
            "sw_verify_motion_stroke",
            "sw_execute_csharp",
        ):
            self.assertIn(required_tool, tool_names)

        joints_tool = next(t for t in tools if t["name"] == "sw_inspect_assembly_joints")
        for prop in (
            "max_hole_diameter_mm",
            "nut_style",
            "material",
            "top_level_only",
            "include_subassembly_boundary_joints",
            "include_components",
        ):
            self.assertIn(prop, joints_tool["inputSchema"]["properties"])
        self.assertEqual(
            set(joints_tool["inputSchema"]["properties"]["nut_style"]["enum"]),
            {"nylock", "hex_nut", "jam_nut_stack"},
        )
        self.assertEqual(
            joints_tool["inputSchema"]["properties"]["material"]["default"],
            "18-8 Stainless Steel",
        )

        search_tool = next(t for t in tools if t["name"] == "sw_search_and_download_mcmaster")
        for prop in ("prepare_fastener_axis", "material", "nut_style", "mcmaster_pns", "specs", "expected_length_mm"):
            self.assertIn(prop, search_tool["inputSchema"]["properties"])

        pop_tool = next(t for t in tools if t["name"] == "sw_populate_mcmaster_hardware")
        for prop in ("allow_placeholder_generation", "create_mates", "placements", "items"):
            self.assertIn(prop, pop_tool["inputSchema"]["properties"])
        self.assertFalse(pop_tool["inputSchema"]["properties"]["allow_placeholder_generation"]["default"])

        stack_tool = next(t for t in tools if t["name"] == "sw_install_fastener_stack")
        for prop in ("stacks", "download_if_missing", "folder_name", "create_mates"):
            self.assertIn(prop, stack_tool["inputSchema"]["properties"])

        clr_tool = next(t for t in tools if t["name"] == "sw_measure_clearance")
        for prop in (
            "include_components",
            "ignore_pairs",
            "exclude_expected_thread_engagements",
            "exclude_same_subassembly_pairs",
            "group_by_pair",
        ):
            self.assertIn(prop, clr_tool["inputSchema"]["properties"])
        self.assertTrue(clr_tool["inputSchema"]["properties"]["group_by_pair"]["default"])
        self.assertTrue(clr_tool["inputSchema"]["properties"]["exclude_same_subassembly_pairs"]["default"])

        build_part_tool = next(t for t in tools if t["name"] == "sw_build_part_features")
        for prop in ("reset_first", "units", "equations", "features", "auto_fully_define", "keep_sketches_visible"):
            self.assertIn(prop, build_part_tool["inputSchema"]["properties"])
        self.assertTrue(build_part_tool["inputSchema"]["properties"]["auto_fully_define"]["default"])

        sketch_2d_tool = next(t for t in tools if t["name"] == "sw_create_2d_sketch")
        for prop in ("sketches", "relations", "dimensions", "auto_fully_define"):
            self.assertIn(prop, sketch_2d_tool["inputSchema"]["properties"])
        self.assertTrue(sketch_2d_tool["inputSchema"]["properties"]["auto_fully_define"]["default"])

        hole_preset_tool = next(t for t in tools if t["name"] == "sw_hole_preset")
        hole_props = hole_preset_tool["inputSchema"]["properties"]
        for prop in (
            "preset_type",
            "size",
            "fit",
            "hardware_type",
            "through_all",
            "thread_depth",
            "cbore_diameter",
            "cbore_depth",
            "head_clearance",
            "wrench_clearance",
            "near_csink",
            "slot_length",
            "in_context_concentric_targets",
            "relations",
            "dimensions",
            "auto_fully_define",
            "operations",
        ):
            self.assertIn(prop, hole_props)
        self.assertTrue(hole_props["auto_fully_define"]["default"])
        for req_preset in ("clearance", "counterbore", "countersink", "tapped", "3dp_self_thread", "cbore_slot", "csink_slot", "clearance_slot"):
            self.assertIn(req_preset, hole_props["preset_type"]["enum"])
        for req_fit in ("close", "tight", "normal", "loose"):
            self.assertIn(req_fit, hole_props["fit"]["enum"])
        for req_hw in ("shcs", "bhcs", "hex_bolt", "hex_cap", "pan", "flat_head_90", "flat_head_82", "flat_head_100"):
            self.assertIn(req_hw, hole_props["hardware_type"]["enum"])

        org_tree_tool = next(t for t in tools if t["name"] == "sw_organize_feature_tree")
        self.assertIn("fix_underdefined_sketches", org_tree_tool["inputSchema"]["properties"])
        self.assertTrue(org_tree_tool["inputSchema"]["properties"]["fix_underdefined_sketches"]["default"])

        ext_tool = next(t for t in tools if t["name"] == "sw_extrude_boss_or_cut")
        for prop in ("start_offset", "start_offset_mode", "sketch", "operations", "hide_sketch", "through_all_both", "depth2"):
            self.assertIn(prop, ext_tool["inputSchema"]["properties"])

        route_tool = next(t for t in tools if t["name"] == "sw_route_cable_or_loom")
        self.assertIn("routes", route_tool["inputSchema"]["properties"])

        save_tool = next(t for t in tools if t["name"] == "sw_save_document")
        for prop in ("target_docs", "check_mate_health"):
            self.assertIn(prop, save_tool["inputSchema"]["properties"])

        sweep_tool = next(t for t in tools if t["name"] == "sw_create_airflow_loft_or_sweep")
        self.assertIn("circular_diameter", sweep_tool["inputSchema"]["properties"])

        dfm_tool = next(t for t in tools if t["name"] == "sw_audit_dfm")
        self.assertIn("build_direction", dfm_tool["inputSchema"]["properties"])
        self.assertIn("min_web_thickness_mm", dfm_tool["inputSchema"]["properties"])

        manage_tool = next(t for t in tools if t["name"] == "sw_manage_assembly_components")
        for prop in ("pose_and_mate_items", "top_level_only", "check_mate_health"):
            self.assertIn(prop, manage_tool["inputSchema"]["properties"])
        manage_actions = set(manage_tool["inputSchema"]["properties"]["action"]["enum"])
        for req_action in ("move_to_folder", "delete_mates", "batch", "pose_and_mate_batch", "swap_fastener_and_insert_spacer"):
            self.assertIn(req_action, manage_actions)

        mate_audit_tool = next(t for t in tools if t["name"] == "sw_audit_assembly_mates")
        for prop in ("include_subassemblies", "include_feature_errors", "include_suppressed", "rebuild_first"):
            self.assertIn(prop, mate_audit_tool["inputSchema"]["properties"])
        self.assertTrue(mate_audit_tool["inputSchema"]["properties"]["include_subassemblies"]["default"])
        self.assertTrue(mate_audit_tool["inputSchema"]["properties"]["include_feature_errors"]["default"])
        self.assertFalse(mate_audit_tool["inputSchema"]["properties"]["include_suppressed"]["default"])
        self.assertFalse(mate_audit_tool["inputSchema"]["properties"]["rebuild_first"]["default"])

        r3_payload = json.loads(by_id[3]["result"]["content"][0]["text"])
        self.assertEqual(r3_payload["gauge"]["effective_bend_radius_in"], 0.035)
        self.assertEqual(r3_payload["gauge"]["k_factor"], 0.42)

        r4_payload = json.loads(by_id[4]["result"]["content"][0]["text"])
        self.assertFalse(by_id[4]["result"]["isError"])
        self.assertGreaterEqual(r4_payload["findings_count"], 2)

        r5_payload = json.loads(by_id[5]["result"]["content"][0]["text"])
        self.assertFalse(by_id[5]["result"]["isError"], f"Roslyn compile failed: {r5_payload}")
        self.assertTrue(r5_payload["compiled"])
        self.assertEqual(r5_payload["compiler"], "roslyn_latest")

        # Static guard tests (swUserDecision and EditDelete)
        self.assertTrue(by_id[6]["result"]["isError"])
        r6_payload = json.loads(by_id[6]["result"]["content"][0]["text"])
        self.assertIn("swUserDecision", r6_payload["error"])

        self.assertTrue(by_id[7]["result"]["isError"])
        r7_payload = json.loads(by_id[7]["result"]["content"][0]["text"])
        self.assertIn("EditDelete", r7_payload["error"])

        # Runtime exception preserves logs and full inner C# script stack trace + PDB line numbers
        self.assertTrue(by_id[8]["result"]["isError"])
        r8_payload = json.loads(by_id[8]["result"]["content"][0]["text"])
        self.assertTrue(r8_payload["compiled"])
        self.assertIn("intentional_boom", r8_payload["error"])
        self.assertEqual(r8_payload["logs"], ["step_alpha", "step_beta"])
        self.assertIn("DynamicSwScript.Run", r8_payload.get("details", ""))
        self.assertIn("DynamicSwScript.cs:line", r8_payload.get("details", ""))

        self.assertTrue(by_id[9]["result"]["isError"])
        r9_payload = json.loads(by_id[9]["result"]["content"][0]["text"])
        self.assertIn("CommandInProgress", r9_payload["error"])

        self.assertTrue(by_id[10]["result"]["isError"])
        r10_payload = json.loads(by_id[10]["result"]["content"][0]["text"])
        self.assertIn("DisplayDimension2", r10_payload["error"])

        self.assertTrue(by_id[11]["result"]["isError"])
        r11_payload = json.loads(by_id[11]["result"]["content"][0]["text"])
        self.assertIn("GetWhatsWrong", r11_payload["error"])

        r12_payload = json.loads(by_id[12]["result"]["content"][0]["text"])
        self.assertFalse(by_id[12]["result"]["isError"])
        self.assertTrue(r12_payload["compiled"])
        self.assertFalse(r12_payload["cache_hit"])

        r13_payload = json.loads(by_id[13]["result"]["content"][0]["text"])
        self.assertFalse(by_id[13]["result"]["isError"])
        self.assertTrue(r13_payload["compiled"])
        self.assertTrue(r13_payload["cache_hit"])

        self.assertTrue(by_id[14]["result"]["isError"])
        self.assertIn("CreateSketchSlot", json.loads(by_id[14]["result"]["content"][0]["text"])["error"])

        self.assertTrue(by_id[15]["result"]["isError"])
        self.assertIn("ToolsCheckInterference", json.loads(by_id[15]["result"]["content"][0]["text"])["error"])

        self.assertTrue(by_id[16]["result"]["isError"])
        self.assertIn("Save3", json.loads(by_id[16]["result"]["content"][0]["text"])["error"])

        self.assertTrue(by_id[17]["result"]["isError"])
        self.assertIn("DeleteSelection2", json.loads(by_id[17]["result"]["content"][0]["text"])["error"])

        self.assertFalse(by_id[18]["result"]["isError"], f"Mesh raycast test failed: {by_id[18]}")
        r18_res = json.loads(by_id[18]["result"]["content"][0]["text"])["result"]
        self.assertAlmostEqual(r18_res["measured_mm"], 0.95, places=3)
        self.assertAlmostEqual(r18_res["mid_z_mm"], 0.48, places=2)

        self.assertFalse(by_id[19]["result"]["isError"], f"InsertSketchSafe / valid FeatureExtrusion3 compile failed: {by_id[19]}")
        r19_payload = json.loads(by_id[19]["result"]["content"][0]["text"])
        self.assertTrue(r19_payload["compiled"])

        self.assertTrue(by_id[20]["result"]["isError"])
        self.assertIn("Parameter 2", json.loads(by_id[20]["result"]["content"][0]["text"])["error"])

        self.assertTrue(by_id[21]["result"]["isError"])
        self.assertIn("ProjectModelToSketch2D", json.loads(by_id[21]["result"]["content"][0]["text"])["error"])

        self.assertFalse(by_id[22]["result"]["isError"], f"ProjectModelMmToSketch2D test failed: {by_id[22]}")
        r22_res = json.loads(by_id[22]["result"]["content"][0]["text"])["result"]
        self.assertAlmostEqual(r22_res["u"], -0.022, places=6)
        self.assertAlmostEqual(r22_res["v"], 0.05113, places=6)
        self.assertAlmostEqual(r22_res["p2u"], -0.022, places=6)
        self.assertAlmostEqual(r22_res["p2v"], 0.05113, places=6)

        self.assertTrue(by_id[23]["result"]["isError"])
        self.assertIn("CreatePolygon", json.loads(by_id[23]["result"]["content"][0]["text"])["error"])

        self.assertFalse(by_id[24]["result"]["isError"], f"UI mask offline execution failed: {by_id[24]}")
        self.assertEqual(json.loads(by_id[24]["result"]["content"][0]["text"])["result"], "ui_mask_ok")

        invoke_tool = next(t for t in tools if t["name"] == "sw_invoke_repo_addin_tool")
        for prop in ("face_tokens", "bed_plane_or_face", "build_direction", "layer_height_mm", "joint_type", "use_macro_feature"):
            self.assertIn(prop, invoke_tool["inputSchema"]["properties"])

        exec_tool = next(t for t in tools if t["name"] == "sw_execute_csharp")
        self.assertIn("use_undo_transaction", exec_tool["inputSchema"]["properties"])
        self.assertIn("allow_disconnected", exec_tool["inputSchema"]["properties"])

        conn_tool = next(t for t in tools if t["name"] == "sw_connect_status")
        conn_props = conn_tool["inputSchema"]["properties"]
        for prop in ("minimize_cef_ui", "lock_action", "agent_id", "task", "lease_ttl_seconds", "abort_running_worker"):
            self.assertIn(prop, conn_props)
        self.assertFalse(conn_props["minimize_cef_ui"]["default"])
        self.assertEqual(
            set(conn_props["lock_action"]["enum"]),
            {"status", "claim", "release", "heartbeat", "force_claim", "force_unlock"},
        )

        self.assertFalse(by_id[99]["result"]["isError"])
        r99_payload = json.loads(by_id[99]["result"]["content"][0]["text"])
        self.assertTrue(r99_payload["lock_acquired"])
        self.assertTrue(r99_payload["session_lock"]["is_locked"])
        self.assertEqual(r99_payload["session_lock"]["agent_id"], "test-agent-1")

    def test_supervised_worker_crash_isolation_and_recovery(self) -> None:
        reqs = [
            {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05"}},
            {"jsonrpc": "2.0", "method": "notifications/initialized"},
            {
                "jsonrpc": "2.0",
                "id": 100,
                "method": "tools/call",
                "params": {
                    "name": "sw_connect_status",
                    "arguments": {
                        "lock_action": "claim",
                        "agent_id": "crash-test-agent",
                        "task": "Crash isolation test",
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 2,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "agent_id": "crash-test-agent",
                        "allow_disconnected": True,
                        "code_body": "System.Environment.Exit(-1); return null;",
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 3,
                "method": "tools/call",
                "params": {
                    "name": "sw_get_shop_profile",
                    "arguments": {"section": "hierarchy"},
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 4,
                "method": "tools/call",
                "params": {
                    "name": "sw_connect_status",
                    "arguments": {"launch_if_closed": False},
                },
            },
        ]
        with tempfile.TemporaryDirectory() as td:
            resps = self._run_rpc_sequence(
                reqs,
                extra_env={"SOLIDWORKS_MCP_CRASH_JOURNAL_DIR": td, "SOLIDWORKS_MCP_LOCK_DIR": td},
            )
            by_id = {r["id"]: r for r in resps if "id" in r}

            self.assertFalse(by_id[100]["result"]["isError"])
            self.assertTrue(by_id[2]["result"]["isError"])
            crash_payload = json.loads(by_id[2]["result"]["content"][0]["text"])
            details = crash_payload.get("details", {})
            self.assertTrue(details.get("worker_recovered"))
            self.assertEqual(details.get("exit_code_hex"), "0xFFFFFFFF")

            self.assertFalse(by_id[3]["result"]["isError"])
            prof_payload = json.loads(by_id[3]["result"]["content"][0]["text"])
            self.assertIn("manufacturing_preference_hierarchy", prof_payload)

            status_payload = json.loads(by_id[4]["result"]["content"][0]["text"])
            self.assertIn("worker_crash_history", status_payload)
            history = status_payload["worker_crash_history"]
            self.assertIsInstance(history, dict)
            self.assertGreaterEqual(history["total_recorded_crashes"], 1)
            self.assertGreaterEqual(len(history["recent_crashes"]), 1)

            sl = status_payload.get("session_lock", {})
            self.assertIsNone(sl.get("in_flight_tool"))
            self.assertIn("Worker PID", sl.get("last_note", ""))

            journal = Path(td) / "crash_journal.jsonl"
            self.assertTrue(journal.is_file(), f"Expected crash journal at {journal}")

    def test_rpc_disconnect_exception_is_recorded_in_crash_journal(self) -> None:
        reqs = [
            {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05"}},
            {"jsonrpc": "2.0", "method": "notifications/initialized"},
            {
                "jsonrpc": "2.0",
                "id": 100,
                "method": "tools/call",
                "params": {
                    "name": "sw_connect_status",
                    "arguments": {
                        "lock_action": "claim",
                        "agent_id": "rpc-crash-agent",
                        "task": "RPC disconnect crash journal test",
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 2,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
                        "agent_id": "rpc-crash-agent",
                        "allow_disconnected": True,
                        "code_body": "throw new System.Runtime.InteropServices.COMException(\"The RPC server is unavailable.\", unchecked((int)0x800706BA));",
                    },
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 3,
                "method": "tools/call",
                "params": {
                    "name": "sw_connect_status",
                    "arguments": {"launch_if_closed": False},
                },
            },
        ]
        with tempfile.TemporaryDirectory() as td:
            resps = self._run_rpc_sequence(
                reqs,
                extra_env={"SOLIDWORKS_MCP_CRASH_JOURNAL_DIR": td, "SOLIDWORKS_MCP_LOCK_DIR": td},
            )
            by_id = {r["id"]: r for r in resps if "id" in r}
            self.assertFalse(by_id[100]["result"]["isError"])
            self.assertTrue(by_id[2]["result"]["isError"])
            payload = json.loads(by_id[2]["result"]["content"][0]["text"])
            self.assertEqual(payload.get("hresult_hex"), "0x800706BA")
            journal = Path(td) / "crash_journal.jsonl"
            self.assertTrue(journal.is_file(), f"Expected RPC crash journal at {journal}")
            lines = [json.loads(l) for l in journal.read_text(encoding="utf-8").splitlines() if l.strip()]
            self.assertEqual(lines[-1]["exit_code_hex"], "0x800706BA")
            self.assertEqual(lines[-1]["tool_name"], "sw_execute_csharp")
            status_payload = json.loads(by_id[3]["result"]["content"][0]["text"])
            self.assertGreaterEqual(status_payload["worker_crash_history"]["total_recorded_crashes"], 1)

    def test_multi_agent_session_lock_claim_block_expiry_and_force_override(self) -> None:
        if not MCP_EXE.exists():
            raise unittest.SkipTest(f"MCP executable not built yet: {MCP_EXE}")

        reqs = [
            {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05"}},
            {"jsonrpc": "2.0", "method": "notifications/initialized"},
            # A: Unclaimed tool call must fail with lock_required
            {
                "jsonrpc": "2.0",
                "id": 2,
                "method": "tools/call",
                "params": {"name": "sw_audit_dfm", "arguments": {"process": "sheet_metal_sendcutsend", "material": "5052_aluminum", "thickness_in": 0.063}},
            },
            # B: Agent A claims lock
            {
                "jsonrpc": "2.0",
                "id": 3,
                "method": "tools/call",
                "params": {"name": "sw_connect_status", "arguments": {"lock_action": "claim", "agent_id": "agent-alpha", "task": "Modeling Z carriage", "lease_ttl_seconds": 900}},
            },
            # C: Agent B claim rejected
            {
                "jsonrpc": "2.0",
                "id": 4,
                "method": "tools/call",
                "params": {"name": "sw_connect_status", "arguments": {"lock_action": "claim", "agent_id": "agent-beta", "task": "Competing task"}},
            },
            # D1: Agent B tool call rejected
            {
                "jsonrpc": "2.0",
                "id": 5,
                "method": "tools/call",
                "params": {"name": "sw_audit_dfm", "arguments": {"agent_id": "agent-beta", "process": "sheet_metal_sendcutsend", "material": "5052_aluminum", "thickness_in": 0.063}},
            },
            # D2: Unattributed tool call rejected
            {
                "jsonrpc": "2.0",
                "id": 6,
                "method": "tools/call",
                "params": {"name": "sw_audit_dfm", "arguments": {"process": "sheet_metal_sendcutsend", "material": "5052_aluminum", "thickness_in": 0.063}},
            },
            # E: Agent A tool call succeeds
            {
                "jsonrpc": "2.0",
                "id": 7,
                "method": "tools/call",
                "params": {"name": "sw_audit_dfm", "arguments": {"agent_id": "agent-alpha", "process": "sheet_metal_sendcutsend", "material": "5052_aluminum", "thickness_in": 0.063}},
            },
            # F: User-authorized force_claim by Agent B
            {
                "jsonrpc": "2.0",
                "id": 8,
                "method": "tools/call",
                "params": {"name": "sw_connect_status", "arguments": {"lock_action": "force_claim", "agent_id": "agent-beta", "task": "Urgent hotfix"}},
            },
            # G1: Agent A now blocked
            {
                "jsonrpc": "2.0",
                "id": 9,
                "method": "tools/call",
                "params": {"name": "sw_audit_dfm", "arguments": {"agent_id": "agent-alpha", "process": "sheet_metal_sendcutsend", "material": "5052_aluminum", "thickness_in": 0.063}},
            },
            # G2: Agent B succeeds
            {
                "jsonrpc": "2.0",
                "id": 10,
                "method": "tools/call",
                "params": {"name": "sw_audit_dfm", "arguments": {"agent_id": "agent-beta", "process": "sheet_metal_sendcutsend", "material": "5052_aluminum", "thickness_in": 0.063}},
            },
            # H: Agent B releases lock
            {
                "jsonrpc": "2.0",
                "id": 11,
                "method": "tools/call",
                "params": {"name": "sw_connect_status", "arguments": {"lock_action": "release", "agent_id": "agent-beta"}},
            },
        ]

        with tempfile.TemporaryDirectory() as td:
            resps = self._run_rpc_sequence(reqs, extra_env={"SOLIDWORKS_MCP_LOCK_DIR": td})
            by_id = {r["id"]: r for r in resps if "id" in r}

            self.assertTrue(by_id[2]["result"]["isError"])
            p2 = json.loads(by_id[2]["result"]["content"][0]["text"])
            self.assertTrue(p2["details"]["lock_required"])

            self.assertFalse(by_id[3]["result"]["isError"])
            p3 = json.loads(by_id[3]["result"]["content"][0]["text"])
            self.assertTrue(p3["lock_acquired"])
            self.assertEqual(p3["session_lock"]["agent_id"], "agent-alpha")

            self.assertTrue(by_id[4]["result"]["isError"])
            p4 = json.loads(by_id[4]["result"]["content"][0]["text"])
            self.assertTrue(p4["locked_by_other_agent"])
            self.assertIn("agent-alpha", p4["error"])
            self.assertIn("Modeling Z carriage", p4["error"])

            self.assertTrue(by_id[5]["result"]["isError"])
            p5 = json.loads(by_id[5]["result"]["content"][0]["text"])
            self.assertTrue(p5["details"]["locked_by_other_agent"])

            self.assertTrue(by_id[6]["result"]["isError"])
            p6 = json.loads(by_id[6]["result"]["content"][0]["text"])
            self.assertTrue(p6["details"]["locked_by_other_agent"])

            self.assertFalse(by_id[7]["result"]["isError"])

            self.assertFalse(by_id[8]["result"]["isError"])
            p8 = json.loads(by_id[8]["result"]["content"][0]["text"])
            self.assertTrue(p8["lock_acquired"])
            self.assertEqual(p8["session_lock"]["agent_id"], "agent-beta")

            self.assertTrue(by_id[9]["result"]["isError"])
            self.assertFalse(by_id[10]["result"]["isError"])

            self.assertFalse(by_id[11]["result"]["isError"])
            p11 = json.loads(by_id[11]["result"]["content"][0]["text"])
            self.assertTrue(p11["lock_released"])
            self.assertFalse(p11["session_lock"]["is_locked"])

            # I: Multi-process live TTL expiry + CLI --lock-status / --force-unlock
            env = os.environ.copy()
            env["SOLIDWORKS_MCP_LOCK_DIR"] = td
            env["SOLIDWORKS_MCP_OFFLINE_TEST"] = "1"
            proc_a = subprocess.Popen(
                [str(MCP_EXE)],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                env=env,
            )
            try:
                def rpc(proc, req):
                    proc.stdin.write(json.dumps(req) + "\n")
                    proc.stdin.flush()
                    if "id" in req:
                        return json.loads(proc.stdout.readline())
                    return None

                rpc(proc_a, {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05"}})
                claim_a = rpc(proc_a, {
                    "jsonrpc": "2.0",
                    "id": 2,
                    "method": "tools/call",
                    "params": {"name": "sw_connect_status", "arguments": {"lock_action": "claim", "agent_id": "proc-a-agent", "task": "Short lease", "lease_ttl_seconds": 1}},
                })
                self.assertFalse(claim_a["result"]["isError"])

                # While lease (<1s) is fresh and proc_a is alive, proc_b non-forced claim must fail
                b1 = self._run_rpc_sequence([
                    {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05"}},
                    {"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "sw_connect_status", "arguments": {"lock_action": "claim", "agent_id": "proc-b-agent", "task": "Wait test"}}},
                ], extra_env={"SOLIDWORKS_MCP_LOCK_DIR": td})
                self.assertTrue({r["id"]: r for r in b1}[2]["result"]["isError"])

                # Wait >1.0s TTL expiry while proc_a sits idle -> proc_b automatically reclaims
                time.sleep(1.3)
                proc_b = subprocess.Popen(
                    [str(MCP_EXE)],
                    stdin=subprocess.PIPE,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    text=True,
                    encoding="utf-8",
                    env=env,
                )
                try:
                    rpc(proc_b, {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05"}})
                    b2 = rpc(proc_b, {
                        "jsonrpc": "2.0",
                        "id": 2,
                        "method": "tools/call",
                        "params": {"name": "sw_connect_status", "arguments": {"lock_action": "claim", "agent_id": "proc-b-agent", "task": "After TTL expiry", "lease_ttl_seconds": 600}},
                    })
                    self.assertFalse(b2["result"]["isError"])
                    b2_payload = json.loads(b2["result"]["content"][0]["text"])
                    self.assertTrue(b2_payload["lock_acquired"])
                    self.assertEqual(b2_payload["session_lock"]["agent_id"], "proc-b-agent")

                    cli_status = subprocess.run([str(MCP_EXE), "--lock-status"], capture_output=True, text=True, encoding="utf-8", env=env, timeout=15)
                    self.assertEqual(cli_status.returncode, 0)
                    self.assertEqual(json.loads(cli_status.stdout)["agent_id"], "proc-b-agent")

                    cli_unlock = subprocess.run([str(MCP_EXE), "--force-unlock"], capture_output=True, text=True, encoding="utf-8", env=env, timeout=15)
                    self.assertEqual(cli_unlock.returncode, 0)
                    unlock_json = json.loads(cli_unlock.stdout)
                    self.assertTrue(unlock_json["lock_released"])
                    self.assertTrue(unlock_json["forced"])
                    self.assertFalse(unlock_json["session_lock"]["is_locked"])
                finally:
                    proc_b.terminate()
                    proc_b.wait(timeout=5)
            finally:
                proc_a.terminate()
                proc_a.wait(timeout=5)


if __name__ == "__main__":
    unittest.main()
