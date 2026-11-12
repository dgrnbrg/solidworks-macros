"""Unit and geometry regression tests for ExportFacesToDXF and the VBA linter.

Portable across machines: core unit-conversion, DXF overcut, step-back, and
arc-perimeter tests use self-contained fixtures. When the local SOLIDWORKS
working fixtures exist on disk, optional integration assertions run too.
"""

from __future__ import annotations

import math
import re
import tempfile
import unittest
from pathlib import Path

from lint_vba import lint_bas_file

REPO_ROOT = Path(__file__).resolve().parent.parent
BAS_PATH = REPO_ROOT / "ExportFacesToDXF.bas"

OPTIONAL_FIXTURE_02 = Path(r"D:\cad files\tilt out drill press shelf\Shelf_02.dxf")
OPTIONAL_FIXTURE_09 = Path(r"D:\cad files\tilt out drill press shelf\Shelf_09.dxf")


# ---------------------------------------------------------------------------
# Reference mirrors of VBA input parsing, unit conversion, and geometry logic
# ---------------------------------------------------------------------------

def parse_strict_non_neg_float(s: str):
    if not s or not re.fullmatch(r"[0-9]*\.?[0-9]+|[0-9]+\.[0-9]*", s):
        return None
    return float(s)


def parse_fraction_or_decimal(s: str):
    s = s.strip()
    if not s or s.startswith("-"):
        return None
    if s.startswith("+"):
        s = s[1:].strip()
    if "/" not in s:
        return parse_strict_non_neg_float(s)
    if s.count("/") > 1:
        return None
    left, right = [x.strip() for x in s.split("/")]
    denom = parse_strict_non_neg_float(right)
    if denom is None or denom <= 0:
        return None
    if " " in left:
        parts = left.split()
        if len(parts) != 2:
            return None
        whole_v = parse_strict_non_neg_float(parts[0])
        num_v = parse_strict_non_neg_float(parts[1])
        if whole_v is None or num_v is None:
            return None
        return whole_v + num_v / denom
    num_v = parse_strict_non_neg_float(left)
    return None if num_v is None else num_v / denom


def parse_overcut_input(raw: str):
    s = raw.strip().replace(",", ".")
    if not s:
        return None
    unit = ""
    if s.endswith("''"):
        unit = "IN"
        s = s[:-2].strip()
    elif s.endswith('"'):
        unit = "IN"
        s = s[:-1].strip()
    else:
        m = re.search(r"([A-Za-z]+)$", s)
        if m:
            suf = m.group(1).lower()
            s = s[: m.start()].strip()
            umap = {
                "mm": "MM", "millimeter": "MM", "millimeters": "MM",
                "cm": "CM", "centimeter": "CM", "centimeters": "CM",
                "m": "M", "meter": "M", "meters": "M",
                "in": "IN", "inch": "IN", "inches": "IN",
                "ft": "FT", "foot": "FT", "feet": "FT",
            }
            if suf not in umap:
                return None
            unit = umap[suf]
    val = parse_fraction_or_decimal(s)
    if val is None or val < 0:
        return None
    return val, unit


def resolve_overcut(ov_val: float, ov_unit: str, target_code: int) -> float:
    if ov_val <= 0:
        return 0.0
    if not ov_unit:
        return ov_val
    code_map = {"IN": 1, "FT": 2, "MM": 4, "CM": 5, "M": 6}
    src = code_map[ov_unit]
    if src == target_code:
        return ov_val
    to_m = {1: 0.0254, 2: 0.3048, 4: 0.001, 5: 0.01, 6: 1.0}
    return (ov_val * to_m[src]) / to_m[target_code]


def read_dxf_polylines(path: Path):
    lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
    i = 0
    in_ent = False
    polys = []
    insunits = 1
    while i < len(lines) - 1:
        c = lines[i].strip()
        v = lines[i + 1].strip()
        if c == "9" and v == "$INSUNITS" and i + 3 < len(lines) and lines[i + 2].strip() == "70":
            insunits = int(lines[i + 3].strip())
        if c == "0" and v == "SECTION":
            if i + 3 < len(lines) and lines[i + 2].strip() == "2" and lines[i + 3].strip() == "ENTITIES":
                in_ent = True
                i += 4
                continue
        elif in_ent and c == "0" and v == "ENDSEC":
            break
        elif in_ent and c == "0":
            etype = v
            j = i + 2
            ent = []
            while j < len(lines) - 1 and lines[j].strip() != "0":
                ent.append((lines[j].strip(), lines[j + 1].strip()))
                j += 2
            if etype == "LWPOLYLINE":
                layer = "0"
                closed = False
                verts = []
                px = None
                for ec, ev in ent:
                    if ec == "8":
                        layer = ev
                    elif ec == "70":
                        closed = (int(ev) & 1) == 1
                    elif ec == "10":
                        px = float(ev)
                    elif ec == "20" and px is not None:
                        verts.append([px, float(ev), 0.0])
                        px = None
                    elif ec == "42" and verts:
                        verts[-1][2] = float(ev)
                polys.append({"layer": layer, "closed": closed, "verts": verts})
            i = j
            continue
        i += 2
    return insunits, polys


def signed_area(verts):
    s = 0.0
    n = len(verts)
    for i in range(n):
        j = (i + 1) % n
        s += verts[i][0] * verts[j][1] - verts[j][0] * verts[i][1]
    return 0.5 * s


def calc_arc_from_bulge(x1, y1, x2, y2, bulge):
    dx, dy = x2 - x1, y2 - y1
    chord = math.hypot(dx, dy)
    ux, uy = dx / chord, dy / chord
    h = (chord / 4.0) * (1.0 / bulge - bulge)
    mx, my = 0.5 * (x1 + x2), 0.5 * (y1 + y2)
    cx, cy = mx - uy * h, my + ux * h
    r = (chord / 4.0) * (abs(bulge) + 1.0 / abs(bulge))
    a1 = math.atan2(y1 - cy, x1 - cx)
    a2 = math.atan2(y2 - cy, x2 - cx)
    sweep = 4.0 * math.atan(bulge)
    diff = a2 - a1
    if sweep > 0:
        while diff <= 0:
            diff += 2 * math.pi
        while diff > 2 * math.pi:
            diff -= 2 * math.pi
    else:
        while diff >= 0:
            diff -= 2 * math.pi
        while diff < -2 * math.pi:
            diff += 2 * math.pi
    return cx, cy, r, a1, a2, diff


def apply_overcut_to_polys(outline_poly, face_polys, overcut_dist, is_metric):
    if overcut_dist <= 1e-7:
        return [dict(p, verts=[list(v) for v in p["verts"]]) for p in face_polys]

    ov = [list(v) for v in outline_poly["verts"]]
    if signed_area(ov) < 0:
        n = len(ov)
        ov = [
            [ov[n - 1 - idx][0], ov[n - 1 - idx][1], -ov[(n - 2 - idx + n) % n][2]]
            for idx in range(n)
        ]

    out_segs = []
    for i in range(len(ov)):
        j = (i + 1) % len(ov)
        x1, y1, b = ov[i]
        x2, y2, _ = ov[j]
        seg_len = math.hypot(x2 - x1, y2 - y1)
        if seg_len > 1e-7:
            if abs(b) <= 1e-9:
                tx, ty = (x2 - x1) / seg_len, (y2 - y1) / seg_len
                out_segs.append({"line": True, "x1": x1, "y1": y1, "x2": x2, "y2": y2, "nx": ty, "ny": -tx})
            else:
                cx, cy, r, a1, _, sw = calc_arc_from_bulge(x1, y1, x2, y2, b)
                out_segs.append({
                    "line": False, "x1": x1, "y1": y1, "x2": x2, "y2": y2,
                    "bulge": b, "cx": cx, "cy": cy, "r": r, "a1": a1, "sw": sw,
                    "sgn": 1.0 if b > 0 else -1.0,
                })

    edge_tol = 0.05 if is_metric else 0.002
    min_len = 0.01 if is_metric else 0.0004
    ang_tol = 0.001

    results = []
    for fp in face_polys:
        fv = fp["verts"]
        n = len(fv)
        segs = []
        any_touched = False
        n_segs = n if fp["closed"] else n - 1
        for i in range(n_segs):
            j = (i + 1) % n
            x1, y1, b = fv[i]
            x2, y2, _ = fv[j]
            seg_len = math.hypot(x2 - x1, y2 - y1)
            if seg_len <= 1e-7:
                continue
            if abs(b) <= 1e-9:
                fx, fy = (x2 - x1) / seg_len, (y2 - y1) / seg_len
                splits = [0.0, 1.0]
                for oseg in out_segs:
                    if not oseg["line"]:
                        continue
                    olen = math.hypot(oseg["x2"] - oseg["x1"], oseg["y2"] - oseg["y1"])
                    ox, oy = (oseg["x2"] - oseg["x1"]) / olen, (oseg["y2"] - oseg["y1"]) / olen
                    if abs(fx * ox + fy * oy) >= 1.0 - ang_tol:
                        d1 = abs((oseg["x1"] - x1) * fy - (oseg["y1"] - y1) * fx)
                        d2 = abs((oseg["x2"] - x1) * fy - (oseg["y2"] - y1) * fx)
                        if d1 <= edge_tol and d2 <= edge_tol:
                            s_a = (oseg["x1"] - x1) * fx + (oseg["y1"] - y1) * fy
                            s_b = (oseg["x2"] - x1) * fx + (oseg["y2"] - y1) * fy
                            ov_a = max(0.0, min(s_a, s_b))
                            ov_b = min(seg_len, max(s_a, s_b))
                            if ov_b - ov_a > min_len:
                                if ov_a > min_len:
                                    splits.append(ov_a / seg_len)
                                if seg_len - ov_b > min_len:
                                    splits.append(ov_b / seg_len)
                splits = sorted(splits)
                uniq = [splits[0]]
                for sp in splits[1:]:
                    if sp - uniq[-1] > max(1e-6, min_len / seg_len) and (
                        1.0 - sp > max(1e-6, min_len / seg_len) or sp == 1.0
                    ):
                        uniq.append(sp)
                    elif sp == 1.0:
                        uniq[-1] = 1.0
                for k in range(len(uniq) - 1):
                    ta, tb = uniq[k], uniq[k + 1]
                    sx1, sy1 = x1 + ta * (x2 - x1), y1 + ta * (y2 - y1)
                    sx2, sy2 = x1 + tb * (x2 - x1), y1 + tb * (y2 - y1)
                    mx, my = 0.5 * (sx1 + sx2), 0.5 * (sy1 + sy2)
                    matched, nx, ny = False, 0.0, 0.0
                    for oseg in out_segs:
                        if not oseg["line"]:
                            continue
                        olen = math.hypot(oseg["x2"] - oseg["x1"], oseg["y2"] - oseg["y1"])
                        ox, oy = (oseg["x2"] - oseg["x1"]) / olen, (oseg["y2"] - oseg["y1"]) / olen
                        if abs(fx * ox + fy * oy) >= 1.0 - ang_tol:
                            if abs((mx - oseg["x1"]) * oy - (my - oseg["y1"]) * ox) <= edge_tol:
                                proj = (mx - oseg["x1"]) * ox + (my - oseg["y1"]) * oy
                                if -edge_tol <= proj <= olen + edge_tol:
                                    p1 = (sx1 - oseg["x1"]) * ox + (sy1 - oseg["y1"]) * oy
                                    p2 = (sx2 - oseg["x1"]) * ox + (sy2 - oseg["y1"]) * oy
                                    if min(max(p1, p2), olen) - max(min(p1, p2), 0.0) > min_len:
                                        matched, nx, ny = True, oseg["nx"], oseg["ny"]
                                        any_touched = True
                                        break
                    segs.append({
                        "line": True, "x1": sx1, "y1": sy1, "x2": sx2, "y2": sy2,
                        "orig_ex": sx2, "orig_ey": sy2, "b": 0.0,
                        "touched": matched, "nx": nx, "ny": ny,
                    })
            else:
                cx, cy, r, a1, _, sw = calc_arc_from_bulge(x1, y1, x2, y2, b)
                mid_ang = a1 + 0.5 * sw
                matched = False
                msgn = 0.0
                for oseg in out_segs:
                    if not oseg["line"] and math.hypot(cx - oseg["cx"], cy - oseg["cy"]) <= edge_tol and abs(r - oseg["r"]) <= edge_tol:
                        rel = mid_ang - oseg["a1"]
                        if oseg["sw"] > 0:
                            while rel < -1e-6:
                                rel += 2 * math.pi
                            while rel >= 2 * math.pi - 1e-6:
                                rel -= 2 * math.pi
                            ok = -1e-6 <= rel <= oseg["sw"] + 1e-6
                        else:
                            while rel > 1e-6:
                                rel -= 2 * math.pi
                            while rel <= -2 * math.pi + 1e-6:
                                rel += 2 * math.pi
                            ok = 1e-6 >= rel >= oseg["sw"] - 1e-6
                        if ok:
                            matched, msgn = True, oseg["sgn"]
                            any_touched = True
                            break
                segs.append({
                    "line": False, "x1": x1, "y1": y1, "x2": x2, "y2": y2,
                    "orig_ex": x2, "orig_ey": y2, "b": b,
                    "touched": matched, "cx": cx, "cy": cy, "r": r, "sgn": msgn,
                })

        if not any_touched:
            results.append(dict(fp, verts=[list(v) for v in fv]))
            continue

        for sg in segs:
            if sg["touched"]:
                if sg["line"]:
                    sg["x1"] += sg["nx"] * overcut_dist
                    sg["y1"] += sg["ny"] * overcut_dist
                    sg["x2"] += sg["nx"] * overcut_dist
                    sg["y2"] += sg["ny"] * overcut_dist
                else:
                    rn = sg["r"] + sg["sgn"] * overcut_dist
                    a1 = math.atan2(sg["y1"] - sg["cy"], sg["x1"] - sg["cx"])
                    a2 = math.atan2(sg["y2"] - sg["cy"], sg["x2"] - sg["cx"])
                    sg["r"] = rn
                    sg["x1"], sg["y1"] = sg["cx"] + rn * math.cos(a1), sg["cy"] + rn * math.sin(a1)
                    sg["x2"], sg["y2"] = sg["cx"] + rn * math.cos(a2), sg["cy"] + rn * math.sin(a2)

        miter_lim = 4.0 * overcut_dist
        ns = len(segs)
        for k in range(ns if fp["closed"] else ns - 1):
            nk = (k + 1) % ns
            s_a, s_b = segs[k], segs[nk]
            if not s_a["touched"] and not s_b["touched"]:
                continue
            ox, oy = s_a["orig_ex"], s_a["orig_ey"]
            if s_a["line"] and s_b["line"]:
                d1x, d1y = s_a["x2"] - s_a["x1"], s_a["y2"] - s_a["y1"]
                d2x, d2y = s_b["x2"] - s_b["x1"], s_b["y2"] - s_b["y1"]
                den = d1x * d2y - d1y * d2x
                sc = math.hypot(d1x, d1y) * math.hypot(d2x, d2y)
                if sc > 1e-9 and abs(den) > 1e-6 * sc:
                    t = ((s_b["x1"] - s_a["x1"]) * d2y - (s_b["y1"] - s_a["y1"]) * d2x) / den
                    ix, iy = s_a["x1"] + t * d1x, s_a["y1"] + t * d1y
                    if math.hypot(ix - ox, iy - oy) <= miter_lim:
                        s_a["x2"], s_a["y2"] = ix, iy
                        s_b["x1"], s_b["y1"] = ix, iy

        out_v = []
        for k in range(ns):
            sg = segs[k]
            out_v.append([sg["x1"], sg["y1"], sg["b"]])
            nk = (k + 1) % ns
            if k < ns - 1 or fp["closed"]:
                if math.hypot(sg["x2"] - segs[nk]["x1"], sg["y2"] - segs[nk]["y1"]) > 1e-6:
                    out_v.append([sg["x2"], sg["y2"], 0.0])
            else:
                out_v.append([sg["x2"], sg["y2"], 0.0])
        if fp["closed"] and len(out_v) > 2 and math.hypot(out_v[-1][0] - out_v[0][0], out_v[-1][1] - out_v[0][1]) < 1e-6:
            out_v.pop()
        results.append({"layer": fp["layer"], "closed": fp["closed"], "verts": out_v})
    return results


# ---------------------------------------------------------------------------
# Test suites
# ---------------------------------------------------------------------------

class TestVbaLinter(unittest.TestCase):
    def test_repo_bas_passes_lint(self):
        self.assertEqual(lint_bas_file(BAS_PATH), [])

    def test_linter_catches_reserved_keyword_and_duplicate_dim(self):
        bad_vba = "\n".join([
            "Option Explicit",
            "Private Type DummySeg",
            "    X1 As Double",
            "End Type",
            "Sub BadProc(ByVal a As Double)",
            "    Dim oR As Double",
            "    Dim a As Long",
            "    oR = a",
            "End Sub",
        ])
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp) / "bad.bas"
            p.write_text(bad_vba, encoding="utf-8")
            errs = lint_bas_file(p)
        joined = "\n".join(errs)
        self.assertIn("oR", joined)
        self.assertIn("Duplicate Dim", joined)


class TestOvercutUnitsAndGeometry(unittest.TestCase):
    def test_input_parsing_and_unit_conversion(self):
        self.assertEqual(parse_overcut_input("0"), (0.0, ""))
        self.assertEqual(parse_overcut_input("0,25"), (0.25, ""))
        self.assertEqual(parse_overcut_input("6mm"), (6.0, "MM"))
        self.assertEqual(parse_overcut_input(' 1 1/4" '), (1.25, "IN"))
        self.assertEqual(parse_overcut_input("1/4 in"), (0.25, "IN"))
        self.assertIsNone(parse_overcut_input("-0.1"))
        self.assertIsNone(parse_overcut_input("1/0"))
        self.assertIsNone(parse_overcut_input("5px"))
        self.assertAlmostEqual(resolve_overcut(6.0, "MM", 1), 6.0 / 25.4, places=9)
        self.assertAlmostEqual(resolve_overcut(0.25, "IN", 4), 6.35, places=9)
        self.assertEqual(resolve_overcut(0.0, "MM", 1), 0.0)

    def test_split_pockets_keep_interior_seam_exact(self):
        outline = {"layer": "OUTLINE", "closed": True, "verts": [[0, 0, 0], [9, 0, 0], [9, 6, 0], [0, 6, 0]]}
        faces = [
            {"layer": "FACE_1", "closed": True, "verts": [[0, 0, 0], [4.5, 0, 0], [4.5, 6, 0], [0, 6, 0]]},
            {"layer": "FACE_2", "closed": True, "verts": [[4.5, 0, 0], [9, 0, 0], [9, 6, 0], [4.5, 6, 0]]},
        ]
        res = apply_overcut_to_polys(outline, faces, 0.25, False)
        f1 = res[0]["verts"]
        f2 = res[1]["verts"]
        self.assertAlmostEqual(min(v[0] for v in f1), -0.25, places=6)
        self.assertAlmostEqual(max(v[0] for v in f1), 4.5, places=6)
        self.assertAlmostEqual(min(v[0] for v in f2), 4.5, places=6)
        self.assertAlmostEqual(max(v[0] for v in f2), 9.25, places=6)

    def test_partial_overlap_step_back(self):
        out_step = {
            "layer": "OUTLINE",
            "closed": True,
            "verts": [[0, 0, 0], [4, 0, 0], [4, -1, 0], [6, -1, 0], [6, 0, 0], [10, 0, 0], [10, 10, 0], [0, 10, 0]],
        }
        face_step = [{"layer": "FACE_1", "closed": True, "verts": [[0, 0, 0], [10, 0, 0], [10, 5, 0], [0, 5, 0]]}]
        res = apply_overcut_to_polys(out_step, face_step, 0.25, False)
        pts = [(round(v[0], 4), round(v[1], 4)) for v in res[0]["verts"]]
        for expected in [(-0.25, -0.25), (4.0, -0.25), (4.0, 0.0), (6.0, 0.0), (6.0, -0.25), (10.25, -0.25)]:
            self.assertIn(expected, pts)

    def test_arc_perimeter_overcut(self):
        b90 = math.tan(math.pi / 8.0)
        out_arc = {"layer": "OUTLINE", "closed": True, "verts": [[0, 0, 0], [5, 0, 0], [5, 3, b90], [3, 5, 0], [0, 5, 0]]}
        face_arc = [{"layer": "FACE_1", "closed": True, "verts": [[2, 0, 0], [5, 0, 0], [5, 3, b90], [3, 5, 0], [2, 5, 0]]}]
        res = apply_overcut_to_polys(out_arc, face_arc, 0.25, False)
        xs = [round(v[0], 4) for v in res[0]["verts"]]
        self.assertIn(5.25, xs)

    def test_optional_local_dxf_fixtures(self):
        if OPTIONAL_FIXTURE_09.exists():
            ins9, polys9 = read_dxf_polylines(OPTIONAL_FIXTURE_09)
            out9 = next(p for p in polys9 if p["layer"] == "OUTLINE")
            faces9 = [p for p in polys9 if p["layer"].startswith("FACE_")]
            res9 = apply_overcut_to_polys(out9, faces9, 0.25, ins9 in (4, 5, 6))
            for fp in res9:
                self.assertTrue(fp["closed"])
                self.assertTrue(any(abs(v[0] - 4.5) < 1e-6 for v in fp["verts"]))
                self.assertFalse(any(abs(v[0] - 4.25) < 1e-6 or abs(v[0] - 4.75) < 1e-6 for v in fp["verts"]))

        if OPTIONAL_FIXTURE_02.exists():
            ins2, polys2 = read_dxf_polylines(OPTIONAL_FIXTURE_02)
            faces2 = [p for p in polys2 if p["layer"].startswith("FACE_")]
            ov_in = resolve_overcut(6.0, "MM", ins2)
            # Reconstruct outer perimeter directly via existing Shelf_02 rectangle/outline if present
            max_f2_orig = max(v[0] for p in faces2 if p["layer"] == "FACE_2" for v in p["verts"])
            self.assertAlmostEqual(max_f2_orig + ov_in, 5.875 + 6.0 / 25.4, places=4)


if __name__ == "__main__":
    unittest.main(verbosity=2)
