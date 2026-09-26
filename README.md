# LevelBuilder

A compact desktop 2D level editor for Aseprite and PNG assets, protobuf projects, and Go games. Built with C# and Avalonia for Linux and Windows.

## Install

Download the [latest release](https://github.com/isaiahpettingill/LevelBuilder/releases/latest), or run the Linux installer:

```sh
curl -fsSL https://github.com/isaiahpettingill/LevelBuilder/releases/latest/download/install.sh | sh
```

On Windows PowerShell, download `install.ps1` from the release and run `powershell -ExecutionPolicy Bypass -File .\install.ps1`. Both installers verify the release ZIP's SHA-256 checksum. The app checks for updates at startup and from **Check updates**; accepting an update downloads it and restarts the app.

Desktop release binaries use Native AOT, full trimming, size optimization, and stripped symbols. They do not need a .NET installation. Development requires the .NET 10 SDK; run `dotnet run --project src/LevelBuilder.App`. Linux Native AOT publishing additionally requires clang and zlib development headers. Run tests with `dotnet test tests/LevelBuilder.Core.Tests`.

## Project and file formats

Create a project in the app. A simple `project.json` describes asset directories and editable source files. The saved `.level` protobuf file is the portable project: it holds its own project settings, multiple named maps, strongly typed collider and anchor definitions, Aseprite/PNG source references, and **embedded PNG texture atlases with tags, frame timing, and directions**. A game can read one `.level` file to access its levels and decoded textures. It does not need the original Aseprite or PNG files. The editor refreshes the cache when a source changes and can keep working from the embedded cache if the source is missing.

A `.levelz` is a ZIP containing `levels/level.level`, the source files that are available, and the protobuf schema. Open, save, or share one from the toolbar. It can be opened even when an original source file has gone missing because the `.level` inside contains a texture cache. Project files written by version 0.1.0 (which contained a single bare level) are imported using the adjacent project configuration.

Import `.ase`, `.aseprite`, or `.png` as a tileset or visual sprite. Aseprite tilesets treat each frame as a tile: nonoverlapping tags give stable tag-relative variants. PNG tilesets split the image into grid tiles using the active level's tile dimensions and assign the `tiles` tag. PNG sprites use one frame. Animated Aseprite sprites preserve their tag directions and frame timing. The editor indicates unstable, untagged Aseprite frames in the palette.

**Export** writes `game.level`, PNG atlases and JSON metadata, and Go enum/query helpers (`types.go`) to the project's `build/` directory. The protobuf schema is in `schemas/level.proto` and copied into newly created projects. Go games can generate protobuf bindings from that schema and read `LevelDocument.CachedAssets[*].AtlasPng` without reading source asset files.

The canvas supports paint, erase, rectangle and flood fills, selection, undo and redo, visual layers, a collider grid, visual sprite objects, and typed entity anchors. Middle drag pans; wheel zooms. Shortcuts include Ctrl+S save, Ctrl+Z/Y undo/redo, Ctrl+C/X/V copy/cut/paste, Ctrl+D duplicate, Delete remove, I picker, Tab toggle visual/collider, +/- zoom, and arrow keys to move objects or pan. A `.recovery` copy is saved beside the current level project every 30 seconds while edited.

## Current limits

The Aseprite reader handles normal RGBA/grayscale/indexed cels, linked cels, tags, and frame timing. Advanced blend modes and Aseprite tilemap cels are not rendered. Released Windows and Linux targets are x64. The user interface is not a gameplay or physics simulator.
