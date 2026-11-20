"""Tests for the unified SOLIDWORKS plugin catalogs, skills, and MCP stdio server."""

from __future__ import annotations

import json
import os
import re
import subprocess
import tempfile
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
        ):
            self.assertIn(expected_tok, cad_skill_text)

        print_skill_text = (PLUGIN_ROOT / "skills" / "dfm-3d-printing" / "SKILL.md").read_text(encoding="utf-8")
        for expected_3dp in ("42°", "D-loop", "Glass Transition", "Cold Creep", "Abrasion Resistance", "Slipperiness"):
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


class TestSolidWorksMcpStdioServer(unittest.TestCase):
    @staticmethod
    def _run_rpc_sequence(requests: list[dict], extra_env: dict[str, str] | None = None) -> list[dict]:
        if not MCP_EXE.exists():
            raise unittest.SkipTest(f"MCP executable not built yet: {MCP_EXE}")

        env = os.environ.copy()
        if extra_env:
            env.update(extra_env)

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

    def test_mcp_initialize_tools_list_and_offline_tools(self) -> None:
        reqs = [
            {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05"}},
            {"jsonrpc": "2.0", "method": "notifications/initialized"},
            {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}},
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
                    "arguments": {"process": "sheet_metal_sendcutsend", "material": "5052_aluminum", "thickness_in": 0.063},
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
                            "Func<string> fn = () => nameof(SwHelper.Activate) + nameof(SwHelper.SuppressUiUpdates) + nameof(SwHelper.TopComponents) + nameof(SwHelper.MakeTransform) + nameof(SwHelper.MateEntities) + nameof(SwHelper.SaveSilent) + nameof(SwHelper.BatchDeleteComponents) + nameof(SwHelper.BatchDeleteMates) + nameof(SwHelper.BatchDeleteMatesForComponents) + nameof(SwHelper.GetVisibleSketchNames) + nameof(SwHelper.HideSketches) + nameof(SwHelper.MinimizeCefUiNoise);\n"
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
        for prop in ("reset_first", "units", "equations", "features", "keep_sketches_visible"):
            self.assertIn(prop, build_part_tool["inputSchema"]["properties"])

        sketch_2d_tool = next(t for t in tools if t["name"] == "sw_create_2d_sketch")
        self.assertIn("sketches", sketch_2d_tool["inputSchema"]["properties"])

        ext_tool = next(t for t in tools if t["name"] == "sw_extrude_boss_or_cut")
        for prop in ("start_offset", "start_offset_mode", "sketch", "operations", "hide_sketch"):
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

        # Runtime exception preserves logs
        self.assertTrue(by_id[8]["result"]["isError"])
        r8_payload = json.loads(by_id[8]["result"]["content"][0]["text"])
        self.assertTrue(r8_payload["compiled"])
        self.assertIn("intentional_boom", r8_payload["error"])
        self.assertEqual(r8_payload["logs"], ["step_alpha", "step_beta"])

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

        exec_tool = next(t for t in tools if t["name"] == "sw_execute_csharp")
        self.assertIn("use_undo_transaction", exec_tool["inputSchema"]["properties"])
        self.assertIn("allow_disconnected", exec_tool["inputSchema"]["properties"])

        conn_tool = next(t for t in tools if t["name"] == "sw_connect_status")
        self.assertIn("minimize_cef_ui", conn_tool["inputSchema"]["properties"])
        self.assertFalse(conn_tool["inputSchema"]["properties"]["minimize_cef_ui"]["default"])

    def test_supervised_worker_crash_isolation_and_recovery(self) -> None:
        reqs = [
            {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05"}},
            {"jsonrpc": "2.0", "method": "notifications/initialized"},
            {
                "jsonrpc": "2.0",
                "id": 2,
                "method": "tools/call",
                "params": {
                    "name": "sw_execute_csharp",
                    "arguments": {
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
            resps = self._run_rpc_sequence(reqs, extra_env={"SOLIDWORKS_MCP_CRASH_JOURNAL_DIR": td})
            by_id = {r["id"]: r for r in resps if "id" in r}

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

            journal = Path(td) / "crash_journal.jsonl"
            self.assertTrue(journal.is_file(), f"Expected crash journal at {journal}")


if __name__ == "__main__":
    unittest.main()
