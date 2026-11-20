---
name: solidworks-feature-tree-hygiene
description: >-
  Use this skill when organizing, renaming, reordering, grouping, or parameterizing
  a SOLIDWORKS FeatureManager tree, extracting material thicknesses and tolerances
  into shared external equations.txt files for assemblies, fixing under-defined (-)
  sketches, or breaking a multibody SLDPRT into a mated SLDASM assembly.
---

# SOLIDWORKS Feature Tree Hygiene, Shared Equations & Multibody Splitting

## 1. Core Principles of Clean Tree Refactoring

1. **Preserve Exact Geometry (Zero Drift Guard)**:
   - Before touching any feature or equation, record baseline `IMassProperty` (mass, volume, surface area, center of gravity) via `sw_inspect_model_summary`.
   - Always execute structural refactors inside an Undo recording block; roll back immediately if rebuild errors appear or mass/volume deviates beyond floating-point tolerance (`> 0.001%`).
2. **Audit First**: Call `sw_inspect_feature_tree` to retrieve the full parent/child dependency graph (`GetParents`/`GetChildren`), sketch constraint status (`(-)` vs fully defined), and existing dimensions.
3. **Zero Yellow/Red Mates Gate (Assemblies)**: On any `.SLDASM` document — especially after editing part topology, renaming/splitting bodies, replacing components, or organizing folders — run **`sw_audit_assembly_mates(include_subassemblies=true)`**. Never leave mates in yellow (warning) or red (error) states; delete dangling/conflicting mates via `sw_manage_assembly_components(action="delete_mates", mate_names=[...])` and re-mate cleanly until `clean == true` (`red_mate_count == 0`, `yellow_mate_count == 0`).

---

## 2. Semantic Renaming & Logical Stage Foldering

Use `sw_organize_feature_tree(rename_semantically=true, reorder_dag=true, group_folders=true)`:

- **Semantic Naming Conventions**:
  - Rename generic features (`Boss-Extrude1`, `Cut-Extrude4`, `Sketch2`) to concise `<Role>_<Detail>_<KeyDim>` labels:
    - Examples: `Base_Plate_3_4_Ply`, `Side_Rail_Walnut`, `M3_HeatSet_94180A333_Bosses`, `Domino_DF500_5x30_Mortises`, `QCW_3_8_Dowel_Grid`, `DFM_Bottom_Chamfer_45deg`, `CNC_Dogbone_Reliefs`.
  - Keep absorbed child sketches synced with a matching `_Sk` suffix (e.g., `Base_Plate_Sk`).
- **Dependency-Safe DAG Reordering (`ReorderFeature`)**:
  - **Top**: Origin/Default Planes -> Reference Geometry (`RefPlane`, `RefAxis`, `CoordSys`) -> Master Skeleton / Layout Sketches.
  - **Middle**: Primary structural bosses/extrudes/flanges/lofts -> Joinery/Functional cuts & Hole Wizard / Heat-Set pockets -> Patterns & Mirrors.
  - **Bottom**: Edge Fillets, Chamfers, DFM details (Teardrops, Bridges, Dogbone reliefs, Engraved labels).
  - Never attempt to move a child feature above its `GetParents()` ancestor; use the DAG topological sort computed by `sw_organize_feature_tree`.
- **Stage Folders (`InsertFeatureTreeFolder2`)**:
  - Group contiguous sibling features into logical folders such as `01_Reference_And_Layout`, `02_Primary_Bodies`, `03_Joinery_And_Hardware`, `04_DFM_And_Finishing`.
  - Do **not** wrap Hole-Wizard-style `ISwComFeature` MacroFeatures inside redundant folders (they already nest their own child cuts/sketches).
- **Consolidate & Fully Define Sketches**:
  - Collapse duplicated independent cut-extrudes into `LinearPattern`, `CircularPattern`, `Mirror`, or `HoleWizard` features where geometry is identical.
  - Constrain `(-)` under-defined sketches relative to Origin, centerlines, or stable parent edges using `sw_create_2d_sketch` / `sw_execute_csharp`.
  - **Hide Cluttering Sketches**: Hide newly created feature, layout, and 3D reference sketches (e.g. cable/sweep paths) once bodies are built (`BlankSketch()` / `SwHelper.HideSketches(...)`); leave sketches visible only if they were already visible prior to editing or if the sketch itself is the requested deliverable.

---

## 3. Shared External `equations.txt` Extraction (Assembly-Ready)

Because your parts usually live inside assemblies, global variables should be stored in a **shared external `equations.txt`** file rather than trapped inside a single part.

Use `sw_refactor_to_shared_equations`:
1. **What to Extract (and What NOT to Extract)**:
   - **DO extract**:
     - Standard shop material thicknesses (`Ply_3_4 = 0.718in`, `Ply_1_2 = 0.468in`, `Ply_1_4 = 0.218in`, `Corian_1_2 = 0.500in`, `SCS_5052_063 = 0.063in`, `Print_Wall_4P = 1.6mm`),
     - Standard shop clearances (`Glue_Clear = 0.008in`, `Slip_Clear_3DP = 0.2mm`, `SCS_Tab_Clear = 0.010in`),
     - Derived joint formulas (`Dado_Width_3_4 = "Ply_3_4" + "Glue_Clear"`, `Tab_Slot_Width = "Mat_Thick" + "SCS_Tab_Clear"`),
     - Key overall assembly envelopes (`Cabinet_Width`, `Grid_Units_X`, `Duct_ID`).
   - **DO NOT extract** cosmetic edge fillets or one-off incidental sketch coords that clutter the global table.
2. **External Linking**:
   - Write the shared `equations.txt` alongside the project `.SLDASM` (e.g., `<project_dir>\equations.txt`).
   - Set `IEquationMgr.FilePath = path` and `IEquationMgr.LinkToFile = true` on **both** the assembly and every linked component part so editing `Ply_3_4` updates every dado, panel, and rabbet across the entire project cleanly.

---

## 4. Breaking Multibody `.SLDPRT` Files into `.SLDASM` Assemblies

When requested to break a multibody part (e.g., designed quickly in a master multibody file) into a proper `.SLDASM`, use `sw_split_multibody_to_assembly` and choose the dependency strategy intentionally:

| Strategy | When to Choose | How SOLIDWORKS Links It |
| :--- | :--- | :--- |
| **`master_derived`** (`CreateSaveBodyFeature` / `InsertPart2`) | Sculpted curved furniture (e.g., tapered table/chair legs, bent-lamination forms + cauls), boolean-subtracted fixtures around `.STEP` parts, or complex interlocking organic joinery where one master skeleton controls shared curves. | Each body is saved into its own `<PartName>.SLDPRT` referencing the master skeleton part, and inserted into `<Project>.SLDASM` locked at origin (`Fixed` / `Coincident` default planes). |
| **`independent_shared_equations_mated`** | Modular cabinetry, drawer boxes, CNC/sheet-metal brackets, multi-part jigs, and Gridfinity multi-part assemblies where parts need independent clean feature trees, separate drawings/cutlists, and kinematic motion (doors/drawers). | Each body becomes an independent parametric `.SLDPRT` linked to the shared `equations.txt` (for thicknesses, widths, and clearances), assembled in `.SLDASM` with explicit `Coincident`, `Concentric`, `Width`, and `Distance` mates (never leaving floating parts unmated!). |

Always finish by running `sw_audit_assembly_mates(include_subassemblies=true)` (`clean == true`, zero yellow/red mates) and `sw_capture_viewport_quad` on the resulting `.SLDASM`, and verifying total assembly mass equals the original multibody mass.
