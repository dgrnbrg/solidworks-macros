---
name: dfm-sheet-metal-sendcutsend
description: >-
  Use this skill ONLY when evaluating or designing metal/laser-cut sheet parts
  for SendCutSend, enforcing the cost/lead-time deprioritization gate, exact
  SendCutSend bend/K-factor/die-line/PEM/countersink rules, weld-free assembly,
  and 1:1 flat-pattern DXF or vendor drawing exports.
---

# Design for Sheet Metal & Laser Cutting (SendCutSend — Deprioritized)

> [!CAUTION]
> **Tier 3 Deprioritized Vendor Gate**: SendCutSend is expensive and adds multi-day shipping lead time. **Always evaluate Tier 1 & Tier 2 in-house alternatives first** (Power-Tool Woodworking, `Tough PLA Basic/Pro` 3D Printing, CNC `1/2"` Corian + MMA, or CNC `1/8"`/`4"` Acrylic). Proceed with SendCutSend **only** when metal stiffness, wear, thermal resistance, thin-profile structural strength, or decorative Brass/Copper/Stainless aesthetics are strictly required.

Default unit system for SendCutSend parts is **`IPS`** (matches SendCutSend imperial gauges in `sendcutsend_catalog.json`).

---

## 1. Cost & Secondary-Operation Minimization Rules

When SendCutSend is genuinely justified, keep costs and lead time down:
1. **Prefer Flat Laser Cutting Over Bending/PEM/Tapping**: Every secondary SendCutSend service (press-brake bending, countersinking, tapping, PEM insertion, anodizing/powder coat) multiplies cost and turnaround.
2. **Use Your Own Shop Metal Tools When Practical**: You own a **Portaband** and **Angle Grinder**, plus stocked `M2/M3/M4/M6/1/4-20` bolts and `30-min epoxy`. Consider laser-cutting flat blanks or simple tab-and-slot plates and handling simple chamfering/trimming/countersinking in-house unless precision CNC press-brake bends or factory PEM fasteners are essential.
3. **Never Specify Welding**: You do not weld. Design 100% of multi-piece sheet metal assemblies to lock together via **self-fixturing tabs & slots + bent flanges + PEMs / bolts / rivets / `30-min 2-part epoxy`**.

---

## 2. Exact SendCutSend Material & Bending Rules (`sendcutsend_catalog.json`)

| Material | Bending Allowed? | Notes & Key Gauges |
| :--- | :---: | :--- |
| **`5052-H32 Aluminum`** | **Yes** | Primary choice for bent brackets/guards (`0.063"`: `R=0.035"`, `K=0.42`, min flat flange `0.255"`, die `0.472"`; `0.080"`: `R=0.045"`, `K=0.48`, min flat `0.255"`; `0.125"`: `R=0.125"`, min flat `0.368"`, die `0.630"`). |
| **`6061-T6 Aluminum`** | **NO (Flat Only)** | SendCutSend does **not** bend `6061-T6` (T6 temper cracks). Use ONLY for flat fixture plates, router templates, or machined brackets. |
| **`1008/A36 Mild Steel`** | **Yes** | High stiffness / magnetic brackets (`0.048"`, `0.059"`, `0.074"`, `0.119"`, `0.187"`). |
| **`304 / 316 Stainless`** | **Yes** | Corrosion/wear brackets (`0.048"`, `0.060"`, `0.074"`, `0.120"`). |
| **`C260 Brass` / `C110 Copper`** | **Flat Only** | Custom furniture inlays, escutcheons, brass sabots, decorative edge accents. |

### Hard Press-Brake & Laser Geometry Constraints
- **No Custom Bend Radii**: Always configure `sw_create_scs_sheet_metal_base` with SendCutSend's exact inside bend radius and K-factor from `sendcutsend_catalog.json` (and re-verify in SendCutSend's live Bending Calculator before checkout).
- **Minimum Flange Lengths**: Every bent flange must meet the gauge's minimum flat and post-bend flange length across $\ge 50\text{–}60\%$ of the bend line; add explicit **bend reliefs** if tapering flanges.
- **Die-Line Hole Clearance**: Keep all holes, slots, and cutouts **at least `0.5 × die_width`** away from every bend centerline (e.g., `>= 0.236"` for `0.472"` dies; `>= 0.315"` for `0.630"` dies on `0.125"` Al) so features don't stretch oval on the press brake.
- **Minimum Laser Feature Sizes**: Holes and positive webs/bridges must be **`>= 50%` of sheet thickness** (if powder coating, all holes `>= 0.040"` and hanging hole `>= 0.063"`).

---

## 3. Countersinking, PEM Hardware & Weld-Free Tab-and-Slot

- **Countersinking**:
  - Eligible metals typically **`>= 0.125"` thick**.
  - **CRITICAL DXF RULE**: Draw **ONLY the minor thru-hole diameter** in CAD/DXF (`verify_minor_csk_only = true`); SendCutSend automatically resizes and machines the countersink cone.
  - Keep the outer major countersink diameter completely outside `0.5 × die_width` from any bend line.
- **PEM Hardware (Nuts, Studs, Standoffs)**:
  - Upload the exact catalog mounting hole size (`M3 = 0.166"`, `M4 = 0.213"`, `1/4"-20 = 0.328"`, `M6 = 0.344"`, min sheet `>= 0.030"–0.040"`).
  - Measure edge/bend clearances from the **hole center** because SendCutSend auto-resizes holes for the selected PEM SKU.
- **Weld-Free Self-Fixturing Tabs & Slots (`sw_add_scs_tab_and_slot`)**:
  - **Tab Length**: Equal to the mating sheet thickness (for flush finish) or slightly proud if peened/epoxied.
  - **Slot Sizing**: Make slots **`+0.010"` larger** in both width and length than the mating tab for clean laser-kerf slip-fit assembly.
  - **Slot Corner Reliefs & Edge Spacing**: Add **`R >= 0.015"`** internal corner radii on slots (never sharp 90° laser corners that concentrate stress) and keep slots **`>= 1.5×` slot width** from part edges.

---

## 4. Export & Vendor Drawing Checklist

1. Run `sw_audit_dfm(process="sheet_metal_sendcutsend")`.
2. Export clean 1:1 flat-pattern `.DXF` via `sw_export_scs_flat_pattern_dxf` (no stray construction lines, closed outer/inner loops, distinct bend lines).
3. If ordering bent parts, countersinks, or PEM hardware, optionally generate a formal vendor drawing via `sw_create_fabrication_drawing(mode="sendcutsend_vendor", include_flat_pattern=true)`.
