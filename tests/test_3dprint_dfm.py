"""Comprehensive unit and geometry tests for SolidWorks3DPrintDFMAddin.

Verifies:
1. Circle-to-Bed Angle filtering (default 40° to 90°; excludes 0° parallel circles).
2. Teardrop Crown Relief math with 40° overhang angle from horizontal (50° from vertical)
   and 0.6 mm max cap extension measured along the print bed normal B_hat:
   - Pointed apex when uncapped extension <= 0.6 mm * sin(beta_circle).
   - Flat-top truncated roof when uncapped extension > 0.6 mm * sin(beta_circle).
   - Exact tangent wall slope (tan(40°)) and bed-normal layer height projection.
3. Integral 2-Tier Crisscross Bridge general 2D loop clipping (Sutherland-Hodgman + analytical circles):
   - Circular counterbores (Tier 1 chord endpoints and Tier 2 circumscribed square).
   - Arbitrary outer shapes (Hexagonal nut trap, Rectangular / Slotted recess).
   - Shallow counterbore corner clipping when inner square corners exceed outer radius.
4. Compiled SolidWorks3DPrintDFMAddin.dll build output verification.
"""

from __future__ import annotations

import math
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent


def compute_circle_angle_to_bed_deg(hole_axis: tuple[float, float, float], bed_up: tuple[float, float, float]) -> float:
    ax, ay, az = hole_axis
    bx, by, bz = bed_up
    a_len = math.sqrt(ax * ax + ay * ay + az * az)
    b_len = math.sqrt(bx * bx + by * by + bz * bz)
    dot = abs((ax * bx + ay * by + az * bz) / (a_len * b_len))
    dot = min(1.0, max(0.0, dot))
    return math.degrees(math.acos(dot))


def qualifies_for_teardrop(
    hole_axis: tuple[float, float, float],
    bed_up: tuple[float, float, float],
    min_circle_angle_deg: float = 40.0,
) -> bool:
    beta = compute_circle_angle_to_bed_deg(hole_axis, bed_up)
    return (min_circle_angle_deg - 1e-6) <= beta <= (90.0 + 1e-6)


def compute_teardrop_profile_2d(
    radius: float,
    overhang_angle_from_bed_deg: float = 40.0,
    circle_angle_to_bed_deg: float = 90.0,
    enable_cap: bool = True,
    max_cap_height_along_bed_normal: float = 0.6,
) -> dict:
    alpha_rad = math.radians(overhang_angle_from_bed_deg)
    beta_rad = math.radians(circle_angle_to_bed_deg)

    sin_a = math.sin(alpha_rad)
    cos_a = math.cos(alpha_rad)

    t_right = (radius * sin_a, radius * cos_a)
    t_left = (-radius * sin_a, radius * cos_a)

    v_peak = radius / cos_a
    uncapped_delta_v = v_peak - radius
    apex = (0.0, v_peak)

    # Bed-normal cap height projected into the circle's cross-section plane:
    effective_max_delta_v = max_cap_height_along_bed_normal * math.sin(beta_rad)
    v_cap = radius + effective_max_delta_v

    is_capped = enable_cap and (uncapped_delta_v > effective_max_delta_v + 1e-9) and (effective_max_delta_v > 1e-9)
    cap_right = apex
    cap_left = apex

    if is_capped:
        u_cap = (radius - v_cap * cos_a) / sin_a
        cap_right = (u_cap, v_cap)
        cap_left = (-u_cap, v_cap)

    return {
        "is_capped": is_capped,
        "t_left": t_left,
        "t_right": t_right,
        "apex": apex,
        "cap_left": cap_left,
        "cap_right": cap_right,
        "v_peak": v_peak,
        "v_cap": v_cap,
        "uncapped_delta_v": uncapped_delta_v,
        "effective_max_delta_v": effective_max_delta_v,
    }


def clip_against_half_plane(
    polygon: list[tuple[float, float]], nx: float, ny: float, c: float
) -> list[tuple[float, float]]:
    if not polygon:
        return []
    out: list[tuple[float, float]] = []
    prev = polygon[-1]
    prev_d = nx * prev[0] + ny * prev[1] - c
    prev_in = prev_d >= -1e-10

    for curr in polygon:
        curr_d = nx * curr[0] + ny * curr[1] - c
        curr_in = curr_d >= -1e-10
        if curr_in:
            if not prev_in:
                t = prev_d / (prev_d - curr_d)
                out.append((prev[0] + t * (curr[0] - prev[0]), prev[1] + t * (curr[1] - prev[1])))
            out.append(curr)
        elif prev_in:
            t = prev_d / (prev_d - curr_d)
            out.append((prev[0] + t * (curr[0] - prev[0]), prev[1] + t * (curr[1] - prev[1])))
        prev, prev_d, prev_in = curr, curr_d, curr_in
    return out


def clip_polygon_to_oriented_strip(
    polygon: list[tuple[float, float]], nx: float, ny: float, s_min: float, s_max: float
) -> list[tuple[float, float]]:
    s1 = clip_against_half_plane(polygon, nx, ny, s_min)
    s2 = clip_against_half_plane(s1, -nx, -ny, -s_max)
    return s2


def clip_polygon_to_strip(
    polygon: list[tuple[float, float]], x_min: float, x_max: float
) -> list[tuple[float, float]]:
    return clip_polygon_to_oriented_strip(polygon, 1.0, 0.0, x_min, x_max)


def clip_polygon_to_box(
    polygon: list[tuple[float, float]], x_min: float, x_max: float, y_min: float, y_max: float
) -> list[tuple[float, float]]:
    s_strip = clip_polygon_to_strip(polygon, x_min, x_max)
    s3 = clip_against_half_plane(s_strip, 0.0, 1.0, y_min)
    s4 = clip_against_half_plane(s3, 0.0, -1.0, -y_max)
    return s4


def _norm_axis(rad: float) -> float:
    n = rad % math.pi
    if n < 0.0:
        n += math.pi
    if math.pi - n < 1e-9:
        n = 0.0
    return n


def _axis_diff(a: float, b: float) -> float:
    d = abs(_norm_axis(a) - _norm_axis(b))
    return math.pi - d if d > 0.5 * math.pi else d


def _poly_area(poly: list[tuple[float, float]]) -> float:
    if len(poly) < 3:
        return 0.0
    area = 0.0
    for i in range(len(poly)):
        x1, y1 = poly[i]
        x2, y2 = poly[(i + 1) % len(poly)]
        area += x1 * y2 - x2 * y1
    return abs(area) * 0.5


def plan_multi_tier_bridges(
    outer_radius_mm: float,
    holes_xyr_mm: list[tuple[float, float, float]],
    inner_clearance_mm: float = 0.015,
    outer_inset_mm: float = 0.010,
    min_bridge_mm: float = 0.25,
) -> list[dict]:
    if not holes_xyr_mm:
        return []
    eff_r_out = outer_radius_mm - outer_inset_mm
    outer_poly = [
        (eff_r_out * math.cos(2 * math.pi * i / 72), eff_r_out * math.sin(2 * math.pi * i / 72))
        for i in range(72)
    ]
    eff_holes = [(cx, cy, r + inner_clearance_mm) for cx, cy, r in holes_xyr_mm]

    if len(eff_holes) == 1:
        cx, cy, r = eff_holes[0]
        t1 = clip_polygon_to_strip(outer_poly, cx - r, cx + r)
        t2 = clip_polygon_to_box(outer_poly, cx - r, cx + r, cy - r, cy + r)
        return [
            {"tier": 1, "angle_deg": 0.0, "polygons": [t1]},
            {"tier": 2, "angle_deg": 90.0, "polygons": [t2]},
        ]

    def proj_hole(h: tuple[float, float, float], ang: float) -> tuple[float, float]:
        nx, ny = math.cos(ang), math.sin(ang)
        c = h[0] * nx + h[1] * ny
        return (c - h[2], c + h[2])

    def is_tight(poly: list[tuple[float, float]], h: tuple[float, float, float], ang: float) -> bool:
        nx, ny = math.cos(ang), math.sin(ang)
        h_min, h_max = proj_hole(h, ang)
        qs = [p[0] * nx + p[1] * ny for p in poly]
        return (h_min - min(qs)) <= min_bridge_mm + 1e-6 and (max(qs) - h_max) <= min_bridge_mm + 1e-6

    def is_single_complete(poly: list[tuple[float, float]], h: tuple[float, float, float], axes: list[float]) -> bool:
        tight = [a for a in axes if is_tight(poly, h, a)]
        if not tight:
            return False
        for a in tight:
            if is_tight(poly, h, _norm_axis(a + 0.5 * math.pi)):
                return True
        if len(tight) < 2:
            return False
        for i in range(len(tight)):
            for j in range(i + 1, len(tight)):
                if _axis_diff(tight[i], tight[j]) >= math.radians(80.0) - 1e-4:
                    return True
        max_excess = 0.0
        for px, py in poly:
            dx, dy = px - h[0], py - h[1]
            dist = math.hypot(dx, dy)
            if dist > 1e-9:
                max_excess = max(max_excess, dist - h[2])
        return max_excess <= 0.48 * h[2] + min_bridge_mm

    active = [{"poly": outer_poly, "holes": eff_holes, "axes": []}]
    tiers: list[dict] = []

    for t_idx in range(1, 7):
        if not active:
            break
        tier_polys: list[list[tuple[float, float]]] = []
        next_active: list[dict] = []
        rep_angle = None

        for reg in active:
            holes = reg["holes"]
            axes = reg["axes"]
            poly = reg["poly"]
            if len(holes) == 1:
                h = holes[0]
                if is_single_complete(poly, h, axes):
                    continue
                tight = [a for a in axes if is_tight(poly, h, a)]
                ang = _norm_axis(tight[0] + 0.5 * math.pi) if tight else 0.0
                h_min, h_max = proj_hole(h, ang)
                clipped = clip_polygon_to_oriented_strip(poly, math.cos(ang), math.sin(ang), h_min, h_max)
                if _poly_area(clipped) > 1e-6:
                    tier_polys.append(clipped)
                    if rep_angle is None:
                        rep_angle = ang
                    new_axes = axes + [ang]
                    if not is_single_complete(clipped, h, new_axes):
                        next_active.append({"poly": clipped, "holes": [h], "axes": new_axes})
            else:
                cands: list[float] = []

                def add_ang(r: float) -> None:
                    nr = _norm_axis(r)
                    if all(_axis_diff(nr, x) >= 0.001 for x in cands):
                        cands.append(nr)

                add_ang(0.0)
                add_ang(0.5 * math.pi)
                for a in axes:
                    add_ang(a + 0.5 * math.pi)
                    add_ang(a)
                for i in range(len(holes)):
                    for j in range(i + 1, len(holes)):
                        dx = holes[j][0] - holes[i][0]
                        dy = holes[j][1] - holes[i][1]
                        d = math.hypot(dx, dy)
                        if d > 1e-6:
                            phi = math.atan2(dy, dx)
                            add_ang(phi)
                            add_ang(phi + 0.5 * math.pi)
                for deg in range(0, 180, 5):
                    add_ang(math.radians(deg))

                best = None
                for ang in cands:
                    ivs = sorted(
                        [(proj_hole(h, ang)[0], proj_hole(h, ang)[1], [h]) for h in holes],
                        key=lambda x: x[0],
                    )
                    groups = [list(ivs[0])]
                    for iv in ivs[1:]:
                        if iv[0] - groups[-1][1] < min_bridge_mm - 1e-9:
                            groups[-1][1] = max(groups[-1][1], iv[1])
                            groups[-1][2].extend(iv[2])
                        else:
                            groups.append(list(iv))
                    if len(groups) < 2:
                        continue
                    gaps = [groups[k + 1][0] - groups[k][1] for k in range(len(groups) - 1)]
                    ortho_ok = 1 if (not axes or abs(math.sin(ang - axes[-1])) >= 0.5 - 1e-6) else 0
                    singles = sum(1 for g in groups if len(g[2]) == 1)
                    pref_diff = min(_axis_diff(ang, 0.0), _axis_diff(ang, 0.5 * math.pi))
                    key = (
                        ortho_ok,
                        len(groups),
                        singles,
                        round(min(gaps), 4),
                        round(sum(gaps), 4),
                        -round(pref_diff, 4),
                    )
                    if best is None or key > best[0]:
                        best = (key, ang, groups)

                if best is None:
                    # Explicit failure / abort: no verified separating angle exists
                    return []
                _, ang, groups = best
                nx, ny = math.cos(ang), math.sin(ang)
                if rep_angle is None:
                    rep_angle = ang
                for g_min, g_max, g_holes in groups:
                    clipped = clip_polygon_to_oriented_strip(poly, nx, ny, g_min, g_max)
                    if _poly_area(clipped) > 1e-6:
                        tier_polys.append(clipped)
                        new_axes = axes + [ang]
                        if not (len(g_holes) == 1 and is_single_complete(clipped, g_holes[0], new_axes)):
                            next_active.append({"poly": clipped, "holes": g_holes, "axes": new_axes})

        if not tier_polys:
            break
        tiers.append({
            "tier": t_idx,
            "angle_deg": math.degrees(rep_angle or 0.0),
            "polygons": tier_polys,
        })
        active = next_active

    return tiers


class TestCircleToBedAngleFilter(unittest.TestCase):
    def test_parallel_circle_excluded(self):
        # Hole axis is vertical (0, 0, 1), BedUp is (0, 0, 1) -> circle is parallel to bed (0 deg)
        bed_up = (0.0, 0.0, 1.0)
        hole_axis = (0.0, 0.0, 1.0)
        angle = compute_circle_angle_to_bed_deg(hole_axis, bed_up)
        self.assertAlmostEqual(angle, 0.0, places=5)
        self.assertFalse(qualifies_for_teardrop(hole_axis, bed_up, min_circle_angle_deg=40.0))

    def test_shallow_30deg_circle_excluded_by_40deg_default(self):
        # Circle tilted at 30 deg to bed -> axis makes 30 deg with BedUp
        bed_up = (0.0, 0.0, 1.0)
        hole_axis = (math.sin(math.radians(30.0)), 0.0, math.cos(math.radians(30.0)))
        angle = compute_circle_angle_to_bed_deg(hole_axis, bed_up)
        self.assertAlmostEqual(angle, 30.0, places=5)
        self.assertFalse(qualifies_for_teardrop(hole_axis, bed_up, min_circle_angle_deg=40.0))

    def test_40deg_boundary_and_90deg_vertical_circle_included(self):
        bed_up = (0.0, 0.0, 1.0)
        hole_40 = (math.sin(math.radians(40.0)), 0.0, math.cos(math.radians(40.0)))
        hole_90 = (1.0, 0.0, 0.0)  # Horizontal hole axis -> vertical circle (90 deg to bed)
        self.assertTrue(qualifies_for_teardrop(hole_40, bed_up, min_circle_angle_deg=40.0))
        self.assertAlmostEqual(compute_circle_angle_to_bed_deg(hole_90, bed_up), 90.0, places=5)
        self.assertTrue(qualifies_for_teardrop(hole_90, bed_up, min_circle_angle_deg=40.0))


class TestTeardropMath40DegAnd06MmCap(unittest.TestCase):
    def test_small_hole_uncapped_pointed_apex(self):
        # Diameter = 3.0 mm -> R = 1.5 mm. At 40° from bed, uncapped_delta_v = 1.5 * (1/cos(40°) - 1) = 0.458 mm < 0.6 mm
        prof = compute_teardrop_profile_2d(
            radius=1.5,
            overhang_angle_from_bed_deg=40.0,
            circle_angle_to_bed_deg=90.0,
            enable_cap=True,
            max_cap_height_along_bed_normal=0.6,
        )
        self.assertFalse(prof["is_capped"])
        self.assertAlmostEqual(prof["uncapped_delta_v"], 1.5 * (1.0 / math.cos(math.radians(40.0)) - 1.0), places=6)
        self.assertLess(prof["uncapped_delta_v"], 0.6)

        # Verify roof slope is exactly 40° from horizontal
        tx, ty = prof["t_right"]
        ax, ay = prof["apex"]
        slope = abs((ay - ty) / (tx - ax))
        self.assertAlmostEqual(slope, math.tan(math.radians(40.0)), places=6)

    def test_large_hole_flat_top_capped_at_06mm(self):
        # Diameter = 10.0 mm -> R = 5.0 mm. Uncapped delta_v = 1.527 mm > 0.6 mm -> capped at v_cap = 5.6 mm!
        prof = compute_teardrop_profile_2d(
            radius=5.0,
            overhang_angle_from_bed_deg=40.0,
            circle_angle_to_bed_deg=90.0,
            enable_cap=True,
            max_cap_height_along_bed_normal=0.6,
        )
        self.assertTrue(prof["is_capped"])
        self.assertAlmostEqual(prof["v_cap"], 5.6, places=6)
        cx, cy = prof["cap_right"]
        self.assertAlmostEqual(cy, 5.6, places=6)
        self.assertGreater(cx, 0.0)

        # Verify tangent wall from t_right to cap_right still has exact 40° slope from horizontal
        tx, ty = prof["t_right"]
        slope = (cy - ty) / (tx - cx)
        self.assertAlmostEqual(slope, math.tan(math.radians(40.0)), places=6)

    def test_tilted_hole_bed_normal_cap_projection(self):
        # For a hole whose circle is at 50° to the bed, delta_v_max = 0.6 * sin(50°)
        prof = compute_teardrop_profile_2d(
            radius=6.0,
            overhang_angle_from_bed_deg=40.0,
            circle_angle_to_bed_deg=50.0,
            enable_cap=True,
            max_cap_height_along_bed_normal=0.6,
        )
        self.assertTrue(prof["is_capped"])
        expected_delta_v = 0.6 * math.sin(math.radians(50.0))
        self.assertAlmostEqual(prof["effective_max_delta_v"], expected_delta_v, places=6)
        # Projecting back onto BedUp (dividing by sin(50°)) recovers the exact 0.6 mm layer budget
        bed_normal_height = (prof["v_cap"] - 6.0) / math.sin(math.radians(50.0))
        self.assertAlmostEqual(bed_normal_height, 0.6, places=6)


class TestIntegralCrisscrossBridgesGeneralClipping(unittest.TestCase):
    def test_circular_counterbore_chords_and_tier2_square(self):
        # M3 counterbore: Outer D_cb = 6.5 mm (R = 3.25), Inner d_in = 3.4 mm (r = 1.70)
        r_out = 3.25
        r_in = 1.70
        expected_y_chord = math.sqrt(r_out * r_out - r_in * r_in)

        outer_poly = [
            (r_out * math.cos(2 * math.pi * i / 120), r_out * math.sin(2 * math.pi * i / 120))
            for i in range(120)
        ]
        tier1 = clip_polygon_to_strip(outer_poly, -r_in, r_in)
        tier2 = clip_polygon_to_box(outer_poly, -r_in, r_in, -r_in, r_in)

        # Tier 1 X bounds must be clamped to [-r_in, +r_in], Y bounds reach +/- R_out
        xs1 = [p[0] for p in tier1]
        ys1 = [p[1] for p in tier1]
        self.assertAlmostEqual(min(xs1), -r_in, places=4)
        self.assertAlmostEqual(max(xs1), r_in, places=4)
        self.assertAlmostEqual(max(ys1), r_out, places=2)
        self.assertGreater(expected_y_chord, r_in)

        # Since r_in * sqrt(2) = 2.404 < 3.25, Tier 2 is an exact [-1.7, 1.7] x [-1.7, 1.7] square
        xs2 = [p[0] for p in tier2]
        ys2 = [p[1] for p in tier2]
        self.assertAlmostEqual(min(xs2), -r_in, places=5)
        self.assertAlmostEqual(max(xs2), r_in, places=5)
        self.assertAlmostEqual(min(ys2), -r_in, places=5)
        self.assertAlmostEqual(max(ys2), r_in, places=5)

    def test_hex_nut_trap_general_polygon_clipping(self):
        # Hexagonal nut trap of circumradius 4.0 mm with inner bolt hole r_in = 1.6 mm
        r_hex = 4.0
        hex_poly = [
            (r_hex * math.cos(math.radians(60 * i)), r_hex * math.sin(math.radians(60 * i)))
            for i in range(6)
        ]
        r_in = 1.6
        tier1 = clip_polygon_to_strip(hex_poly, -r_in, r_in)
        tier2 = clip_polygon_to_box(hex_poly, -r_in, r_in, -r_in, r_in)

        self.assertTrue(all(-r_in - 1e-6 <= p[0] <= r_in + 1e-6 for p in tier1))
        self.assertEqual(len(tier2), 4)


def compute_merged_angle_span_deg(intervals: list[tuple[float, float]]) -> float:
    if not intervals:
        return 0.0
    sorted_iv = sorted(intervals, key=lambda x: x[0])
    total = 0.0
    cur_s, cur_e = sorted_iv[0]
    for s, e in sorted_iv[1:]:
        if s <= cur_e + 0.5:
            cur_e = max(cur_e, e)
        else:
            total += (cur_e - cur_s)
            cur_s, cur_e = s, e
    total += (cur_e - cur_s)
    return min(360.0, total)


class TestCircularHoleSweepFilterAndTangentClearance(unittest.TestCase):
    def test_rectangular_corner_fillets_rejected_split_circle_accepted(self):
        min_sweep = 240.0
        # 90-degree rectangular corner fillet with top & bottom edges covering the same [0, 90] arc
        corner_fillet_edges = [(0.0, 90.0), (0.0, 90.0)]
        self.assertAlmostEqual(compute_merged_angle_span_deg(corner_fillet_edges), 90.0, places=5)
        self.assertLess(compute_merged_angle_span_deg(corner_fillet_edges), min_sweep)

        # 180-degree slot end cap
        slot_end_edges = [(0.0, 180.0), (0.0, 180.0)]
        self.assertLess(compute_merged_angle_span_deg(slot_end_edges), min_sweep)

        # Split circular hole (two coaxial 180-degree half-cylinders)
        split_circle_edges = [(0.0, 180.0), (180.0, 360.0)]
        self.assertAlmostEqual(compute_merged_angle_span_deg(split_circle_edges), 360.0, places=5)
        self.assertGreaterEqual(compute_merged_angle_span_deg(split_circle_edges), min_sweep)

    def test_bridge_tangent_clearance_expansion_and_outer_inset(self):
        r_out = 0.00325
        r_in = 0.00170
        inner_clearance = 0.000015
        outer_inset = 0.000010

        eff_r_out = r_out - outer_inset
        x_min, x_max = -r_in - inner_clearance, r_in + inner_clearance
        y_min, y_max = -r_in - inner_clearance, r_in + inner_clearance

        outer_poly = [
            (eff_r_out * math.cos(2 * math.pi * i / 72), eff_r_out * math.sin(2 * math.pi * i / 72))
            for i in range(72)
        ]
        tier1 = clip_polygon_to_strip(outer_poly, x_min, x_max)
        tier2 = clip_polygon_to_box(outer_poly, x_min, x_max, y_min, y_max)

        self.assertGreater(max(p[0] for p in tier1), r_in)
        self.assertLess(min(p[0] for p in tier1), -r_in)
        self.assertLess(max(p[1] for p in tier1), r_out)
        self.assertAlmostEqual(max(p[0] for p in tier2), r_in + inner_clearance, places=6)
        self.assertAlmostEqual(max(p[1] for p in tier2), r_in + inner_clearance, places=6)


class TestMultiTierTangentBridgePlanner(unittest.TestCase):
    @staticmethod
    def _dist_pt_to_seg(pt: tuple[float, float], a: tuple[float, float], b: tuple[float, float]) -> float:
        abx, aby = b[0] - a[0], b[1] - a[1]
        len_sq = abx * abx + aby * aby
        if len_sq < 1e-18:
            return math.hypot(pt[0] - a[0], pt[1] - a[1])
        t = max(0.0, min(1.0, ((pt[0] - a[0]) * abx + (pt[1] - a[1]) * aby) / len_sq))
        return math.hypot(pt[0] - (a[0] + t * abx), pt[1] - (a[1] + t * aby))

    @classmethod
    def _point_in_or_on(cls, pt: tuple[float, float], poly: list[tuple[float, float]], tol: float = 1e-4) -> bool:
        n = len(poly)
        for i in range(n):
            if cls._dist_pt_to_seg(pt, poly[i], poly[(i + 1) % n]) <= abs(tol):
                return tol >= 0.0
        inside = False
        j = n - 1
        for i in range(n):
            xi, yi = poly[i]
            xj, yj = poly[j]
            if ((yi > pt[1]) != (yj > pt[1])) and (
                pt[0] < (xj - xi) * (pt[1] - yi) / ((yj - yi) + 1e-18) + xi
            ):
                inside = not inside
            j = i
        return inside

    @classmethod
    def _is_chord_single_supported_segment(
        cls, parent_poly: list[tuple[float, float]], nx: float, ny: float, s: float
    ) -> bool:
        tx, ty = -ny, nx
        hits: list[float] = []
        n = len(parent_poly)
        for i in range(n):
            a = parent_poly[i]
            b = parent_poly[(i + 1) % n]
            da = a[0] * nx + a[1] * ny - s
            db = b[0] * nx + b[1] * ny - s
            if abs(da) <= 1e-6 and abs(db) <= 1e-6:
                hits.extend([a[0] * tx + a[1] * ty, b[0] * tx + b[1] * ty])
            elif (da > 1e-6 and db < -1e-6) or (da < -1e-6 and db > 1e-6):
                frac = da / (da - db)
                px, py = a[0] + frac * (b[0] - a[0]), a[1] + frac * (b[1] - a[1])
                hits.append(px * tx + py * ty)
            elif abs(da) <= 1e-6:
                hits.append(a[0] * tx + a[1] * ty)
        if len(hits) < 2:
            return False
        hits.sort()
        uniq = [hits[0]]
        for h in hits[1:]:
            if h - uniq[-1] > 1e-3:
                uniq.append(h)
        if len(uniq) < 2 or (uniq[-1] - uniq[0]) < 0.25:
            return False
        for k in range(len(uniq) - 1):
            t_mid = 0.5 * (uniq[k] + uniq[k + 1])
            p_mid = (s * nx + t_mid * tx, s * ny + t_mid * ty)
            if not cls._point_in_or_on(p_mid, parent_poly, 1e-4):
                return False
        return True

    def _assert_all_tiers_bridge_capable(
        self, outer_radius_mm: float, holes_xyr_mm: list[tuple[float, float, float]], tiers: list[dict]
    ) -> None:
        eff_r_out = outer_radius_mm - 0.010
        outer_poly = [
            (eff_r_out * math.cos(2 * math.pi * i / 72), eff_r_out * math.sin(2 * math.pi * i / 72))
            for i in range(72)
        ]
        for t_idx, tier in enumerate(tiers):
            parent_polys = [outer_poly] if t_idx == 0 else tiers[t_idx - 1]["polygons"]
            for poly in tier["polygons"]:
                self.assertGreater(_poly_area(poly), 1e-4)
                # Every edge avoids all physical hole interiors
                for i in range(len(poly)):
                    a, b = poly[i], poly[(i + 1) % len(poly)]
                    for cx, cy, r in holes_xyr_mm:
                        self.assertGreaterEqual(self._dist_pt_to_seg((cx, cy), a, b), r - 1e-4)
                # Centroid nests inside at least one parent polygon
                cen = (sum(p[0] for p in poly) / len(poly), sum(p[1] for p in poly) / len(poly))
                self.assertTrue(any(self._point_in_or_on(cen, par, 1e-3) for par in parent_polys))
        # Every hole reaches a deepest isolated polygon containing no other hole
        for idx, (cx, cy, _) in enumerate(holes_xyr_mm):
            deepest = None
            for tier in reversed(tiers):
                for poly in tier["polygons"]:
                    if self._point_in_or_on((cx, cy), poly, 1e-4):
                        deepest = poly
                        break
                if deepest is not None:
                    break
            self.assertIsNotNone(deepest)
            for jdx, (ox, oy, _) in enumerate(holes_xyr_mm):
                if idx != jdx:
                    self.assertFalse(self._point_in_or_on((ox, oy), deepest, 1e-4))

    def test_acme_nut_retainer_5_holes_solves_in_3_tiers_at_45deg(self):
        # Exact acme nut retainer recess: R_out = 12 mm, center hole r = 5.5 mm,
        # and 4 bolt holes on R_bolt = 8 mm at +/-45, +/-135 deg with r = 1.5 mm.
        d_diag = 8.0 / math.sqrt(2.0)  # 5.656854 mm
        holes = [
            (0.0, 0.0, 5.5),
            (d_diag, -d_diag, 1.5),
            (d_diag, d_diag, 1.5),
            (-d_diag, d_diag, 1.5),
            (-d_diag, -d_diag, 1.5),
        ]
        tiers = plan_multi_tier_bridges(outer_radius_mm=12.0, holes_xyr_mm=holes)
        self.assertEqual(len(tiers), 3)
        self.assertEqual(len(tiers[0]["polygons"]), 3)  # Tier 1: 3 disjoint strips at 45 deg
        self.assertEqual(len(tiers[1]["polygons"]), 5)  # Tier 2: 5 disjoint regions at 135 deg
        self.assertEqual(len(tiers[2]["polygons"]), 2)  # Tier 3: 2 remaining bolt squares
        self.assertAlmostEqual(min(tiers[0]["angle_deg"], 180.0 - tiers[0]["angle_deg"]), 45.0, places=2)
        self._assert_all_tiers_bridge_capable(12.0, holes, tiers)

    def test_asymmetric_3_bolt_plus_center_hole_solves_in_4_tiers(self):
        holes = [(0.0, 0.0, 5.5)] + [
            (8.0 * math.cos(math.radians(a)), 8.0 * math.sin(math.radians(a)), 1.5)
            for a in (0.0, 120.0, 240.0)
        ]
        tiers = plan_multi_tier_bridges(outer_radius_mm=12.0, holes_xyr_mm=holes)
        self.assertGreaterEqual(len(tiers), 3)
        self.assertLessEqual(len(tiers), 4)
        self.assertEqual(len(tiers), 4)
        self._assert_all_tiers_bridge_capable(12.0, holes, tiers)

    def test_single_hole_uses_2_tiers(self):
        tiers = plan_multi_tier_bridges(outer_radius_mm=6.5, holes_xyr_mm=[(0.0, 0.0, 1.7)])
        self.assertEqual(len(tiers), 2)
        self.assertEqual(len(tiers[0]["polygons"]), 1)
        self.assertEqual(len(tiers[1]["polygons"]), 1)
        self._assert_all_tiers_bridge_capable(6.5, [(0.0, 0.0, 1.7)], tiers)

    def test_concave_u_notch_chord_verification_rejects_broken_span(self):
        # Concave U-shaped outer polygon: prongs [0,7]x[0,10] and [13,20]x[0,10], connected via [0,20]x[0,3]
        u_poly = [
            (0.0, 0.0), (20.0, 0.0), (20.0, 10.0), (13.0, 10.0),
            (13.0, 3.0), (7.0, 3.0), (7.0, 10.0), (0.0, 10.0),
        ]
        # A horizontal slice at y = 5.5 crosses the concave empty U-notch [7, 13] -> must be REJECTED
        self.assertFalse(self._is_chord_single_supported_segment(u_poly, 0.0, 1.0, 5.5))
        # A vertical slice at x = 3.5 spans solely inside the left prong [0, 10] -> must PASS
        self.assertTrue(self._is_chord_single_supported_segment(u_poly, 1.0, 0.0, 3.5))

    def test_unbridgeable_overlapping_holes_explicitly_aborts(self):
        # Three concentric/mutually overlapping holes with zero straight bridge corridor in any direction
        overlapping = [(0.0, 0.0, 4.0), (1.0, 0.0, 4.0), (0.0, 1.0, 4.0)]
        tiers = plan_multi_tier_bridges(outer_radius_mm=12.0, holes_xyr_mm=overlapping)
        self.assertEqual(tiers, [])


class TestCompiledAddinArtifact(unittest.TestCase):
    def test_compiled_dll_exists(self):
        dll_path = REPO_ROOT / "src" / "SolidWorks3DPrintDFMAddin" / "bin" / "x64" / "Release" / "SolidWorks3DPrintDFMAddin.dll"
        self.assertTrue(dll_path.is_file(), f"Expected compiled DLL at {dll_path}")

    def test_macrofeature_nesting_no_group_folder_fallback(self):
        macro_cs = (REPO_ROOT / "src" / "SolidWorks3DPrintDFMAddin" / "Builders" / "DfmMacroFeature.cs").read_text(encoding="utf-8")
        builder_cs = (REPO_ROOT / "src" / "SolidWorks3DPrintDFMAddin" / "Builders" / "DfmFeatureBuilder.cs").read_text(encoding="utf-8")
        clip_cs = (REPO_ROOT / "src" / "SolidWorks3DPrintDFMAddin" / "Core" / "BridgeClippingEngine.cs").read_text(encoding="utf-8")

        self.assertNotIn("InsertFeatureTreeFolder2", macro_cs)
        self.assertIn("MakeSubFeature", macro_cs)
        self.assertIn("ComputeMultiTierBridgePlan", builder_cs)
        self.assertIn("ABORT:", builder_cs)
        self.assertIn("VerifyBridgePlan", clip_cs)
        self.assertIn("IsChordSingleSupportedSegment", clip_cs)
        self.assertIn("TrySolvePlanRecursive", clip_cs)
        self.assertNotIn("RunGreedyFallbackPlan", clip_cs)

    def test_installer_multiselect_scripts(self):
        bat_text = (REPO_ROOT / "Install-Addin.bat").read_text(encoding="utf-8")
        install_ps1 = (REPO_ROOT / "scripts" / "Install-Addin.ps1").read_text(encoding="utf-8")
        build_ps1 = (REPO_ROOT / "scripts" / "Build-Addin.ps1").read_text(encoding="utf-8")
        register_ps1 = (REPO_ROOT / "scripts" / "Register-Addin.ps1").read_text(encoding="utf-8")

        self.assertIn("-STA", bat_text)
        self.assertIn("Install-Addin.ps1", bat_text)

        self.assertIn("CheckedListBox", install_ps1)
        self.assertIn("SolidWorks3DPrintDFMAddin", install_ps1)
        self.assertIn("SolidWorksJoineryAddin", install_ps1)
        self.assertIn("-Projects", install_ps1)

        self.assertIn("[string[]]$Projects", build_ps1)
        self.assertIn("-split ','", build_ps1)

        self.assertIn("[string[]]$Projects", register_ps1)
        self.assertIn("-split ','", register_ps1)
        self.assertIn('-Project `"$($requestedProjects -join \',\')`"', register_ps1)

    def test_addin_icon_resources_present_and_wired(self):
        dfm_icons = REPO_ROOT / "src" / "SolidWorks3DPrintDFMAddin" / "Resources" / "Icons"
        joinery_icons = REPO_ROOT / "src" / "SolidWorksJoineryAddin" / "Resources" / "Icons"
        for sz in (20, 32, 40, 64, 96, 128):
            self.assertTrue((dfm_icons / f"dfm_cmd_strip_{sz}.png").is_file())
            self.assertTrue((dfm_icons / f"dfm_main_{sz}.png").is_file())
            self.assertTrue((joinery_icons / f"joinery_cmd_strip_{sz}.png").is_file())
            self.assertTrue((joinery_icons / f"joinery_main_{sz}.png").is_file())
        for mode in ("regular", "suppressed", "highlight"):
            self.assertTrue((dfm_icons / f"teardrop_tree_{mode}.png").is_file())
            self.assertTrue((dfm_icons / f"bridge_tree_{mode}.png").is_file())
            self.assertTrue((joinery_icons / f"joinery_tree_{mode}.png").is_file())

        dfm_sw = (REPO_ROOT / "src" / "SolidWorks3DPrintDFMAddin" / "SwAddin.cs").read_text(encoding="utf-8")
        joinery_sw = (REPO_ROOT / "src" / "SolidWorksJoineryAddin" / "SwAddin.cs").read_text(encoding="utf-8")
        dfm_macro = (REPO_ROOT / "src" / "SolidWorks3DPrintDFMAddin" / "Builders" / "DfmMacroFeature.cs").read_text(encoding="utf-8")
        joinery_macro = (REPO_ROOT / "src" / "SolidWorksJoineryAddin" / "MacroFeatures" / "JoineryMacroFeature.cs").read_text(encoding="utf-8")

        self.assertIn("IconResourceHelper.GetCommandIconList()", dfm_sw)
        self.assertIn("IconResourceHelper.GetMainIconList()", dfm_sw)
        self.assertIn("IconResourceHelper.GetCommandIconList()", joinery_sw)
        self.assertIn("IconResourceHelper.GetMainIconList()", joinery_sw)
        self.assertIn("IconResourceHelper.GetTeardropTreeIconFiles()", dfm_macro)
        self.assertIn("IconResourceHelper.GetBridgeTreeIconFiles()", dfm_macro)
        self.assertIn("IconResourceHelper.GetJoineryTreeIconFiles()", joinery_macro)


if __name__ == "__main__":
    unittest.main()
