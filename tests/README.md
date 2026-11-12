# Pre-Flight VBA Linting & Tests

This folder provides automated offline checks for `.bas` SOLIDWORKS macro source files before copying changes into `.swp` files or committing.

## Why This Exists

Microsoft VBA7 does not ship a standalone CLI compiler, and the SOLIDWORKS VBA Editor leaves **Compile On Demand** enabled by default. Under Compile On Demand, syntax or type errors inside helper procedures (such as reserved-keyword variable names like `oR`, duplicate `Dim`s, or `ByRef` mismatches) may not surface until execution enters that procedure at runtime.

## Quick Usage

Run both the static VBA linter and unit/geometry tests from the repository root (requires Python 3, zero external packages):

```powershell
python tests/run_checks.py
```

Or run the linter by itself on any `.bas` file:

```powershell
python tests/lint_vba.py ExportFacesToDXF.bas
```

## What `lint_vba.py` Checks

- `Option Explicit` presence and `Type` block placement before procedures
- Balanced `Sub`/`Function` definitions and control-flow blocks (`If`/`For`/`Do`/`Select`/`With`)
- Duplicate `Dim` declarations inside a procedure
- Reserved VBA keywords used as variables, parameters, `Const`s, or `Type` fields (e.g. `oR` colliding with `Or`)
- Undeclared local identifiers / loop variables under `Option Explicit`
- `ByRef` argument mismatches (such as passing untyped/expression `Variant` lookups to strictly typed `ByRef` parameters)

## Automated `.swp` Creation / Updating (`scripts/Build-Swp.ps1`)

Instead of manually opening the SOLIDWORKS VBA Editor and copy-pasting [ExportFacesToDXF.bas](../ExportFacesToDXF.bas) into `ExportFacesToDXF1`, run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Build-Swp.ps1
```

This creates (from [template_ExportFacesToDXF.swp](../scripts/template_ExportFacesToDXF.swp) if missing) or updates [ExportFacesToDXF.swp](../ExportFacesToDXF.swp) directly using `[MS-OVBA]` + Windows built-in `ole32.dll` (`StgOpenStorage` / `StgCreateDocfile`):
- Preserves `ThisLibrary`, project/module names (`ExportFacesToDXF` / `ExportFacesToDXF1`), and SOLIDWORKS type library references
- Compresses `ExportFacesToDXF.bas` into `VBA/ExportFacesToDXF1` and verifies round-trip decompression fidelity
- Clears stale p-code caches (`MODULEOFFSET = 0`, `_VBA_PROJECT` `Version = 0xFFFF`) so SOLIDWORKS VBA7 compiles cleanly from source on load

*(Close the macro inside the SOLIDWORKS VBA Editor if it is currently open for editing so `.swp` isn't write-locked.)*

---

## Automated Live SOLIDWORKS COM Test (`scripts/Test-SolidWorksCom.ps1`)

With SOLIDWORKS running (normal non-elevated user session matching your PowerShell elevation), run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Test-SolidWorksCom.ps1
```

What this automates via `SldWorks.Application` COM (`ISldWorks::RunMacro2`):
1. Rebuilds `ExportFacesToDXF.swp` from `ExportFacesToDXF.bas` (unless `-SkipBuild` is passed).
2. Calls `ExportFacesToDXF1.CompileCheckOnly` via `RunMacro2` to verify full VBA7 compilation inside SOLIDWORKS.
3. Creates a temporary stepped/pocketed test `.SLDPRT` in `%TEMP%` (leaving any open user parts untouched), selects its parallel planar faces, runs `ExportFacesToDXF1.RunAutomatedFromTempConfig` headlessly in **Mode 1 (`0.25in` overcut)** and **Mode 3 (`6mm` overcut)**, and asserts closed `LWPOLYLINE` (`70=1`), `OUTLINE` dimensions, `FACE_*` overcut extensions, and cleanup.

---

## VBA Trust Mode & Security Settings Explained

If you have seen **"Trust access to the VBA project object model"** (`AccessVBOM`) mentioned for COM VBA automation:
- **Microsoft Office vs. SOLIDWORKS:** `AccessVBOM` is a **Microsoft Office Trust Center** setting (`File > Options > Trust Center > Trust Center Settings > Macro Settings > Trust access to the VBA project object model`, or registry `HKCU\Software\Microsoft\Office\<ver>\Excel\Security\AccessVBOM = 1`). SOLIDWORKS VBA 7.1 (`VBE7.DLL` / `apc71.dll`) does **not** have an `AccessVBOM` checkbox or Office-style Trust Center UI, nor does `ISldWorks` expose `.VBE` over external COM.
- **Why `Build-Swp.ps1` avoids Office Trust issues:** Because `Build-Swp.ps1` writes `[MS-OVBA]` streams directly into the `.swp` OLE2 container instead of driving `VBIDE` through Office or UI automation, you do **not** need `AccessVBOM` enabled.
- **Settings inside SOLIDWORKS & VBA 7.1 that do matter:**
  1. **Uncheck `Compile On Demand` (strongly recommended):** Open SOLIDWORKS VBA Editor (`Alt+F11`) → **Tools > Options > General** → uncheck **Compile On Demand** (registry: `HKCU\Software\Microsoft\VBA\7.1\Common\CompileOnDemand = 0`). Without this, `CompileCheckOnly` only compiles `CompileCheckOnly` itself until another procedure runs.
  2. **SOLIDWORKS Macro Execution (`swRunMacroError_MacrosAreDisabled`):** On enterprise-managed installs where macros are blocked by group policy/admin image, `RunMacro2` returns error code `2`; otherwise SOLIDWORKS allows `.swp` macros to run by default.

## SOLIDWORKS VBA Editor Setup (Manual Fallback)

Because offline Python checks cannot replace the real VBA7 / SOLIDWORKS type-library compiler (`sldworks.tlb`, `swconst.tlb`), when editing manually inside the SOLIDWORKS VBA Editor (`Alt+F11`):

1. Open **Tools > Options > General** and **uncheck `Compile On Demand`**.
   This forces VBA to compile the entire project up front before running `Sub main`.
2. After pasting updated `.bas` code into the module, run **Debug > Compile ExportFacesToDXF**, then press `Ctrl+S` to save compiled p-code inside the `.swp` macro.

## Optional Git Pre-Commit Hook

To automatically block commits when `.bas` checks fail, add `.git/hooks/pre-commit`:

```sh
#!/bin/sh
python tests/run_checks.py || exit 1
```
