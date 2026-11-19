# SOLIDWORKS CAD, Fixture & Shop DFM Rules

Follow these engineering defaults whenever creating, inspecting, refactoring, or auditing SOLIDWORKS models in this shop:

## 1. Domain Unit System Discipline (Mandatory)
Always configure and verify the SOLIDWORKS document unit system before sketching or dimensioning:
- **`MMGS` (millimeter, gram, second)** for:
  - All **3D-printed parts** (including airflow/dust/air-blast lofts and nozzles),
  - All **Gridfinity** bins, baseplates, and toolholders,
  - All **KiCad PCB enclosures**, light pipes, and SMD reflow/soldering/pogo fixtures.
- **`IPS` (inch, pound, second)** for:
  - All **woodworking** parts and custom furniture assemblies,
  - All **CNC furniture parts & shop jigs/fixtures** (including `1/2"` Corian vacuum pods and QCW dowel fixtures),
  - All **residential / shop electrical cutouts** integrated into woodwork,
  - **SendCutSend** imperial gauge parts.
- *Internal API reminder*: SOLIDWORKS COM calls always accept lengths in **meters** and angles in **radians**; convert explicitly and never mix display units blindly.

## 2. Manufacturing Preference & Cost/Lead-Time Hierarchy
Default to fast, low-cost in-house fabrication before considering external vendors:
1. **Tier 1 (Preferred — Fast, Low-Cost In-House)**: Power-tool woodworking, 3D printing (`MatterHackers Tough PLA Basic` on Prusa MK3.5S+ w/ `0.4 mm` nozzle), and CNC carving (`Onefinity Elite Journeyman Gen 2`).
2. **Tier 2 (Specialty / Higher-Cost In-House)**: `1/2" Corian` + `MMA structural glue` (for extreme rigidity or vacuum-impermeable CNC pods/jigs), `MatterHackers Tough PLA Pro` (only when impact/mechanical stress justifies extra cost), `4"` / `1/8"` acrylic, and secondary filaments (`PLA` -> `PETG`/`Transparent PETG` -> `PVB`/`CPE`/`ASA`/`TPU` -> last-resort `Prusament PC`).
3. **Tier 3 (Deprioritized External Vendor — Expensive & Slow)**: **SendCutSend** laser cutting and sheet metal. Use ONLY when metal stiffness, heat/wear resistance, thin-wall structural profiles, or brass/copper/stainless hardware aesthetics are strictly required. Minimize costly vendor secondary operations (bending, countersinking, tapping, PEM insertion) when a flat plate or in-house Portaband/Angle Grinder/countersink work suffices.
4. **Tier 4 (Rare Last Resort)**: External CNC machine shop (supported with formal 2D drawings & GD&T callouts).

## 3. Multimodal Design Intent & Visual Self-Verification
- When given a **photo or hand sketch**, use multimodal reasoning to infer true engineering intent (orthogonality, symmetry, load paths, clearances, mounting datums, airflow continuity) rather than blindly tracing distorted perspective pixels. Use `sw_insert_sketch_picture` only when tracing a literal 2D organic contour.
- After creating or significantly modifying 3D geometry, **always call `sw_capture_viewport_quad`** and visually inspect the 4-up composite image to catch flipped extrudes, intersecting flanges, twisted lofts, or misaligned hole patterns before presenting results.

## 4. Shared Parameters, Hardware BOMs & DFM Gates
- Define common stock thicknesses and `material_thickness + tolerance` derivations in an external linked **`equations.txt`** file so parts across an assembly stay synchronized.
- For hardware assemblies, inspect coaxial hole stacks with `sw_inspect_assembly_joints` (defaulting to `"18-8 Stainless Steel"` unless another material is requested, and choosing the appropriate `nut_style`: `"nylock"`, `"hex_nut"`, or `"jam_nut_stack"`), batch-download & verify genuine McMaster-Carr `.SLDPRT` geometry via `sw_search_and_download_mcmaster` (using the installed McMaster-Carr SOLIDWORKS Add-in bridge), ensure each part has a `"Fastener_Axis"` reference axis, and populate/mate (`Concentric` + `Coincident`, unfixed, unlocked rotation unless hex-flat clocking is required) in `"McMaster Hardware"` via **`sw_install_fastener_stack`** (for complete multi-joint stacks) or `sw_populate_mcmaster_hardware` with `<PartNumber>_<Description>.SLDPRT` naming (`^\d{4,5}[a|k|n|t]\d{1,3}_`). Never synthesize placeholder hardware geometry (`allow_placeholder_generation=false`) without explicit user approval.
- Verify assembled hardware with `sw_measure_clearance` (checking `unexpected_collision_count == 0` while separating expected thread/tap engagement overlaps).
- Before declaring a design manufacturing-ready, read the relevant domain DFM skill (`dfm-3d-printing`, `dfm-cnc-carving`, `dfm-woodworking`, or `dfm-sheet-metal-sendcutsend`) and check against `.agents/plugins/solidworks/data/shop_profile.json` via `sw_audit_dfm`.
