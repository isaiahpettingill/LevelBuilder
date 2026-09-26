# LevelBuilder

A focused 2D level editor for Aseprite assets, protobuf levels, and Go games. Built with C# and Avalonia for Linux and Windows.

## Install

Download the [latest release](https://github.com/isaiahpettingill/LevelBuilder/releases/latest), or run the platform installer:

```sh
curl -fsSL https://github.com/isaiahpettingill/LevelBuilder/releases/latest/download/install.sh | sh
```

On Windows PowerShell, download `install.ps1` from the release and run `powershell -ExecutionPolicy Bypass -File .\install.ps1`. Both installers verify the release archive's SHA-256 checksum. The app checks for updates at startup and from **Check updates**; accepting an update downloads it and restarts the app.

## Develop

Install the .NET 10 SDK, then run `dotnet run --project src/LevelBuilder.App`. Build with `dotnet build src/LevelBuilder.App`. Tests: `dotnet test tests/LevelBuilder.Core.Tests`.

Create a project in the app. Its JSON defines `assetDirectory`, `levelDirectory`, `assets` (relative source paths mapped to `Tileset` or `AnimatedSprite`), `colliderTypes`, and `anchorTypes`. Import `.ase` / `.aseprite` files through the toolbar. A tileset's frame canvas dimensions must match the level tile size; frames in nonoverlapping tags are stable references such as `grass[2]`. Untagged frames appear with a warning and use unstable absolute references.

The editor writes `.level` protobuf files following `schemas/level.proto`. **Export** writes the level, runtime PNG atlases and JSON metadata, and `types.go` to the project's `build/` directory. The runtime reads protobuf and PNG; it does not need to parse Aseprite. Asset changes reload automatically and missing references are reported in the status bar and at save/export. `.recovery` files are saved beside edited levels every 30 seconds.

Select a visual layer, palette tile, and tool to paint. Switch to collider mode to paint property cells. Select mode supports dragging sprites and anchors and selecting rectangular tile or collider regions. Middle drag pans; wheel zooms. Shortcuts: Ctrl+S save, Ctrl+Z/Y undo/redo, Ctrl+C/X/V copy/cut/paste, Ctrl+D duplicate, Delete remove, I picker, Tab toggle visual/collider, +/- zoom, arrow keys move objects or pan.

## Current limitations

The Aseprite reader composites normal RGBA/grayscale/indexed cels and reads tag directions, frame timing, and linked cels. Complex Aseprite blend modes and tilemap cels are not yet rendered. An animated sprite's tag can be previewed; reverse and ping-pong directions are supported. Runtime animation metadata preserves the original direction number. The editor supports one platform architecture per OS in release builds (`linux-x64`, `win-x64`).
