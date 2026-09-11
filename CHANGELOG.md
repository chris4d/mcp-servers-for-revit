# Changelog

All notable changes to this fork are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `reload_command_set` built-in plugin command + MCP tool: live-reload of command set DLLs
  after a build, without restarting Revit. Command set assemblies are byte-loaded
  (SHA-256 hash cached) so the files on disk are never locked by Revit.
  (`feature/live-command-reload`, `f3d6ea2`)
- `dwg-commandset`: per-wall `SubTransaction` in the poche build loop — one
  un-buildable wall no longer rolls back the whole batch (`0203a67`).
- Geometry tuning for `create_walls_from_dwg_poche`: `MergeGapFt` 0.5 → 0.8
  (`dbf18eb`), `minWallLengthFt` default 3.5 → 2.0 (`383fd70`),
  `maxWallThicknessFt` relaxed to 5ft default, JambRatio gate disabled (`d2c0039`).
- Pure 2D geometry core (`dwg-commandset/Geometry/PocheGeometryCore.cs`) extracted
  from Revit API code, with behavior-locked unit tests (`tests/dwg-geometry`,
  `05b738b`) and a `WallComparator` target-layout scorer (`0e3bb63`).

## [1.1.2] - 2026-09-04

### Added

- AnythingLLM client configuration in the installer (`3689ce3`), alongside
  Claude Desktop, Cursor, and opencode.

### Fixed

- `build-installer.ps1 -SkipPluginBuild`: staged `Server` directory size cast to
  int failed on large trees; now `TryParse`-gated (`e3ee942`).
- Poche face pairing gated by hatch-fill containment, eliminating ghost walls in
  open areas (`619b2f1`).

## [1.1.1] - 2026-08

### Added

- Inno Setup installer bundles the compiled MCP server and configures AI clients
  to point at it instead of `npx` (`5d59063`).
- CI release build uses Node 22 (`8bff8f9`, `79fd6a1`).

## [1.1.0] - 2026-08

### Added

- **Off-Axis toolkit** (`offaxis-commandset/`): 10 compiled MCP commands for
  detecting and snapping near-axis model geometry
  (`detect_off_axis_hybrid`, `detect_off_axis_lines`, `detect_spacing_elements`,
  `fix_off_axis_walls_and_beams`, `fix_off_axis_grids`,
  `fix_off_axis_reference_planes`, `fix_off_axis_sketches`,
  `fix_off_axis_inplace`, `fix_off_axis_model_lines`, `fix_spacing_elements`).
- **DWG command set** (`dwg-commandset/`): `create_walls_from_dwg_layer`,
  `create_grids_from_dwg_layer`, `create_model_lines_from_dwg_layer`,
  `create_walls_from_dwg_poche` — generate Revit elements from imported/linked
  DWG linework and hatch fills.
- Inno Setup-based Windows installer (`installer/`) that installs the add-in,
  command sets, bundled MCP server, and configures AI clients.

### Fixed

- Revit 2025/2026 compatibility: `ElementId.IntegerValue` (removed in 2025)
  swapped for `GetIntValue` across off-axis handlers (`14286eb`).
- Release workflow builds the installer for Revit 2024-2026 and no longer
  publishes the fork-broken npm package (`2d4effd`).

[Unreleased]: https://github.com/mcp-servers-for-revit/mcp-servers-for-revit/compare/v1.1.2...HEAD
[1.1.2]: https://github.com/mcp-servers-for-revit/mcp-servers-for-revit/compare/v1.1.1...v1.1.2
[1.1.1]: https://github.com/mcp-servers-for-revit/mcp-servers-for-revit/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/mcp-servers-for-revit/mcp-servers-for-revit/compare/v1.0.0...v1.1.0
