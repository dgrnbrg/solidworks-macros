---
name: dfm-woodworking
description: >-
  Use this skill when designing, modifying, or auditing custom furniture,
  cabinetry, curved vacuum-press laminations, hardwood/plywood jigs, Blum
  hinge/slide casework, furniture electrical channels, or clamp-staged glue-ups
  for your professional woodworking shop in IPS.
---

# Design for Custom Furniture & Power-Tool Woodworking (`IPS`)

This is a **Tier 1 (Preferred In-House)** process in `shop_profile.json`. Always configure SOLIDWORKS woodworking and furniture assemblies in **`IPS`** (`inch, pound, second`) and store real stock thicknesses + glue clearances in a shared external **`equations.txt`**.

---

## 1. Real Stock Inventory & Shared `equations.txt` Variables

Never model nominal sheet-good sizes as exact integers:
- **Plywood Actual Thicknesses** (link in `equations.txt`):
  - `Ply_1_4 = 0.218in` (`~5.5 mm`)
  - `Ply_1_2 = 0.468in` (`~12 mm`)
  - `Ply_3_4 = 0.718in` (or `18mm` = `0.709in` for Baltic birch — specify which sheet stock)
  - `Glue_Clear = 0.008in` (add `+0.005"` to `+0.010"` clearance to all dadoes, grooves, rabbets, and mortises for glue film and minor thickness variation).
  - Scraps of **MDF** and **Melamine** available for templates, cauls, and shop fixtures.
- **Hardwood Stock (Often Smaller Pieces / Billets)**:
  - Current species: **`Bubinga`**, **`Ash`**, **`Walnut`**, **`Sapele`**, **`Morado`**.
  - Design panels, frames, and laminated parts keeping shorter/narrower billet availability in mind; design glue-ups and decorative inlays/accents to exploit high-value figured offcuts cleanly.
- **Specialty Rigid / Transparent Stock**:
  - **`1/2" (0.500")` Corian + MMA Glue**: Use for high-rigidity, warp-proof router templates, vacuum-impermeable CNC pods, and precision miter/drill jigs.
  - **`4"` Thick Acrylic (2 large chunks)** & **`1/8"` Acrylic (2 small sheets)**.

---

## 2. Stationary Machine Envelope & Process Routing

Design parts so every milling, cutting, and thicknessing step matches your exact equipment:

| Machine | Key Spec / Limit | DFM Design Rule |
| :--- | :--- | :--- |
| **8" Jointer (`1.5 HP`)** | Max direct face-joint width: **`8.0"`** | Keep single-board glue-up staves $\le 7.5^{\prime\prime}$ wide before final panel glue-up, or flatten wider slabs via planer sled / CNC `Freud 16-108` / drum sander. |
| **12" Planer** | Max width: **`12.0"`** | Pre-dimension individual sub-panels $\le 11.75^{\prime\prime}$ before final gluing if machine-planing; wider panels go to the drum sander. |
| **Grizzly G1066Z Dual Drum Sander (`5 HP`)** | Max width: **`24.0"`** | Ideal for flattening `12–24"` wide glued panels, shop-sawn veneers, and figured tropical hardwoods (**Bubinga, Sapele, Morado**) without planer tearout. |
| **52" 3HP SawStop + Sliding Table** | Full kerf **`0.125"`**, thin kerf **`0.094"`**, `8"` dado stack | Specify exact blade kerf in `sw_generate_cutlist_and_templates`. Exploit the sliding table for dead-square crosscuts and wide panel tenons/miters. |
| **10" Radial Arm Saw** | Crosscuts & joinery | Ideal for repeatable stop-block crosscuts, half-laps, and trenching dadoes across narrow rails. |
| **18" 3HP Bandsaw** | Resawing & curves | Resaw thick Bubinga/Walnut/Sapele/Morado billets for bookmatched panels and shop-sawn veneers; rough out curved legs before flush-trim routing. |
| **12" Sliding Miter Saw** | Miter & bevel crosscuts | Standard compound trim & case molding cuts. |
| **Sanders (`5"` ROS, `6"` geared ROS, `10"` disc / `2"` belt)** | Finish prep & bevel shaping | Leave `1/32"` proud flush-trim margin where appropriate for final edge flush sanding. |

---

## 3. Joinery, Routing, Forstner Bits & Hardware Standards

### Joinery Systems
- **Festool Domino `DF 500` & `DF 700 XL`**:
  - You own **all cutter sizes (`4, 5, 6, 8, 10, 12, 14 mm`)**, but **`4 mm`, `5 mm`, and `8 mm` Dominos** are most heavily stocked and should be preferred by default unless heavy leg-apron structural loads require `10/12/14 mm` `DF 700` tenons.
  - Set `sw_hole_preset(preset_type="domino_mortise")` mortise depths `1–2 mm` deeper than half the tenon length for glue relief; use tight width on one side and medium width on mating side for self-alignment without binding.
- **Wooden Dowels & Pocket Hole Jig**: Reserve pocket holes for hidden utility cabinets, face frames, or shop jigs; use Dominos, loose tenons, or dowels for fine show furniture.
- **Router Table & Plunge Routers**:
  - **Guide-Bushing Offset Math**: When exporting 1:1 router templates via `sw_generate_cutlist_and_templates`, apply:
    $$\text{Template Offset} = \frac{OD_{\text{bushing}} - D_{\text{bit}}}{2}$$
  - **Bearing Flush-Trim (Top & Bottom)**: Keep template thickness $\ge 1/4^{\prime\prime}$ (`1/2"` MDF or `1/2"` Corian preferred) so bearing rides cleanly without tipping.
  - **Standard Edge Profiles**: Specify **`1/4"` (`0.250"`)** or **`1/2"` (`0.500"`)** roundovers (your stocked router roundover bits) rather than odd decimal radii like `0.187"` or `0.375"` unless CNC ballnose-carved.
- **Metric Forstner Bits (`10 mm` to `30 mm`)**:
  - Call out all wood counterbores, flush plug pockets, and circular hardware recesses in **metric diameters between `10 mm` and `30 mm`** (plus `35 mm` for Blum hinge cups).

### Furniture & Jig Hardware (`shop_profile.json`)
- **Blum Soft-Close Hinges (`CLIP top BLUMOTION` & `COMPACT BLUMOTION`)**:
  - Cup bore: **`Ø35.0 mm × 13.0 mm` deep**, typical `3–6 mm` boring distance (`5 mm` default) from door edge, **`45.0 mm`** mounting screw centers.
- **Blum Undermount Drawer Slides (`TANDEM` & `MOVENTO`)**:
  - Account for **`1/2"` (`0.500"`) to `5/8"` (`0.625"`)** drawer box side thickness, **`1/2"` (`0.500"`)** bottom recess, **`Ø6.0 mm × 10 mm`** rear hook hole, **`Ø2.5 mm`** locking-device pilots (different vertical offsets between `TANDEM` vs `MOVENTO`—state chosen line explicitly!), and **`50 mm × 13 mm`** rear notch (`125 mm` wide for `TIP-ON BLUMOTION`).
- **Linear & Small Stock Hardware**:
  - Long stock of **cuttable piano hinge** and **cuttable aluminum finger/edge pull** (design continuous door/lid pulls or hinges and report exact cut lengths in the BOM).
  - Assorted **small wood hinges**, **`1/4"-20` insert T-nuts** (default jig fastener), and **standard toggle clamps**.

---

## 4. Wood Movement, Vacuum Press Forms & Electrical Integration

- **Solid Wood Seasonal Movement**: Never cross-grain screw or rigidly glue solid wood tops (`Bubinga`, `Ash`, `Walnut`, `Sapele`, `Morado`) across grain width; model **elongated figure-8 / Z-clip slots**, **floating panel grooves** (`+1/8"` expansion space + space balls), or **pinned & slotted breadboard tenons**.
- **Vacuum-Press Bent Lamination (`sw_create_bending_form`)**:
  - Generate ribbed `3/4"` plywood/MDF male/female bending forms or curved cauls; offset matching faces by `(N_plies × ply_thickness) + breather_allowance`, adding CNC `3/8"` alignment pin holes so forms lock cleanly inside the vacuum bag.
- **Furniture Electrical & LED Integration (`IPS`)**:
  - Model NEMA 1-gang (`2.250"×3.625"`) / 2-gang (`4.000"×3.625"`), IEC C14 power inlets, and dadoed LED extrusion channels + rear wire chases with removable service covers (`1/4"-20` T-nuts or decorative brass screws).

---

## 5. Clamp-Count Glue-Up Staging & Printable Shop Drawings

1. **Clamp Inventory Limit**: You own **`4` Parallel Bar Clamps**, **`6` Pipe Clamps**, **`40–50` F-Clamps**, and **`10–15` C-Clamps**.
   - Never require a single glue-up stage that demands more than **`4` parallel + `6` pipe clamps** simultaneously.
   - Break large cabinets, frame-and-panel assemblies, or multi-ribbed lamination forms into explicit **Stage 1 / Stage 2 / Stage 3 sub-assembly glue-ups**, using F-clamps/C-clamps + cauls for face-frame/edge-trim stages and Domino self-squaring alignment to preserve bar clamps.
2. **Deliverables**: Always run `sw_audit_dfm(process="woodworking")`, generate kerf-aware cutlists & router templates (`sw_generate_cutlist_and_templates`), and produce a printable **`8.5"×11"` or `11"×17"` Workshop Fabrication Sheet PDF** (`sw_create_fabrication_drawing(mode="workshop_sheet")`) showing exploded views, Domino/Forstner/Blum callouts, cut lengths, and clamp stages.
