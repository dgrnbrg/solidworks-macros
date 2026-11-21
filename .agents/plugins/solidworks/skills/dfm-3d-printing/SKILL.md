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
  1. **`MatterHackers Tough PLA (Basic)`** *(Default primary)* — crisp prints, low warp, tough enough for most ambient-temp, non-sliding shop fixtures, ducts, Gridfinity, and enclosures.
  2. **`MatterHackers Tough PLA (Pro)`** *(Reserve for high-impact/stress ambient parts)* — use only when impact strength or toughness justifies the higher material cost *(still PLA-class heat & cold-creep limits!)*.
  3. **Secondary Filaments** *(by prep & cleanup overhead)*:
     - `Regular PLA` (low-stress prototypes/organizers)
     - `PETG` (higher temp/creep/chemical resistance, e.g., near warm motors/electronics)
     - `Transparent PETG` (LED light pipes, optical indicators, vacuum sight ports)
     - `PVB` / `CPE` (PVB for IPA cosmetic smoothing; CPE for higher heat/creep/wear toughness)
     - `ASA` (UV / outdoor / high-temp / low-creep / cleaner matte sliding than PETG; compensate for shrinkage)
     - `TPU` (best abrasion resistance & high grip — vacuum pod lip seals, non-marring clamp pads, vibration feet, cable chafe grommets)
  4. **`Prusament PC`** *(Last option only)* — avoid unless thermal, creep, or wear limits eliminate Tough PLA Pro, PETG, CPE, and ASA.

### Material Property Matrix (Thermal, Cold Creep, Abrasion & Slipperiness)

Always evaluate these properties **before** locking in `Tough PLA Basic` or choosing clearances/fasteners:

| Filament | Glass Transition ($T_g$) / TDS HDT | Max Loaded / Anti-Sag Temp | Cold Creep (Static Preload / Clips) | Abrasion Resistance | Slipperiness / Friction |
| :--- | :---: | :---: | :--- | :--- | :--- |
| **`MH Build Tough PLA` ("Basic") / `PLA`** | $T_g$ `~55–60 °C`; **TDS HDT `52 °C`** *(GB/T 1634; `34.3 MPa`, `90%` elong., `63 kJ/m²` Izod)* | **`40–45 °C`** *(sags quickly above `45–52 °C`)* | **Poor (High Creep)** — relaxes bolt preload & constant spring clips at room temp | **Low–Moderate** — ductile vs brittle PLA, but softens/galls under frictional heat | **Low–Moderate** ($\mu \approx 0.15\text{–}0.25$ cool/dry; loses slip if warmed) |
| **`PRO Series Tough PLA` (Ingeo 3D870)** | Base $T_g$ `55–60 °C`; **HDT `~55 °C` as-printed / `75–80 °C` annealed** *(MatterHackers TDS; `40 MPa`, `160 J/m` impact)* | **`45–50 °C` as-printed** / **`70–75 °C` annealed** *(anneal `110–120 °C`; causes shrink/warp)* | **Poor–Moderate (as-printed)** — higher stiffness/impact than Build Tough PLA, still creeps under sustained clamp load unless annealed | **Moderate** — resists brittle chipping better than Basic PLA | **Low–Moderate** ($\mu \approx 0.18\text{–}0.28$ at low speed) |
| **`PETG` / `Transp. PETG`** | `75–80 °C` | **`65–70 °C`** *(safe near warm motors/PSUs)* | **Moderate (Much Better Than PLA)** — holds moderate loads/clips without fast relaxation | **Moderate–Good** — ductile, scuffs easily but resists cracking | **Grabby / Slightly Tacky** ($\mu \approx 0.30\text{–}0.45$; avoid PETG-on-PETG slides) |
| **`PVB`** | `60–68 °C` | **`50–55 °C`** | **Poor–Moderate** *(cosmetic/non-structural)* | **Low** — soft surface, mars easily | **Moderate** *(slightly tacky when IPA-smoothed)* |
| **`CPE`** | `75–85 °C` | **`70–75 °C`** | **Good (Low–Moderate Creep)** — holds bolt preload & dimensional geometry well | **Good** — durable against chips/dust | **Moderate** — cleaner sliding than tacky PETG |
| **`ASA`** | `95–105 °C` | **`85–95 °C`** *(motors, routers, sunlight)* | **Very Good (Low Creep)** — strong long-term clamp, press-fit & clip retention | **Good** — hard matte wear-resistant surface | **Smooth / Low–Moderate** ($\mu \approx 0.20\text{–}0.30$ dry matte; clean sliding jigs) |
| **`TPU` (95A/98A)** | `< -20 °C` *(elastomer)* | **`60–75 °C`** *(compliant, non-structural)* | **Moderate Compression Set** — limit gasket crush to `15–25%` so seals rebound | **Excellent (Best in Shop)** — top choice for abrasive dust/cable chafe | **Very High Grip** ($\mu \approx 0.60\text{–}1.0+$; anti-slip pads/seals only) |
| **`Prusament PC`** *(Last Resort)* | `110–115 °C` | **`100–110 °C`** | **Excellent (Very Low Creep)** — best rigid retention under heavy preload | **Very Good** — hard, high wear life | **Slick / Low–Moderate** ($\mu \approx 0.20\text{–}0.30$ on steel/wood/aluminum) |

### Thermal, Creep & Tribology Design Mitigations (Apply in CAD)
1. **Glass Transition & Sagging Guard (`10–15 °C` Margin Below $T_g$)**:
   - Check proximity to stepper motors (`50–65 °C`), router bodies/spindles, PSUs/Einsy drivers, sunlit windows, and soldering/hot-air tools.
   - Never mount loaded **`Tough PLA Basic/Pro`** parts directly against stepper faces, heatsinks, or `> 45 °C` zones — step up to **`PETG`** (`<= 65 °C`), **`CPE`** (`<= 75 °C`), **`ASA`** (`<= 90 °C`), or **`Prusament PC`** (last resort `<= 105 °C`), or add an air gap / Corian/metal thermal standoff.
   - Add gussets/ribs under long cantilevered arms to cut bending stress that accelerates creep and thermal sagging.
2. **Cold Creep & Sustained Clamping Preload**:
   - **Never rely on PLA / Tough PLA plastic clamp tension alone to keep bolted joints tight**: always pair through-bolts with **Nylon-Insert Locknuts (Nylocks)**, **wide flat washers** (spreading bearing stress), or **metal compression limiters / spacers** on high-clamp structural joints (e.g., CNC/router lift mounts).
   - For **constantly loaded snap clips, spring detents, or press-fit pins**, step up to **`PETG` / `CPE` / `ASA`** (or use metal springs/fasteners) rather than PLA, which permanently relaxes open over weeks at room temperature.
3. **Abrasion Resistance & Slipperiness**:
   - **Sliding Jigs, Miter Runners & Guides**: Avoid unlubricated **PETG-on-PETG** sliding pairs (tacky galling); choose **`ASA`** or **`CPE`** (or cool, low-speed `Tough PLA`) with `0.20–0.30 mm` slip clearance, and orient **print layer lines parallel to travel direction** to minimize stair-step drag and wear.
   - **High-Wear / Low-Friction Surfaces**: For heavy rubbing against wood, aluminum, or dust (router sleds, fence runners), design recessed relief (`0.13–0.25 mm`) for self-adhesive **UHMW/PTFE slick tape** rather than relying on bare filament.
   - **Abrasive Chafe & Non-Slip Contact**: Use **`TPU`** sleeves/grommets where cables rub continuously (highest abrasion resistance) and recessed **`TPU`** pads (`1.0–1.5 mm` pocket, `15–25%` max crush) where high friction/non-marring grip is wanted.

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

### Fastener Selection & Hole Wizard Rules
- **Prefer Plentiful Stock**: `M2` (SHCS), `M3` (SHCS + hex/nylock nuts), `M4` (Hex head bolts or SHCS + hex/nylock nuts), `M6` (Hex head + hex nuts), and `1/4"-20` (Hex head/SHCS + hex nuts).
- **Decorative Brass Hardware**: `M2` and `M2.5` flat-head hex-drive brass screws (`90°` countersink).
- **Avoid Unless Required**: `#8-32` and `M5` (limited stock).
- **Mandatory Hole Wizard (`sw_hole_preset` / `HoleWizard5`)**: Never cut screw/bolt holes as raw plain circles.
  - **Through Clearance & Counterbores/Countersinks**: Use `sw_hole_preset(preset_type="clearance"|"counterbore"|"countersink", size=..., fit="close"|"normal"|"loose", hardware_type="shcs"|"bhcs"|"hex_bolt"|"pan"|"flat_head_90"|"flat_head_82")`. Since `M4`, `M6`, and `1/4"-20` are often hex-head bolts, set `hardware_type="hex_bolt", wrench_clearance=true` (or `cbore_diameter` override) for socket-wrench wall clearance, or design captive hex nut traps (`M3 = 5.75 mm`, `M4 = 7.30 mm`, `M6 = 10.35 mm`, `1/4"-20 = 11.45 mm` flat-to-flat).
  - **3D-Printed Self-Threading Plastic Taps (`preset_type="3dp_self_thread"`)**: When a small screw threads directly into printed Tough PLA / PETG / ASA without heat-set inserts, use `sw_hole_preset(preset_type="3dp_self_thread", size="M3", fit="tight"|"normal"|"loose", ...)` — it sizes the plastic tap drill accurately (`M2.5 -> 2.05 mm`, `M3 -> 2.50 mm`, `M4 -> 3.30 mm`), enables a `90°` near-side entry lead-in countersink (`NearSideCounterSink=true`) so perimeters don't delaminate on start, and constrains the Hole Wizard Location Sub-Sketch (`auto_fully_define=true`).

### Adhesives & Joint Clearances
- **Press-Fit**: `0.10 mm` clearance per side.
- **Slip-Fit / Sliding**: `0.20 mm` clearance per side (`0.40 mm` for print-in-place hinges).
- **`30-Minute 2-Part Epoxy`**: `0.10–0.20 mm` bond-line gap + internal glue relief grooves.
- **`Thin & Gel CA` + `Loctite 770` Primer**: `0.05 mm` gap (Thin CA wicks) or `0.10 mm` (Gel CA); note Loctite 770 compatibility for slick/polyolefin interfaces.

---

## 3. Build Orientation, Anisotropic Load Strategy, Overhangs & Add-in Features

- **Bed Adhesion & Bottom Edges**: Never put rounded fillets on edges touching the build plate; use **`45°` chamfers** (`0.6–1.5 mm`).
- **Overhang Limit**: Keep unsupported overhangs $\le 45^\circ$ from vertical.
- **Anisotropic Strength & Part-Specific Orientation Strategy**:
  - **XY-Plane Preference for Arms, Clips, Flexures & Loaded Features**: Preferentially orient any cantilevered arm, snap clip, living hinge/flexure, hook, or load-bearing feature in the **XY print plane** so continuous perimeter extrusions take the bending, flexing, or tensile stress instead of fracturing along Z layer lines.
  - **Multi-Piece Dovetail Splitting for Orthogonal XY Strength**: When a part has arms, clips, or loaded features in orthogonal directions that cannot all lie in the XY plane at once, split the design into **smaller sub-pieces that each print flat in their own XY plane and dovetail together** (with `0.10 mm` press-fit or `0.10–0.20 mm` adhesive/fastener joints) to preserve full XY perimeter strength in every critical member.
  - **`45°` Angled Printing for Multi-Directional Reinforcement**: For monolithic parts subjected to loads across multiple axes simultaneously, consider designing the part with a dedicated **`45°` build-plate facet/chamfer** to print at a **`45°` angle**, reinforcing multiple load directions at once across shared layer lines (and pass the tilted `build_direction` vector to `sw_audit_dfm`).
  - **Part-Specific Tradeoff Evaluation**: Because orientation is highly part-specific, explicitly evaluate single-piece XY orientation vs. multi-piece dovetailed XY sub-components vs. `45°` angled printing based on the part's primary load paths, flex cycle life, and support-free geometry before committing sketch datums.
- **Horizontal Holes & Downward Counterbores (Headless Add-in Automation)**:
  - Apply **`40°` capped teardrops** (`0.6 mm` max cap) to horizontal circular bores and **multi-tier crisscross bridges** (`0.20 mm` layer step) to downward-facing counterbores/hex nut traps headlessly via **`sw_invoke_repo_addin_tool`** OR directly inside **`sw_build_part_features`**:
    - `sw_invoke_repo_addin_tool(tool="3dprint_teardrop", bed_plane_or_face="Top Plane", build_direction=[0,0,1], face_tokens=[...], overhang_angle_deg=40, max_cap_height_mm=0.6)` *(omit `face_tokens` to auto-scan qualifying `40°–90°` circular holes)*
    - `sw_invoke_repo_addin_tool(tool="3dprint_bridge", bed_plane_or_face="Top Plane", build_direction=[0,0,1], face_tokens=[...], layer_height_mm=0.20)` *(omit `face_tokens` to auto-scan qualifying downward recesses with inner holes)*
    - Or inside `sw_build_part_features`: include `{op: "teardrop", build_direction: [0,0,1]}` and `{op: "crisscross_bridge", build_direction: [0,0,1], layer_height_mm: 0.20}`.
  - When `SolidWorks3DPrintDFMAddin` is loaded in SOLIDWORKS, this invokes `InvokeDfmHeadless` via `GetAddInObject` to create genuine editable **Teardrop Fillet** / **Crisscross Bridge** MacroFeatures with nested subfeatures and **Edit Feature** PropertyManager support; if the add-in is unloaded or stale, it automatically falls back in-process (`allowMacroFeature: false`) to create the exact same native SOLIDWORKS sketches/cuts with zero UI stalls.

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
2. **Snap-In C-Barrels (100% Support-Free & Flex-Safe)**:
   - Size inner bore ID to **`cable_OD + 0.30 mm`** and barrel wall thickness **`R_out - R_in >= 1.60 mm`** (`>= 4` perimeters at `0.4 mm` nozzle).
   - Cap C-barrel open entry mouths at **`<= 35°` above horizontal** so overhanging lips print crisply with zero supports while providing positive snap retention.
   - When a snap-in C-barrel clip or cantilevered saddle arm experiences significant flexing or opening stress, orient the C-clip profile in the **XY plane** (or **dovetail** an XY-printed clip into the mounting base) so flexing doesn't split `Z` layer lines.
3. **Printable `42°` Gable-Roof Zip-Tie Retention Tunnels**:
   - Route zip-tie slots under each barrel using a **`42°` gable-roof crown** (`<= 45°` print overhang limit) with **`>= 1.60 mm` solid bed floor** below the tunnel and **`>= 1.60 mm` solid crown web** between the gable apex and barrel bore.
   - Model zip ties in CAD as tangent **D-loops** matching the flat tunnel bottom + rounded crown—never circular rings, which falsely interfere with rectangular pedestal corners.
4. **Rolling U-Loops & Multi-Segment Solid Clearance**:
   - Keep `120V` AC cords and low-voltage stepper/endstop runs in separate parallel barrels (**`>= 20.0 mm` center-to-center**).
   - Size rolling U-loop bend radii **`R >= 3 × OD`** for split loom and **`R >= 4 × OD`** for jacketed rubber AC cord (`R > D/2` hard geometric minimum), with opposing saddle barrel Z-offsets separated by exactly **`ΔZ = 2R`**.
   - Leave **`0.5–2.0 mm` axial butt-gaps** where carriage-mounted wire sweeps enter saddle-clamped U-loops so subset interference checks report `0.000 mm³` collision volume, and temporarily suppress static-pose U-loops during multi-stop stroke sweeps (`sw_verify_motion_stroke`).

---

## 6. Mandatory Pre-Save Audit & Export Gate

Run `sw_audit_dfm(process="3d_printing")` (verifies build volume, unsupported `> 45°` overhangs, bed chamfers vs rounded fillets, and pure-C# `IFace2.GetTessTriangles` `< 1.2 mm` thin-web mesh raycast checks with zero `RayIntersections` COM faults), verify `sw_capture_viewport_quad`, and save `.SLDPRT` / `.SLDASM`. **Never export `.3MF` or `.STL` automatically unless the user explicitly asks to print or slice the part.**
