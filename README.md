# Photo Cutter (Image UI Slicer)

Photo Cutter is a Windows desktop app for turning full images/mockups into clean transparent PNG cutouts.

## Features

- Manual cutout tools: `Select`, `Lasso`, `Polygon`, and `Shapes`
- Reusable custom shapes (save, apply, duplicate, and paste workflows)
- Brush refinement (`Brush +` and `Brush -`) on active selections or cutouts
- Inspector preview with optional split preview (before/after)
- Cutout gallery window for quick visual review
- PNG export presets with naming and edge outline options
- Project save/load (`.iusproj`)

## Requirements

- Windows 10/11
- .NET SDK `8.0.124` or compatible (see `global.json`)

## Setup

```powershell
# from repo root
dotnet restore .\solution\ImageUiSlicer\ImageUiSlicer.csproj
```

If you are restoring without internet but already have a local package cache, set `NUGET_PACKAGES` first.

## Run

```powershell
dotnet run --project .\solution\ImageUiSlicer\ImageUiSlicer.csproj
```

## Build

```powershell
dotnet build .\solution\ImageUiSlicer\ImageUiSlicer.csproj
```

## Publish

```powershell
dotnet publish .\solution\ImageUiSlicer\ImageUiSlicer.csproj -c Release -r win-x64 --self-contained false
```

## Folder Structure

```text
solution/ImageUiSlicer/      # WPF app source
solution/ImageUiSlicer/Views # XAML views
solution/ImageUiSlicer/ViewModels
solution/ImageUiSlicer/Services
solution/ImageUiSlicer/Models
solution/ImageUiSlicer/CanvasEngine
brand/                       # brand assets
png/                         # icon size ladder PNGs
specs/                       # project/spec docs
```

## Custom Shapes Quick Start

1. Draw and commit a clean cutout.
2. Select that cutout in the left list.
3. Switch to `Shapes` tool.
4. Click `Save Shape`.
5. Pick it from the saved-shape dropdown next to `Shapes`.
6. Click `Use Saved` to place it as an active selection, move/adjust, then `Commit Cutout`.
7. Optional: enable `Grow` to scale it before placement.

## Screenshots

Add screenshots here.

## License

MIT (see `LICENSE`).
