# Photo Cutter Audit Report

Date: 2026-03-10
Workspace: C:\Users\Marcus\Desktop\Marcus APPs\windows\Photo-Cutter
Primary application: solution/ImageUiSlicer

## Scope

This audit reviewed the maintained source tree under `solution/ImageUiSlicer`, the repository-level documentation and assets, and the generated build trees committed under `solution/ImageUiSlicer/bin` and `solution/ImageUiSlicer/obj`.

Maintained code reviewed:
- App startup: `App.xaml`, `App.xaml.cs`, `GlobalUsings.cs`, `ImageUiSlicer.csproj`
- Infrastructure: `ObservableObject.cs`, `RelayCommand.cs`
- Models: `BBox.cs`, `PointF.cs`, `SourceImageModel.cs`, `SelectionModel.cs`, `ProjectModel.cs`, `CutoutPreviewItem.cs`, `PathGeometryModel.cs`, `ExportOptionsModel.cs`, `CutoutModel.cs`
- Canvas engine: `CanvasTool.cs`, `GeometryHelper.cs`, `CanvasController.cs`
- Services: `ImageService.cs`, `ProjectService.cs`, `SettingsService.cs`, `ExportService.cs`, `CutoutPreviewFactory.cs`, `AutoCutoutService.cs`, `AutoCutoutService.Ai.cs`, `AiCutoutDetectionService.cs`, `ShapeReuseDetectionService.cs`
- View models: `MainViewModel.cs`, `MainViewModel.ExportAndBrush.cs`, `MainViewModel.Ai.cs`, `MainViewModel.ShapeReuse.cs`
- Views: `MainWindow.xaml`, `MainWindow.xaml.cs`, `CutoutGalleryWindow.xaml`, `CutoutGalleryWindow.xaml.cs`, `Views/Canvas/SkiaCanvasView.xaml`, `Views/Canvas/SkiaCanvasView.xaml.cs`
- Documentation/assets: root spec `.txt` files, `specs/`, `brand/`, `png/`, `README_Scaffold.txt`
- Generated artifacts: `solution/ImageUiSlicer/bin`, `solution/ImageUiSlicer/obj`

## Architecture Summary

Current architecture is workable but not cleanly modular:
- UI layer: WPF XAML views plus code-behind for dialogs, window lifecycle, and keyboard routing.
- Application layer: one very large `MainViewModel` split across partial files, but still owning project state, command state, export policy, preview generation, undo/redo, brush refinement, auto detect, AI detect, and shape reuse.
- Domain/data layer: simple observable models with JSON persistence.
- Services layer: image IO, project IO, settings IO, cutout export, preview generation, heuristic auto-detect, AI-assisted detect, and shape reuse matching.
- Rendering/editor engine: `CanvasController` + `GeometryHelper` + Skia canvas view.

There are no hard compile-time circular dependencies between namespaces, but there are several soft architectural leaks:
- `MainViewModel` directly instantiates all services instead of depending on abstractions or a composition root.
- `MainViewModel` exposes a view-specific `SelectionSyncRequested` event for listbox synchronization.
- `CanvasController` is tightly coupled to WPF input types and directly depends on `MainViewModel`.
- Export preview rendering is split between `ExportService` and `CutoutPreviewFactory` with duplicated outline policy logic.

## Findings

### High Risk

1. God-object view model mixes orchestration, editor state, persistence rules, export rules, preview generation, undo/redo, AI, and shape reuse.
- Risk: High maintainability risk and fragile feature interactions.
- Files:
  - `solution/ImageUiSlicer/ViewModels/MainViewModel.cs`
  - `solution/ImageUiSlicer/ViewModels/MainViewModel.ExportAndBrush.cs`
  - `solution/ImageUiSlicer/ViewModels/MainViewModel.Ai.cs`
  - `solution/ImageUiSlicer/ViewModels/MainViewModel.ShapeReuse.cs`
- Evidence:
  - Service construction and command wiring live in one class.
  - The same object handles project loading/saving, canvas/editor state, export policy, preview generation, brush refinement, auto detect, AI detect, and smart shape reuse.
- Recommendation:
  - Split into a small shell view model plus focused coordinators/services such as `ProjectSessionController`, `SelectionEditorService`, `ExportProfileService`, `CutoutDetectionCoordinator`, `PreviewService`, and `UndoHistoryService`.
  - Keep partial files only for presentation groupings, not as the main modularization mechanism.

2. Generated build output is committed to the repository and there was no `.gitignore`.
- Risk: High repository instability and noisy diffs; stale artifacts can hide the real source of a regression.
- Files/directories:
  - `solution/ImageUiSlicer/bin`
  - `solution/ImageUiSlicer/obj`
  - repository root had no `.gitignore`
- Evidence:
  - `bin` + `obj` contain 200+ generated files and third-party binaries.
  - Multiple transient `*_wpftmp*` files are present under `obj`.
- Action taken:
  - Added `.gitignore` to block future `bin/` and `obj/` churn.
- Recommendation:
  - In a dedicated cleanup change, remove tracked generated artifacts from source control and let CI/build recreate them.

### Medium Risk

3. View-model-to-view selection synchronization leaks UI responsibilities into the application layer.
- Risk: Medium architectural coupling; harder to test and replace the list view behavior.
- Files:
  - `solution/ImageUiSlicer/ViewModels/MainViewModel.cs`
  - `solution/ImageUiSlicer/Views/MainWindow.xaml.cs`
- Evidence:
  - `SelectionSyncRequested` exists only to tell the window to imperatively sync `ListBox.SelectedItems`.
- Recommendation:
  - Introduce a dedicated selection model / adapter owned by the view layer, or use a bindable selected-items behavior so the view model stays UI-agnostic.

4. Canvas editing logic is heavily coupled to the full `MainViewModel` and WPF input types.
- Risk: Medium; editor bugs become harder to isolate, test, or port.
- Files:
  - `solution/ImageUiSlicer/CanvasEngine/CanvasController.cs`
  - `solution/ImageUiSlicer/Views/Canvas/SkiaCanvasView.xaml.cs`
- Evidence:
  - `CanvasController` consumes `Point`, `Key`, `ModifierKeys`, and directly mutates `MainViewModel`.
- Recommendation:
  - Extract an `ICanvasEditorSession` or similar interface that exposes only the editor operations/state required by the controller.

5. AI detection is wired directly as a raw HTTP integration inside app code with no abstraction seam or offline fallback contract.
- Risk: Medium; hard to mock, test, or swap providers. Failure modes are mostly runtime-only.
- File:
  - `solution/ImageUiSlicer/Services/AiCutoutDetectionService.cs`
- Evidence:
  - Static `HttpClient`, raw JSON body construction, manual response parsing, environment variable resolution, and OpenAI-specific prompt/schema all live in one concrete class.
- Recommendation:
  - Introduce an `ICutoutDetectionProvider` abstraction with `Heuristic`, `ShapeReuse`, and `OpenAiVision` implementations.
  - Keep provider-specific HTTP and schema parsing behind that boundary.

6. Preview rendering responsibilities are duplicated across export and preview services.
- Risk: Medium maintenance drift.
- Files:
  - `solution/ImageUiSlicer/Services/ExportService.cs`
  - `solution/ImageUiSlicer/Services/CutoutPreviewFactory.cs`
- Evidence:
  - Both services independently resolve outline color policy and render clipped geometry.
- Recommendation:
  - Centralize cutout rasterization in one renderer service and have preview/export call it with different render options.

### Low Risk

7. `ProjectService.Load` uses defensive null-coalescing against non-nullable model properties, which signals a schema/model mismatch.
- Risk: Low, but it hides the boundary between trusted model defaults and defensive deserialization.
- File:
  - `solution/ImageUiSlicer/Services/ProjectService.cs`
- Recommendation:
  - Either mark persistence properties nullable and normalize after load, or keep them non-nullable and validate with explicit load normalization methods.

8. `SettingsService` swallows all settings-load failures silently.
- Risk: Low operational visibility; corrupt settings silently reset with no diagnostics.
- File:
  - `solution/ImageUiSlicer/Services/SettingsService.cs`
- Recommendation:
  - Log or surface a one-time warning when settings are unreadable.

9. `RelayCommand` is too minimal for the current feature set.
- Risk: Low now, but it encourages more `async void` command handlers and prevents command parameter/cancellation patterns.
- File:
  - `solution/ImageUiSlicer/Infrastructure/RelayCommand.cs`
- Recommendation:
  - Replace with typed sync/async commands or adopt a small MVVM toolkit command abstraction.

10. Repository documentation is duplicated and one scaffold file is stale.
- Risk: Low, but guaranteed documentation drift over time.
- Files:
  - root `*.txt` spec files
  - matching files under `specs/`
  - `solution/ImageUiSlicer/README_Scaffold.txt`
- Evidence:
  - Root `.txt` files and `specs/` contain exact duplicate name/length pairs.
  - `README_Scaffold.txt` still describes the project as a scaffold and contains encoding corruption.
- Recommendation:
  - Keep one canonical spec location (`specs/`) and archive or remove the duplicates.
  - Replace the scaffold README with current product/operator docs or delete it.

## Dead Code / Redundant Code Review

### Removed safely

1. No-op assignment removed.
- File:
  - `solution/ImageUiSlicer/ViewModels/MainViewModel.cs`
- Removed line:
  - `project.SourceImage.Path = project.SourceImage.Path;`
- Reason:
  - It had no effect and did not contribute to project loading behavior.

### Consolidated / guarded

2. Repository ignore rules added.
- File:
  - `.gitignore`
- Reason:
  - Prevents future accidental inclusion of `bin/` and `obj/` output.

### Flagged for manual review instead of deleting

3. `ExportOptionsModel.Format`
- File:
  - `solution/ImageUiSlicer/Models/ExportOptionsModel.cs`
- Why flagged:
  - Confirmed unused by runtime code today, but it is part of the serialized project contract.
  - Removing it would subtly change saved project shape.
- Recommended action:
  - Either remove in a schema-versioned cleanup or mark deprecated in code comments and docs.

4. `CutoutModel.Notes`
- File:
  - `solution/ImageUiSlicer/Models/CutoutModel.cs`
- Why flagged:
  - No runtime UI or service uses it, but it is persisted data and may be intended for future metadata.
- Recommended action:
  - Remove only after deciding whether notes belong in the project format.

5. Root duplicate spec files.
- Files:
  - root `00_...13_...txt`, `MASTER_LOCK_SPEC_...txt`
- Why flagged:
  - Exact duplicates of `specs/`, but documentation location is a repo policy decision rather than dead runtime code.
- Recommended action:
  - Keep `specs/` as canonical and delete the root copies in a docs-only cleanup.

6. `README_Scaffold.txt`
- File:
  - `solution/ImageUiSlicer/README_Scaffold.txt`
- Why flagged:
  - Stale and misleading, but documentation removal should be deliberate.

## Data Flow Summary

1. Image/project ingress
- `MainWindow.xaml.cs` opens image/project dialogs or handles drag-drop.
- `MainViewModel` uses `ImageService` and `ProjectService` to load state.
- `ApplyProject` replaces the active session, resets canvas state, refreshes previews, and raises command/property changes.

2. Editing flow
- `SkiaCanvasView` translates WPF mouse events into canvas pixel coordinates.
- `CanvasController` interprets input for selection, lasso, polygon, brush refine, panning, and active-selection moves.
- `CanvasController` mutates editor state through `MainViewModel` methods such as `SetActiveSelection`, `TryMoveActiveSelectionFromOrigin`, `BeginBrushRefineStroke`, and `CommitSelectionCommand`.

3. Detection flow
- Quick detect: `MainViewModel.AutoDetectCutouts` -> `AutoCutoutService.Detect` -> cutout suggestions -> project cutouts.
- AI detect: `MainViewModel.AiDetectCutouts` -> `AiCutoutDetectionService.DetectAsync` -> OpenAI response -> `AutoCutoutService.CreateSuggestionFromHint` -> project cutouts.
- Shape reuse: `MainViewModel.MatchSelectedCutout` -> `ShapeReuseDetectionService.FindMatches` -> project cutouts.

4. Preview/export flow
- Committed cutout previews and inspector previews are built through `CutoutPreviewFactory`.
- Exports go through `ExportService`, which always writes PNG files into `<base>\cut outs`.

5. Persistence/settings flow
- Project save/load uses JSON via `ProjectService`.
- App settings load/save use `SettingsService` under `%APPDATA%\ImageUiSlicer`.

## Dependency Structure Review

Healthy boundaries:
- Models are mostly dependency-light.
- Geometry helper is reusable across services and canvas/editor code.
- Services generally depend on models plus SkiaSharp.

Fragile boundaries:
- `MainViewModel` is a central dependency sink for nearly every major feature.
- `CanvasController` depends on the concrete view model rather than an editor interface.
- `AiCutoutDetectionService` depends on OpenAI request/response details directly, not behind a provider contract.
- `CutoutPreviewFactory` partially duplicates rasterization behavior instead of delegating to one renderer.

## Recommended Rebuild Plan

### Phase 1: Stabilize boundaries without changing UX
- Introduce interfaces for editor session, preview rendering, cutout detection, and project/session persistence.
- Move service construction into a small composition root at app startup.
- Replace `SelectionSyncRequested` with a view-layer selected-items adapter.

### Phase 2: Split the main view model
- Keep `MainShellViewModel` for top-level window state.
- Move project session state into `ProjectSessionController`.
- Move selection/editor logic into `SelectionEditorService`.
- Move export preset/naming logic into `ExportProfileService`.
- Move undo/redo into `UndoHistoryService`.
- Move detect orchestration into `DetectionCoordinator`.

### Phase 3: Unify rendering
- Create a shared `CutoutRenderService` that owns:
  - outline policy
  - crop bounds
  - transparent render output
  - preview render variants
- Have gallery preview, inspector preview, and export all depend on that single renderer.

### Phase 4: Formalize detection providers
- `ICutoutDetectionProvider`
  - `HeuristicCutoutDetectionProvider`
  - `OpenAiCutoutDetectionProvider`
  - `ShapeReuseDetectionProvider`
- Add provider health/state objects so UI can show readiness without embedding provider logic in the main view model.

### Phase 5: Repository hygiene
- Remove tracked `bin/` and `obj/` artifacts.
- Keep only one documentation source of truth under `specs/`.
- Replace stale scaffold docs with current developer/operator docs.
- Add tests for geometry transforms, export rendering, project load/save normalization, and detection suggestion normalization.

## Open Questions

1. Is per-cutout export customization part of the intended product, or should export options remain project-wide only?
2. Should AI detection remain cloud-only, or do you want an offline/local provider contract next?
3. Are root spec files intentionally duplicated for convenience, or can `specs/` become the only canonical location?
4. Do you want `CutoutModel.Notes` kept as hidden future metadata, or removed from the project schema?

## Audit Outcome

- Runtime/source audit completed across the maintained application code.
- Generated artifacts and duplicated docs were identified and categorized.
- One dead no-op statement was removed safely.
- Future build-artifact churn is now guarded by `.gitignore`.
- No automated tests exist yet, so residual risk remains around editor interactions, render edge cases, and persistence regressions.
