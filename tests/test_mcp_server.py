"""Tests for the unified SOLIDWORKS plugin catalogs, skills, and MCP stdio server."""

from __future__ import annotations

import json
import re
import subprocess
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
PLUGIN_ROOT = REPO_ROOT / ".agents" / "plugins" / "solidworks"
MCP_EXE = REPO_ROOT / "src" / "SolidWorksMcpServer" / "bin" / "x64" / "Release" / "SolidWorksMcpServer.exe"


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
    def _run_rpc_sequence(requests: list[dict]) -> list[dict]:
        if not MCP_EXE.exists():
            raise unittest.SkipTest(f"MCP executable not built yet: {MCP_EXE}")

        stdin_payload = "\n".join(json.dumps(r) for r in requests) + "\n"
        proc = subprocess.run(
            [str(MCP_EXE)],
            input=stdin_payload,
            text=True,
            capture_output=True,
            timeout=30,
            encoding="utf-8",
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
                        "code_body": "return swApp != null ? swApp.RevisionNumber() : \"offline\";",
                    },
                },
            },
        ]

        resps = self._run_rpc_sequence(reqs)
        by_id = {r["id"]: r for r in resps if "id" in r}

        self.assertEqual(by_id[1]["result"]["serverInfo"]["name"], "solidworks-mcp")
        self.assertTrue(by_id[1]["result"].get("instructions"), "initialize must return instructions for Antigravity")

        tools = by_id[2]["result"]["tools"]
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
            "sw_audit_dfm",
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
        for prop in ("allow_placeholder_generation", "create_mates", "placements"):
            self.assertIn(prop, pop_tool["inputSchema"]["properties"])
        self.assertFalse(pop_tool["inputSchema"]["properties"]["allow_placeholder_generation"]["default"])

        stack_tool = next(t for t in tools if t["name"] == "sw_install_fastener_stack")
        for prop in ("stacks", "download_if_missing", "folder_name", "create_mates"):
            self.assertIn(prop, stack_tool["inputSchema"]["properties"])

        clr_tool = next(t for t in tools if t["name"] == "sw_measure_clearance")
        self.assertIn("exclude_expected_thread_engagements", clr_tool["inputSchema"]["properties"])
        self.assertIn("group_by_pair", clr_tool["inputSchema"]["properties"])
        self.assertTrue(clr_tool["inputSchema"]["properties"]["group_by_pair"]["default"])

        manage_tool = next(t for t in tools if t["name"] == "sw_manage_assembly_components")
        self.assertIn("action", manage_tool["inputSchema"]["properties"])
        self.assertIn("move_to_folder", manage_tool["inputSchema"]["properties"]["action"]["enum"])

        r3_payload = json.loads(by_id[3]["result"]["content"][0]["text"])
        self.assertEqual(r3_payload["gauge"]["effective_bend_radius_in"], 0.035)
        self.assertEqual(r3_payload["gauge"]["k_factor"], 0.42)

        r4_payload = json.loads(by_id[4]["result"]["content"][0]["text"])
        self.assertFalse(by_id[4]["result"]["isError"])
        self.assertGreaterEqual(r4_payload["findings_count"], 2)

        r5_payload = json.loads(by_id[5]["result"]["content"][0]["text"])
        self.assertFalse(by_id[5]["result"]["isError"])
        self.assertTrue(r5_payload["compiled"])

        exec_tool = next(t for t in tools if t["name"] == "sw_execute_csharp")
        self.assertIn("use_undo_transaction", exec_tool["inputSchema"]["properties"])


if __name__ == "__main__":
    unittest.main()
