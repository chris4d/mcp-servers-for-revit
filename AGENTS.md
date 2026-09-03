# AGENTS.md

Guidance for agent work in this repository — a fork of `mcp-servers-for-revit` that extends the upstream Revit MCP plugin with an **off-axis line inaccuracy detection and remediation toolkit**.

---

## Repository Overview

This repo is a fork of [mcp-servers-for-revit](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit). It retains the upstream MCP server + Revit plugin + command-set architecture and adds an off-axis remediation toolkit plus installer distribution.

| Directory | Purpose |
|---|---|
| `server/` | TypeScript MCP server (exposes tools to AI clients, bridges to the Revit plugin over WebSocket). |
| `plugin/` | C# Revit add-in (`revit_mcp_plugin`). Listens, dispatches commands, hosts the Settings UI. |
| `commandset/` | C# `RevitMCPCommandSet` — core command set (compiles to `RevitMCPCommandSet.dll`). |
| `offaxis-commandset/` | C# `OffAxisCommandSet` — the off-axis detection/fix command set (compiles to `OffAxisCommandSet.dll`), plus standalone ZIP distribution. |
| `installer/` | Inno Setup packaging (`build-installer.ps1` generates `.iss`, compiles `mcp-servers-for-revit-setup.exe`). |
| `scripts/` | Release script (`release.ps1`). |
| `tests/` | Tests. |

The off-axis detector/fixer logic is **promoted into compiled C# commands** (see §Off-Axis Toolkit). The original Roslyn `.csx` prototype scripts are **frozen** in a separate archive repo (see §Frozen Script Archive) — do not treat them as live source.

---

## Off-Axis Toolkit

The core feature of this fork. Detect and align slightly off-axis lines (near 0°, 45°, 90°, 135°, 180°) across Revit model elements.

### Compiled MCP Tools

The 10 commands live in `offaxis-commandset/` and are exposed as MCP tools:

| MCP Tool Name | Type | Purpose | Key Parameters |
|---|---|---|---|
| `detect_off_axis_hybrid` | Detector | Warning-driven fast detection (uses Revit `Document.GetWarnings()`) | (none) |
| `detect_off_axis_lines` | Detector | Geometric scan for lines with angular deviation | `minAngleDeg`, `maxAngleDeg` |
| `detect_spacing_elements` | Detector | 1/4" lattice spacing regularizer detector | `limit` |
| `fix_off_axis_walls_and_beams` | Fixer | Snaps walls & structural framing | `elementIds`, `minAngleDeg`, `maxAngleDeg` |
| `fix_off_axis_grids` | Fixer | Rotates 3D grids about midpoint | `elementIds`, `minAngleDeg`, `maxAngleDeg` |
| `fix_off_axis_reference_planes` | Fixer | Snaps vertical reference plane normals | `elementIds`, `minAngleDeg`, `maxAngleDeg` |
| `fix_off_axis_sketches` | Fixer | Ray-ray closed sketch profile solver (Floors/Ceilings/Roofs) | `hostIds`, `lineIds`, `minAngleDeg`, `maxAngleDeg` |
| `fix_off_axis_inplace` | Fixer | In-place Component Extrusion sketch fixer | `hostIds`, `lineIds`, `minAngleDeg`, `maxAngleDeg` |
| `fix_off_axis_model_lines` | Fixer | Top-level model line & curve chain solver | `elementIds`, `minAngleDeg`, `maxAngleDeg` |
| `fix_spacing_elements` | Fixer | Iterative 1/4" lattice regularizer | `elementIds`, `maxMoveInches` |

### Architecture

- **`offaxis-commandset/Commands/OffAxis/*.cs`** — command declarations.
- **`offaxis-commandset/Services/OffAxis/*.cs`** — `ExternalEventCommandBase` + `IExternalEventHandler`/`IWaitableExternalEventHandler` handlers implementing the logic.
- **`offaxis-commandset/Utils/OffAxis/`** — `OffAxisGeometryUtils.cs` and `SilentFailuresPreprocessor.cs`.
- **`offaxis-commandset/command.json`** — command set declaration (name `OffAxisCommandSet`, developer Scott Mitchell Studio).
- **MCP tool definitions** in `server/src/tools/*.ts` (auto-registered via glob in `server/src/tools/register.ts`).

Commands only live in **one** assembly. Off-axis commands are **not** duplicated in `RevitMCPCommandSet`; the root `command.json` holds only core commands.

---

## Distribution

Two packages are produced:

1. **Full Plugin Installer** (`installer/output/mcp-servers-for-revit-setup.exe`) — built by `installer/build-installer.ps1`. Installs the add-in, both command sets, and configures AI clients (Claude Desktop, Cursor, opencode). `AppVersion` is **derived from the plugin** (`plugin/Properties/AssemblyInfo.cs` `AssemblyFileVersion`), not hardcoded.
2. **Standalone CommandSet ZIP** (`offaxis-commandset/dist/OffAxisCommandSet.zip`) — built by `offaxis-commandset/build-package.ps1`. Contains `OffAxisCommandSet`, `install.ps1`, `uninstall.ps1`, `README.md`. Merges 10 commands into an existing plugin's `commandRegistry.json`, or can be dropped in via Revit MCP Settings -> "Open CommandSet Folder" -> Refresh -> Save.

The installer exposes a **Command Sets selection step** (pre-selects all) plus an AI-client config step.

### Deployment Gotcha — Installer overwrites AI-client configs

Running the installer re-runs `ConfigureClaudeDesktop` / `ConfigureCursor` / `ConfigureOpencode` in `[Code]`. Critically, `ConfigureOpencode` **overwrites `~/.config/opencode/opencode.json` back to `npx -y mcp-server-for-revit`** — the **broken npm package** that crashes on startup with `Cannot find module 'ajv'` (MCP error 32000). The published `mcp-server-for-revit@1.0.0` was never re-published with the `ajv: ^8.17.1` fix (user is not an npm owner; fix is local-only). So any reinstall breaks the opencode MCP connection until the config is pointed back at the local build:

```jsonc
// ~/.config/opencode/opencode.json — point at the LOCAL build
"mcp-server-for-revit": {
  "type": "local",
  "command": ["node", "C:\\dev\\mcp-servers-for-revit\\server\\build\\index.js"],
  "enabled": true
}
```

Verify the local server boots with a manual MCP handshake (`node server/build/index.js` from `server/`; respond to `initialize`; 33 tools register). Note: `better-sqlite3` has **no prebuilt binary for Node 26** and this machine lacks node-gyp/Python/VS, so the 3 sqlite-backed tools (`query_stored_data`, `store_project_data`, `store_room_data`) fail to register — the other 33 tools and the connection work fine.

---

### Future Work - Dynamic tool advertisement (planned, not yet implemented)

The Node server's tool list is **static** (`server/src/tools/register.ts` imports every tool file at startup) while the plugin only dispatches commands present in `Commands\commandRegistry.json` (`RevitCommandRegistry.TryGetCommand` at request time; unknown methods return `Method not found`). Result: the server advertises ~34 tools regardless of what Revit actually registered - users see listed-but-unreachable tools. Desired end state: on socket connect (or handshake), the plugin sends its registered command list, and the Node server filters/folds its tool list accordingly (skip tool registration or return a clear command-not-enabled result). Requires touching `server/src/utils/SocketClient.ts`, `server/src/index.ts` (dynamic registration after connect), and a plugin-side advert message in `plugin/Core/SocketService.cs`. Coordinate with the existing static-registration code path so stdio client UX stays stable when the plugin is briefly offline.

## Off-Axis Toolkit Knowledge (durable)

The following learnings were hard-won during development and remain authoritative for the compiled commands.

### Safety Mechanisms & Thresholds

- **`Application.FailuresProcessing` Delegate Pipeline**: All fixers hook `document.Application.FailuresProcessing` using a lambda delegate (`try ... finally { app.FailuresProcessing -= handler; }`). It deletes warnings (`fa.DeleteWarning(msg)`) to suppress GUI popups and triggers silent rollback (`args.SetProcessingResult(ProceedWithRollBack)`) on errors, eliminating modal UI blocks.
- **Large-Fix Flags**: `FLAG_MOVEMENT_IN = 0.5` in; `FLAG_DEVIATION_DEG = 0.1` deg; predicted swing `= 2·L·sin(Δθ/2)`. Any element exceeding either threshold → `LargeFix = true`.
- **Pre-check filters**: pinned elements; hosted inserts; dimension-locked elements; profiles containing non-Line curves; gaps in profiles > 0.01 ft.

### Key Pitfalls & Critical Learnings

- **`OfClass(typeof(ModelCurve))` returns EMPTY** for standalone/top-level model curves. Collect with `OfClass(typeof(CurveElement))` and filter `e is ModelCurve`. Sketch-hosted category names contain `"Sketch"` and must be denied.
- **`OfClass(typeof(Extrusion))` / `OfClass(typeof(GenericForm))` return EMPTY** for in-place form elements. Enumerate warning-driven from `Document.GetWarnings()`: failing set is `[FamilyInstance host, form, ModelLine(<Sketch>)]`.
- **Form type detection** by `e.GetType().Name`: `"Extrusion"` is fixable; `"Blend"/"Sweep"/"Revolve"/"GenericForm"/"SweptBlend"` are advisory-only; categories `Mass`/`Toposolid` excluded.
- **In-place strategies**: (1) length-preserving midpoint rotation vs an **18-direction** world-axis candidate set (3 axes ± and 6 diagonals ±) — all 18 must be present else a true 135° diagonal reports ~45° and never snaps; (2) fallback corner-vertex ray-ray closure.
- **Sketch plane frame** built from member `ModelLine.SketchPlane.GetPlane()`; Plane uses `XVec`/`YVec` (via reflection), not `BasisX`/`BasisY`.
- **Sketch access differences**: Floors/Ceilings → `((Floor)el).SketchId` → `Sketch.Profile` (`CurveArrArray`). FootPrint Roofs → no `.SketchId`; use `roof.GetProfiles()` (`ModelCurveArrArray`).
- **Stale warnings**: `Document.GetWarnings()` can retain stale entries; always verify fixes by measuring geometry, not warning-list presence.
- **`IsJoined`/`IsJoinedWith` do not exist** in this build — use `JoinGeometryUtils.GetJoinedElements(doc, el)`.

### Pass B — 1/4" Lattice Spacing Regularizer

- **Lattice math**: for a segment parallel (±0.1°) to a grid with signed perpendicular offset `p`, phase `φ = p − G·floor(p/G)` (`G = 0.25 in`); nearest lattice line `s_n = φ + G·round((p−φ)/G)`; deviation `= (p − s_n)·12` in. `DeltaIn = 0` ⇔ on-lattice.
- **Detector exclusions**: pinned; dimension-referenced (walk `Dimension.References`); API-joined (`JoinGeometryUtils.GetJoinedElements`); bbox fully embedded in another wall's XY bbox (±0.02 ft); hosted by a `FamilyInstance` whose host is in wall set; length < 0.5 ft.
- **Fixer = live recompute, never CSV deltas**: after batch moves, Revit joint cleanup silently steals part of the move. Recompute current `p → φ → s_n →` shift on the fly, snap-commit, re-read, self-correct up to 3 passes.
- **CRITICAL**: never call `SetProcessingResult(ProceedWithCommit)` when there are no errors — it triggers an infinite failure-processing loop / UI lock / MCP timeout.
- **Batching**: keep batches to **10–12 element IDs per MCP call** to stay under the 120s window in large models.

### Interaction Guidelines

- Report exact counts: **Fixed**, **Skipped (reason)**, and **LargeFix**; flag constraint-locked (dimensioned/curtain-wall-hosted) elements that rolled back safely.
- When dispatching to the compiled tools, pass `elementIds`/`hostIds`/`lineIds` as JSON arrays or CSV strings. For spacing passes use 10–12 IDs per call; all warnings/errors are handled headlessly by the `FailuresProcessing` delegate.
- Always set `transactionMode: "none"` when a script/command manages its own transactions.

---

## Frozen Script Archive

The original Roslyn `.csx` prototype scripts (detectors, fixers, spacing regularizer) and the full spec are **frozen and preserved** in the separate archive repo `revit-fix-line-innacuracy` (last state: branch `feature/failures-processing-pipeline`). They are **superseded** by the compiled `OffAxisCommandSet` commands in this repo and should be treated as historical reference only — the compiled commands are the live source of truth.

---

## Build & Conventions

- **Revit installed locally is 2024**; command sets build with `-c "Release R24"`.
- Repo targets Revit 2020–2026 (R20–R26).
- Plugin side-loads command sets from `%APPDATA%\Autodesk\Revit\Addins\{year}\revit_mcp_plugin\Commands\{SetName}\{year}\*.dll`, discovered via `commandRegistry.json` + `Assembly.LoadFrom`.
- **Commit only intentionally.** Don't push to upstream unless explicitly requested.
