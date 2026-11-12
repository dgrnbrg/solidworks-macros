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

## SOLIDWORKS VBA Editor Setup (Recommended)

Because offline Python checks cannot replace the real VBA7 / SOLIDWORKS type-library compiler (`sldworks.tlb`, `swconst.tlb`), also configure the SOLIDWORKS VBA Editor (`Alt+F11`):

1. Open **Tools > Options > General** and **uncheck `Compile On Demand`**.
   This forces VBA to compile the entire project up front before running `Sub main`.
2. After pasting updated `.bas` code into the module, run **Debug > Compile ExportFacesToDXF**, then press `Ctrl+S` to save compiled p-code inside the `.swp` macro.

## Optional Git Pre-Commit Hook

To automatically block commits when `.bas` checks fail, add `.git/hooks/pre-commit`:

```sh
#!/bin/sh
python tests/run_checks.py || exit 1
```
