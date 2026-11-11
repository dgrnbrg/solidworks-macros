# ExportFacesToDXF

A SOLIDWORKS macro designed for CNC and CAM workflows that exports selected planar faces into clean, organized 2D DXF files with automatic outline extraction, layer separation, and closed vectors.

---

## How to Use

1. **Select Faces in SOLIDWORKS:**
   * Open your part, multibody part, sheet metal part, or assembly.
   * Hold `Ctrl` and click to select the planar (flat) faces you want to export (e.g. top faces, pocket floors, step faces).

2. **Run the Macro:**
   * Click the macro button on your toolbar (or run `ExportFacesToDXF.swp`).

3. **Choose Export Mode:**
   * **[1] Smart CAM Grouping (Default):** Overlays parallel faces belonging to the same body into a single DXF with separate layers per face and an outline layer. Non-parallel faces or different bodies are automatically separated into their own DXF files.
   * **[2] Single Combined DXF:** Combines all selected faces across all bodies and assembly components into one DXF file, each on its own labeled layer. Multiple parts are automatically arranged and spaced out neatly for CNC cutting.
   * **[3] Separate DXF Per Face:** Exports each face to an individual DXF file.

4. **Pocket Overcut Distance (Conditional):**
   * Appears automatically whenever any selected body has two or more parallel planar faces (multi-pocket areas) across any of Modes 1, 2, or 3.
   * Enter a positive distance (e.g., `0.25`, `1/4"`, or `6mm`) to extend every face boundary edge that runs along and touches the body's outer `OUTLINE` outward past the outline by that distance, saving manual pocket extension setup in CAM while keeping `OUTLINE` and interior step walls exact.
   * Explicit unit suffixes (`mm`, `cm`, `m`, `in`, `"`, `ft`, including fractions like `1 1/4"`) are automatically converted into each exported DXF's coordinate units (`$INSUNITS` / part units) when your CNC tool units differ from CAD part units. Bare numbers use the part's units.
   * Leave at the default `0` to disable overcuts completely (the overcut geometry engine is bypassed entirely so standard DXF workflows remain unaffected).

5. **Select Output Folder:**
   * Confirm the default folder (the active part/assembly directory) or browse to a different destination.

6. **Load into CAM:**
   * Import the generated DXF directly into your CAM software (Vectric VCarve/Aspire, Fusion 360, Mastercam, SheetCAM, LightBurn, etc.).

---

## Key Features

* **Automatic Multi-Part Spacing (Mode 2):**
  * When combining multiple parts or bodies from an assembly into a single DXF, the macro calculates each part's 2D bounding box and automatically spaces them out side-by-side (with compact 0.5" or 12 mm gaps and standard 48" / 1200 mm sheet row wrapping) so parts never overlap.
  * Within each individual part, stepped pocket faces and outline vectors maintain their exact relative alignment.

* **Dedicated Outline Layer (`OUTLINE`):**
  * Automatically calculates and generates a single continuous outer boundary enclosing the part and any open side pockets.
  * In CAM, use this layer directly for your outside profile cutout toolpath (with exterior tool radius compensation) without needing to manually draw or trace boundary vectors.

* **Per-Face CAM Layers (`FACE_1`, `FACE_2`, etc.):**
  * When exporting multiple parallel faces (such as pocket floors at different depths and top reference faces), each face is placed on its own distinct layer.
  * In CAM, this allows you to immediately assign different toolpaths, machining depths, and cut parameters layer-by-layer without manual vector separation.

* **100% Closed Vectors:**
  * All connected line and arc segments are pre-joined into fully closed polylines.
  * Eliminates open vector warnings, manual vector joining, and node editing in your CAM software.

* **Parallel Face Overlays (Pocket Depths):**
  * Parallel faces on the same body maintain their exact 1:1 relative positions and alignment in the DXF.
  * Ideal for programming 2.5D stepped pockets and island cuts from a single CNC setup.

* **Clean File Naming:**
  * Files are automatically named using the part name, custom body name (if given a meaningful name in the feature tree), and an incrementing index to avoid overwriting existing files.

* **Assembly Configuration Support:**
  * Fully supports assemblies containing multiple instances of the same part configured differently (e.g. Left/Right or Short/Long variants).
  * Automatically activates each component's referenced configuration during export, ensures different configurations are treated as distinct physical parts, and includes the configuration name in DXF filenames and CAM layer prefixes.

* **Universal Geometry Support:**
  * Works on standard solid parts, multibody parts, weldments, assemblies, and sheet metal parts.
  * In multibody parts, adjacent touching bodies (e.g. interlocking tabs, tenons, and mortises) are automatically isolated during export so touching bodies never clip or trim tabs or joint features. Body visibility is fully restored upon completion.

---

## Further Algorithmic Details

For those interested in the technical implementation details and compatibility considerations:

* **Seam Cancellation & Silhouette Extraction:**
  When multiple adjacent faces (e.g. a top face and a pocket floor) are exported together, the internal step wall between them produces coincident edges. The macro identifies these internal seams and cancels them out from both faces. The remaining exterior edges then chain together at the seam endpoints into a single continuous outer perimeter.

* **Area Evaluation & Winding Normalization:**
  Candidate boundary loops are evaluated using the Shoelace polygon area formula to identify the true exterior perimeter. If the resulting boundary is wound clockwise, the vertex sequence and arc bulges are automatically reversed to counter-clockwise (CCW) order, ensuring predictable toolpath offset behavior for CNC cutter compensation (G41/G42).

* **AutoCAD DXF Specification Adherence:**
  To ensure compatibility with CAM engines (such as Vectric, Fusion 360, and Mastercam), the macro generates standard AutoCAD 2000+ (`AC1015`) lightweight polylines (`LWPOLYLINE`) with complete ObjectARX subclass markers (`AcDbEntity` and `AcDbPolyline`), unique object handles, and model-space ownership pointers. This prevents CAM importers from dropping geometry or corrupting DXF symbol tables.

* **Perimeter Pocket Overcut Extension:**
  When a positive overcut distance is entered on multi-pocket selections, each face's closed boundary segments are matched against the CCW-normalized body `OUTLINE` (computing union `OUTLINE` across parallel body faces in Mode 3 as well). Straight and arc segments whose interiors run along the outline are split at any partial overlap endpoints, shifted outward along the outline's exterior normal by the unit-converted overcut distance, and re-stitched cleanly via corner intersections (with a miter limit) or perpendicular step-backs. Interior step walls whose endpoints merely meet the outline stay exact. When set to `0`, the overcut pass is never invoked.
