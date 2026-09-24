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
| `dwg-commandset/` | C# `DwgCommandSet` — DWG import toolset (grid/model lines/walls/poche walls from placed DWG layers; compiles to `DwgCommandSet.dll`). Includes a pure-geometry core (`Geometry/`) used by unit tests. |
| `installer/` | Inno Setup packaging (`build-installer.ps1` generates `.iss`, compiles `mcp-servers-for-revit-setup.exe`). |
| `scripts/` | Release script (`release.ps1`) + DWG wall scorecard scorer (`score-walls.ps1`). |
| `tests/` | `dwg-geometry/` TUnit suite (36 tests) covering `dwg-commandset/Geometry`; run via the test exe directly (`dotnet test` crashes with this TUnit/MTP combo). |

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

### Deployment Gotcha — Installer overwrites AI-client configs (RESOLVED as of v1.1.1)

As of **v1.1.1** this gotcha is fixed in the installer: `installer/build-installer.ps1` bundles the compiled MCP server with the exe (`{app}\Server\build\index.js` + `node_modules`), and `ConfigureClaudeDesktop` / `ConfigureCursor` / `ConfigureOpencode` write client configs that run the bundled server via `node` — never `npx`. Install-time logic also **upgrades pre-existing `npx`-based entries** in those configs to the bundled-server form. The npm package `mcp-server-for-revit@1.0.0` remains broken upstream (unfixed `ajv` dep), and this fork deliberately never depends on it. Historical context for anyone reverting:

```jsonc
// ~/.config/opencode/opencode.json — pre-v1.1.1 symptom was a forced npx entry
// of the broken npm package; if you hit that state, point back at the local build:
"mcp-server-for-revit": {
  "type": "local",
  "command": ["node", "C:\\dev\\mcp-servers-for-revit\\server\\build\\index.js"],
  "enabled": true
}
```

Verify the local server boots with a manual MCP handshake (`node server/build/index.js` from `server/`; respond to `initialize`; 33 tools register). Note: `better-sqlite3` has **no prebuilt binary for Node 26** and this machine lacks node-gyp/Python/VS, so the 3 sqlite-backed tools (`query_stored_data`, `store_project_data`, `store_room_data`) fail to register — the other 33 tools and the connection work fine.

As of **v1.1.4** the installer additionally **bundles a portable Node runtime** (`{app}\node\node.exe`, staged from nodejs.org v22 win-x64 at build time, cached in `installer/.node-cache/`) and a stdio launcher **shim** (`{app}\Server\run-mcp-server.cmd`: prefer `{app}\node\node.exe`, fall back to PATH `node`, exec `{app}\Server\build\index.js`). **All** AI-client entries - including `ConfigureAnythingLLM` (`%APPDATA%\anythingllm-desktop\storage\plugins\anythingllm_mcp_servers.json`) - are written as `cmd /c {app}\Server\run-mcp-server.cmd`, so staff machines need **no system Node install** for the standalone installer either; the entry-refresh logic now upgrades stale `npx`- AND direct-`node`-form entries to the shim form. (Suite-side AnythingLLM wiring in SMS-toolkit already used its own portable node + shim; the two never collide - the suite deploys the plugin/server directly and does not run this installer.) Invoke the installer build with `-Command` (e.g. `powershell -Command "& { .\installer\build-installer.ps1 ... }"`), **not** `-File` - with `-File`, `@(2024,2025,2026)` is split at the commas and `2025`/`2026)` bind positionally to later params (this once set `OutputDir=2025` and built only Revit 2024).

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
- `send_code_to_revit` contract: the snippet runs inside `Execute(Document document, object[] parameters)` — use lowercase `document`, return with `return ...`, length-like values are in internal **feet**. Compile-error line numbers are 1-based snippet-relative (since v1.1.6). Failure results are labeled `"Code failed to execute."` instead of a success envelope. A companion global opencode skill `revit-code-execution` (in the user's `~/.config/opencode/skills/`) carries the durable API lessons for agents.
- `reload_command_set` (v1.1.7+): plugin built-in byte-loads matching command-set DLLs by hash; `RevitMCPCommandSet.dll` itself is LoadFrom-resident (Roslyn dependency probing) so it can't hot-reload — restart Revit to refresh it. The tool returns the full registered-command list, useful to verify tool advertisement vs registration.
- Release workflow (as of v1.1.7): merge feature branches into `main`, bump version in `plugin/Properties/AssemblyInfo.cs` + `server/package.json` (+ `npm install --package-lock-only` in `server/`), build installer via `powershell -Command "& { .\installer\build-installer.ps1 }"` (R20–R23 dwg/offaxis build failures inside the installer are **expected** — those extras ship only for R24–R26), build `offaxis-commandset/build-package.ps1` if the off-axis set changed, commit the regenerated `.iss`, push, tag, `gh release create -R chris4d/mcp-servers-for-revit` (plain `gh` targets the upstream repo and 403s).`

---

## Frozen Script Archive

The original Roslyn `.csx` prototype scripts (detectors, fixers, spacing regularizer) and the full spec are **frozen and preserved** in the separate archive repo `revit-fix-line-innacuracy` (last state: branch `feature/failures-processing-pipeline`). They are **superseded** by the compiled `OffAxisCommandSet` commands in this repo and should be treated as historical reference only — the compiled commands are the live source of truth.

---

## Build & Conventions

- **Revit installed locally is 2024**; command sets build with `-c "Release R24"`.
- Repo targets Revit 2020–2026 (R20–R26) for the **core** set/plugin; the `dwg-commandset` / `offaxis-commandset` extras use Revit 2024+ APIs (`Floor.SketchId`, long ElementId values) and compile **only for R24/R25/R26** — release zips and the installer ship them for 2024–2026, core-only for 2020–2023.
- Plugin side-loads command sets from `%APPDATA%\Autodesk\Revit\Addins\{year}\revit_mcp_plugin\Commands\{SetName}\{year}\*.dll`, discovered via `commandRegistry.json` + `Assembly.LoadFrom`.
- **Commit only intentionally.** Don't push to upstream unless explicitly requested.
