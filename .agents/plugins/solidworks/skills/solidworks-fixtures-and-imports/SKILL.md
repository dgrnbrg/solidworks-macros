---
name: solidworks-fixtures-and-imports
description: >-
  Use this skill when inspecting imported STEP, SLDPRT, SLDASM, or KiCad PCB
  files, designing form-fitting fixtures, 1/2" Corian MMA vacuum pods, QCW dowel
  jigs, KiCad electronics enclosures and reflow/soldering fixtures, residential or
  furniture electrical cutouts, Gridfinity shadow toolholders, or populating
  McMaster-Carr fasteners for live BOMs.
---

# SOLIDWORKS Fixtures, Imports, Electronics & Hardware BOMs

## 1. Automated B-Rep Extraction & Assembly Joint-Stack Inspection (`.STEP` / `.SLDPRT` / `.SLDASM`)

Imported `.STEP` files have no parametric feature tree, and multi-part assemblies need global joint-stack analysis across mating faces. Never guess mounting dimensions or bolt lengths by eye alone:
1. Open/import via `sw_open_or_import_document`.
2. For single `.SLDPRT` / `.STEP` parts, run `sw_inspect_topology_datums(extract_hole_patterns=true, extract_planar_datums=true, pcb_mode=...)` to extract deterministic face/edge tokens, planar datum normals/bounds, coaxial hole depths, and coplanar bolt-hole patterns.
3. For `.SLDASM` assemblies, run **`sw_inspect_assembly_joints`** (with optional `nut_style`: `"nylock"`, `"hex_nut"`, or `"jam_nut_stack"`, `material`: defaulting to `"18-8 Stainless Steel"`, `top_level_only`: default `true`, `include_subassembly_boundary_joints`: default `true`, and optional `include_components` subset filter) to pair coaxial holes across mating components and subassembly boundaries into joint stacks (`already_fastened` vs `unfastened_count`). It automatically detects **tapped receivers** vs **through-bolted** joints, **countersunk (CSK)** cones (auto-selecting Flat Head CSK screws and suppressing washers), **counterbores (CBore)** (offsetting bolt seating to the CBore floor and omitting washers when `washer_OD > cbore_ID`), **radial boss/wall clearance** (computing `recommended_clock_angle_deg` for hex-flat boss clocking), and **bidirectional axial clearances** (auto-recommending low-profile Button Head `BHCS` screws when a moving part clears close above the bolt head).
   - *Fast B-Rep transform tip*: Use **`sw_query_assembly_transforms`** for multi-frame transforms across nested subassemblies (`router carriage-1/rf1101 speed controller-1/cable-1`), or when writing custom `sw_execute_csharp` assembly scans read `double[] m = comp.Transform2.ArrayData as double[]` once per component (`gx = (lx*m[0] + ly*m[3] + lz*m[6])*s + m[9]`), check `surf.IsPlane()`/`surf.IsCylinder()`/`surf.IsCone()` before `face.GetBox()`, and skip helical lead/ball-screw faces.
4. For assembly interference verification, always scope **`sw_measure_clearance(include_components=[...], exclude_same_subassembly_pairs=true, group_by_pair=true)`** (plus optional `ignore_pairs` for actuated switch feelers/known contacts; unfiltered all-to-all checks are disabled to avoid `>3m0s` timeouts). It automatically skips internal component pairs within the same subassembly when checking across >=2 top-level components/subassemblies, and separates `expected_thread_engagement` from `unexpected_collision`.

---

## 2. Workholding & CNC / Router Fixture Design (`IPS`)

Follow the Tier 1–2 manufacturing preference hierarchy:
- **Extreme Rigidity or 100% Vacuum-Impermeable Fixtures (`1/2"` Corian + MMA Glue)**:
  - Use **`1/2" (0.500")` Corian bonded with MMA structural glue** when building vacuum pods, suction manifolds, precision zero-play drill jigs, or moisture-immune router templates.
  - Tap **`1/8" NPT` (`27 TPI`)** vacuum ports (`0.332"` / `11/32"` tap drill, `>= 0.625"` boss wall thickness) directly into the Corian fixture (`sw_hole_preset(preset_type="npt_1_8_vac_tap")`).
  - Route continuous O-ring / closed-cell foam gasket channels (`1/4" Bits&Bits Downcut` or `1/4" Amana 51402-Z` w/ air assist) on the pod top face.
- **Sacrificial or Mechanical Wood/MDF/Acrylic Jigs**:
  - Use `3/4"` (`0.718"`) / `1/2"` (`0.468"`) / `1/4"` (`0.218"`) plywood, MDF/melamine scraps, or `4"` / `1/8"` acrylic when vacuum impermeability isn't required.
- **QCW Registration & Standard Jig Hardware**:
  - Onefinity QCW slats have `3/8" (0.375")` dowel holes on a `2.000"` grid **independently per slat (~`9"–10"` wide × `42"` long)**. Always keep paired registration dowel holes within a single slat (`<= 8.0"` cross-slat span).
  - Use stocked **`1/4"-20` insert T-nuts** (`0.312"–0.328"` bore + counterbore as needed) and **standard toggle clamps**, checking clamp arm swing and spindle/collet Z-clearance via subset `sw_measure_clearance(include_components=[...])`.
- **Boolean Cavity / Form-Fitting Nests (`sw_boolean_or_fixture_cavity`)**:
  - Use `operation="indent"` (`clearance_offset = 0.008"–0.015"` for CNC nests, `0.20–0.40 mm` for 3D-printed nests) or `operation="subtract"` (`SWBODYCUT`) around imported `.STEP` bodies, remembering to relieve internal vertical corners to $R \ge 0.135^{\prime\prime}$ (`1/4"` endmills) for CNC fixtures.
- **Auto-Engraved Jig Labels (`sw_engrave_or_emboss_label`)**:
  - Always label router templates and CNC fixtures with bit size, guide-bushing offset, dowel grid orientation, and fence side:
    - **Wood / Plywood**: Use the **`3/8"` `90°` Cadence Mfg GrooVee Jenny** down-shear bit for clean, tearout-free V-grooved text.
    - **Corian / Acrylic**: Use the **`30° 0.2 mm` V-engraving bit**.
    - **3D Prints**: Deboss/emboss `0.6–0.8 mm` into top/side walls.

---

## 3. KiCad PCB Enclosures & Reflow / Soldering / Rework Fixtures (`MMGS`)

When the user supplies a KiCad `.STEP` PCB export (always design enclosures and bench fixtures in **`MMGS`**):
1. Run `sw_inspect_topology_datums(pcb_mode=true)` to extract:
   - Board outline (`X × Y`) and FR4 thickness (typically `1.60 mm`),
   - Mounting hole pattern (`M2`, `#2-56`, `M3` locations and pad keep-out radii),
   - Tallest component height & edge connectors (USB-C, JST, barrel jacks, headers, SD slots, LEDs, switches).
2. **Enclosure Design Rules (`MatterHackers Tough PLA Basic`, `0.4 mm` nozzle)**:
   - Perimeter XY PCB clearance: `0.35 mm` per side; connector bezel window clearance: `0.40 mm` per side; vertical tall-component clearance: `>= 1.00 mm`.
   - Standoff bosses sized to exact stocked McMaster inserts (`94459A110` M2 -> `6.4 mm` boss OD; `93365A112` #2-56 -> `6.0 mm` boss OD; `94180A333` M3 -> `8.5 mm` boss OD).
   - Add **`Transparent PETG` light pipes** (`>= 2.0 mm` dia, press-fit `0.10 mm`) over SMD status LEDs and `TPU` gaskets/feet when sealing/damping is needed.
3. **PCB Soldering, SMD Reflow & Rework Fixtures**:
   - Design board-holding jigs for your **reflow oven**, **soldering iron**, **hot air gun**, and **pogo-pin test harnesses**:
     - Leave generous thermal & tool-access clearance around SMD pads and hot-air rework zones.
     - For reflow-oven carriers or hot-air shields, avoid low-temp PLA near direct heat—use `PETG`/`ASA` (or `Prusament PC` last resort) for moderate heat, or CNC-cut `1/2"` Corian/MDF trays for insulated workholding (`<= 120°C` hand-soldering jigs).
     - Include recessed pockets or slots for solder wick, flux syringe rests, or toggle-clamp hold-downs on pogo fixtures.

---

## 4. Residential & Furniture-Integrated Electrical Design (`IPS`)

For custom furniture and shop equipment incorporating electrical wiring:
- **Unit System**: Model wood/cabinet electrical cutouts in **`IPS`**.
- **Standard Cutouts (`shop_profile.json`)**:
  - **NEMA 1-Gang Plastic Old-Work Box**: `2.250" W × 3.625" H` (CNC internal corner radius `R0.140"` or squared with chisel/corner jig).
  - **NEMA 2-Gang Plastic Old-Work Box**: `4.000" W × 3.625" H`.
  - **NEMA Metal Old-Work Box**: `1.940" W × 3.060" H` (1-gang) + ear clearance notches.
  - **IEC C14 Switched Inlet**: `1.102" × 0.787"` (`28.0 × 20.0 mm`) snap-in (for `0.8–2.0 mm` plates/3D-printed bezels) or `40 mm` M3 screw-flange mount when recessing into thicker `3/4"` wood panels.
- **LED Channels & Wire Chases**:
  - Design dadoed aluminum LED extrusion channels (`Bits&Bits 1/4" Downcut` or table-saw dado) and hidden wire chases with strain-relief grommets (`TPU` or `Tough PLA`) and accessible removable service panels fastened with `1/4"-20` T-nuts or brass screws.

---

## 5. Gridfinity Custom Shadow Toolholders (`MMGS`)

Use `sw_create_gridfinity` with `hollow_interior=false` + `sw_boolean_or_fixture_cavity` (or traced tool contours from `sw_insert_sketch_picture`) in **`MMGS`**:
- Subtract tool profiles with `0.40 mm` per-side clearance.
- Add `R18.0 mm` finger-grab relief cutouts perpendicular to long narrow tools so fingers can lift tools effortlessly.
- Add debossed text labels (`>= 0.6 mm` stroke width for `0.4 mm` nozzle) identifying bit/wrench/insert sizes.

---

## 6. McMaster-Carr SOLIDWORKS Add-In Fastener, Axis Prep & Assembly Component Management

Your installed `McMaster-Carr SolidWorks Add-in` (`McMaster.SolidWorks.CustomerAddIn.dll`) scans assembly components and automatically builds an orderable BOM when file names, component names, or configurations match `^\d{4,5}[a|k|n|t]\d{1,3}_`.

Use this streamlined 4-step workflow on hardware assemblies (plus in-context Hole Wizard alignment for custom brackets & adapter plates):
1. **Joint Analysis (`sw_inspect_assembly_joints`)**: Inspect coaxial stacks across parts and subassembly boundaries using the appropriate `nut_style` (`"nylock"`, `"hex_nut"`, or `"jam_nut_stack"`) and `material` (default `"18-8 Stainless Steel"`), then confirm BOM consolidation and edge cases with the user:
   - **SKU Consolidation**: Merge close grip lengths sharing the same thread size when extra protrusion clears downstream obstacles (e.g., standardizing `22.7 mm` and `26.1 mm` M8 joints on `M8×40 mm`).
   - **Hole Wizard (`sw_hole_preset`) & Self-Threading 3D-Printed Tap Holes**: Model all mounting/fastener holes using **Hole Wizard (`HoleWizard5` / `sw_hole_preset`)** with explicit `fit` (`"close"`, `"normal"`, `"loose"`) and `hardware_type` (`"shcs"`, `"bhcs"`, `"hex_bolt"`, `"pan"`, `"flat_head_90"`, `"flat_head_82"`). For small blind holes in 3D-printed brackets (`~Ø2.05 mm` for M2.5, `~Ø2.50 mm` for M3) with no heat-set insert, use **`preset_type="3dp_self_thread"`** (sized for `1.5×–2.0×D` thread cutting with a `90°` entry countersink and `>= 0.8–1.0 mm` bottom clearance rather than through-nuts).
   - **In-Context Custom Brackets (`->`) vs. COTS Boundary**: On **custom brackets, plates, clips, and cable saddles**, constrain Hole Wizard placement points (`in_context_concentric_targets` / `SwHelper.EditHoleWizardLocations`) or sketch outlines (`SwHelper.BeginEditPartInAssembly` -> `AddInContextRelation` -> `EndEditPartInAssembly`) directly to mating COTS holes/edges so moving a motor, rail, or switch via assembly mates automatically shifts the bracket holes on rebuild. **Never** add in-context `->` references to COTS parts themselves (motors, rails, switches, McMaster hardware, `.STEP` imports).
2. **Batch Download & Geometry Verification (`sw_search_and_download_mcmaster`)**: Pass `mcmaster_pns` or `specs: [{mcmaster_pn, expected_length_mm, expected_kind}]` to resolve/download genuine `.SLDPRT` files through the McMaster-Carr SOLIDWORKS Add-in bridge into `<assembly_dir>/Hardware`. Automatically opens each part silently, canonicalizes its axis/bearing face/thickness, creates **`"Fastener_Axis"`** if missing, and checks measured shank length & part kind against the requested spec (always trust measured `.SLDPRT` B-Rep geometry over catalog tables).
3. **One-Shot Batched Stack Installation (`sw_install_fastener_stack`)** *(or batched `sw_populate_mcmaster_hardware(items=[...])`)*:
   - **Never synthesize unapproved fake hardware**: placeholder generation (`allow_placeholder_generation=false`) is disabled unless explicitly approved by the user.
   - `sw_install_fastener_stack` accepts all joint stacks in one batched call (`EnableFeatureTree=false`, never setting `CommandInProgress=true`), auto-reads each part's measured axis/bearing offset/thickness, inserts, unfixes every component (`Fixed=0`), creates **`Concentric` (`"Fastener_Axis"`) + `Coincident` mates** (leaving Concentric rotation **unlocked** unless hex-flat clocking is required; checking `swAddMateError_e.swAddMateError_NoError == 1`, never `== 0`), and groups all hardware into **`"McMaster Hardware"`**.
4. **Verification, Mate Health Gate, Visual Check & Save**: Run **`sw_audit_assembly_mates(include_subassemblies=true)`** (intermediate edits automatically attach `<5ms` top-level `mate_health`) to guarantee **zero yellow (warning) and zero red (error) mates** across the assembly and loaded subassemblies, run fast grouped **`sw_measure_clearance(include_components=[...], exclude_same_subassembly_pairs=true, group_by_pair=true)`** (`unexpected_collision_count == 0`), call **`sw_capture_viewport_quad`** once to inspect the 4-up image, and save with **`sw_save_document`** (`save_all_components=true` saves dirty components only; stop before exporting `.STEP`/`.3MF`/`.DXF` unless explicitly asked to fabricate). *(Use `sw_manage_assembly_components` with `pose_and_mate_batch`, `batch`, or `swap_fastener_and_insert_spacer` with geometric `entity_a_spec`/`entity_b_spec` selectors and `@top:` top-level prefixing for atomic single-rebuild edits.)*
