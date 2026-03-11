using ImageUiSlicer.CanvasEngine;

namespace ImageUiSlicer.ViewModels;

public sealed partial class MainViewModel
{
    private const double DefaultLeftPaneWidth = 228;
    private const double DefaultRightPaneWidth = 258;
    private const double MinLeftPaneWidth = 190;
    private const double MaxLeftPaneWidth = 360;
    private const double MinRightPaneWidth = 220;
    private const double MaxRightPaneWidth = 420;

    private double _leftPaneWidth = DefaultLeftPaneWidth;
    private double _rightPaneWidth = DefaultRightPaneWidth;
    private bool _isPreviewFocusMode;
    private bool _showCanvasHud = true;

    public double LeftPaneWidth
    {
        get => _leftPaneWidth;
        set
        {
            var normalized = NormalizePaneWidth(value, DefaultLeftPaneWidth, MinLeftPaneWidth, MaxLeftPaneWidth);
            if (!SetProperty(ref _leftPaneWidth, normalized))
            {
                return;
            }

            _settings.LastLeftPaneWidth = normalized;
            PersistSettings();
        }
    }

    public double RightPaneWidth
    {
        get => _rightPaneWidth;
        set
        {
            var normalized = NormalizePaneWidth(value, DefaultRightPaneWidth, MinRightPaneWidth, MaxRightPaneWidth);
            if (!SetProperty(ref _rightPaneWidth, normalized))
            {
                return;
            }

            _settings.LastRightPaneWidth = normalized;
            PersistSettings();
        }
    }

    public bool IsPreviewFocusMode
    {
        get => _isPreviewFocusMode;
        private set
        {
            if (!SetProperty(ref _isPreviewFocusMode, value))
            {
                return;
            }

            _settings.LastPreviewFocusMode = value;
            PersistSettings();
            RaisePropertyChanged(nameof(PreviewFocusButtonLabel));
            RaisePropertyChanged(nameof(PreviewPrimaryStats));
        }
    }

    public bool ShowCanvasHud
    {
        get => _showCanvasHud;
        set
        {
            if (!SetProperty(ref _showCanvasHud, value))
            {
                return;
            }

            _settings.LastShowCanvasHud = value;
            PersistSettings();
        }
    }

    public string PreviewFocusButtonLabel => IsPreviewFocusMode ? "Restore Panes" : "Focus Preview";

    public string PreviewPrimaryStats => HasImage
        ? $"Source {Project.SourceImage.PixelWidth} x {Project.SourceImage.PixelHeight}  •  {Project.Cutouts.Count} cutout(s)  •  Tool {GetToolLabel(ActiveTool)}  •  {ZoomText}"
        : "Load an image to start cutting.";

    public string PreviewSecondaryStats => HasActiveSelection
        ? $"Selection {ActiveSelection.SizeLabel}  •  {ActiveSelectionPointCount} pts  •  {ActiveSelectionMode}"
        : PrimarySelectedCutout is not null
            ? $"{PrimarySelectedCutout.Name}  •  {PrimarySelectedCutout.SizeLabel}  •  {PrimarySelectedCutout.ConfidenceLabel}"
            : StatusText;

    private void InitializeLayoutPreferences()
    {
        _leftPaneWidth = NormalizePaneWidth(_settings.LastLeftPaneWidth, DefaultLeftPaneWidth, MinLeftPaneWidth, MaxLeftPaneWidth);
        _rightPaneWidth = NormalizePaneWidth(_settings.LastRightPaneWidth, DefaultRightPaneWidth, MinRightPaneWidth, MaxRightPaneWidth);
        _isPreviewFocusMode = _settings.LastPreviewFocusMode;
        _showCanvasHud = _settings.LastShowCanvasHud;
    }

    public void SetPreviewFocusMode(bool isEnabled)
    {
        IsPreviewFocusMode = isEnabled;
    }

    public void StorePaneWidths(double leftWidth, double rightWidth)
    {
        if (IsPreviewFocusMode)
        {
            return;
        }

        LeftPaneWidth = leftWidth;
        RightPaneWidth = rightWidth;
    }

    private void RefreshPreviewStats()
    {
        RaisePropertyChanged(nameof(PreviewPrimaryStats));
        RaisePropertyChanged(nameof(PreviewSecondaryStats));
        RaisePropertyChanged(nameof(PreviewFocusButtonLabel));
    }

    private static double NormalizePaneWidth(double value, double fallback, double min, double max)
    {
        return double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    }

    private static string GetToolLabel(CanvasTool tool)
    {
        return tool switch
        {
            CanvasTool.Select => "Select",
            CanvasTool.Lasso => "Lasso",
            CanvasTool.Polygon => "Polygon",
            CanvasTool.Shape => "Shapes",
            CanvasTool.BrushAdd => "Brush +",
            CanvasTool.BrushErase => "Brush -",
            _ => "Select",
        };
    }
}
