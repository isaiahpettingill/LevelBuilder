# LevelBuilder

A compact desktop 2D level editor for Aseprite and PNG assets, protobuf projects, and Go games. Built with C# and Avalonia for Linux and Windows.

## Install

Download the [latest release](https://github.com/isaiahpettingill/LevelBuilder/releases/latest), or run the Linux installer:

```sh
curl -fsSL https://github.com/isaiahpettingill/LevelBuilder/releases/latest/download/install.sh | sh
```

The Linux installer adds a LevelBuilder launcher and icon to the desktop application menu and registers `.level` and `.levelz` files for Open With. It refreshes the MIME, icon, and desktop menu caches when available. For an offline install, run `sh install.sh --archive LevelBuilder-linux-x64.zip`.

On Windows PowerShell, download `install.ps1` from the release and run `powershell -ExecutionPolicy Bypass -File .\install.ps1`. Release downloads are verified with SHA-256 checksums. The app checks for updates at startup and from **Project → Check updates**; accepting an update downloads it and restarts the app.

Desktop release binaries use Native AOT, full trimming, size optimization, and stripped symbols. They do not need a .NET installation. Development requires the .NET 10 SDK; run `dotnet run --project src/LevelBuilder.App`. Linux Native AOT publishing additionally requires clang and zlib development headers. Run tests with `dotnet test tests/LevelBuilder.Core.Tests`.

## Project and file formats

Create a `.level` project in the app. This protobuf file is the project: it holds project settings, multiple named maps, strongly typed collider and anchor definitions, Aseprite/PNG source references, and **embedded PNG texture atlases with tags, frame timing, and directions**. A game can read one `.level` file to access its levels and decoded textures. It does not need the original Aseprite or PNG files. While the editor is open, it watches registered sources anywhere inside the project folder, refreshes only the affected embedded cache and previews, and marks the project unsaved. If a source disappears or is temporarily invalid, the previous embedded cache remains available.

A `.levelz` is a ZIP containing `levels/level.level`, the source files that are available, and the protobuf schema. Open, save, or share one from the Project menu. It can be opened even when an original source file has gone missing because the `.level` inside contains a texture cache.

Import `.ase`, `.aseprite`, or `.png` as a tileset or visual sprite. Aseprite tilesets treat each frame as a tile: nonoverlapping tags give stable tag-relative variants. PNG tilesets split the image into grid tiles using the active level's tile dimensions and assign the `tiles` tag. PNG sprites use one frame. Animated Aseprite sprites preserve their tag directions and frame timing. The editor indicates unstable, untagged Aseprite frames in the palette.

**Export** writes `game.level`, PNG atlases and JSON metadata, and Go enum/query helpers (`types.go`) to the project's `build/` directory. The protobuf schema is in `schemas/level.proto` and included in shareable bundles. Go games can generate protobuf bindings from that schema and read `LevelDocument.CachedAssets[*].AtlasPng` without reading source asset files.

The canvas supports paint, erase, rectangle and flood fills, selection, undo and redo, visual layers, a collider grid, visual sprite objects, and typed entity anchors. The icon tool rail sits beside the canvas; project management and export live in the Project menu. Middle drag pans; wheel zooms. Shortcuts include Ctrl+S save, Ctrl+Z/Y undo/redo, Ctrl+C/X/V copy/cut/paste, Ctrl+D duplicate, Delete remove, I picker, Tab toggle visual/collider, +/- zoom, and arrow keys to move objects or pan. A `.recovery` copy is saved beside the current level project every 30 seconds while edited.

## Current limits

The Aseprite reader handles normal RGBA/grayscale/indexed cels, linked cels, tags, and frame timing. Advanced blend modes and Aseprite tilemap cels are not rendered. Released Windows and Linux targets are x64. The user interface is not a gameplay or physics simulator.
