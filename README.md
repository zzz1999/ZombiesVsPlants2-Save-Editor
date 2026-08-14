# Zombies vs Plants 2 Save Editor

A dependency-free .NET 8 terminal user interface for inspecting and editing standard RTON v1 `pp.dat` save files.

The published executable enters the TUI by default. File loading, profile discovery, editing, undo, and saving are all implemented as interactive terminal flows; the command-line surface exists only for development diagnostics.

## TUI capabilities

- Automatically discovers every profile in the loaded save.
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

## Codec design

The editor uses its own type-preserving standard RTON v1 codec. It preserves original tags and string-pool references where possible and supports:

- Fixed-width, raw-varint, ZigZag, and unsigned integers.
- `f32` and `f64`, including unchanged non-finite bit patterns.
- Legacy single-byte and UTF-8 direct and interned strings.
- Automatic promotion to UTF-8 when an edited string is not ASCII.
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
dotnet run -c Release -- --inspect <pp.dat>
dotnet run -c Release -- --self-test <pp.dat>
dotnet run -c Release -- --roundtrip <input.dat> <output.dat>
dotnet run -c Release -- --fixture-test
```

The tests verify byte-identical no-op round trips, UTF-8 promotion, legacy string preservation, binary blobs, RTIDs, Boolean payloads, signaling-NaN preservation, capacity arrays, malformed VarInt rejection, catalog limits, ownership edits, undo behavior, backups, transactional saves, and external-change conflict detection.

## Continuous delivery

`.github/workflows/rolling-release.yml` runs after every push to `main`. It builds a trimmed, self-contained `win-x64` single-file executable, generates a SHA-256 checksum, moves the fixed `latest` tag to the current commit, and replaces the assets in one rolling GitHub Release named `Latest Windows Build`.

## Project layout

- `Rton/`: type-preserving RTON model, reader, and writer.
- `Editor/`: profile navigation, dynamic scalar search, undo, and save sessions.
- `Tui/`: keyboard-driven terminal interface.
- `Diagnostics/`: inspection and regression-test entry points.

## Disclaimer

This project is intended for private, offline experimentation and technical research. Do not use it for online competition, harassment, resale, attention-seeking, or bragging in player communities. Do not flood communities with modified saves or present the tool as an exploit trophy. Keep its use low-key and respect other players and community spaces.

## License

MIT. This is an independent community project and is not affiliated with or endorsed by any game publisher or studio.
