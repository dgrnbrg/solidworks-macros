# End-to-End SOLIDWORKS CAD & Fabrication Workflows

Use these step-by-step tool sequences for common shop projects. Each workflow enforces domain units, shared parameters, 7-view minimum visual verification (`*Isometric`, `*Top`, `*Bottom`, `*Left`, `*Right`, `*Front`, `*Back` via `sw_capture_viewport_quad`), shop DFM gates, and the **Export Gate** (stop at `.SLDPRT`/`.SLDASM` save + `sw_audit_dfm` + `sw_capture_viewport_quad`; export `.3MF`/`.STL`/`.DXF`/`.STEP` manufacturing files **only** when the user explicitly asks to print, CAM, or order parts; `.PDF` drawing/documentation files are exempt from the manufacturing export gate and are automatically exported whenever creating or updating a `.SLDDRW` drawing or assembly guide).

---

## Workflow 1: Photo or Hand Sketch to 3D-Printed Part (`MMGS`)
1. **Reason on Image Intent**: Inspect user's photo/sketch, identify key functional faces, fasteners (`M2/M3/M4/M6/1/4-20` or McMaster heat-set inserts), and target filament (default `MatterHackers Tough PLA Basic`, `0.4 mm` nozzle).
2. `sw_create_document(doc_type="part", domain="3d_printing")` -> ensures **`MMGS`**.
3. `sw_manage_equations(action="link_file", ...)` -> define `Wall_Thick = 1.6mm` (4 perimeters), `Slip_Clear = 0.2mm`, `Boss_OD = 8.5mm`, etc.
4. *(Optional if tracing organic curve)* `sw_insert_sketch_picture(...)` on the primary plane.
5. **`sw_build_part_features`** (`features`: inline `extrude` + `hole_preset` + `fillet`/`chamfer`; `keep_sketches_visible=false`) — or batch `sw_create_2d_sketch(sketches=[...])` -> `sw_extrude_boss_or_cut(operations=[...])` -> `sw_hole_preset` (`mcmaster_insert` for `94459A110`/`93365A112`/`94180A333`/`94180A353`) -> `sw_fillet_or_chamfer` (`45°` bottom chamfers off bed).
6. `sw_capture_viewport_quad` -> visually inspect all 7 standard views (`*Isometric`, `*Top`, `*Bottom`, `*Left`, `*Right`, `*Front`, `*Back`) against original photo/sketch so opposite sides (`Bottom`, `Right`, `Back`) are never missed.
7. `sw_organize_feature_tree` -> `sw_audit_dfm(process="3d_printing")` -> `sw_save_document`. *(Export `.3MF`/`.STL` via `sw_export_document` only when user asks to print.)*

---

## Workflow 2: `.STEP` / `.SLDPRT` Component to CNC / Corian Vacuum Fixture (`IPS`)
1. `sw_open_or_import_document` on target `.STEP` / `.SLDPRT`.
2. `sw_inspect_topology_datums` -> extract planar mounting faces, coaxial holes, bolt-hole patterns, and bounding box.
3. Choose fixture stock:
   - Use **`1/2"` Corian + MMA glue** when extreme rigidity or vacuum-impermeable pods/suction manifolds are needed.
   - Use **`3/4"` (`0.718"`) Plywood / MDF / Hardwood** for sacrificial or mechanical clamp fixtures.
4. `sw_create_document(doc_type="part", domain="cnc_furniture_and_jigs")` -> ensures **`IPS`**.
5. Model fixture block cleanly via **`sw_build_part_features`** (`features`: outline boss, `1/4"` min cutter relief $R \ge 0.135^{\prime\prime}\text{–}0.150^{\prime\prime}$ or dogbones, `hole_preset` for `3/8"` QCW dowel registration pins on `2.000"` single-slat centers, `1/4"-20` T-nuts, toggle clamps, and `1/8" NPT` vacuum port taps).
6. `sw_boolean_or_fixture_cavity(operation="indent" | "subtract", clearance_offset=...)` to carve form-fitting cradle around the imported component.
7. `sw_engrave_or_emboss_label` (`3/8"` `90°` GrooVee Jenny in wood; `30° 0.2mm` bit in Corian/acrylic) -> `sw_capture_viewport_quad` -> `sw_audit_dfm(process="cnc_carving")` -> `sw_save_document`. *(Export `.DXF`/`.STEP` only when user explicitly asks to CAM.)*

---

## Workflow 3: 3D-Printed Airflow, Vacuum & Air-Blast Manifold (`MMGS`)
1. `sw_create_document(doc_type="part", domain="3d_printing")` (`MMGS`).
2. `sw_create_reference_geometry(items=[...])` -> batch offset/angled planes for inlet collar (e.g., `1-7/8"` tapered vacuum port, Festool Cleantec, `1/4"` air quick-connect, or `4"` custom hose `TODO`), transition sections, and outlet nozzle.
3. `sw_create_2d_sketch(sketches=[...])` -> batch loft profiles across planes (using vaulted/teardrop cross-sections on horizontal segments for support-free interior printing) + `sw_create_3d_guide_sketch` if curved centerline is needed ($R_{\text{cl}} \ge 1.5 \times D_h$) — or batch planes & sketches inside `sw_build_part_features`.
4. `sw_create_airflow_loft_or_sweep(sync_connectors=true, start_tangent_type="normal", end_tangent_type="normal", wall_thickness_mm=2.0)`.
5. Verify inlet/outlet area ratio from tool output, ensure created guide sketches are hidden, and check internal overhangs with `sw_capture_viewport_quad` (including a section view via `sw_execute_csharp` if needed).
6. `sw_audit_dfm(process="3d_printing")` -> `sw_save_document`. *(Export `.3MF` only when user asks to print.)*

---

## Workflow 4: KiCad PCB `.STEP` to Enclosure & Soldering/Reflow Fixture (`MMGS`)
1. `sw_open_or_import_document` on KiCad `.STEP` export -> `sw_inspect_topology_datums(pcb_mode=true)` to identify FR4 thickness (`1.6 mm`), mounting hole coordinates, and tall connectors/LEDs.
2. `sw_create_document(doc_type="assembly", domain="electronics_kicad")` (`MMGS`).
3. Build bottom tray + snap/screw lid (`Tough PLA Basic`, `1.6 mm` walls) in 1 call each via **`sw_build_part_features`** with `0.35 mm` XY PCB clearance, McMaster heat-set insert bosses (`94459A110` M2 / `93365A112` #2-56 / `94180A333` M3), connector windows (`0.40 mm` clearance), and `Transparent PETG` light-pipe sleeves (`>= 2.0 mm` dia).
4. Optional: Create SMD reflow/rework carrier (`PETG`/`ASA` or `1/2"` Corian/MDF fixture for CNC) keeping clear of SMD pads, hot-air airflow zones, and pogo-pin test points.
5. `sw_populate_mcmaster_hardware(items=[...])` for screws & heat-set inserts -> `sw_audit_assembly_mates(include_subassemblies=true)` (`clean == true`, zero yellow/red mates) -> `sw_capture_viewport_quad` -> `sw_audit_dfm(process="3d_printing")` -> `sw_save_document`.

---

## Workflow 5: Parametric Gridfinity Bin / Toolholder (`MMGS`)
1. `sw_create_document(doc_type="part", domain="gridfinity")` (`MMGS`).
2. `sw_create_gridfinity(units_x=..., units_y=..., height_u=..., include_stacking_lip=true, magnet_pockets=true, embossed_label=...)`.
3. Verify Prusa MK3.5S+ footprint (`<= 250 × 210 mm`, i.e. max `5U × 5U` single-piece print) via `sw_audit_dfm(process="3d_printing")`.
4. `sw_capture_viewport_quad` -> `sw_save_document`. *(Export `.3MF` only when user asks to print.)*

---

## Workflow 6: Custom Furniture Assembly, BOM & Workshop Documentation (`IPS`)
1. `sw_create_document(doc_type="assembly", domain="woodworking")` (`IPS`).
2. Link shared `equations.txt` (`Ply_3_4 = 0.718in`, `Ply_1_2 = 0.468in`, `Glue_Clear = 0.008in`, `Dado_3_4 = "Ply_3_4" + "Glue_Clear"`).
3. Model joinery & hardware preps via `sw_hole_preset` (`domino_mortise` preferring `4/5/8 mm`, `forstner_metric` `10–30 mm`, `blum_hinge_cup` `35×13 mm`, `blum_slide_prep` for `TANDEM`/`MOVENTO`), plus electrical/LED chases (`NEMA 1/2-gang`, `IEC C14`) if wired furniture.
4. `sw_populate_mcmaster_hardware` (inserts, bolts, `1/4"-20` T-nuts, brass screws) -> `sw_create_exploded_assembly_view(steps=[...])`.
5. `sw_audit_assembly_mates(include_subassemblies=true)` (`clean == true`, zero yellow/red mates) -> `sw_audit_dfm(process="woodworking")` -> `sw_generate_cutlist_and_templates` (kerf-aware cut list & clamp-staging check `<= 4` parallel / `<= 6` pipe clamps) -> `sw_save_document`.
6. *(Only when user asks for printable workshop sheets or CNC templates)* `sw_create_fabrication_drawing(mode="workshop_sheet", sheet_size="tabloid_11x17", include_exploded_view=true, export_pdf_path=...)`.

---

## Workflow 7: Feature Tree Cleanup, Shared Equations & Multibody Split
1. `sw_inspect_feature_tree` + `sw_inspect_model_summary` (record baseline mass/volume/CoG).
2. `sw_refactor_to_shared_equations` -> extract stock thicknesses & clearances into linked `equations.txt`.
3. `sw_organize_feature_tree(rename_semantically=true, reorder_dag=true, group_folders=true, fix_underdefined_sketches=true, verify_mass_equivalence=true)`.
4. *(When requested for multibody parts)* `sw_split_multibody_to_assembly` using `master_derived` (for organic sculpted furniture/boolean fixtures) or `independent_shared_equations_mated` (for modular cabinetry/brackets).
5. *(For assemblies)* `sw_audit_assembly_mates(include_subassemblies=true)` (`clean == true`, zero yellow/red mates) -> `sw_capture_viewport_quad` to confirm visual identity -> `sw_save_document`.

---

## Workflow 8: SendCutSend Sheet Metal (Only After Tier 1–2 Gate) (`IPS`)
1. Confirm why in-house woodworking, `Tough PLA Basic/Pro`, `1/2"` Corian, or CNC aluminum/acrylic cannot meet requirements.
2. `sw_create_document(doc_type="part", domain="sheet_metal_sendcutsend")` (`IPS`).
3. `sw_create_scs_sheet_metal_base` -> `sw_add_scs_edge_flange` -> `sw_add_scs_tab_and_slot` (`+0.010"` clearance, `R0.015"` corner relief, weld-free).
4. `sw_audit_dfm(process="sheet_metal_sendcutsend")` -> `sw_capture_viewport_quad` -> `sw_save_document`.
5. *(Only when user explicitly asks to order/export parts)* `sw_export_scs_flat_pattern_dxf` and `sw_create_fabrication_drawing(mode="sendcutsend_vendor", include_flat_pattern=true, export_pdf_path=...)`.

---

## Workflow 9: Mechatronics Linear Slide, Limit Switch & Cable U-Loop Routing
1. `sw_query_assembly_transforms` -> retrieve exact world-to-local matrices and bounding boxes for moving stages, switches, rails, and nested cable components (eliminates multi-frame coordinate sign errors).
2. Design support-free `MMGS` cable saddles / strain reliefs (`z=0` flat back, `0.6mm × 45°` bed chamfer, `<=35°` snap C-barrels with `ID = OD + 0.30mm`, `42°` gable zip-tie tunnels with `>=1.6mm` floor & crown, and `>=20mm` `120V` vs low-voltage separation) -> `sw_audit_dfm(process="3d_printing")`.
3. Route rolling U-loops and harnesses via `sw_route_cable_or_loom(routes=[...]|u_loop_spec=..., outer_diameter_mm=..., wall_thickness_mm=..., clearance_check_components=[...])`, keeping $\Delta Z = 2R$ between opposing saddle barrels and `0.5–2.0 mm` axial butt-gaps at saddle clamps.
4. **Scope-Aware Motion Verification (`sw_verify_motion_stroke`)**: Skip when edits stay outside the swept bounding box; during iterative design run abbreviated model-discovered stops (1–2 linear endpoints — often just the 1 affected endpoint — plus 1 intermediate tightest-bend inflection stop on rotating/U-loop assemblies); at **sign-off or fabrication prep** run the full `[home, mid, soft_max, hard_max]` sweep to check bearing overrun, `3.0–3.5 mm` limit-switch actuation vs `>= 5 mm` housing crash clearance, and subset `IInterferenceDetectionMgr` (`exclude_same_subassembly_pairs=true`) collisions -> sync firmware limits (`Z_MIN_POS`, `Z_MAX_POS`, `G53 G0 Z...`).
5. `sw_audit_assembly_mates(include_subassemblies=true)` (`clean == true`, zero yellow/red mates) -> `sw_capture_viewport_quad` -> `sw_save_document(save_all_components=true)`.

---

## Workflow 10: Multi-Sheet Step-by-Step Assembly Instruction Guide & Turnkey BOM/PDF Documentation
1. **Batch Custom Properties & Reference-Part BOM Exclusion**: Call **`sw_manage_assembly_components(action="configure_bom_and_properties", exclude_from_bom_components=["Valve_Body*", ...], component_properties=[{component_name, properties:{"BOM_Desc": "...", "BOM_Role": "...", "PartNumber": "..."}}])`** to flag reference/envelope components `ExcludeFromBOM = true` and populate human-readable `BOM_Desc` and `BOM_Role` properties across all parts without manual `sw_execute_csharp` file loops.
2. **Stage Configurations & Exploded Views with 3D Explode Lines**: For each assembly stage configuration (e.g., `Step1_Adapter_And_Frame`, `Step2_Actuator_Linkage`), call **`sw_create_exploded_assembly_view(configuration=..., create_configuration_if_missing=true, clear_existing_steps=true, create_explode_lines=true, steps=[...])`** (uses `SelectData.Mark = 1`, clears unwanted `Chain1` AutoExplode steps, supports compound multi-axis `vector:[dx,dy,dz]` / `motions:[...]` steps on the same component, and generates 3D explode-line trajectory sketches).
3. **Author Multi-Sheet `.SLDDRW` & Auto-Export `.PDF`**: Call **`sw_create_fabrication_drawing(mode="assembly_guide", sheet_size="tabloid_11x17", sheets=[...], bom={columns:[{type:"item", title:"ITEM", width_mm:14}, {type:"custom_property", property_name:"BOM_Desc", title:"COMPONENT / DESCRIPTION", width_mm:75}, {type:"custom_property", property_name:"BOM_Role", title:"FUNCTION / FASTENER SPEC", width_mm:80}, {type:"quantity", title:"QTY", width_mm:15}]}, title_block={dwg_no:..., title:..., units_note:...}, export_pdf=true)`**:
   - Automatically loads `b - landscape.slddrt` via `SetupSheet5` / `NewSheet4`, cleans up `<INSERT COMPANY NAME HERE>`, `$PRP:"SW-File Name"` overflow, and `"DIMENSIONS ARE IN INCHES"` on `MMGS` models inside `EditTemplate()`.
   - Applies Shaded With Edges (`SetDisplayMode3(false, swSHADED = 3, false, true)`), auto-fits views into layout `box:[xMin,yMin,xMax,yMax]` via `GetOutline()`, links all views across sheets to the master BOM (`SetKeepLinkedToBOM`), applies delta-only ballooning (`only_balloon_components` / `exclude_balloon_components`) with automatic `InsertBOMBalloon2` fallback, and exports the `.PDF` automatically.
4. **Multimodal Sheet Verification**: Call **`sw_capture_drawing_sheets`** (uses maximized-frame `SaveBMP(path, 0, 0)` raster capture) to visually inspect every sheet for view framing, balloon clarity, BOM columns, and title-block layout.
