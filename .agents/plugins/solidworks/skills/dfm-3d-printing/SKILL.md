---
name: dfm-3d-printing
description: >-
  Use this skill when designing, modifying, or auditing 3D-printed parts
  (including airflow/dust-collection/air-blast lofts, support-free cable saddles,
  strain reliefs, zip-tie tunnels, Gridfinity bins/toolholders, and KiCad
  electronics enclosures/fixtures) for your Prusa MK3.5S+ in MMGS.
---

# Design for 3D Printing (Prusa MK3.5S+ — `MMGS`)

This is a **Tier 1 (Preferred In-House)** process in `shop_profile.json`. Always configure the SOLIDWORKS document to **`MMGS`** (`millimeter, gram, second`).

---

## 1. Machine, Nozzle & Filament Hierarchy

- **Printer & Build Envelope**: **Prusa MK3.5S+** — **`250 × 210 × 210 mm`** maximum build volume. Larger parts must be split with alignment pins/rabbets or bonded using `30-min epoxy` / `CA glue`.
- **Nozzle Discipline**: Keep the **`0.4 mm` nozzle** installed (avoid requiring `0.2 mm` or `0.6 mm` swaps unless strictly unavoidable).
  - Design vertical wall thicknesses as integer multiples of `0.4 mm`: **`1.2 mm`** (3 walls, light bins), **`1.6 mm`** (4 walls, standard brackets/enclosures), **`2.0–2.4 mm`** (5–6 walls, airtight vacuum/air-blast ducts or high-torque bosses).
  - Minimum feature width / fin thickness: **`>= 0.8 mm`** (`>= 1.2 mm` structural).
- **Filament Preference Order**:
  1. **`MatterHackers Tough PLA (Basic)`** *(Default primary)* — crisp prints, low warp, tough enough for most shop fixtures, ducts, Gridfinity, and enclosures.
  2. **`MatterHackers Tough PLA (Pro)`** *(Reserve for high-stress parts)* — use only when impact, creep, or heavy clamping loads justify the higher material cost.
  3. **Secondary Filaments** *(by prep & cleanup overhead)*:
     - `Regular PLA` (low-stress prototypes/organizers)
     - `PETG` (higher temp/chemical resistance, e.g., near warm electronics)
     - `Transparent PETG` (LED light pipes, optical indicators, vacuum sight ports)
     - `PVB` / `CPE` (specialty smoothing or chemical resistance)
     - `ASA` (UV / outdoor / moderately elevated temperature; compensate for shrinkage)
     - `TPU` (vacuum pod lip seals, non-marring clamp pads, vibration feet, strain-relief grommets)
  4. **`Prusament PC`** *(Last option only)* — avoid unless thermal/mechanical limits eliminate Tough PLA Pro, PETG, CPE, and ASA.

---

## 2. Stocked Hardware, Heat-Set Inserts & Adhesives

### Exact McMaster-Carr Heat-Set Insert Specs
Model insert pockets via `sw_hole_preset(preset_type="mcmaster_insert", ...)` and populate BOM instances via `sw_populate_mcmaster_hardware`:

| McMaster PN | Thread | Style | Length (`mm`) | Top Entry Dia (`mm`) | Bottom Dia (`mm`) | Taper | Recommended Depth (`mm`) | Min Boss OD (`mm`) |
| :--- | :--- | :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **`94459A110`** | `M2 × 0.4` | Straight | `2.50` | `3.20` | `3.20` | `0°` | `3.50` | `6.40` |
| **`93365A112`** | `#2-56 UNC` | Tapered | `3.96` | `2.84` | `2.72` | `8°` | `5.00` | `6.00` |
| **`94180A333`** | `M3 × 0.5` | Tapered | `6.40` | `5.23` | `4.70` | `8°` | `7.60` | `8.50` |
| **`94180A353`** | `M4 × 0.7` | Tapered | `7.92` | `6.22` | `5.65` | `8°` | `9.20` | `10.00` |

### Fastener Selection Rules
- **Prefer Plentiful Stock**: `M2` (SHCS), `M3` (SHCS + hex/nylock nuts), `M4` (Hex head bolts or SHCS + hex/nylock nuts), `M6` (Hex head + hex nuts), and `1/4"-20` (Hex head/SHCS + hex nuts).
- **Decorative Brass Hardware**: `M2` and `M2.5` flat-head hex-drive brass screws (`90°` countersink).
- **Avoid Unless Required**: `#8-32` and `M5` (limited stock).
- **Head Clearances**: Since `M4`, `M6`, and `1/4"-20` are primarily stocked as **hex head bolts**, size counterbores or external wrench pockets for hex socket / open-end wrench swing clearance—or use hexagonal captive nut/bolt traps (`M3 = 5.75 mm`, `M4 = 7.30 mm`, `M6 = 10.35 mm`, `1/4"-20 = 11.45 mm` flat-to-flat).

### Adhesives & Joint Clearances
- **Press-Fit**: `0.10 mm` clearance per side.
- **Slip-Fit / Sliding**: `0.20 mm` clearance per side (`0.40 mm` for print-in-place hinges).
- **`30-Minute 2-Part Epoxy`**: `0.10–0.20 mm` bond-line gap + internal glue relief grooves.
- **`Thin & Gel CA` + `Loctite 770` Primer**: `0.05 mm` gap (Thin CA wicks) or `0.10 mm` (Gel CA); note Loctite 770 compatibility for slick/polyolefin interfaces.

---

## 3. Overhangs, Orientation & Existing Add-in Features

- **Bed Adhesion & Bottom Edges**: Never put rounded fillets on edges touching the build plate; use **`45°` chamfers** (`0.6–1.5 mm`).
- **Overhang Limit**: Keep unsupported overhangs $\le 45^\circ$ from vertical.
- **Anisotropic Strength**: Orient parts so tensile/clamping stresses lie in the **XY print plane**, never trying to pull layers apart along Z.
- **Horizontal Holes & Downward Counterbores**: Apply **40° capped teardrops** (`0.6 mm` max cap) to horizontal circular bores and **multi-tier crisscross bridges** to downward-facing counterbores/hex nut traps (can invoke `SolidWorks3DPrintDFMAddin` directly via `sw_invoke_repo_addin_tool`).

---

## 4. Airflow Lofts, Gridfinity & KiCad Electronics DFM

- **3D-Printed Airflow / Dust-Collection / Air-Blast Lofts**:
  - Design internal air passages with **vaulted arches or teardrop roofs (`<= 45°`)** so internal manifolds print **100% support-free** with zero loose support strings inside the airway.
  - Shell ducts to **`1.6–2.4 mm`** (4–6 perimeters) for airtightness; match shop port standards (`1-7/8"` shop vac taper `46.6–48.4 mm`, Festool Cleantec adapter, `1/4"` air quick-connect for `9 CFM` supply, and `TODO` placeholder for custom `4"` dust system).
- **Gridfinity (`MMGS`)**:
  - Max single-piece footprint on Prusa MK3.5S+ (`250 × 210 mm`): **`5U × 5U`** (`209.5 × 209.5 mm`) or `5U × 4U`. Split larger baseplates with dovetail/puzzle tabs.
  - Standard parameters: `42.0 mm` pitch, `7.0 mm` U height, `(42×N - 0.5) mm` bin XY, `R3.75 mm` bin corner, `4.75 mm` stepped foot, `4.4 mm` stacking lip, `6.5 × 2.4 mm` magnet pockets at `26.0 mm` centers.
- **KiCad PCB Enclosures & Bench Fixtures (`MMGS`)**:
  - `0.35 mm` XY PCB clearance, `0.40 mm` connector bezel clearance, `>= 1.0 mm` vertical clearance above tallest SMD part.
  - Use `Transparent PETG` sleeves (`>= 2.0 mm` dia) for status LED light pipes.
  - For soldering/hot-air/reflow carriers, maintain heat clearance around pads and switch from Tough PLA to `PETG`/`ASA` (or CNC `1/2"` Corian/MDF) wherever tools approach operating temperatures above `60°C`.

---

## 5. Support-Free 3D-Printed Cable Saddles, Strain Reliefs & Rolling U-Loops (`MMGS`)

1. **Print-Flat Flange Orientation**:
   - Place the flat mounting flange back (`z = 0`) on the print bed with a **`0.6 mm × 45°` perimeter chamfer** so M4/M6/M8 mounting holes print vertically (`Z`-axis) with maximum layer compression strength under fastener clamp load.
2. **Snap-In C-Barrels (100% Support-Free)**:
   - Size inner bore ID to **`cable_OD + 0.30 mm`** and barrel wall thickness **`R_out - R_in >= 1.60 mm`** (`>= 4` perimeters at `0.4 mm` nozzle).
   - Cap C-barrel open entry mouths at **`<= 35°` above horizontal** so overhanging lips print crisply with zero supports while providing positive snap retention.
3. **Printable `42°` Gable-Roof Zip-Tie Retention Tunnels**:
   - Route zip-tie slots under each barrel using a **`42°` gable-roof crown** (`<= 45°` print overhang limit) with **`>= 1.60 mm` solid bed floor** below the tunnel and **`>= 1.60 mm` solid crown web** between the gable apex and barrel bore.
   - Model zip ties in CAD as tangent **D-loops** matching the flat tunnel bottom + rounded crown—never circular rings, which falsely interfere with rectangular pedestal corners.
4. **Rolling U-Loops & Multi-Segment Solid Clearance**:
   - Keep `120V` AC cords and low-voltage stepper/endstop runs in separate parallel barrels (**`>= 20.0 mm` center-to-center**).
   - Size rolling U-loop bend radii **`R >= 3 × OD`** for split loom and **`R >= 4 × OD`** for jacketed rubber AC cord (`R > D/2` hard geometric minimum), with opposing saddle barrel Z-offsets separated by exactly **`ΔZ = 2R`**.
   - Leave **`0.5–2.0 mm` axial butt-gaps** where carriage-mounted wire sweeps enter saddle-clamped U-loops so subset interference checks report `0.000 mm³` collision volume, and temporarily suppress static-pose U-loops during multi-stop stroke sweeps (`sw_verify_motion_stroke`).

---

## 6. Mandatory Pre-Save Audit & Export Gate

Run `sw_audit_dfm(process="3d_printing")` (verifies build volume, unsupported `> 45°` overhangs, bed chamfers vs rounded fillets, and `< 1.2 mm` thin-web raycast checks), verify `sw_capture_viewport_quad`, and save `.SLDPRT` / `.SLDASM`. **Never export `.3MF` or `.STL` automatically unless the user explicitly asks to print or slice the part.**
