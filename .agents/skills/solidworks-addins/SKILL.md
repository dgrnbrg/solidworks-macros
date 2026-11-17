---
name: solidworks-addins
description: >-
  Use this skill when creating, editing, building, registering, debugging, or
  testing C# SOLIDWORKS Add-ins (ISwAddin), PropertyManager Pages
  (IPropertyManagerPage2Handler9), or MacroFeatures (ISwComFeature).
---

# SOLIDWORKS C# Add-in Development & Testing Guide

## 1. Project & Build Setup (`.csproj` & `scripts/Build-Addin.ps1`)

- **Target Framework & Platform**: Target `.NET Framework 4.0` (or `4.8`) with `<PlatformTarget>x64</PlatformTarget>` and `<RegisterForComInterop>false</RegisterForComInterop>`.
- **SOLIDWORKS Interop References**: Reference the 64-bit redistributable assemblies from `C:\Program Files\Dassault Systemes\SOLIDWORKS 3DEXPERIENCE R2026x\SOLIDWORKS\api\redist\` with `<EmbedInteropTypes>False</EmbedInteropTypes>` and `<Private>True</Private>`:
  - `SolidWorks.Interop.sldworks.dll`
  - `SolidWorks.Interop.swconst.dll`
  - `SolidWorks.Interop.swpublished.dll`
- **`Environment` Namespace Ambiguity**: Always qualify `System.Environment` explicitly (`SolidWorks.Interop.sldworks` also defines an `Environment` interface, causing compiler error `CS0104` if unqualified).
- **NTFS Hot-Rebuild Trick**: When `SLDWORKS.exe` is running, Windows locks loaded `.dll` and `.pdb` files against overwriting (`MSB3027`), **but NTFS allows renaming locked files**. Before invoking MSBuild in `scripts/Build-Addin.ps1`, rename any existing `.dll` / `.pdb` to `*.bak.<ticks>` (and attempt cleanup of unlocked `.bak` files) so MSBuild can write the fresh binary immediately.
  - Note: `.NET Framework` cannot unload an assembly from `SLDWORKS.exe`'s AppDomain once loaded—SOLIDWORKS must still be restarted to execute the newly built DLL.

## 2. COM Registration & Add-in Discovery (`scripts/Register-Addin.ps1` & `[ComRegisterFunction]`)

- **`HKEY_LOCAL_MACHINE` Requirement**: SOLIDWORKS **only** enumerates Add-ins listed under `HKLM` for **Tools > Add-Ins** (`HKCU\Software\SolidWorks\AddIns` is ignored):
  - `HKLM\SOFTWARE\SolidWorks\AddIns\<GUID>` (`(Default)` = `DWORD 1`, `Default` = `DWORD 1`, `Title`, `Description`)
  - `HKLM\SOFTWARE\SolidWorks\SOLIDWORKS 2026\Addins\<GUID>` (`(Default)` = `DWORD 1`, `Default` = `DWORD 1`, `Title`, `Description`)
  - `HKCU\Software\SolidWorks\AddInsStartup\<GUID>` (`(Default)` = `DWORD 1` to load at startup)
- **UAC Auto-Elevation**: In `scripts/Register-Addin.ps1`, check `WindowsPrincipal.IsInRole([WindowsBuiltInRole]::Administrator)` and self-elevate via `Start-Process -FilePath "powershell.exe" -ArgumentList ... -Verb RunAs -Wait -PassThru` so double-clicking `Install-Addin.bat` prompts via UAC automatically.
- **`regasm /regfile` Trap**: Never rely on `regasm /regfile` to execute `[ComRegisterFunction]`—`/regfile` only exports standard `CLSID` entries and silently skips custom `[ComRegisterFunction]` methods. Always invoke 64-bit `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\regasm.exe /codebase "<dllPath>"` while elevated, AND explicitly write the `HKLM` and `HKCU\...\AddInsStartup` registry keys in the registration script.
- **When to Re-register**: Recompiling an existing add-in in-place via `scripts/Build-Addin.ps1` does **not** require re-running `regasm` unless you add a new `[ComVisible(true)]` class (e.g., a new `ISwComFeature`), change a `[Guid(...)]` or `AssemblyVersion`, or move the output DLL path.
- **Toolbar Refresh**: Pass `IgnorePreviousVersion = true` to `_cmdMgr.CreateCommandGroup2(...)` during development so SolidWorks refreshes added or renamed command buttons immediately on startup.
- **Avoid `UpdateFunction` Re-Entrancy During File Open (`CAmApp::startFileOpen`)**: Pass `""` (empty string) for the `UpdateFunction` parameter in `cmdGroup.AddCommandItem2(...)` and validate `_swApp.ActiveDoc` inside the click handler instead. Registering an enable/update callback causes SolidWorks' MFC idle loop to invoke `.NET` `IDispatch::Invoke` polling re-entrantly inside `su_WaitForSingleObject` during `CAmApp::startFileOpen`, while `_swApp.ActiveDoc` is mid-load.

## 3. PropertyManager Page (`IPropertyManagerPage2Handler9`) Critical Rules

- **Prevent Blank Pages (Only Checkmark & X Visible)**:
  - `AddGroupBox` defaults to invisible unless `swGroupBoxOptions_Visible` (`4`) is included:
    ```csharp
    int grpOpts = (int)(swAddGroupBoxOptions_e.swGroupBoxOptions_Visible |
                        swAddGroupBoxOptions_e.swGroupBoxOptions_Expanded);
    ```
  - `AddControl2` defaults to hidden and disabled unless `swControlOptions_Visible | swControlOptions_Enabled` (`1 | 2 = 3`) is passed:
    ```csharp
    int ctrlOpts = (int)(swAddControlOptions_e.swControlOptions_Visible |
                         swAddControlOptions_e.swControlOptions_Enabled);
    ```
- **Prevent COM Crash on Page Interaction / Close**:
  - Always include `swPropertyManagerPageOptions_e.swPropertyManagerOptions_LockedPage` (`4`) in `CreatePropertyManagerPage`:
    ```csharp
    int options = (int)(swPropertyManagerPageOptions_e.swPropertyManagerOptions_OkayButton |
                        swPropertyManagerPageOptions_e.swPropertyManagerOptions_CancelButton |
                        swPropertyManagerPageOptions_e.swPropertyManagerOptions_LockedPage);
    ```
  - **Hold a Strong Reference**: `_page.Show()` is **modeless** and returns immediately. Always store the `IPropertyManagerPage2Handler9` instance in a class/static field (e.g., `private static MyPMPHandler _activePmp;`) on `SwAddin`. Storing it only in a local variable lets the .NET GC collect the COM Callable Wrapper while the page is open, causing an unhandled access violation when SolidWorks fires callbacks.
- **Selection & Feature Lifecycle (`OnClose` vs `AfterClose`)**:
  - Capture all control values and `ISelectionMgr` entities by mark (`GetSelectedObjectCount2(mark)`, `GetSelectedObject6(i, mark)`) inside `OnClose(int Reason)` **before** SolidWorks closes the page and clears selection marks.
  - Execute sketch creation, cut/extrude generation, and MacroFeature commits inside `AfterClose()` **after** the PropertyManager Page has fully exited.
- **Interop Type Casts**:
  - `IEntity.Select4(bool Append, SelectData Data)` requires casting: `SelectData selData = (SelectData)selMgr.CreateSelectData();`.
  - Toggling control enablement requires casting to the base control interface: `((IPropertyManagerPageControl)numBox).Enabled = isChecked;`.

## 4. Clean B-Rep Sketching & FeatureManager Tree Nesting (Hole-Wizard Pattern)

- **Avoid 3D Sketches**:
  - Sketch directly on existing planar faces (`IFace2`) whenever possible (e.g., recessed counterbore faces or a cylindrical hole's adjacent perpendicular planar cap face). When generating multiple sequential cuts on the same planar face (e.g., Tier 1 and Tier 2 crisscross cuts), create **all** 2D sketches on the planar face **before** executing the `FeatureCut4` operations.
  - If a cylindrical hole has no adjacent perpendicular planar face, select one of its circular edges (`ICurve.IsCircle()`) and call `InsertRefPlane((int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident, 0, 0, 0, 0, 0)`.
  - If a 3D sketch line + point is ever required as a last resort for `InsertRefPlane(Perpendicular, 0, Coincident, 0, 0, 0)`, extract the point from the line itself via `((ISketchLine)lineSeg).GetStartPoint2()`—**never** call `CreatePoint` at the line's endpoint (creating a duplicate point makes `InsertRefPlane` return `null`).
- **Sketch Coordinate Transformation (`ModelToSketchTransform`)**:
  - Never assume a sketch's local 2D X/Y orientation on a face or reference plane. Compute all profile coordinates in 3D model space (in **meters**) and project every point through `activeSketch.ModelToSketchTransform` via `IMathUtility.CreatePoint` + `IMathPoint.MultiplyTransform`.
  - Wrap sketch creation in `sketchMgr.AddToDB = true;` ... `sketchMgr.AddToDB = false;` to disable interactive inference snapping and accelerate creation.
- **Single-Direction Cut Fallback**:
  - When cutting into a boundary face or plane, try `FeatureCut4(true, false, false, ...)` (`Dir = false`) first; if it returns `null` (meaning `Dir = false` pointed into empty air), clear selection, re-select the sketch, and retry with `Dir = true`.
- **Hole-Wizard Style Feature Nesting (`ISwComFeature` + `MakeSubFeature`)**:
  - Define a separate `[ComVisible(true)]` `ISwComFeature` class with its own `[Guid(...)]` per distinct command/feature type so right-clicking the feature in the FeatureManager Design Tree -> **Edit Feature** opens its dedicated PropertyManager Page via `Edit(app, modelDoc, feature)`.
  - Store serialized configuration (`DfmConfig` or similar) in `IMacroFeatureData` string parameters and nest generated child cuts/sketches under the MacroFeature via `macroFeat.MakeSubFeature((Feature)child)` (falling back to `InsertFeatureTreeFolder2` if any child feature type cannot be absorbed directly).

## 5. Verification & Diagnostic Logging

- **Add-in Diagnostic Logging**: Write timestamped breadcrumbs and exception stack traces to `%AppData%\<AddinName>\debug.log` in `ConnectToSW`, `Show`, `OnClose`, and `AfterClose` so silent COM or SolidWorks failures can be diagnosed immediately without attaching a debugger.
- **Direct .NET Assembly Verification**: Verify compiled C# geometry and math methods directly from PowerShell without launching SolidWorks:
  ```powershell
  $dll = Resolve-Path "src\<AddinProject>\bin\x64\Release\<AddinProject>.dll"
  [System.Reflection.Assembly]::LoadFrom($dll.Path) | Out-Null
  ```
- **Repository Check Suite**: Run the repository test suite after any changes:
  ```powershell
  python tests/run_checks.py
  ```
