# SOLIDWORKS CAD, Fixtures & DFM Plugin

This Antigravity plugin packages the `solidworks-mcp` server, machine-readable shop and vendor engineering catalogs (`data/shop_profile.json`, `data/sendcutsend_catalog.json`), always-on modeling & DFM rules (`rules/AGENTS.md`), and 9 specialized CAD, fixture, tree-hygiene, DFM, add-in, and macro skills.

## Bundled Components

- **MCP Server (`solidworks-mcp`)**: 64-bit `.NET Framework 4.8` stdio server attaching to `SldWorks.Application` via early-bound COM Interop (`43` structured tools + transactional `sw_execute_csharp`).
- **Shop & Vendor Catalogs (`data/`)**: Exact local shop equipment, bit library, McMaster heat-set inserts, Gridfinity specs, electronics/electrical specs, SendCutSend gauge/bend tables, and full MCP architecture/tool map.
- **Skills (`skills/`)**:
  - `solidworks-cad-modeling` (with `workflows/end_to_end_workflows.md`)
  - `solidworks-fixtures-and-imports`
  - `solidworks-feature-tree-hygiene`
  - `dfm-3d-printing`
  - `dfm-cnc-carving`
  - `dfm-sheet-metal-sendcutsend`
  - `dfm-woodworking`
  - `solidworks-addins`
  - `solidworks-macros`
