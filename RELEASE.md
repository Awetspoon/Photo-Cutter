# Release Guide

This repo is set up to publish Photo Cutter through GitHub Releases.

## Automated release path (recommended)

1. Commit your changes on `main`.
2. Create and push a version tag:
   ```powershell
   git tag v1.0.0
   git push origin v1.0.0
   ```
3. GitHub Actions workflow `Release Windows App` will:
   - Build the app in Release mode
   - Publish single-file `PhotoCutter.exe`
   - Upload release assets to GitHub Releases

## Manual trigger path

1. Open Actions -> `Release Windows App`.
2. Click `Run workflow`.
3. Enter tag (for example `v1.0.1`).
4. Run and wait for release assets to upload.

## Local publish command

```powershell
dotnet publish .\\solution\\ImageUiSlicer\\ImageUiSlicer.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeAllContentForSelfExtract=true /p:EnableCompressionInSingleFile=true /p:DebugType=None /p:DebugSymbols=false
```

Output binary:

```text
solution/ImageUiSlicer/bin/Release/net8.0-windows/win-x64/publish/ImageUiSlicer.exe
```
