---
name: solidworks-macros
description: >-
  Use this skill when writing, editing, debugging, compiling (.bas to .swp),
  linting, or testing SOLIDWORKS VBA macros (.swp / .bas) or automating
  SOLIDWORKS via COM Interop.
---

# SOLIDWORKS VBA Macro Development & Testing Guide

## 1. Architecture & Source-of-Truth Workflow

- **Source in `.bas`, Binary in `.swp`**: Maintain human-readable VBA code in `<MacroName>.bas` (`Option Explicit` at the top, all `Type` / `Enum` / module-level `Const` and `Dim` declarations before any `Sub` or `Function`) and compile it into `<MacroName>.swp`.
- **Dual-Mode Non-Interactive Entrypoints**: Alongside interactive `Sub main()`, provide headless entrypoints for automated compile checks and end-to-end COM tests without `MsgBox` / `InputBox` dialogs blocking execution:
  - `Public Sub CompileCheckOnly()` — writes `"OK|COMPILED"` to `%TEMP%\<MacroName>_compile_ok.txt` so `ISldWorks::RunMacro2` verifies full-project compilation cleanly.
  - `Public Sub RunAutomatedFromTempConfig()` — reads key=value settings (e.g., `mode`, `overcut`, `folder`, `result`) from `%TEMP%\<MacroName>_test.cfg` and writes pass/fail status to `%TEMP%\<MacroName>_test_result.txt`.

## 2. VBA Language & SOLIDWORKS API Pitfalls

- **Case-Insensitive Reserved Keywords**: VBA identifiers are case-insensitive. Never name variables, parameters, `Const`s, or UDT fields after VBA keywords (even with mixed case): e.g., `oR` (`Or`), `mod` (`Mod`), `in` (`In`), `len` (`Len`), `line` (`Line`), `circle` (`Circle`), `scale` (`Scale`), `width` (`Width`), `text` (`Text`), `stop` (`Stop`), `next` (`Next`), `step` (`Step`), `date` (`Date`), `name` (`Name`), `error` (`Error`).
- **`ByRef` Default Parameter Trap**: VBA procedure parameters default to `ByRef`. Passing a `Collection` / `Dictionary` lookup, array/UDT expression, or literal to a typed `ByRef` parameter causes a type-mismatch compile/runtime error (`ByRef argument type mismatch`). Explicitly declare value parameters `ByVal`, or assign expressions to a typed local variable before passing by reference.
- **VBA Pre-Flight Static Linting**: Always run `python tests/run_checks.py` (which invokes `tests/lint_vba.py`) before building or committing `.bas` edits. It catches reserved keywords, missing `Option Explicit`, duplicate `Dim`s, `Type` blocks after procedures, unbalanced control flow, undeclared locals, and `ByRef` mismatches before SOLIDWORKS ever opens the macro.
- **SOLIDWORKS Internal Units**: All SOLIDWORKS API lengths/coordinates are in **meters** (`1 in = 0.0254 m`, `1 mm = 0.001 m`), and angles are in **radians**.
- **DXF Export Quirks (`ExportToDWG2`)**:
  - SOLIDWORKS can export straight split edges as degree-1 linear `SPLINE` entities (`group 71 = 1`, 2 control points) instead of `LINE` entities. When reconstructing closed contours from DXF output, parse `LINE`, `ARC`, `CIRCLE`, `LWPOLYLINE`, `POLYLINE`, **and** degree-1 linear `SPLINE` entities.
  - Assert closed loops, layer placement (`OUTLINE` vs depth/pocket layers), and overcut geometry in offline unit tests (`tests/`).

## 3. Building `.swp` Files Programmatically (`[MS-OVBA]` + OLE2)

SOLIDWORKS `.swp` files are OLE2 Compound Documents hosted by Microsoft APC (`apc71.dll`), with internal storage path:
`apc\The VBA Project\_VBA_Project\VBA\{dir, ThisLibrary, <ModuleName>, _VBA_PROJECT, PROJECT, PROJECTwm}`.

- **Critical APC Integrity Rule**:
  - **NEVER** truncate `_VBA_PROJECT` to the 7-byte Office `[MS-OVBA]` header (`CC 61 FF FF 00 03 00`).
  - **NEVER** strip `ThisLibrary`'s `PerformanceCache`.
  - **NEVER** repackage the OLE2 root storage with `StgCreateDocfile` + `CopyTo`.
  - Violating any of these causes SOLIDWORKS APC to fail with `"Cannot open <file>.swp"` and `RunMacro2` returning `ok=False, errCode=0`.
- **Safe In-Place Update Procedure** (implemented in `scripts/Build-Swp.ps1`):
  1. Seed from a known-valid SOLIDWORKS `.swp` template (e.g., `scripts/template_ExportFacesToDXF.swp`) if the output file is missing or has a truncated `_VBA_PROJECT` (`<= 16` bytes).
  2. Open the working `.swp` in-place via `ole32.dll` `StgOpenStorage(..., STGM_READWRITE | STGM_SHARE_EXCLUSIVE, ...)`.
  3. Decompress `dir` (`[MS-OVBA]` RLE), zero **only** the target module's `MODULEOFFSET` (`0x0031` tag) to `0`, and recompress `dir` (leaving `ThisLibrary` and `_VBA_PROJECT` untouched).
  4. Compress `Attribute VB_Name = "<ModuleName>"\r\n` + CRLF-normalized CP1252 `.bas` source with `[MS-OVBA]` RLE, verify round-trip decompression, write to `<ModuleName>`, and commit storages in-place.
- **Double-Click Installer (`Install-Macro.bat` / `scripts/Install-Macro.ps1`)**: Checks for exclusive write locks (prompting the user to close VBE / SOLIDWORKS if locked), builds the `.swp`, and optionally triggers live COM compile verification if SOLIDWORKS is running.

## 4. SOLIDWORKS COM Automation & Live Testing

- **64-bit PowerShell `IDispatch` Bug (`0x8002802B TYPE_E_ELEMENTNOTFOUND`)**:
  - Do **not** invoke methods directly on `System.__ComObject` from 64-bit PowerShell (`SldWorks.Application` TypeLib `win64` registry lookup fails).
  - Do **not** pass `System.__ComObject` across PowerShell-to-C# method boundaries (fails casting to `ISldWorks`).
  - Instead, reference `SolidWorks.Interop.sldworks.dll` (from `<SOLIDWORKS>\api\redist\`) inside an `Add-Type` C# helper class, acquire `[Marshal]::GetActiveObject` (`Marshal.GetActiveObject("SldWorks.Application")`) inside C#, hold `_swApp` as `ISldWorks` in C#, and expose static helper methods to PowerShell (see `scripts/Test-SolidWorksCom.ps1`).
- **Always Unload Macro After `RunMacro2`**:
  - Pass option `2` (`swRunMacroUnloadAfterRun`) rather than `1` (`swRunMacroDefault`):
    ```csharp
    bool ok = _swApp.RunMacro2(swpPath, moduleName, procName, 2, out errCode);
    ```
  - Option `1` keeps the `.swp` loaded and file-locked in `sldworks.exe`, preventing subsequent `.swp` rebuilds until the VBA Editor or SOLIDWORKS is closed.
- **Eager Compilation (`CompileOnDemand = 0`)**:
  - For `CompileCheckOnly` to compile all procedures eagerly (rather than only executed paths), set `HKCU:\Software\Microsoft\VBA\7.1\Common\CompileOnDemand = 0` (in SOLIDWORKS VBE: **Tools > Options > General > uncheck Compile On Demand**).
  - Office's `AccessVBOM` Trust Center setting does not apply when building `.swp` files via direct OLE2 structured storage.
- **Desktop Session Isolation Note**: `Marshal.GetActiveObject("SldWorks.Application")` only sees instances running in the same Windows desktop session; automated sandboxed shells cannot bind to an interactive desktop SOLIDWORKS instance (`MK_E_UNAVAILABLE`), so live COM tests must run in the user's desktop session.
- **Never Force-Kill `SLDWORKS.exe` (3DEXPERIENCE License Protection)**: Never run `Stop-Process -Force`, `taskkill /F`, or `Process.Kill()` on `SLDWORKS.exe` (force-killing prevents SOLIDWORKS Connected from checking its seat back into the 3DEXPERIENCE server and triggers a ~15–20 minute cloud license lock). If a modal dialog ever appears, only dismiss the child popup window via `WM_CLOSE` (`0x0010`) or `Esc` (`VK_ESCAPE`).

## 5. Standard Verification Workflow

1. **Offline Linter & Unit Tests**:
   ```powershell
   python tests/run_checks.py
   ```
2. **Build / Update `.swp`**:
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts/Build-Swp.ps1
   ```
3. **Live SOLIDWORKS COM Compile + Export Tests** (run in user's desktop session while SOLIDWORKS is open):
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts/Test-SolidWorksCom.ps1
   ```
