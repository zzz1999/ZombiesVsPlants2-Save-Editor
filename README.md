# Zombies vs Plants 2 Save Editor

A dependency-free .NET 8 terminal user interface for inspecting and editing standard RTON v1 files, with dedicated shortcuts for supported `pp.dat` save data.

The published executable enters the TUI by default. File loading, profile discovery, editing, undo, and saving are all implemented as interactive terminal flows; the command-line surface exists only for development diagnostics.

## TUI capabilities

- Automatically discovers every profile in the loaded save.
- Opens ordinary standard RTON v1 files even when they contain no recognizable save profile.
- Browses objects and arrays hierarchically with live, escaped breadcrumbs and lazy menus.
- Renames editable object keys and rejects duplicate keys within the same object.
- Edits Boolean, integer, floating-point, and string values while displaying their RTON type tags.
- Displays profile names, resources, unlocked-plant counts, file status, and RTON metadata.
- Edits individual resource fields or assigns one value to all five resource fields.
- Resolves supported plant IDs to English names and keeps unknown future IDs visible.
- Unlocks a plant through the profile ownership array without changing its progression values.
- Edits player-visible Levels with per-plant maxima of 1, 10, 15, or 20.
- Applies Mastery only where supported, with a maximum of 200.
- Edits Seed Packets from 0 through 9,999,999.
- Keeps Imitater at fixed Level 1 with no Mastery progression.
- Locates a specific plant by ID or English name when needed.
- Searches the decoded tree for dynamically named scalar fields.
- Supports undo, reload, Save As, and in-place save from the TUI.

The hierarchical RTON browser exposes raw stored values. The dedicated save-data menus apply the documented plant progression limits and player-visible Level conversion.

## Codec design

The editor uses its own type-preserving standard RTON v1 codec. It preserves original tags and string-pool references where possible and supports:

- Fixed-width, raw-varint, ZigZag, and unsigned integers.
- `f32` and `f64`, including unchanged non-finite bit patterns.
- Legacy single-byte and UTF-8 direct and interned strings.
- Automatic promotion to UTF-8 when an edited string is not ASCII.
- Type-preserving object-key edits, including string-pool reconstruction and UTF-8 promotion.
- Unicode scalar counts for UTF-8 strings.
- Objects and capacity-based arrays with early terminators.
- Opaque RTID and binary-blob payload round trips.

Compact runtime tags `0xB0` through `0xBB` are intentionally outside the project scope. Standard v1 Boolean tag `0xBC` is supported with byte-preserving no-op round trips.

## Save pipeline

Saves use a same-directory temporary file, flush it to disk, decode and compare the written bytes, revalidate the destination immediately before commit, and atomically create or replace the destination. Existing destinations receive a timestamped backup, and externally changed files are rejected.

## Development

Requirements:

- .NET 8 SDK
- Windows for the distributed self-contained executable

Build:

```powershell
dotnet build -c Release
```

Run the TUI during development:

```powershell
dotnet run -c Release
```

Developer-only diagnostics:

```powershell
dotnet run -c Release -- --inspect <rton-file>
dotnet run -c Release -- --self-test <rton-file>
dotnet run -c Release -- --roundtrip <input.dat> <output.dat>
dotnet run -c Release -- --fixture-test
```

The tests verify byte-identical no-op round trips, key and value editing, duplicate-key rejection, UTF-8 promotion, string-pool reconstruction, legacy string preservation, binary blobs, RTIDs, Boolean payloads, signaling-NaN preservation, capacity arrays, malformed VarInt rejection, catalog limits, ownership edits, no-profile RTON sessions, undo behavior, backups, transactional saves, and external-change conflict detection.

## Continuous delivery

`.github/workflows/versioned-release.yml` validates every push to `main`. Increase the project `Version` to a new `major.minor.patch` value to publish a release. The workflow creates a permanent `v<Version>` tag and a GitHub Release containing the trimmed, self-contained `win-x64` executable and its SHA-256 checksum. Published version releases are never modified by the workflow. Pushes that do not increase `Version` run the build and regression fixtures without creating a release; a manual workflow run can reconcile an interrupted draft for the current version.

## Project layout

- `Rton/`: type-preserving RTON model, reader, and writer.
- `Editor/`: profile navigation, dynamic scalar search, undo, and save sessions.
- `Tui/`: keyboard-driven profile tools and hierarchical RTON browser.
- `Diagnostics/`: inspection and regression-test entry points.

## Disclaimer

This project is intended for private, offline experimentation and technical research. Do not use it for online competition, harassment, resale, attention-seeking, or bragging in player communities. Do not flood communities with modified saves or present the tool as an exploit trophy. Keep its use low-key and respect other players and community spaces.

## License

MIT. This is an independent community project and is not affiliated with or endorsed by any game publisher or studio.
