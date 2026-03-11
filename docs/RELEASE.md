# Release Guide

This repository publishes Photo Cutter through GitHub Releases.

## Automated release flow (recommended)

1. Ensure `main` contains your final release commit.
2. Create and push a semantic version tag:

   ```powershell
   git tag v1.0.2
   git push origin v1.0.2
   ```

3. GitHub Actions workflow `Release Windows App` will:
   - Restore and build the project
   - Publish a single-file Windows executable
   - Create/update a GitHub Release
   - Upload one asset: `PhotoCutter.exe`

## Manual release flow

1. Open GitHub Actions -> `Release Windows App`.
2. Click `Run workflow`.
3. Enter a valid version tag (example `v1.0.2`).
4. Run workflow and confirm release asset upload.

## Local publish command

```powershell
dotnet publish .\\solution\\ImageUiSlicer\\ImageUiSlicer.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeAllContentForSelfExtract=true /p:EnableCompressionInSingleFile=true /p:DebugType=None /p:DebugSymbols=false
```

Published binary output:

```text
solution/ImageUiSlicer/bin/Release/net8.0-windows/win-x64/publish/ImageUiSlicer.exe
```
