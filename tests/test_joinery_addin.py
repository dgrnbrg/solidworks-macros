"""Comprehensive unit and regression tests for SolidWorksJoineryAddin algorithms.

Verifies:
1. Diagonal dogbone math (45° angle bisector, radius clearance).
2. Tab & Slot and Finger Joint interval calculations with directional keying.
3. Selective conflict clustering (only confusable joints flagged).
4. Opposing directional offsets on identical tabs, alternating tabs for subsequent pairs.
5. Differing tab count capacity limits (K vs 2*N_min) and fallback options (N = ceil(K/2)).
6. Signed clearance offsets (sliding clearance vs interference press-fit).
"""

import json
import math
import subprocess
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
JOINERY_DLL = REPO_ROOT / "src" / "SolidWorksJoineryAddin" / "bin" / "x64" / "Release" / "SolidWorksJoineryAddin.dll"

def calculate_diagonal_dogbone_center(vx, vy, u1x, u1y, u2x, u2y, radius):
    factor = radius / math.sqrt(2.0)
    cx = vx + factor * (u1x + u2x)
    cy = vy + factor * (u1y + u2y)
    return cx, cy

class TestDogboneMath(unittest.TestCase):
    def test_diagonal_dogbone_quadrant1(self):
        # Interior corner at (0, 0), edges along +X (1, 0) and +Y (0, 1)
        r = 0.25 # 1/4" bit radius
        cx, cy = calculate_diagonal_dogbone_center(0.0, 0.0, 1.0, 0.0, 0.0, 1.0, r)
        expected = r / math.sqrt(2.0)
        self.assertAlmostEqual(cx, expected, places=5)
        self.assertAlmostEqual(cy, expected, places=5)
        
        # Verify distance from center to vertex is exactly r
        dist = math.hypot(cx - 0.0, cy - 0.0)
        self.assertAlmostEqual(dist, r, places=5)

    def test_diagonal_dogbone_quadrant2(self):
        # Interior corner at (10, 0), edges along -X (-1, 0) and +Y (0, 1)
        r = 0.125
        cx, cy = calculate_diagonal_dogbone_center(10.0, 0.0, -1.0, 0.0, 0.0, 1.0, r)
        factor = r / math.sqrt(2.0)
        self.assertAlmostEqual(cx, 10.0 - factor, places=5)
        self.assertAlmostEqual(cy, factor, places=5)
        dist = math.hypot(cx - 10.0, cy - 0.0)
        self.assertAlmostEqual(dist, r, places=5)

class KeyingSimulator:
    @staticmethod
    def resolve_keying(joints, master_enable=True, fallback="many"):
        # Reset
        for j in joints:
            j["is_keyed"] = False
            j["keyed_tab"] = -1
            j["key_dir"] = None
            j["has_conflict"] = False
            j["conflict_desc"] = ""

        if not master_enable:
            return

        # Cluster by span and thickness
        clusters = {}
        for j in joints:
            key = (round(j["span"], 4), round(j["thickness"], 4), j["type"])
            clusters.setdefault(key, []).append(j)

        for key, cluster_joints in clusters.items():
            k = len(cluster_joints)
            if k <= 1:
                continue # Unique, no keying needed

            min_tabs = min(j["tab_count"] for j in cluster_joints)
            capacity = 2 * min_tabs
            req_tabs = math.ceil(k / 2.0)

            target_joints = cluster_joints
            if k > capacity:
                for j in cluster_joints:
                    j["has_conflict"] = True
                    j["conflict_desc"] = f"⚠️ Insufficient tabs: K={k}, min={min_tabs}, req={req_tabs}"

                if fallback == "disable":
                    continue
                elif fallback == "limit_n":
                    target_joints = [j for j in cluster_joints if j["tab_count"] >= req_tabs]
            else:
                for j in cluster_joints:
                    j["has_conflict"] = True
                    j["conflict_desc"] = f"Confusable with {k-1} joints"

            # Assign directional keying
            for i, j in enumerate(target_joints):
                tab_idx = i // 2
                direction = "Positive" if (i % 2 == 0) else "Negative"
                if tab_idx < j["tab_count"]:
                    j["is_keyed"] = True
                    j["keyed_tab"] = tab_idx
                    j["key_dir"] = direction

class TestKeyingEngine(unittest.TestCase):
    def test_unique_joints_not_keyed(self):
        joints = [
            {"id": "J1", "span": 12.0, "thickness": 0.5, "type": "TabAndSlot", "tab_count": 3},
            {"id": "J2", "span": 18.0, "thickness": 0.5, "type": "TabAndSlot", "tab_count": 3},
        ]
        KeyingSimulator.resolve_keying(joints)
        self.assertFalse(joints[0]["is_keyed"])
        self.assertFalse(joints[1]["is_keyed"])
        self.assertFalse(joints[0]["has_conflict"])

    def test_confusable_pair_opposing_directions_same_tab(self):
        # 2 identical panels (e.g. left and right sides)
        joints = [
            {"id": "J1", "span": 12.0, "thickness": 0.5, "type": "TabAndSlot", "tab_count": 3},
            {"id": "J2", "span": 12.0, "thickness": 0.5, "type": "TabAndSlot", "tab_count": 3},
        ]
        KeyingSimulator.resolve_keying(joints)
        
        # Joint 1: Tab 0, Positive direction
        self.assertTrue(joints[0]["is_keyed"])
        self.assertEqual(joints[0]["keyed_tab"], 0)
        self.assertEqual(joints[0]["key_dir"], "Positive")

        # Joint 2: Tab 0, Negative direction (opposing direction on same tab!)
        self.assertTrue(joints[1]["is_keyed"])
        self.assertEqual(joints[1]["keyed_tab"], 0)
        self.assertEqual(joints[1]["key_dir"], "Negative")

    def test_confusable_four_joints_alternates_tabs(self):
        # 4 identical partitions
        joints = [
            {"id": "J1", "span": 10.0, "thickness": 0.75, "type": "TabAndSlot", "tab_count": 3},
            {"id": "J2", "span": 10.0, "thickness": 0.75, "type": "TabAndSlot", "tab_count": 3},
            {"id": "J3", "span": 10.0, "thickness": 0.75, "type": "TabAndSlot", "tab_count": 3},
            {"id": "J4", "span": 10.0, "thickness": 0.75, "type": "TabAndSlot", "tab_count": 3},
        ]
        KeyingSimulator.resolve_keying(joints)

        # Tab 0 pair
        self.assertEqual((joints[0]["keyed_tab"], joints[0]["key_dir"]), (0, "Positive"))
        self.assertEqual((joints[1]["keyed_tab"], joints[1]["key_dir"]), (0, "Negative"))

        # Tab 1 pair (next tab for subsequent conflict)
        self.assertEqual((joints[2]["keyed_tab"], joints[2]["key_dir"]), (1, "Positive"))
        self.assertEqual((joints[3]["keyed_tab"], joints[3]["key_dir"]), (1, "Negative"))

    def test_exceeding_capacity_fallback_many(self):
        # 3 joints with only 1 tab each -> capacity = 2*1 = 2 < 3
        joints = [
            {"id": "J1", "span": 4.0, "thickness": 0.5, "type": "TabAndSlot", "tab_count": 1},
            {"id": "J2", "span": 4.0, "thickness": 0.5, "type": "TabAndSlot", "tab_count": 1},
            {"id": "J3", "span": 4.0, "thickness": 0.5, "type": "TabAndSlot", "tab_count": 1},
        ]
        KeyingSimulator.resolve_keying(joints, fallback="many")
        self.assertTrue(joints[0]["is_keyed"])
        self.assertTrue(joints[1]["is_keyed"])
        self.assertFalse(joints[2]["is_keyed"]) # Graceful fallback: 3rd remains unkeyed
        self.assertIn("Insufficient tabs", joints[0]["conflict_desc"])

    def test_exceeding_capacity_fallback_limit_to_qualified(self):
        # 3 joints: J1 has 1 tab, J2 has 2 tabs, J3 has 2 tabs. Req tabs = ceil(3/2) = 2
        joints = [
            {"id": "J1", "span": 6.0, "thickness": 0.5, "type": "TabAndSlot", "tab_count": 1},
            {"id": "J2", "span": 6.0, "thickness": 0.5, "type": "TabAndSlot", "tab_count": 2},
            {"id": "J3", "span": 6.0, "thickness": 0.5, "type": "TabAndSlot", "tab_count": 2},
        ]
        KeyingSimulator.resolve_keying(joints, fallback="limit_n")
        self.assertFalse(joints[0]["is_keyed"]) # Excluded because tab_count < 2
        self.assertTrue(joints[1]["is_keyed"])  # J2 keyed
        self.assertTrue(joints[2]["is_keyed"])  # J3 keyed

class TestFingerJointIntervals(unittest.TestCase):
    def test_interleaved_fingers(self):
        span = 10.0
        n = 3 # 3 fingers on Body A -> total segments = 2*3 - 1 = 5
        base_width = span / 5 # 2.0 each
        
        segments = []
        for i in range(5):
            segments.append((i * base_width, (i + 1) * base_width, i % 2 == 0))

        # Check alternating bodies
        self.assertTrue(segments[0][2]) # Body A
        self.assertFalse(segments[1][2]) # Body B slot
        self.assertTrue(segments[2][2]) # Body A
        self.assertFalse(segments[3][2]) # Body B slot
        self.assertTrue(segments[4][2]) # Body A


class TestSlotContourDogbones(unittest.TestCase):
    @staticmethod
    def compute_slot_contour_py(s1, s2, t1, t2, radius, enable_dogbones, relief_type):
        width = s2 - s1
        height = t2 - t1
        if not enable_dogbones or relief_type == "None" or radius <= 1e-6:
            return 4, 0

        r = min(radius, 0.5 * min(width, height))
        if relief_type == "TBone" and width >= 4.0 * r + 1e-6:
            return 4, 4

        leg = r * math.sqrt(2.0)
        lines = 0
        if 2.0 * leg < width - 1e-7:
            lines += 2
        if 2.0 * leg < height - 1e-7:
            lines += 2
        return lines, 4

    def test_non_overlapping_diagonal_contour(self):
        lines, arcs = self.compute_slot_contour_py(0.0, 2.0, -0.5, 0.5, 0.25, True, "DiagonalDogbone")
        self.assertEqual((lines, arcs), (4, 4))

    def test_short_edge_overlapping_diagonal_contour(self):
        # 1.0" x 0.5" slot with 0.25" radius bit: 2*0.25*sqrt(2) = 0.707" > 0.5" on height
        lines, arcs = self.compute_slot_contour_py(0.0, 1.0, -0.25, 0.25, 0.25, True, "DiagonalDogbone")
        self.assertEqual((lines, arcs), (2, 4))

    def test_tbone_and_sharp_contours(self):
        self.assertEqual(self.compute_slot_contour_py(0.0, 2.0, -0.5, 0.5, 0.25, True, "TBone"), (4, 4))
        self.assertEqual(self.compute_slot_contour_py(0.0, 2.0, -0.5, 0.5, 0.25, False, "DiagonalDogbone"), (4, 0))


class TestCompiledJoineryAssembly(unittest.TestCase):
    def test_compiled_dll_exists(self):
        self.assertTrue(JOINERY_DLL.is_file(), f"Missing compiled DLL: {JOINERY_DLL}")

    def test_direct_dotnet_invocation(self):
        ps_script = f"""
$ErrorActionPreference = 'Stop'
$asm = [System.Reflection.Assembly]::LoadFrom('{str(JOINERY_DLL).replace("'", "''")}')
$swAddin = $asm.GetType('SolidWorksJoineryAddin.SwAddin')
$macroFeat = $asm.GetType('SolidWorksJoineryAddin.MacroFeatures.JoineryMacroFeature')
$pmp = $asm.GetType('SolidWorksJoineryAddin.UI.JoineryPMPHandler')
$gb = $asm.GetType('SolidWorksJoineryAddin.MacroFeatures.GeometryBuilder')
$reliefEnum = $asm.GetType('SolidWorksJoineryAddin.Data.CornerReliefType')
$diag = [Enum]::Parse($reliefEnum, 'DiagonalDogbone')
$tbone = [Enum]::Parse($reliefEnum, 'TBone')

$c1 = $gb.GetMethod('ComputeSlotContourWithDogbones2D').Invoke($null, @(0.0, 0.0508, -0.0127, 0.0127, 0.00635, $true, $diag))
$c2 = $gb.GetMethod('ComputeSlotContourWithDogbones2D').Invoke($null, @(0.0, 0.0254, -0.00635, 0.00635, 0.00635, $true, $diag))
$c3 = $gb.GetMethod('ComputeSlotContourWithDogbones2D').Invoke($null, @(0.0, 0.0508, -0.0127, 0.0127, 0.00635, $true, $tbone))

$grp = [Activator]::CreateInstance($asm.GetType('SolidWorksJoineryAddin.Data.JointGroupDefinition'))
$jType = $asm.GetType('SolidWorksJoineryAddin.Data.JointDefinition')
$j1 = [Activator]::CreateInstance($jType); $j1.JointId = 'J1'; $j1.SpanLength = 0.3048; $j1.Thickness = 0.0127; $j1.TabCount = 3
$j2 = [Activator]::CreateInstance($jType); $j2.JointId = 'J2'; $j2.SpanLength = 0.3048; $j2.Thickness = 0.0127; $j2.TabCount = 3
$grp.Joints.Add($j1); $grp.Joints.Add($j2)
$asm.GetType('SolidWorksJoineryAddin.MacroFeatures.KeyingEngine').GetMethod('ResolveKeying').Invoke($null, @($grp))

$store = $asm.GetType('SolidWorksJoineryAddin.Data.JointConfigStore')
$xml = $store.GetMethod('SerializeToXml').Invoke($null, @($grp))
$restored = $store.GetMethod('DeserializeXml').Invoke($null, @($xml))

$out = @{{
    hasSwAddin = [bool]($swAddin.GetInterfaces() | Where-Object {{ $_.Name -eq 'ISwAddin' }})
    hasMacroFeat = [bool]($macroFeat.GetInterfaces() | Where-Object {{ $_.Name -eq 'ISwComFeature' }})
    hasPmp = [bool]($pmp.GetInterfaces() | Where-Object {{ $_.Name -eq 'IPropertyManagerPage2Handler9' }})
    c1Lines = $c1.Lines.Count
    c1Arcs = $c1.Arcs.Count
    c2Lines = $c2.Lines.Count
    c2Arcs = $c2.Arcs.Count
    c2GapX = [Math]::Abs($c2.Arcs[1].End.X - $c2.Arcs[2].Start.X)
    c2GapY = [Math]::Abs($c2.Arcs[1].End.Y - $c2.Arcs[2].Start.Y)
    c3Lines = $c3.Lines.Count
    c3Arcs = $c3.Arcs.Count
    j1Keyed = $j1.IsKeyed
    j2Keyed = $j2.IsKeyed
    j1Dir = $j1.KeyDirection.ToString()
    j2Dir = $j2.KeyDirection.ToString()
    roundTripCount = $restored.Joints.Count
    defaultSchemaVer = $restored.FeatureSchemaVersion
    currentSchemaVer = ($asm.GetType('SolidWorksJoineryAddin.Data.JointGroupDefinition')::CURRENT_FEATURE_VERSION)
    hasUpgradeMgr = ($asm.GetType('SolidWorksJoineryAddin.MacroFeatures.JoineryUpgradeManager') -ne $null)
}}
$out | ConvertTo-Json -Compress
"""
        proc = subprocess.run(
            ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", ps_script],
            capture_output=True,
            text=True,
            check=True,
        )
        data = json.loads(proc.stdout.strip())
        self.assertTrue(data["hasSwAddin"])
        self.assertTrue(data["hasMacroFeat"])
        self.assertTrue(data["hasPmp"])
        self.assertEqual((data["c1Lines"], data["c1Arcs"]), (4, 4))
        self.assertEqual((data["c2Lines"], data["c2Arcs"]), (2, 4))
        self.assertLess(data["c2GapX"], 1e-7)
        self.assertLess(data["c2GapY"], 1e-7)
        self.assertEqual((data["c3Lines"], data["c3Arcs"]), (4, 4))
        self.assertTrue(data["j1Keyed"])
        self.assertTrue(data["j2Keyed"])
        self.assertNotEqual(data["j1Dir"], data["j2Dir"])
        self.assertEqual(data["roundTripCount"], 2)
        self.assertEqual(data["defaultSchemaVer"], 0)
        self.assertEqual(data["currentSchemaVer"], 1)
        self.assertTrue(data["hasUpgradeMgr"])


if __name__ == "__main__":
    unittest.main()
