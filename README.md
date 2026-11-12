# SOLIDWORKS Macros

A collection of productivity macros for SOLIDWORKS designed to streamline CAD/CAM manufacturing workflows.

---

## Available Macros

* **[ExportFacesToDXF](ExportFacesToDXF.md)** — Exports selected planar faces from parts, multibody models, or assemblies into clean, production-ready 2D DXF files with dedicated outline layers, per-face depth layers, and 100% closed vectors for CNC CAM tools.

---

## Installation Guide

Follow these steps to set up any macro with a one-click button on your SOLIDWORKS CommandManager or toolbar.

### Step 1: Place Macro Files
Keep the macro file (`.swp`) and associated button icon (`.bmp`) in a dedicated folder on your computer (e.g. `C:\Users\<YourUser>\Documents\solidworks macros\`).

*(Optional: If you wish to inspect or edit the VBA source code, open the `.bas` file in any text editor or paste it into the SOLIDWORKS VBA Editor via **Tools > Macro > Edit...**).*

---

### Step 2: Add a Toolbar Button

1. In SOLIDWORKS, right-click any blank area on the CommandManager ribbon (or go to **Tools > Customize...**).
2. Click the **Commands** tab.
3. In the **Categories** list on the left, select **Macro**.
4. In the right pane, locate the **New Macro Button** (blue ribbon icon with a green play triangle).
5. Drag and drop this icon onto any desired CommandManager tab (e.g. *Features*, *Evaluate*, or *Sheet Metal*) or onto any toolbar.
6. In the **Customize Macro Button** dialog that appears:
   * **Macro Path:** Click `...` and browse to your macro file (e.g. `ExportFacesToDXF.swp`).
   * **Method:** Select `main`.
   * **Tooltip / Prompt:** Enter a descriptive label (e.g. `Export Selected Faces to DXF`).
   * **Button Image:** Click **Choose Image...** and select the corresponding `.bmp` icon (e.g. `CAM_DXF_Icon_24.bmp`).
7. Click **OK**, then click **OK** to close the Customize dialog.

---

### Step 3: (Optional) Assign a Keyboard Shortcut

1. Open **Tools > Customize...** and switch to the **Keyboard** tab.
2. In the search field, type the name of the macro (e.g. `ExportFacesToDXF`).
3. Click in the **Shortcut** column and press your preferred key combination (e.g. `Ctrl + Shift + D` or `Alt + D`).
4. Click **OK**.

---

## Development, `.swp` Build & Tests

If you edit [ExportFacesToDXF.bas](ExportFacesToDXF.bas), run the offline linter/tests, rebuild [ExportFacesToDXF.swp](ExportFacesToDXF.swp), and run the live SOLIDWORKS COM test suite:

```powershell
python tests/run_checks.py
powershell -ExecutionPolicy Bypass -File scripts/Build-Swp.ps1
powershell -ExecutionPolicy Bypass -File scripts/Test-SolidWorksCom.ps1
```

See [tests/README.md](tests/README.md) for full details on `.swp` generation, COM testing, and SOLIDWORKS VBA settings (`Compile On Demand` and VBA trust mode).
