using ImageUiSlicer.CanvasEngine;
using ImageUiSlicer.Infrastructure;
using ImageUiSlicer.Models;
using ImageUiSlicer.Services;
using SkiaSharp;

namespace ImageUiSlicer.ViewModels;

public sealed partial class MainViewModel
{
    private readonly ShapeReuseDetectionService _shapeReuseDetectionService = new();
    private bool _isSmartMatchBusy;
    private SavedShapeModel? _selectedSavedShape;
    private bool _allowSavedShapeGrow;
    private int _savedShapeGrowPercent = 100;

    public RelayCommand MatchSelectedCutoutCommand { get; private set; } = null!;

    public RelayCommand PasteSelectedShapeCommand { get; private set; } = null!;

    public RelayCommand SaveSelectedShapeCommand { get; private set; } = null!;

    public RelayCommand ApplySavedShapeCommand { get; private set; } = null!;

    public RelayCommand DeleteSavedShapeCommand { get; private set; } = null!;

    public RelayCommand DuplicateSelectedCutoutCommand { get; private set; } = null!;

    public bool IsSmartMatchBusy
    {
        get => _isSmartMatchBusy;
        private set
        {
            if (SetProperty(ref _isSmartMatchBusy, value))
            {
                RaisePropertyChanged(nameof(MatchSelectedButtonLabel));
                RaisePropertyChanged(nameof(ShapeReuseStatus));
                RaisePropertyChanged(nameof(ShapeReuseHint));
            }
        }
    }

    public SavedShapeModel? SelectedSavedShape
    {
        get => _selectedSavedShape;
        set
        {
            if (!SetProperty(ref _selectedSavedShape, value))
            {
                return;
            }

            RaisePropertyChanged(nameof(HasSelectedSavedShape));
            RaisePropertyChanged(nameof(ShapeReuseStatus));
            RaisePropertyChanged(nameof(ShapeReuseHint));
            RefreshCommands();
        }
    }

    public bool AllowSavedShapeGrow
    {
        get => _allowSavedShapeGrow;
        set
        {
            if (SetProperty(ref _allowSavedShapeGrow, value))
            {
                RaisePropertyChanged(nameof(SavedShapeGrowLabel));
            }
        }
    }

    public int SavedShapeGrowPercent
    {
        get => _savedShapeGrowPercent;
        set
        {
            var clamped = (int)Math.Clamp(value, 100, 250);
            if (SetProperty(ref _savedShapeGrowPercent, clamped))
            {
                RaisePropertyChanged(nameof(SavedShapeGrowLabel));
            }
        }
    }

    public string SavedShapeGrowLabel => AllowSavedShapeGrow ? $"{SavedShapeGrowPercent}%" : "Default";

    public bool HasSelectedSavedShape => SelectedSavedShape is not null && GeometryHelper.IsValidGeometry(SelectedSavedShape.Geometry);

    public bool HasReusableShape => PrimarySelectedCutout is not null && GeometryHelper.IsValidGeometry(PrimarySelectedCutout.Geometry);

    public string MatchSelectedButtonLabel => IsSmartMatchBusy ? "Matching..." : "Match Similar";

    public string ShapeReuseStatus => IsSmartMatchBusy
        ? "Smart Match is scanning for repeated placements of the selected master shape."
        : HasSelectedSavedShape
            ? $"Saved shape: {SelectedSavedShape!.Name} ({SelectedSavedShape.BBox.W} x {SelectedSavedShape.BBox.H})."
            : HasReusableShape
                ? $"Master cutout: {PrimarySelectedCutout!.Name} ({PrimarySelectedCutout.BBox.W} x {PrimarySelectedCutout.BBox.H})."
                : "Select a cutout, save it as a shape, then reuse it fast.";

    public string ShapeReuseHint => HasSelectedSavedShape
        ? "Use Saved Shape drops that stored outline as a movable selection. Enable Allow Grow to scale it up before placing."
        : HasReusableShape
            ? "Save Shape stores this cutout outline as a custom reusable shape."
            : "Make one perfect cutout, save it as a shape, then reuse that shape instead of redrawing.";

    private void InitializeShapeReuse()
    {
        MatchSelectedCutoutCommand = new RelayCommand(MatchSelectedCutout, () => HasImage && HasReusableShape && !IsDetectionBusy);
        PasteSelectedShapeCommand = new RelayCommand(PasteSelectedShape, () => HasImage && (HasReusableShape || HasSelectedSavedShape) && !IsDetectionBusy);
        SaveSelectedShapeCommand = new RelayCommand(SaveSelectedCutoutAsShape, () => HasImage && HasReusableShape && !IsDetectionBusy);
        ApplySavedShapeCommand = new RelayCommand(ApplySavedShapeToSelection, () => HasImage && HasSelectedSavedShape && !IsDetectionBusy);
        DeleteSavedShapeCommand = new RelayCommand(DeleteSelectedSavedShape, () => HasSelectedSavedShape && !IsDetectionBusy);
        DuplicateSelectedCutoutCommand = new RelayCommand(DuplicateSelectedCutout, () => HasImage && PrimarySelectedCutout is not null && !IsDetectionBusy);
        SyncSavedShapeSelectionWithProject();
    }

    private void SyncSavedShapeSelectionWithProject()
    {
        if (SelectedSavedShape is not null && Project.SavedShapes.Any(shape => shape.Id == SelectedSavedShape.Id))
        {
            return;
        }

        SelectedSavedShape = Project.SavedShapes.FirstOrDefault();
    }

    public bool TryNudgeSelectionOrCutouts(int dx, int dy)
    {
        if (HasActiveSelection)
        {
            return TryNudgeActiveSelection(dx, dy);
        }

        return TryNudgeSelectedCutouts(dx, dy);
    }

    public bool BeginActiveSelectionMove()
    {
        if (!HasActiveSelection || SourceBitmap is null)
        {
            return false;
        }

        PushUndoSnapshot();
        return true;
    }

    public bool TryMoveActiveSelectionFromOrigin(PathGeometryModel originGeometry, float requestedDx, float requestedDy)
    {
        if (SourceBitmap is null || !GeometryHelper.IsValidGeometry(originGeometry))
        {
            return false;
        }

        var clamped = ClampPointDelta(originGeometry.Points, requestedDx, requestedDy, SourceBitmap.Width, SourceBitmap.Height);
        var geometry = GeometryHelper.Translate(originGeometry, clamped.dx, clamped.dy);
        var bbox = GeometryHelper.ComputeBBox(geometry.Points);

        ActiveSelection = new SelectionModel
        {
            Geometry = geometry,
            BBox = bbox,
            HasSelection = true,
        };

        return Math.Abs(clamped.dx) > 0.01f || Math.Abs(clamped.dy) > 0.01f;
    }

    public void CompleteActiveSelectionMove(bool changed)
    {
        if (changed)
        {
            MarkDirty("Moved active selection.", touchProjectTimestamp: false);
        }
    }

    private bool TryNudgeActiveSelection(int dx, int dy)
    {
        if (!HasActiveSelection || SourceBitmap is null)
        {
            return false;
        }

        var clamped = ClampPointDelta(ActiveSelection.Geometry.Points, dx, dy, SourceBitmap.Width, SourceBitmap.Height);
        if (Math.Abs(clamped.dx) < 0.01f && Math.Abs(clamped.dy) < 0.01f)
        {
            return false;
        }

        PushUndoSnapshot();
        TryMoveActiveSelectionFromOrigin(ActiveSelection.Geometry.DeepClone(), clamped.dx, clamped.dy);
        MarkDirty("Moved active selection.", touchProjectTimestamp: false);
        return true;
    }

    private void SaveSelectedCutoutAsShape()
    {
        if (PrimarySelectedCutout is null || !HasReusableShape || SourceBitmap is null || IsDetectionBusy)
        {
            return;
        }

        var source = PrimarySelectedCutout;
        var sourceBounds = source.BBox.W > 0 && source.BBox.H > 0
            ? source.BBox
            : GeometryHelper.ComputeBBox(source.Geometry.Points);
        var normalizedGeometry = GeometryHelper.Translate(source.Geometry.DeepClone(), -sourceBounds.X, -sourceBounds.Y);

        var baseName = string.IsNullOrWhiteSpace(source.Name) ? "Custom Shape" : source.Name.Trim();
        var uniqueName = BuildUniqueSavedShapeName(baseName);
        var savedShape = new SavedShapeModel
        {
            Name = uniqueName,
            Geometry = normalizedGeometry,
            BBox = GeometryHelper.ComputeBBox(normalizedGeometry.Points),
            AllowGrow = false,
        };

        Project.SavedShapes.Add(savedShape);
        SelectedSavedShape = savedShape;
        RaisePropertyChanged(nameof(ShapeReuseStatus));
        RaisePropertyChanged(nameof(ShapeReuseHint));
        MarkDirty($"Saved custom shape '{uniqueName}'.", touchProjectTimestamp: false);
    }

    private string BuildUniqueSavedShapeName(string seedName)
    {
        var baseName = seedName.EndsWith(" shape", StringComparison.OrdinalIgnoreCase)
            ? seedName
            : $"{seedName} Shape";
        if (Project.SavedShapes.All(shape => !string.Equals(shape.Name, baseName, StringComparison.OrdinalIgnoreCase)))
        {
            return baseName;
        }

        var index = 2;
        while (true)
        {
            var candidate = $"{baseName} {index:00}";
            if (Project.SavedShapes.All(shape => !string.Equals(shape.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }

            index++;
        }
    }

    private void DeleteSelectedSavedShape()
    {
        if (!HasSelectedSavedShape || SelectedSavedShape is null || IsDetectionBusy)
        {
            return;
        }

        var removedName = SelectedSavedShape.Name;
        var index = Project.SavedShapes.IndexOf(SelectedSavedShape);
        if (index < 0)
        {
            return;
        }

        Project.SavedShapes.RemoveAt(index);
        if (Project.SavedShapes.Count == 0)
        {
            SelectedSavedShape = null;
        }
        else
        {
            SelectedSavedShape = Project.SavedShapes[Math.Min(index, Project.SavedShapes.Count - 1)];
        }

        RaisePropertyChanged(nameof(ShapeReuseStatus));
        RaisePropertyChanged(nameof(ShapeReuseHint));
        MarkDirty($"Removed saved shape '{removedName}'.", touchProjectTimestamp: false);
    }

    private void ApplySavedShapeToSelection()
    {
        if (SourceBitmap is null || !HasSelectedSavedShape || SelectedSavedShape is null || IsDetectionBusy)
        {
            return;
        }

        var geometry = SelectedSavedShape.Geometry.DeepClone();
        if (AllowSavedShapeGrow)
        {
            var growFactor = Math.Max(1f, SavedShapeGrowPercent / 100f);
            geometry = ScaleGeometryFromCenter(geometry, growFactor);
        }

        PlaceGeometryAsActiveSelection(geometry, SelectedSavedShape.Name);
    }

    private void DuplicateSelectedCutout()
    {
        if (PrimarySelectedCutout is null || SourceBitmap is null || IsDetectionBusy)
        {
            return;
        }

        var source = PrimarySelectedCutout;
        var sourceGeometry = source.Geometry.DeepClone();
        var pasteOffsetX = Math.Max(16, Math.Min(40, source.BBox.W / 5));
        var pasteOffsetY = Math.Max(12, Math.Min(30, source.BBox.H / 8));
        var clamped = ClampPointDelta(sourceGeometry.Points, pasteOffsetX, pasteOffsetY, SourceBitmap.Width, SourceBitmap.Height);
        var pastedGeometry = GeometryHelper.Translate(sourceGeometry, clamped.dx, clamped.dy);

        PushUndoSnapshot();
        SetSelectedCutouts(Array.Empty<CutoutModel>(), requestSync: true);
        SetActiveSelection(pastedGeometry);
        IsSelectTool = true;
        StatusText = $"Duplicated {source.Name}. Move it into place, then Commit Cutout.";
    }

    private void PasteSelectedShape()
    {
        if (SourceBitmap is null || IsDetectionBusy)
        {
            return;
        }

        if (HasSelectedSavedShape)
        {
            ApplySavedShapeToSelection();
            return;
        }

        if (!HasReusableShape || PrimarySelectedCutout is null)
        {
            return;
        }

        var sourceName = PrimarySelectedCutout.Name;
        var sourceGeometry = PrimarySelectedCutout.Geometry.DeepClone();
        var pasteOffsetX = Math.Max(16, Math.Min(36, PrimarySelectedCutout.BBox.W / 5));
        var pasteOffsetY = Math.Max(12, Math.Min(28, PrimarySelectedCutout.BBox.H / 8));
        var clamped = ClampPointDelta(sourceGeometry.Points, pasteOffsetX, pasteOffsetY, SourceBitmap.Width, SourceBitmap.Height);
        var pastedGeometry = GeometryHelper.Translate(sourceGeometry, clamped.dx, clamped.dy);

        PushUndoSnapshot();
        SetSelectedCutouts(Array.Empty<CutoutModel>(), requestSync: true);
        SetActiveSelection(pastedGeometry);
        IsSelectTool = true;
        StatusText = $"Pasted {sourceName} shape. Drag it into place or use arrow keys, then press Commit Cutout.";
    }

    private void PlaceGeometryAsActiveSelection(PathGeometryModel geometry, string sourceName)
    {
        if (SourceBitmap is null || !GeometryHelper.IsValidGeometry(geometry))
        {
            return;
        }

        var geometryBounds = GeometryHelper.ComputeBBox(geometry.Points);
        var sourceCenterX = geometryBounds.X + (geometryBounds.W * 0.5f);
        var sourceCenterY = geometryBounds.Y + (geometryBounds.H * 0.5f);

        var targetCenterX = SourceBitmap.Width * 0.5f;
        var targetCenterY = SourceBitmap.Height * 0.5f;
        if (PrimarySelectedCutout is not null && PrimarySelectedCutout.BBox.W > 0 && PrimarySelectedCutout.BBox.H > 0)
        {
            targetCenterX = PrimarySelectedCutout.BBox.X + (PrimarySelectedCutout.BBox.W * 0.5f);
            targetCenterY = PrimarySelectedCutout.BBox.Y + (PrimarySelectedCutout.BBox.H * 0.5f);
        }

        var requestedDx = targetCenterX - sourceCenterX;
        var requestedDy = targetCenterY - sourceCenterY;
        var clamped = ClampPointDelta(geometry.Points, requestedDx, requestedDy, SourceBitmap.Width, SourceBitmap.Height);
        var placedGeometry = GeometryHelper.Translate(geometry, clamped.dx, clamped.dy);

        PushUndoSnapshot();
        SetSelectedCutouts(Array.Empty<CutoutModel>(), requestSync: true);
        SetActiveSelection(placedGeometry);
        IsSelectTool = true;
        StatusText = $"Applied saved shape '{sourceName}'. Move it if needed, then Commit Cutout.";
    }

    private static PathGeometryModel ScaleGeometryFromCenter(PathGeometryModel geometry, float factor)
    {
        if (!GeometryHelper.IsValidGeometry(geometry) || factor <= 1.001f)
        {
            return geometry;
        }

        var bounds = GeometryHelper.ComputeBBox(geometry.Points);
        var centerX = bounds.X + (bounds.W * 0.5f);
        var centerY = bounds.Y + (bounds.H * 0.5f);

        return new PathGeometryModel
        {
            Type = geometry.Type,
            Mode = geometry.Mode,
            Closed = geometry.Closed,
            Points = geometry.Points
                .Select(point => new PointF(
                    centerX + ((point.X - centerX) * factor),
                    centerY + ((point.Y - centerY) * factor)))
                .ToList(),
        };
    }

    private async void MatchSelectedCutout()
    {
        var sourceBitmapAtStart = SourceBitmap;
        var selectedCutout = PrimarySelectedCutout;
        if (sourceBitmapAtStart is null || selectedCutout is null || !HasReusableShape || IsDetectionBusy)
        {
            return;
        }

        var sourceName = selectedCutout.Name;
        var sessionRevision = _sessionRevision;
        var projectAtStart = Project;
        using var bitmapCopy = sourceBitmapAtStart.Copy();
        var cutoutCopy = selectedCutout.DeepClone();

        try
        {
            IsSmartMatchBusy = true;
            RefreshCommands();
            StatusText = $"Smart Match is scanning for more '{sourceName}' shapes...";

            var suggestions = await Task.Run(() => _shapeReuseDetectionService.FindMatches(bitmapCopy, cutoutCopy));
            if (!IsSessionCurrent(sessionRevision, projectAtStart, sourceBitmapAtStart))
            {
                return;
            }

            if (suggestions.Count == 0)
            {
                StatusText = $"No strong repeated matches found from {sourceName}. Paste Shape lets you place the same outline manually.";
                return;
            }

            var generatedCutouts = BuildGeneratedCutoutsFromSuggestions(suggestions);
            if (generatedCutouts.Count == 0)
            {
                StatusText = $"Smart Match found candidates from {sourceName}, but none were usable cutouts.";
                return;
            }

            PushUndoSnapshot();
            ClearActiveSelection();
            foreach (var cutout in generatedCutouts)
            {
                Project.Cutouts.Add(cutout);
            }

            SetSelectedCutouts(generatedCutouts, requestSync: true);
            IsSelectTool = true;
            MarkDirty($"Matched {generatedCutouts.Count} repeated shape(s) from {sourceName}.", touchProjectTimestamp: false);
        }
        catch (Exception ex)
        {
            StatusText = $"Smart Match failed: {ex.Message}";
        }
        finally
        {
            IsSmartMatchBusy = false;
            RefreshCommands();
        }
    }

    private static (float dx, float dy) ClampPointDelta(IReadOnlyList<PointF> points, float requestedDx, float requestedDy, int imageWidth, int imageHeight)
    {
        if (points.Count == 0)
        {
            return (0f, 0f);
        }

        var minX = points.Min(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxX = points.Max(point => point.X);
        var maxY = points.Max(point => point.Y);

        var dx = requestedDx;
        var dy = requestedDy;

        if (minX + dx < 0f)
        {
            dx = -minX;
        }
        else if (maxX + dx > imageWidth - 1)
        {
            dx = (imageWidth - 1) - maxX;
        }

        if (minY + dy < 0f)
        {
            dy = -minY;
        }
        else if (maxY + dy > imageHeight - 1)
        {
            dy = (imageHeight - 1) - maxY;
        }

        return (dx, dy);
    }
}


