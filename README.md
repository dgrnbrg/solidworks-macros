# SOLIDWORKS Macros

A collection of productivity macros for SOLIDWORKS designed to streamline CAD/CAM manufacturing workflows.

---

## Available Macros

* **[ExportFacesToDXF](ExportFacesToDXF.md)** — Exports selected planar faces from parts, multibody models, or assemblies into clean, production-ready 2D DXF files with dedicated outline layers, per-face depth layers, and 100% closed vectors for CNC CAM tools.

---

## Quick Install (One-Click)

Whenever you pull the latest changes or edit the source code:

1. **Double-click `Install-Macro.bat`** (or `Install-Macro.cmd`) in the repository root.
2. The installer automatically compiles the latest `ExportFacesToDXF.bas` into `ExportFacesToDXF.swp`.
3. If `ExportFacesToDXF.swp` is open/locked in SOLIDWORKS, the installer prompts you to close the VBA Editor (`Alt+F11`) or restart SOLIDWORKS and press Enter to retry.
4. If SOLIDWORKS is running, the installer offers to run live COM compile verification on the spot.

---

## Installation Guide (First-Time Setup)

Follow these steps to set up the macro with a one-click button on your SOLIDWORKS CommandManager or toolbar.

### Step 1: Install Macro Files
Double-click `Install-Macro.bat` to generate the latest `ExportFacesToDXF.swp` file in your macro directory (e.g. `C:\Users\<YourUser>\Documents\solidworks macros\`). Keep `CAM_DXF_Icon_24.bmp` in the same directory.

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
