using ImageUiSlicer.CanvasEngine;
using ImageUiSlicer.Models;

namespace ImageUiSlicer.ViewModels;

public sealed partial class MainViewModel
{
    private const int DefaultExportPixelSize = 256;

    private CutoutModel? _brushRefineTargetCutout;
    private bool _brushRefineTargetsActiveSelection;

    public bool HasBrushTarget => HasSelectedCutout || HasActiveSelection;

    public string BrushTargetLabel => PrimarySelectedCutout is not null
        ? $"Target: {PrimarySelectedCutout.Name}"
        : HasActiveSelection ? "Target: Active selection" : "Target: none";

    public string ExportScaleChoice
    {
        get => NormalizeExportScaleChoice(Project.Defaults.TargetPixelSize);
        set => ApplyExportScaleChoice(value);
    }

    public string ExportOutlineMode
    {
        get => NormalizeOutlineMode(Project.Defaults.OutlineMode);
        set => ApplyExportOutlineMode(value);
    }

    private static string NormalizeExportScaleChoice(int targetPixelSize)
    {
        var resolvedSize = targetPixelSize > 0 ? targetPixelSize : DefaultExportPixelSize;
        return $"{resolvedSize}px";
    }

    private static int ParseExportScaleChoice(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalized) &&
            normalized.EndsWith("px", StringComparison.Ordinal) &&
            int.TryParse(normalized[..^2], out var pixelSize) &&
            pixelSize > 0)
        {
            return pixelSize;
        }

        return DefaultExportPixelSize;
    }

    private static string DescribeExportScaleChoice(int targetPixelSize)
    {
        var resolvedSize = targetPixelSize > 0 ? targetPixelSize : DefaultExportPixelSize;
        return $"{resolvedSize}px on the longest edge";
    }

    private static string NormalizeOutlineMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "white" => "white",
            "black" => "black",
            _ => "none",
        };
    }

    private void ApplyExportScaleChoice(string? value)
    {
        var targetPixelSize = ParseExportScaleChoice(value);
        if (Project.Defaults.TargetPixelSize == targetPixelSize)
        {
            RaisePropertyChanged(nameof(ExportScaleChoice));
            return;
        }

        var previousSuspendState = _suspendDirtyTracking;
        _suspendDirtyTracking = true;
        Project.Defaults.Scale = 1;
        Project.Defaults.TargetPixelSize = targetPixelSize;
        foreach (var cutout in Project.Cutouts)
        {
            cutout.Export.Scale = 1;
            cutout.Export.TargetPixelSize = targetPixelSize;
        }
        _suspendDirtyTracking = previousSuspendState;

        RaisePropertyChanged(nameof(ExportScaleChoice));
        RefreshAllCutoutPreviews();
        RefreshSelectedCutoutInspectorPreview();
        if (HasImage && Project.Cutouts.Count > 0)
        {
            MarkDirty($"Set export size to {DescribeExportScaleChoice(targetPixelSize)}.", touchProjectTimestamp: false);
        }
    }

    private void ApplyExportOutlineMode(string? value)
    {
        var mode = NormalizeOutlineMode(value);
        if (string.Equals(Project.Defaults.OutlineMode, mode, StringComparison.OrdinalIgnoreCase))
        {
            RaisePropertyChanged(nameof(ExportOutlineMode));
            return;
        }

        var previousSuspendState = _suspendDirtyTracking;
        _suspendDirtyTracking = true;
        Project.Defaults.OutlineMode = mode;
        foreach (var cutout in Project.Cutouts)
        {
            cutout.Export.OutlineMode = mode;
        }
        _suspendDirtyTracking = previousSuspendState;

        RaisePropertyChanged(nameof(ExportOutlineMode));
        RefreshAllCutoutPreviews();
        RefreshSelectedCutoutInspectorPreview();
        if (HasImage && Project.Cutouts.Count > 0)
        {
            var label = mode == "none" ? "none" : mode;
            MarkDirty($"Set cutout edge line to {label}.", touchProjectTimestamp: false);
        }
    }

    private bool TryResolveBrushRefineTarget(out CutoutModel? cutout, out PathGeometryModel geometry)
    {
        cutout = PrimarySelectedCutout;
        if (cutout is not null && GeometryHelper.IsValidGeometry(cutout.Geometry))
        {
            geometry = cutout.Geometry;
            return true;
        }

        if (HasActiveSelection && GeometryHelper.IsValidGeometry(ActiveSelection.Geometry))
        {
            cutout = null;
            geometry = ActiveSelection.Geometry;
            return true;
        }

        geometry = new PathGeometryModel();
        return false;
    }

    private bool TryGetBrushRefineGeometry(out CutoutModel? cutout, out PathGeometryModel geometry)
    {
        cutout = _brushRefineTargetCutout;
        if (cutout is not null && GeometryHelper.IsValidGeometry(cutout.Geometry))
        {
            geometry = cutout.Geometry;
            return true;
        }

        if (_brushRefineTargetsActiveSelection && HasActiveSelection && GeometryHelper.IsValidGeometry(ActiveSelection.Geometry))
        {
            cutout = null;
            geometry = ActiveSelection.Geometry;
            return true;
        }

        geometry = new PathGeometryModel();
        return false;
    }

    private void SetBrushRefineTarget(CutoutModel? cutout)
    {
        _brushRefineTargetCutout = cutout;
        _brushRefineTargetsActiveSelection = cutout is null && HasActiveSelection;
    }

    private void ClearBrushRefineTarget()
    {
        _brushRefineTargetCutout = null;
        _brushRefineTargetsActiveSelection = false;
    }

    private void ApplyBrushGeometryResult(CutoutModel? cutout, PathGeometryModel geometry)
    {
        var bounds = GeometryHelper.ComputeBBox(geometry.Points);
        var previousSuspendState = _suspendDirtyTracking;
        _suspendDirtyTracking = true;

        if (cutout is not null)
        {
            cutout.Geometry = geometry;
            cutout.BBox = bounds;
        }
        else
        {
            ActiveSelection = new SelectionModel
            {
                Geometry = geometry,
                BBox = bounds,
                HasSelection = true,
            };
        }

        _suspendDirtyTracking = previousSuspendState;

        if (cutout is not null)
        {
            UpdateCutoutPreview(cutout);
            RaisePropertyChanged(nameof(SelectedCutoutBounds));
            RaisePropertyChanged(nameof(SelectedCutoutSize));
            RaisePropertyChanged(nameof(SelectedCutoutMode));
            RaisePropertyChanged(nameof(SelectedCutoutConfidence));
            RefreshSelectedCutoutInspectorPreview();
            return;
        }

        RaisePropertyChanged(nameof(HasBrushTarget));
        RaisePropertyChanged(nameof(BrushTargetLabel));
        RaisePropertyChanged(nameof(ActiveSelectionMode));
        RaisePropertyChanged(nameof(ActiveSelectionPointCount));
    }

    private string GetBrushRefineTargetLabel()
    {
        return _brushRefineTargetCutout?.Name ?? (_brushRefineTargetsActiveSelection ? "active selection" : "cutout");
    }
}
