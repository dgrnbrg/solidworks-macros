# SOLIDWORKS CAM Face DXF Exporter Macro

A high-productivity SOLIDWORKS macro designed for CAM programmers and CNC machinists to export selected planar faces from parts and assemblies into clean, production-ready 2D DXF files.

---

## 🚀 Key Features

* **Dedicated Outline Layer (`OUTLINE`)**:
  * Automatically calculates and extracts the united exterior silhouette perimeter of all exported faces using an algebraic topological edge-cancellation algorithm.
  * Shared internal edges between pocket floors, step faces, and island walls cancel out ($E_1 \approx -E_2$), leaving only the true outer boundary.
  * Interior holes and cutouts are distinguished using the signed polygon area (Green's theorem / Shoelace formula: CCW outer perimeters have Area $> 0$, CW interior cutouts have Area $< 0$), guaranteeing that **only the outer perimeter is placed on the `OUTLINE` layer**.
  * Perfect for CAM outside profile cutout toolpaths (with exterior tool radius compensation).

* **Per-Face CAM Layers (`FACE_1`, `FACE_2`, etc.)**:
  * When exporting multiple parallel faces (e.g. stepped pockets of varying depths at the same machining setup), each face is placed on its own distinct layer (`FACE_1`, `FACE_2`, `FACE_3`... or `[PartName]_[Index]_F1`, `[PartName]_[Index]_F2` in multi-part assemblies).
  * Layer assignments use clean standard AutoCAD DXF entity group codes (`8`), keeping DXF symbol tables 100% compliant with all CAM engines (Fusion 360, VCarve, Mastercam, SheetCAM, LightBurn).
  * Makes it effortless to assign separate machining operations and depths per layer in CAM software.

* **100% Closed Vectors (`LWPOLYLINE 70 = 1`) & Full Vectric/CAM Compatibility**:
  * Automatically chains and converts all connected lines, arcs, and open polyline segments strictly per-layer into **100% closed contours**.
  * Formatted with full AutoCAD DXF R15+ (`AC1015`) ObjectARX subclass markers (`100 / AcDbEntity` and `100 / AcDbPolyline`), unique object handles (`5`), and model-space owner pointers (`330`).
  * Resolves empty layer issues in Vectric (VCarve Pro, Aspire, Cut2D) and eliminates "Join Vectors" steps or "Open Vector" warnings across Fusion 360, Mastercam, SheetCAM, and LightBurn.

* **Native Hole Drilling Cycles Preserved**:
  * Circular holes remain true native AutoCAD `CIRCLE` entities rather than segmented polylines, enabling CAM software to recognize holes for canned CNC drilling cycles (G81, G83 peck drilling, helical boring).

* **Robust Single Combined DXF Export (Mode 2 Fixed & Optimized)**:
  * Export faces from different bodies or different assembly components into a **single DXF file** seamlessly without losing vectors or dropping geometry.
  * **File I/O Lock Synchronization**: Employs an asynchronous write-flush detector (`WaitForFileReady`) so temporary export files are never read before SOLIDWORKS completes writing to disk.
  * **Per-Layer Collision Prevention**: Strictly chains vector segments within their respective layer, eliminating cross-wiring between touching bodies or coincident edges.

* **Automatic Watermark & Export Details Stripping**:
  * Automatically post-processes every exported DXF to purge the SOLIDWORKS educational/student watermark, export details block, and annotation text so your CAM software receives **100% clean cutting vectors**.

* **Smart CAM Grouping (Pocket Depth Overlay)**:
  * Parallel faces belonging to the same body (such as pocket floors of varying depths and top reference faces) are projected onto a common orthonormal coordinate frame and overlaid in a single DXF file.
  * Preserves 1:1 relative XY positions and offsets for 2.5D/3D CAM toolpath generation.

* **Intelligent File Naming**:
  * Format: `[PartName]_[BodyName]_[Index].dxf` or `[PartName]_[Index].dxf`
  * Body name is included **only** if the body has a custom logical name (i.e. renamed by the user and differing from the last feature applied to that body). Default feature names (e.g. `Boss-Extrude1`, `Sheet-Metal1`) are cleanly omitted.
  * Automatically assigns collision-free numbers (`_01`, `_02`, ...) to prevent accidental file overwrites.

* **Regular & Sheet Metal Support**:
  * Fully supports standard solid bodies, multibody weldments/parts, and sheet metal bodies (formed or flattened).

---

## 📁 Files in this Directory

* **`ExportFacesToDXF.bas`**: Complete VBA macro source code.
* **`CAM_DXF_Icon_24.bmp`**: 24x24 custom toolbar button icon.
* **`CAM_DXF_Icon_16.bmp`**: 16x16 custom toolbar button icon.
* **`README.md`**: Complete documentation and step-by-step setup guide.

---

## 🛠️ Step-by-Step Installation Guide

### Step 1: Update the Macro File in SOLIDWORKS (15 seconds)
1. Open **SOLIDWORKS**.
2. In the top menu, click **Tools > Macro > Edit...**
3. In the file dialog:
   * Navigate to: `C:\Users\dsg12\OneDrive\Documents\solidworks macros\`
   * Select: `ExportFacesToDXF.swp`
   * Click **Open**.
4. In the VBA Editor window (*Microsoft Visual Basic for Applications*), select all code (`Ctrl + A`) and press `Delete` to clear it.
5. Open [`ExportFacesToDXF.bas`](ExportFacesToDXF.bas) in Notepad (or copy its contents), select all, and paste it into the VBA editor window.
6. Press `Ctrl + S` to save, then close the VBA editor window.

---

### Step 2: Add a 1-Click Button to your Toolbar / CommandManager (if not already added)
1. In SOLIDWORKS, right-click on any empty space on the CommandManager / Ribbon tabs and select **Customize...** (or go to **Tools > Customize...**).
2. Click the **Commands** tab.
3. In the Categories list on the left, click **Macro**.
4. In the right pane, locate the **New Macro Button** (blue ribbon with a green play triangle).
5. Drag and drop this icon onto any CommandManager tab (e.g. *Features*, *Evaluate*, or *Sheet Metal*) or onto any toolbar.
6. In the **Customize Macro Button** dialog:
   * **Macro Path**: Click `...` and select `C:\Users\dsg12\OneDrive\Documents\solidworks macros\ExportFacesToDXF.swp`.
   * **Method**: Ensure `main` is selected.
   * **Tooltip / Prompt**: Type `Export Selected Faces to DXF for CAM`.
   * **Button Image**: Click **Choose Image...** and select `CAM_DXF_Icon_24.bmp` from this directory.
7. Click **OK**, then click **OK** to close the Customize window.

---

### Step 3: (Optional) Assign a Keyboard Shortcut
1. Go to **Tools > Customize... > Keyboard** tab.
2. In the search box, type `ExportFacesToDXF`.
3. In the Shortcut column, press your preferred shortcut key combination (for example: `Ctrl + Shift + D` or `Alt + D`).
4. Click **OK**.

---

## 🎯 How to Use

1. Open any **Part** or **Assembly** in SOLIDWORKS.
2. Hold `Ctrl` and select all planar faces you wish to export:
   * Can include top faces, stepped faces, and pocket floors.
3. Click your **CAM DXF** toolbar button (or press your shortcut key).
4. When prompted, choose your export mode:
   * **Mode 1 (Default)**: Smart CAM Grouping (parallel faces overlaid per body with `OUTLINE` + `FACE_1`, `FACE_2`...; non-parallel or different bodies into separate files).
   * **Mode 2**: Single Combined DXF (all selected faces from all bodies/parts into 1 combined DXF with isolated per-face layers and an overall `OUTLINE` layer).
   * **Mode 3**: Separate DXF per face.
5. Confirm the destination folder (pre-filled to your model's directory) or click Cancel to browse.
6. All DXF files are generated with:
   - Dedicated `OUTLINE` layer (outer silhouette)
   - Distinct `FACE_k` layers with AutoCAD color coding
   - 100% closed vectors (`LWPOLYLINE 70 = 1`)
   - Native circles for drill holes
   - Educational watermarks and export annotations completely purged
