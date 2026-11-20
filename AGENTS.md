# SOLIDWORKS Macros & MCP Plugin Repository Rules

These rules apply when developing or maintaining the SOLIDWORKS add-ins, VBA macros, MCP server (`src/SolidWorksMcpServer`), and plugin skills/rules (`.agents/plugins/solidworks`) in this repository.

## 1. Always Ship Plugin Enhancements End-to-End (Mandatory)
Whenever you modify `src/SolidWorksMcpServer/**` or `.agents/plugins/solidworks/**`, complete the entire delivery pipeline automatically without waiting to be asked (unless the user explicitly asks to hold off):
1. **Build** (when C# changed): `powershell -ExecutionPolicy Bypass -File scripts\Build-McpServer.ps1 -Configuration Release` (verify `Build SUCCESS` with 0 errors).
2. **Test**: `python tests\run_checks.py` (all unit tests, Roslyn compile checks, and VBA static lint must pass; keep every MCP tool name `<= 33` chars and update tool counts/schemas in `tests/test_mcp_server.py`).
3. **Install Globally** (so other workspaces get the update immediately): `powershell -ExecutionPolicy Bypass -File scripts\Install-Plugin.ps1 -SkipBuild` (deploys to `C:\Users\dsg12\.gemini\config\plugins\solidworks` and stops stale `SolidWorksMcpServer` processes).
4. **Commit & Push**: Stage intended changes, write a concise commit message, and push to `origin/main`.
5. **Reload Reminder**: Remind the user to toggle/reload `[solidworks](customization://solidworks)` in the Plugins Manager after `SolidWorksMcpServer.exe` changes (or restart SOLIDWORKS via 3DEXPERIENCE after `SolidWorks3DPrintDFMAddin.dll` / `SolidWorksJoineryAddin.dll` changes).

## 2. SOLIDWORKS 2026 MCP Server COM Safety Invariants
- **Never set `app.CommandInProgress = true`** anywhere in `SolidWorksMcpServer` (causes native `0xC0000005` AV in `slduiu:uiModelDoc_c::conditionalBlockCmd` during `InsertSketch`).
- **Never pass `swRebuildOnActivation_e.swUserDecision`** to `ActivateDoc3` (pops a modal dialog that blocks the COM STA thread until `3m0s` timeout); always use `SwConnectionManager.ActivateDocumentSafe` / `swDontRebuildActiveDoc`.
- **Always marshal `IModelDoc2.RayIntersections` bodies as `DispatchWrapper[]`** (`new[] { new DispatchWrapper(body) }`), never raw `object[]` (avoids `0x80010105 RPC_E_SERVERFAULT`).
- **Suppress UI updates during bulk assembly edits**: Wrap multi-step assembly mutations in `SwHelper.SuppressUiUpdates(doc)` (disables both `FeatureManager.EnableFeatureTree` and `IModelView.EnableGraphicsUpdate` to mitigate `SWCEFComWrapper` `libcef` crashes) and batch deletions into a single `DeleteSelection2(0)` call.
- **Keep `mate_health` proactive**: Any new or modified assembly mutation/save tool must return `AssemblyMateHealthAuditor.BuildCompactSummary(doc)` as `mate_health`.
- **No unbounded all-to-all interference checks**: Require explicit component subsets (`include_components`) or entity pairs in `sw_measure_clearance`.

## 3. SOLIDWORKS Connected Session Constraint
- SOLIDWORKS Connected on this machine launches/restarts through the 3DEXPERIENCE portal in Microsoft Edge (`scripts\Launch-SolidWorks3DExperience.ps1` / `sw_connect_status(launch_if_closed=true)`), not by spawning bare `SLDWORKS.exe`.
