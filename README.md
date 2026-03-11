# Photo Cutter

[![Release](https://img.shields.io/github/v/release/Awetspoon/Photo-Cutter?display_name=release)](https://github.com/Awetspoon/Photo-Cutter/releases/latest)
[![Build](https://img.shields.io/github/actions/workflow/status/Awetspoon/Photo-Cutter/ci.yml?branch=main&label=build)](https://github.com/Awetspoon/Photo-Cutter/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D6)](https://github.com/Awetspoon/Photo-Cutter/releases/latest)

Photo Cutter is a Windows desktop app for turning full mockups and screenshots into clean transparent PNG cutouts for app, UI, and game asset workflows.

## Download

- Latest release: [Download Photo Cutter](https://github.com/Awetspoon/Photo-Cutter/releases/latest)
- Single-file download: `PhotoCutter.exe`

## Features

- Manual cutout tools: `Select`, `Lasso`, `Polygon`, `Shapes`
- Reusable custom shapes (save, apply, duplicate, paste)
- Brush refinement for active selection/cutout
- Inspector preview with optional split compare
- Cutout Gallery window for quick review
- Export presets, naming controls, edge/outline options
- Project save/load (`.iusproj`)

## Quick Start

1. Open an image.
2. Draw a selection using `Shapes`, `Lasso`, or `Polygon`.
3. Click `Commit Cutout`.
4. Optional: save a cutout as a reusable shape.
5. Export selected/all cutouts as PNG.

## Build From Source

### Requirements

- Windows 10/11
- .NET SDK `8.0.124` or compatible (see `global.json`)

### Restore

```powershell
dotnet restore .\\solution\\ImageUiSlicer\\ImageUiSlicer.csproj
```

### Run

```powershell
dotnet run --project .\\solution\\ImageUiSlicer\\ImageUiSlicer.csproj
```

### Release Publish (single EXE)

```powershell
dotnet publish .\\solution\\ImageUiSlicer\\ImageUiSlicer.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeAllContentForSelfExtract=true /p:EnableCompressionInSingleFile=true
```

## Releasing

- Tag format: `vX.Y.Z` (example: `v1.0.0`)
- Pushing a version tag triggers GitHub Actions to:
  - Build Release for Windows x64
  - Produce single-file `PhotoCutter.exe`
  - Upload release asset to GitHub Releases

Full process: see [RELEASE.md](RELEASE.md).

## Folder Structure

```text
solution/ImageUiSlicer/      # WPF app source
solution/ImageUiSlicer/Views # XAML views
solution/ImageUiSlicer/ViewModels
solution/ImageUiSlicer/Services
solution/ImageUiSlicer/Models
solution/ImageUiSlicer/CanvasEngine
brand/                       # branding assets
png/                         # icon ladder PNGs
specs/                       # project/spec docs
```

## Screenshots

Add product screenshots or GIF demos in this section.

## License

MIT. See [LICENSE](LICENSE).
