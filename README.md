# Zombies vs Plants 2 Save Editor

A dependency-free .NET 8 terminal user interface for inspecting and editing standard RTON v1 `pp.dat` save files.

The published executable enters the TUI by default. File loading, profile discovery, editing, undo, and saving are all implemented as interactive terminal flows; the command-line surface exists only for development diagnostics.

## TUI capabilities

- Automatically discovers every profile in the loaded save.
- Displays profile names, resources, plant-record counts, file status, and RTON metadata.
- Edits individual resource fields or assigns one value to all five resource fields.
- Browses plant records without requiring the user to know an ID.
- Batch-adjusts level, mastery, and experience values across all detected plant records.
- Locates a specific plant by ID when needed.
- Searches the decoded tree for dynamically named scalar fields.
- Supports undo, reload, Save As, and in-place save from the TUI.

## Codec design

The editor uses its own type-preserving standard RTON v1 codec. It preserves original tags and string-pool references where possible and supports:

- Fixed-width, raw-varint, ZigZag, and unsigned integers.
- `f32` and `f64`, including unchanged non-finite bit patterns.
- Latin-1 and UTF-8 direct and interned strings.
- Unicode scalar counts for UTF-8 strings.
- Objects and capacity-based arrays with early terminators.
- Opaque RTID and binary-blob payload round trips.

Compact runtime RTON (`0x00010001`, tags `0xB0` through `0xBC`) is intentionally outside the project scope.

## Save pipeline

In-place saves use a same-directory temporary file, flush it to disk, decode and compare the written bytes, create a timestamped backup, and then replace the destination. The editor also rejects an in-place save if the loaded file changed externally after it was opened.

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
```

The self-test verifies byte-identical no-op round trips, editable Unicode strings, negative ZigZag values, Latin-1 strings, binary blobs, signaling-NaN preservation, capacity arrays, malformed VarInt rejection, undo behavior, backups, transactional saves, and external-change conflict detection.

## Continuous delivery

`.github/workflows/rolling-release.yml` runs after every push to `main`. It builds a trimmed, self-contained `win-x64` single-file executable, generates a SHA-256 checksum, moves the fixed `latest` tag to the current commit, and replaces the assets in one rolling GitHub Release named `Latest Windows Build`.

## Project layout

- `Rton/`: type-preserving RTON model, reader, and writer.
- `Editor/`: profile navigation, dynamic scalar search, undo, and save sessions.
- `Tui/`: keyboard-driven terminal interface.
- `Diagnostics/`: inspection and regression-test entry points.

## License

MIT. This is an independent community project and is not affiliated with or endorsed by any game publisher or studio.
