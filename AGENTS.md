# SOLIDWORKS Macros & MCP Plugin Repository Rules

These rules apply when developing or maintaining the SOLIDWORKS add-ins, VBA macros, MCP server (`src/SolidWorksMcpServer`), and plugin skills/rules (`.agents/plugins/solidworks`) in this repository.

## 1. Plugin Delivery Pipeline & Safe Deploy Window Gate (Mandatory)
Because another agent is often actively using the live `SolidWorksMcpServer.exe` in parallel, **NEVER disrupt the running MCP server without an explicit deploy go-ahead from the user**:

### Phase A — Implement & Non-Destructive Prep (Safe Anytime)
1. Implement the requested edits in `src/SolidWorksMcpServer/**`, `.agents/plugins/solidworks/**`, and `tests/**` (keeping every MCP tool name `<= 33` chars).
2. **Do NOT overwrite `bin\x64\Release\SolidWorksMcpServer.exe`, do NOT run `scripts\Build-McpServer.ps1 -Configuration Release`, and do NOT run `scripts\Install-Plugin.ps1`** (which stops running `SolidWorksMcpServer` processes) until the user approves a safe deploy window.
   - To compile-verify C# changes safely beforehand without touching the live Release binary, build `-Configuration Debug` (`powershell -ExecutionPolicy Bypass -File scripts\Build-McpServer.ps1 -Configuration Debug`).
   - Non-destructive checks (`python tests\run_checks.py`) are safe when they do not depend on a freshly overwritten Release exe.
3. **Pause and ask the user for the go-ahead to deploy** once changes are ready, so they can sync any parallel agent first.

### Phase B — Deploy, Commit & Push (Run Automatically Once User Gives Deploy Go-Ahead)
Once the user gives the go-ahead for a safe deploy window, complete the entire ship pipeline without waiting to be asked again:
1. **Build Release** (when C# changed): `powershell -ExecutionPolicy Bypass -File scripts\Build-McpServer.ps1 -Configuration Release` (verify `Build SUCCESS` with 0 errors).
2. **Test**: `python tests\run_checks.py` (all unit tests, Roslyn compile checks, and VBA static lint must pass).
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
- **Never Force-Kill `SLDWORKS.exe`**: Never run `Stop-Process -Force`, `taskkill /F`, or `Process.Kill()` on `SLDWORKS.exe` (prevents checking its seat back into the 3DEXPERIENCE server and triggers a ~15–20 minute cloud license lock). If a modal dialog ever appears, only dismiss the child popup window via `WM_CLOSE` (`0x0010`) or `Esc` (`VK_ESCAPE`) and exit SOLIDWORKS gracefully via `ISldWorks.ExitApp()` / `CloseMainWindow()`.
- **Crash / Timeout Yield & Debug Ticket**: When SOLIDWORKS crashes or times out and automatic restart/recovery does not succeed immediately on the first attempt, stop and yield to the user (release the session lock) and provide a structured Bug Ticket (document path, exact tool name + JSON/C# arguments, HRESULT/exit code + `crash_journal_path` + `stderr_tail`, suspected API trigger & repro) for another agent to debug & fix.

## 4. Official Manufacturer Datasheets & Specs for Catalog/Skill Updates
- Whenever encoding properties, tolerances, or dimensions for a specific **brand, product line, material, tool, or hardware SKU** into `.agents/plugins/solidworks/data/*.json` or skill files (e.g., MatterHackers, Prusament, Blum, Bits&Bits, Amana, SendCutSend, McMaster-Carr), **look up and verify the official manufacturer TDS / specification sheet first** (`search_web` or vendor docs).
- Never substitute generic category estimates when a specific brand or product is named; if a property (such as $T_g$, cold creep, or friction coefficient) is omitted from the official TDS, state explicitly what comes from the TDS versus engineering guidance.
