---
name: solidworks-cad-modeling
description: >-
  Use this skill when creating or modifying SOLIDWORKS 3D parts, assemblies,
  3D-printed airflow/dust-collection/air-blast lofts and sweeps, Gridfinity bins
  and baseplates, mechatronics motion limits and cable routing, 2D
  workshop/vendor drawings with exploded views, COM safety scripting, or
  converting photos and hand sketches into parametric CAD.
---

# SOLIDWORKS Parametric CAD, Airflow Lofts, Gridfinity & Drawings

See [end_to_end_workflows.md](./workflows/end_to_end_workflows.md) for complete step-by-step tool sequences across every major shop workflow.

---

## 1. Unit System & Document Setup (Mandatory First Step)

- **3DEXPERIENCE Maker Portal Launch / Restart**: If SOLIDWORKS Connected is closed or needs a clean restart, call `sw_connect_status(launch_if_closed=true)` or `sw_connect_status(restart_if_running=true)` (or run `scripts\Launch-SolidWorks3DExperience.ps1`) to automate signing into `eu1-ds-iam.3dexperience.3ds.com` in Microsoft Edge using saved credentials and launching **`SOLIDWORKS Design`** under **Favorite Apps**.

Before creating sketches or features, ensure the document uses the shop's required unit system via `sw_create_document` or `sw_open_or_import_document`:
- **`MMGS`** (`millimeter, gram, second`): All **3D-printed parts**, **airflow/vacuum ducts**, **Gridfinity**, and **KiCad electronics enclosures/jigs**.
- **`IPS`** (`inch, pound, second`): All **woodworking**, **CNC furniture/jigs** (`1/2"` Corian, QCW dowel fixtures), **residential electrical cutouts**, and **SendCutSend** imperial gauge parts.
- Remember that raw COM calls inside `sw_execute_csharp` always expect **meters** (`1 mm = 0.001 m`, `1 in = 0.0254 m`) and **radians**. Structured MCP tools accept `"units": "mm" | "in" | "m"` and handle conversion cleanly.

---

## 1B. Core MCP Tool & `SwHelper` Signature Card (Zero-Lookup Cheat Sheet)

> **Turn-Efficiency & COM Concurrency Rules**: Never issue parallel SOLIDWORKS MCP tool calls (COM STA is single-threaded; `SolidWorksMcpServer` serializes calls via a cross-process mutex). Never re-read MCP schema JSON files for tools listed here; never re-inspect unchanged datums on small incremental nudges; cap raw COM selector string retries at **1** before switching to geometric lookup (`entity_a_spec` or `SwHelper`). For large assembly refactors, follow the **4-phase order** (`delete_mates -> delete -> edit parts -> pose_and_mate_batch`; batch ops auto-flush intermediate `EditRebuild3()` on destructive->constructive transitions).

- **Batch Part Modeling (`sw_build_part_features`)**:
  - `sw_build_part_features(reset_first=false, units="mm"|"in", equations={action:"set", variables:[...]}, features=[...], keep_sketches_visible=false, target_doc=...)`
  - `features` step `op`: `"ref_plane"`, `"sketch_2d"`, `"sketch_3d"`, `"extrude"`, `"revolve"`, `"hole_preset"`, `"fillet"`, `"chamfer"`, `"shell"`, `"pattern"`, `"mirror"`, `"label"`, `"teardrop"`, `"crisscross_bridge"`, `"joinery"`.
  - `extrude`/`revolve` accept either `sketch_name` OR an inline `sketch: {plane_or_face, sketch_name, entities}` (`polygon` supports `across_flats`, `inscribed=true`, `start_angle_deg`; `slot` safely uses `2x CreateLine + 2x Create3PointArc` for any aspect ratio and accepts `length_type: "center_center" | "full_length"`). Newly created sketches auto-hide cleanly when finished.
- **Headless Repository Add-in Tools (`sw_invoke_repo_addin_tool`)**:
  - `sw_invoke_repo_addin_tool(tool="3dprint_teardrop"|"3dprint_bridge"|"joinery"|"export_faces_dxf", face_tokens=[...], bed_plane_or_face="Top Plane", build_direction=[0,0,1], layer_height_mm=0.20, overhang_angle_deg=40, max_cap_height_mm=0.6, joint_type="tab_and_slot", use_macro_feature=true, ...)`
  - Invokes `SolidWorks3DPrintDFMAddin.InvokeDfmHeadless` or `SolidWorksJoineryAddin.InvokeJoineryHeadless` via `GetAddInObject` to create editable MacroFeatures, with automatic standalone native cut fallback if the add-in isn't loaded yet.
- **Batch Arrays on Individual Tools** *(prefer over sequential calls)*:
  - `sw_create_2d_sketch(sketches=[...])`, `sw_create_3d_guide_sketch(sketches=[...])`, `sw_create_reference_geometry(items=[...])`
  - `sw_extrude_boss_or_cut(operations=[...])`, `sw_revolve_boss_or_cut(operations=[...])`, `sw_fillet_or_chamfer(operations=[...])`, `sw_pattern_or_mirror(operations=[...])`, `sw_hole_preset(operations=[...])`, `sw_engrave_or_emboss_label(labels=[...])`
  - `sw_route_cable_or_loom(routes=[...])`, `sw_populate_mcmaster_hardware(items=[...])`, `sw_save_document(target_docs=[...], save_all_components=true, check_mate_health=true)`
- **Batch Assembly Posing & Geometric Mating (`sw_manage_assembly_components`)**:
  - Prefix `@top:` or `/` on `component_names` to match strict top-level instances only (`top_level_only=true` default on `delete_mates`).
  - `action="pose_and_mate_batch"` accepts `pose_and_mate_items=[{component_name, release_mates, mate_type_filter, unfix, position, ex, ey, ez, axis, mate_to_principal_planes, target_ref_component, mate_specs, fix}]` inside one `SuppressUiUpdates` (with automatic post-delete `EditRebuild3` flush) + final `EditRebuild3()`.
  - `mate_specs` accept selector strings OR geometric specs (`entity_a_spec`/`entity_b_spec`: `{component_name, kind:"planar"|"cylindrical"|"plane"|"axis", world_point_mm, world_normal|world_axis, radius_mm, feature_name}`) OR `mate_type:"principal_planes"`.
- **Fast Verification & Hardware**:
  - `sw_measure_clearance(include_components=[...], exclude_same_subassembly_pairs=true, group_by_pair=true)` (subset 2–40 components; skips internal subassembly pairs cleanly).
  - `sw_audit_assembly_mates(include_subassemblies=true)` (run once before finishing an assembly edit; intermediate calls attach `<5ms` top-level `mate_health`).
- **`SwHelper.*` in `sw_execute_csharp`**:
  - `Activate(docName)`, `SuppressUiUpdates(doc)`, `CreateSlot2D(sm, x1, y1, x2, y2, widthM, fullLength, construction)`, `TopComponents(assy, prefix, includeSuppressed)`, `MakeTransform(ex, ey, ez, originMm)`, `MateEntities(assy, entA, entB, mateType, align, distMm, angleDeg, flip, lockRotation)`, `MateByPrincipalPlanes(assy, comp, targetRef, xf)`, `FindPlanarFace(comp, worldPtMm, worldNorm, minAreaMm2)`, `FindCylindricalFace(comp, worldPtMm, worldAxis, radiusMm)`, `MoveComponentsToFolder(doc, assy, comps, folderName)`, `BatchDeleteComponents(doc, names)`, `BatchDeleteMates(doc, names)`, `BatchDeleteMatesForComponents(doc, compNames)`, `GetVisibleSketchNames(doc)`, `HideSketches(doc, names, preserve)`, `MakeOffsetPlane(...)`, `SaveSilent(doc)`, `MinimizeCefUiNoise(swApp)`.

---

## 2. Multimodal Photo & Hand-Sketch-to-CAD Protocol

When the user provides a photo or hand sketch:
1. **Reason About Intent First (Never Blindly Trace Perspective)**:
   - Identify functional interfaces, symmetries, orthogonal planes, fastener clearances, and load paths.
   - Normalize slightly skewed lines in a rough sketch to clean horizontal/vertical/perpendicular/tangent relations and standard stock dimensions (`shop_profile.json`).
   - State your inferred dimensions and ask clarifying questions only when critical mating dimensions are missing and cannot be inferred from context (e.g., an attached `.STEP` or standard hardware).
2. **Use `sw_insert_sketch_picture` Selectively**:
   - Use it only when tracing a literal organic contour, bespoke hardware silhouette, or complex bracket profile from an orthographic reference photo.
   - Always calibrate across the longest known straight span (`point1_px`, `point2_px`, `real_distance`) before sketching splines/arcs over the underlay.
3. **Link Key Parameters to Shared `equations.txt`**:
   - Define stock thicknesses, clearances, and mating nominal dimensions via `sw_manage_equations` right after creating the document.

---

## 3. Clean 2D & 3D Sketching Rules

- **`ModelToSketchTransform` Coordinate Safety**: `sw_create_2d_sketch` accepts 3D model-space coordinates and projects them through `activeSketch.ModelToSketchTransform` via `IMathUtility.CreatePoint` + `MultiplyTransform`, preventing unexpected X/Y axis flips on arbitrary planar faces.
- **Database Mode**: Wrap sketch creation inside `sketchMgr.AddToDB = true; ... AddToDB = false;` when authoring C# code in `sw_execute_csharp` to disable UI inference snapping and avoid accidental stray relations.
- **Fully Define Sketches Early**: Apply centerlines, symmetry, `Horizontal`/`Vertical`/`Tangent`/`Coincident` constraints, and driving dimensions (`AddDimension2`) so sketches show zero `(-)` under-defined flags.
- **Hide Finished & Reference Sketches Before Ending Turn**: Do not leave newly created 2D/3D sketches (including reference/layout sketches, sweep/loft guide curves, and cable routing paths) visible after their solid bodies are built — hide them via `BlankSketch()` or `SwHelper.HideSketches(doc, sketchNames)` so they don't clutter the viewport. Leave a sketch visible **only** if it was already visible before you started working, or if producing/showing the sketch itself (concept contour, layout idea, or specific dimensions) is the goal of the turn.

---

## 4. First-Class 3D-Printed Airflow, Vacuum & Air-Blast Lofts (`MMGS`)

Use `sw_create_airflow_loft_or_sweep` and `sw_shell_duct_or_body` to build dust-collection adapters, vacuum nozzles, and `9 CFM` air-blast manifolds:

1. **Connector Synchronization (Prevent Loft Twist)**:
   - Align the start-vertex index / `IMathPoint` seam of every loft cross-section along a common axial meridian (e.g., `+Y` top seam) before calling `InsertProtrusionBlend2`.
   - Use **Normal to Profile** start/end tangency (`weight = 0.5–1.5`) when transitioning from straight cylindrical hose collars into curved or rectangular nozzle mouths.
2. **Aerodynamic & Fluid Continuity**:
   - Maintain or smoothly vary cross-sectional area ($A_{\text{in}} \approx A_{\text{out}}$ for low-loss dust adapters; controlled converging taper for `1/4"` quick-connect air-blast nozzles).
   - Keep sweep/loft centerline bend radii $R \ge 1.5 \times D_{\text{hydraulic}}$ to avoid turbulent flow separation and print overhang collapse.
3. **Support-Free Internal Printability (`Prusa MK3.5S+`, `0.4 mm` nozzle)**:
   - Use **teardrop or vaulted (`<= 45°`) arch cross-sections** on horizontal internal ducts rather than flat ceilings so interior flow paths stay smooth and **100% support-free**.
   - Shell ducts (`InsertHollowFeature2`, selecting inlet/outlet faces) to integer multiples of `0.4 mm`: **`1.6 mm`** (4 perimeters, standard adapters) or **`2.0–2.4 mm`** (5–6 perimeters, airtight vacuum/high-pressure air manifolds).
4. **Shop Port Interfaces (`shop_profile.json`)**:
   - **Shop Vacuum Taper**: `1-7/8"` tapered male/female socket (`46.6 mm` to `48.4 mm` over `38 mm` engagement).
   - **Festool Cleantec Adapter**: Match `D27`/`D36` locking bayonet or existing `1-7/8"`-to-Festool adapter collar.
   - **Compressed Air (`9 CFM` supply)**: Boss sized for `1/4"` NPT / `1/4"` quick-connect plug (`9.5 mm` hex clearance + thread boss).
   - **Custom `4"` Dust Collection**: Refer to the `TODO` hook in `shop_profile.json`; verify exact mating OD/ID before printing `4"` collars.

---

## 5. Parametric Gridfinity Generator (`MMGS`)

Use `sw_create_gridfinity` for modular drawer/bench/CNC-tool organizers:
- **Standard Dimensions (`MMGS`)**:
  - Cell Pitch: `42.0 × 42.0 mm` (`41.5 mm` single bin OD, `(42.0 × N - 0.5) mm` multi-unit OD, corner `R3.75 mm`).
  - Height Unit: $1U = 7.0\,\text{mm}$ (e.g., `6U = 42.0 mm` bin height + optional `4.4 mm` stacking lip).
  - Base Foot Profile (`4.75 mm` total height): `0.8 mm × 45°` bottom chamfer -> `1.8 mm` vertical -> `2.15 mm × 45°` top chamfer.
  - Magnet & Screw Pockets: `Ø6.5 mm × 2.4 mm` deep (`6×2 mm` magnets) and `Ø3.0 mm` M3 screw holes centered `8.0 mm` from each cell's outer corners (`26.0 mm` pitch).
- **Custom Shadow / Toolholder Bins**: Pass `shadow_step_file` (or sketch geometry) + `scoop_front = true` + `embossed_label` to subtract an imported tool/caliper/bit profile with `0.4 mm` per-side clearance and a front finger scoop.

---

## 6. 2D Drawings, Exploded Views & Export Gate

Follow the **Export Gate**: never export `.PDF`, `.DXF`, `.STEP`, `.3MF`, or `.STL` automatically unless the user explicitly asks to print, CAM, or order parts:
1. **Exploded Assembly Views (`sw_create_exploded_assembly_view`)**:
   - For multi-part assemblies, pass explicit directional `steps` separating joinery, hardware, and mating brackets cleanly along assembly vectors (`auto_explode` is disabled by default and guarded on `> 20` components).
2. **Printable Workshop Fabrication Sheet (`sw_create_fabrication_drawing` with `mode = "workshop_sheet"`)**:
   - When requested by the user, generate `8.5"×11"` (Letter) or `11"×17"` (Tabloid) `.SLDDRW` / `.PDF` containing:
     - Orthographic views + Isometric/Exploded view with `AutoBalloon5`,
     - Full BOM & Cut-List table (McMaster SKUs, sheet/hardwood dimensions, Blum hardware, Domino sizes `4/5/8 mm`, metric Forstner diameters `10–30 mm`, piano hinge / aluminum edge-pull cut lengths),
     - Explicit bit, guide-bushing offset, and clamp-staging notes (`<= 4` parallel, `<= 6` pipe clamps per glue-up stage).
3. **SendCutSend / Machine Shop Vendor Drawing (`mode = "sendcutsend_vendor"` or `"machine_shop_vendor"`)**:
   - When requested by the user for quoting/ordering, include 3rd-angle projections, section/detail views, flat-pattern view with bend lines/table, `AddHoleCallout2` thread/countersink specs, critical tolerances, and material/finish notes.

---

## 7. Mandatory 4-Up Visual Verification, Zero Bad Mates & `sw_execute_csharp`

- **After every major feature or assembly edit**, call `sw_capture_viewport_quad` once and visually inspect the returned 4-panel PNG (`Iso`, `Top`, `Front`, `Right`) to verify proportions, orientation, cutouts, and zero unintended intersections.
- **Zero Yellow/Red Mates Gate (Assemblies)**: Never leave an assembly with **yellow (warning)** or **red (error)** mates. Intermediate assembly tools attach a `<5ms` top-level `mate_health` summary; always run full **`sw_audit_assembly_mates(include_subassemblies=true)`** once before finishing an assembly edit to inspect every failing mate across loaded subassemblies, dangling entities, and suggested fixes, resolving all bad mates until `red_mate_count == 0` and `yellow_mate_count == 0`.
- **Universal API Escape Hatch (`sw_execute_csharp`)**: Runs Roslyn C# (`-langversion:latest`, SHA-256 cached in-session) against early-bound SOLIDWORKS COM interfaces plus built-in **`SwHelper.*`** utilities inside a supervised self-healing `--worker` child process (`rollback_on_error = true` Undo transaction, `logs` preserved on exception, and modal guards).

---

## 8. Mechatronics: Homing Limit Switches, Travel Budgeting & Cable Routing

1. **Homing Limit Switches & Overtravel Crashes**:
   - Distinguish **extended free position** vs **actuated operating point** (`3.0–3.5 mm` lever depression past initial contact for reliable switch trigger) vs **total overtravel crash point**.
   - Always maintain **`>= 5.0 mm` clearance** between the moving stop block/striker and the rigid switch housing, plunger boss, and solder tabs at hard stop.
2. **Linear Rail Travel Budgeting Formula**:
   $$\text{Total Rail Length} \ge \text{Carriage Bearing Out-to-Out Span} + \text{Usable } Z_{\max} \text{ Stroke} + \text{Switch Actuation Overtravel} + \text{Bottom Hard-Stop Margin} + \text{Top Tramming Gap } (1.5\text{–}2.0\text{ mm})$$
   - Keep firmware motion constants (`Z_MIN_POS`, `Z_MAX_POS`, `Z_PROBE_LOW_POINT`, bit-change macro `G53 G0 Z...`) synchronized with the CAD `LimitDistance` mate in the same step.
3. **Multi-Frame Coordinates, Cable Routing & Scope-Aware Motion Stroke Verification**:
   - Use **`sw_query_assembly_transforms`** to transform points/vectors across nested subassembly frames (`router carriage-1/rf1101 speed controller-1/cable-1`) without manual axis-sign errors.
   - Use **`sw_route_cable_or_loom`** (single or batch `routes=[...]`, or `sw_create_airflow_loft_or_sweep(mode="sweep", circular_diameter=...)`) for solid cords and hollow split loom; pre-validates bend radii $R > D/2$ (keeping `R >= 3×OD` loom, `R >= 4×OD` AC cord, `0.5–2.0 mm` axial butt-gaps at saddle clamps, and `>= 20 mm` AC-vs-low-voltage separation) and automatically hides its 3D path sketch after sweeping (when routing manually, always hide cable/reference sketches via `SwHelper.HideSketches` before ending your turn).
   - **Scope-Aware & Abbreviated Motion Stroke (`sw_verify_motion_stroke`)**:
     - **Skip `sw_verify_motion_stroke` completely** when your edits lie cleanly outside the moving stage's swept bounding box (verify static clearance analytically via component bounding boxes / transforms instead).
     - **During iterative design** on moving stages, slides, or cables, run **`sw_verify_motion_stroke`** with **abbreviated model-discovered geometry stops** chosen by looking at the viewport image and part geometry: on a linear slide test only 1–2 travel endpoints (often just the **1 affected endpoint** nearest the edit, e.g., top hard-stop or bottom plunge), and on a rotating armature or rolling U-loop cable test 1 endpoint plus at most **1 intermediate tightest-bend inflection point**.
     - **At user sign-off or fabrication prep**, run the thorough full-envelope sweep (`[home, mid, soft_max, hard_max]`) checking rail bearing overrun, `3.0–3.5 mm` limit-switch actuation vs `>= 5 mm` housing crash clearance, and subset `IInterferenceDetectionMgr` (`exclude_same_subassembly_pairs=true`) collisions, with guaranteed resting-pose restoration.

---

## 9. COM Performance & Safety Cheat Sheet (`sw_execute_csharp` & `SwHelper`)

Follow these non-obvious SOLIDWORKS API rules so custom `sw_execute_csharp` scripts succeed on the first call without hanging or crashing the COM STA thread (never issue parallel SOLIDWORKS MCP calls; a cross-process COM mutex enforces sequential execution):
1. **Never pass `swRebuildOnActivation_e.swUserDecision` to `ActivateDoc3`** — it pops a modal dialog that blocks the COM thread until `3m0s` timeout. Always use **`SwHelper.Activate(docName)`** or `swRebuildOnActivation_e.swDontRebuildActiveDoc`.
2. **Use `doc.EditRebuild3()` (`< 0.5 s` mate solve) in assembly mate loops**; reserve `ForceRebuild3(false)` only for single-part B-Rep regeneration. Split major assembly refactors into 4 phases (**delete mates -> delete components -> edit parts -> pose & re-mate**; `sw_manage_assembly_components` auto-flushes intermediate rebuilds on destructive->constructive transitions to prevent `0x800706BA` solver crashes).
3. **Static Crash Blocklist in `sw_execute_csharp`**: The script compiler blocks the following known crash/hang triggers before execution:
   - `swUserDecision` & `doc.EditDelete()` (modal hangs; use `SwHelper.Activate` and `doc.Extension.DeleteSelection2(0)`).
   - `CommandInProgress` (causes `0xC0000005` AV in `slduiu:uiModelDoc_c::conditionalBlockCmd` during sketches).
   - `DisplayDimension2[idx]` (causes `0xC0000005` AV; use `doc.Parameter("D1@MateName")` or iterate `IFeature.GetFirstDisplayDimension()` / `GetNextDisplayDimension()`).
   - `.GetWhatsWrong(...)` (dynamic out-object COM AV; use `sw_audit_assembly_mates` or `IFeature.GetErrorCode2`).
   - `.CreateSketchSlot(...)` (kills `SLDWORKS.exe` `0x800706BE` + 20-min 3DEXPERIENCE license lock whenever `center-to-center < width`; use `SwHelper.CreateSlot2D` / `slot` entities).
   - `ToolsCheckInterference*` (throws `0x80010105 RPC_E_SERVERFAULT`; use `sw_measure_clearance`).
   - Raw `.Save3(...)` / `.SaveAs*(...)` inside custom scripts (use `sw_save_document` or `SwHelper.SaveSilent(doc)`).
   - Mixing `DeleteSelection2(...)` with `.Reference` / `.ReferenceComponent` in the same snippet (`0xffffffff` COM crash; split delete & re-inspection into separate calls).
   *(In addition, `SolidWorksMcpServer` isolates COM inside a self-healing `--worker` child process.)*
4. **In `FeatureCut4(Sd, Flip, Dir, ...)`**, keep **`Flip = false`** (`true` is *Flip Side To Cut* and cuts everything *outside* the profile). For offset cuts/bosses without manual planes, pass `start_offset` to `sw_extrude_boss_or_cut`, use `swEndCondMidPlane` on centered offset planes (`SwHelper.MakeOffsetPlane`), and note `swStartOffset` direction follows sketch normal unless `start_offset_mode="model_positive"`.
5. **Read planar face outward normals from `IFace2.Normal`**, NEVER raw `ISurface.PlaneParams[0..2]` (which ignores `FaceInSurfaceSense()`), or call `SwHelper.FindPlanarFace(comp, worldPointMm, worldOutwardNormal, minAreaMm2)`.
6. **Iterate `MateGroup` subfeatures safely with `as IMate2`** (recursing into `FtrFolder`), move assembly components into `"McMaster Hardware"` using **`IAssemblyDoc.ReorderComponents(..., swReorderComponents_After)`** (`SwHelper.MoveComponentsToFolder`), never `ReorderFeature`, and compute analytical `Distance` mate sign with **`SwHelper.MateByPrincipalPlanes`** (zero trial-and-error rebuild loops).
7. **Never set `swApp.CommandInProgress = true`** during sketch or feature operations — in SOLIDWORKS 2026 SP04 it enables `uiBlockMan_c` and causes a native Access Violation in `slduiu:uiModelDoc_c::conditionalBlockCmd` when `InsertSketch` updates the CommandManager ribbon.
8. **Avoid `IModelDoc2.RayIntersections` in Favor of Pure-C# Tessellated Mesh Raycasting**: `sw_audit_dfm(process="3d_printing")` extracts `IFace2.GetTessTriangles(true)` (verified outward winding) and runs Möller–Trumbore ray-triangle intersections in pure C# outside COM, eliminating `0x80010105 (RPC_E_SERVERFAULT)`. (If legacy custom COM code ever calls `RayIntersections`, `BodiesIn` must be marshaled as `DispatchWrapper[]`, never `object[]`.)
9. **Never loop `DeleteSelection2(0)` one item at a time — batch deletions, suppress UI updates & log via `log(...)` (`SWCEFComWrapper` crash guard)**:
   - Calling `SelectByID2` + `DeleteSelection2(0)` per component/mate in a loop forces repeated rebuilds, OpenGL repaints, and cross-thread UI events that can trigger a SOLIDWORKS 2026 SP04 `SWCEFComWrapper:HandlePostMsg` (`libcef`) Access Violation (`0x800706BA`), especially after switching from a saved part back into an open assembly.
   - Prefer `sw_manage_assembly_components(action="batch" | "delete" | "delete_mates")`. Inside `sw_execute_csharp`, always wrap bulk assembly edits in **`using (SwHelper.SuppressUiUpdates(doc))`** (disables both `EnableFeatureTree` and `IModelView.EnableGraphicsUpdate`), use **`SwHelper.BatchDeleteComponents(doc, names)`** / **`SwHelper.BatchDeleteMates(doc, names)`** / **`SwHelper.BatchDeleteMatesForComponents(doc, compNames)`** (one multi-select + one `DeleteSelection2(0)`), and record progress via **`log(...)`** instead of a local `StringBuilder` so partial traces survive exceptions.
   - To further cut CEF background traffic in long sessions, call `sw_connect_status(minimize_cef_ui=true)` (auto-applied on launch/restart) or `SwHelper.MinimizeCefUiNoise(swApp)` to unpin the Task Pane and unload `SldAuraEx.dll` (AURA).
