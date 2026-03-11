# Contributing to Photo Cutter

Thanks for improving Photo Cutter.

## Development setup

1. Install .NET SDK 8.x.
2. Restore packages:
   ```powershell
   dotnet restore .\\solution\\ImageUiSlicer\\ImageUiSlicer.csproj
   ```
3. Run locally:
   ```powershell
   dotnet run --project .\\solution\\ImageUiSlicer\\ImageUiSlicer.csproj
   ```

## Pull request guidelines

- Keep changes focused and small.
- Preserve existing behavior unless the change is intentional and documented.
- Include UI screenshots when changing visuals.
- Update docs for any user-facing workflow changes.
- Ensure build passes before pushing:
  ```powershell
  dotnet build .\\solution\\ImageUiSlicer\\ImageUiSlicer.csproj -c Release
  ```

## Commit style

Use clear commit messages, for example:

- `UI: fix shape toolbar overlap`
- `Export: keep single-file release output`
- `Docs: update release instructions`

## Release process

See [RELEASE.md](RELEASE.md).
