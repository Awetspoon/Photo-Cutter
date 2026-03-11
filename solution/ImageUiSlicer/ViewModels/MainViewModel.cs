using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Media.Imaging;
using ImageUiSlicer.CanvasEngine;
using ImageUiSlicer.Infrastructure;
using ImageUiSlicer.Models;
using ImageUiSlicer.Services;
using SkiaSharp;

namespace ImageUiSlicer.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly SettingsService _settingsService = new();
    private readonly ProjectService _projectService = new();
    private readonly ImageService _imageService = new();
    private readonly ExportService _exportService = new();
    private readonly AutoCutoutService _autoCutoutService = new();
    private readonly CutoutPreviewFactory _cutoutPreviewFactory = new();
    private readonly AppSettings _settings;
    private readonly Stack<EditorSnapshot> _undoStack = new();
    private readonly Stack<EditorSnapshot> _redoStack = new();

    private ProjectModel _project = new();
    private SelectionModel _activeSelection = new();
    private SKBitmap? _sourceBitmap;
    private string _statusText = "Open an image, then use Shapes, Lasso, or Polygon to create your first cutout.";
    private string _exportFolder = string.Empty;
    private string _exportNamingMode = "autoNumbered";
    private string _exportPrefix = string.Empty;
    private bool _isDirty;
    private bool _suspendDirtyTracking;
    private CanvasTool _activeTool = CanvasTool.Lasso;
    private double _canvasScale;
    private double _panX;
    private double _panY;
    private string _exportPreset = "custom";
    private double _autoDetectStrength = 65;
    private bool _splitPreviewEnabled = false;
    private double _splitPreviewRatio = 0.5;
    private int _refineBrushSize = 30;
    private BitmapSource? _selectedCutoutPreviewImage;
    private string _selectedCutoutPreviewCaption = "After";
    private bool _brushRefineStrokeActive;
    private bool _brushRefineStrokeChanged;
    private int _sessionRevision;

    public MainViewModel()
    {
        _settings = _settingsService.Load();
        InitializeLayoutPreferences();
        _exportFolder = _settings.DefaultExportFolder;
        _exportNamingMode = _settings.LastExportNamingMode;
        _exportPrefix = _settings.LastExportPrefix;
        _exportPreset = NormalizeExportPreset(_settings.LastExportPreset);
        _autoDetectStrength = Math.Clamp(_settings.LastAutoDetectStrength, 0, 100);

        SelectedCutouts = new ObservableCollection<CutoutModel>();
        AttachProject(Project);

        ExportAllCommand = new RelayCommand(ExportAll, () => HasImage && Project.Cutouts.Count > 0 && !string.IsNullOrWhiteSpace(ExportFolder));
        ExportSelectedCommand = new RelayCommand(ExportSelected, () => HasImage && SelectedCutouts.Count > 0 && !string.IsNullOrWhiteSpace(ExportFolder));
        AutoDetectCutoutsCommand = new RelayCommand(AutoDetectCutouts, () => HasImage && !IsDetectionBusy);
        CommitSelectionCommand = new RelayCommand(CommitSelection, () => HasActiveSelection && HasImage);
        ClearSelectionCommand = new RelayCommand(ClearActiveSelection, () => HasActiveSelection);
        DeleteSelectedCommand = new RelayCommand(DeleteSelectedCutouts, () => SelectedCutouts.Count > 0);
        MoveUpCommand = new RelayCommand(MoveUp, () => SelectedCutouts.Count > 0);
        MoveDownCommand = new RelayCommand(MoveDown, () => SelectedCutouts.Count > 0);
        UndoCommand = new RelayCommand(Undo, () => _undoStack.Count > 0);
        RedoCommand = new RelayCommand(Redo, () => _redoStack.Count > 0);

        InitializeAiDetection();
        InitializeShapeReuse();
        ApplyExportPreset(ExportPreset, markDirty: false, applyToExistingCutouts: false);
        RefreshSelectedCutoutInspectorPreview();

        UpdateComputedState();
    }

    private bool IsDetectionBusy => IsAiDetectBusy || IsSmartMatchBusy;

    private bool IsSessionCurrent(int sessionRevision, ProjectModel project, SKBitmap? bitmap)
    {
        return sessionRevision == _sessionRevision &&
               ReferenceEquals(Project, project) &&
               ReferenceEquals(SourceBitmap, bitmap);
    }

    public event EventHandler? SelectionSyncRequested;

    public event EventHandler? GalleryRefreshRequested;

    public ProjectModel Project
    {
        get => _project;
        private set
        {
            if (ReferenceEquals(_project, value))
            {
                return;
            }

            DetachProject(_project);
            _project = value;
            AttachProject(_project);
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(ShowCutoutsOverlay));
        }
    }

    public SelectionModel ActiveSelection
    {
        get => _activeSelection;
        private set
        {
            if (SetProperty(ref _activeSelection, value))
            {
                RaisePropertyChanged(nameof(HasActiveSelection));
                RaisePropertyChanged(nameof(CanCommitOrFinalizeSelection));
                RaisePropertyChanged(nameof(HasBrushTarget));
                RaisePropertyChanged(nameof(BrushTargetLabel));
                RaisePropertyChanged(nameof(HasReusableShape));
                RaisePropertyChanged(nameof(ShapeReuseStatus));
                RaisePropertyChanged(nameof(ShapeReuseHint));
                RaisePropertyChanged(nameof(HasSelectedSavedShape));
                RaisePropertyChanged(nameof(ActiveSelectionMode));
                RaisePropertyChanged(nameof(ActiveSelectionPointCount));
                RaisePropertyChanged(nameof(PreviewSecondaryStats));
            }
        }
    }

    public ObservableCollection<CutoutModel> SelectedCutouts { get; }

    public CutoutModel? PrimarySelectedCutout => SelectedCutouts.FirstOrDefault();

    public RelayCommand ExportAllCommand { get; }

    public RelayCommand ExportSelectedCommand { get; }


    public RelayCommand AutoDetectCutoutsCommand { get; }
    public RelayCommand CommitSelectionCommand { get; }

    public RelayCommand ClearSelectionCommand { get; }

    public RelayCommand DeleteSelectedCommand { get; }

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    public RelayCommand UndoCommand { get; }

    public RelayCommand RedoCommand { get; }

    public string ExportPreset
    {
        get => _exportPreset;
        set
        {
            var normalized = NormalizeExportPreset(value);
            if (!SetProperty(ref _exportPreset, normalized))
            {
                return;
            }

            _settings.LastExportPreset = normalized;
            PersistSettings();
            ApplyExportPreset(normalized, markDirty: HasImage && Project.Cutouts.Count > 0, applyToExistingCutouts: true);
        }
    }

    public double AutoDetectStrength
    {
        get => _autoDetectStrength;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (!SetProperty(ref _autoDetectStrength, clamped))
            {
                return;
            }

            _settings.LastAutoDetectStrength = clamped;
            PersistSettings();
            RaisePropertyChanged(nameof(AutoDetectStrengthLabel));
        }
    }

    public string AutoDetectStrengthLabel => AutoDetectStrength < 35
        ? "Conservative"
        : AutoDetectStrength < 70 ? "Balanced" : "Aggressive";

    public bool SplitPreviewEnabled
    {
        get => _splitPreviewEnabled;
        set
        {
            if (SetProperty(ref _splitPreviewEnabled, value))
            {
                RefreshSelectedCutoutInspectorPreview();
            }
        }
    }

    public double SplitPreviewRatio
    {
        get => _splitPreviewRatio;
        set
        {
            var clamped = Math.Clamp(value, 0.05, 0.95);
            if (SetProperty(ref _splitPreviewRatio, clamped))
            {
                RefreshSelectedCutoutInspectorPreview();
            }
        }
    }

    public BitmapSource? SelectedCutoutPreviewImage
    {
        get => _selectedCutoutPreviewImage;
        private set
        {
            if (SetProperty(ref _selectedCutoutPreviewImage, value))
            {
                RaisePropertyChanged(nameof(HasSelectedCutoutPreview));
            }
        }
    }

    public string SelectedCutoutPreviewCaption
    {
        get => _selectedCutoutPreviewCaption;
        private set => SetProperty(ref _selectedCutoutPreviewCaption, value);
    }

    public bool HasSelectedCutoutPreview => SelectedCutoutPreviewImage is not null;

    public int RefineBrushSize
    {
        get => _refineBrushSize;
        set
        {
            var clamped = (int)Math.Clamp(value, 6, 140);
            if (SetProperty(ref _refineBrushSize, clamped))
            {
                RaisePropertyChanged(nameof(RefineBrushSizeLabel));
            }
        }
    }

    public string RefineBrushSizeLabel => $"{RefineBrushSize}px";

    public SKBitmap? SourceBitmap => _sourceBitmap;

    public bool HasImage => _sourceBitmap is not null;

    public bool HasActiveSelection => ActiveSelection.HasSelection;

    public bool CanCommitOrFinalizeSelection => HasImage && (HasActiveSelection || ActiveTool == CanvasTool.Polygon);

    public bool HasSelectedCutout => PrimarySelectedCutout is not null;

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (SetProperty(ref _statusText, value))
            {
                RaisePropertyChanged(nameof(PreviewSecondaryStats));
            }
        }
    }

    public string ExportFolder
    {
        get => _exportFolder;
        set
        {
            if (!SetProperty(ref _exportFolder, value))
            {
                return;
            }

            _settings.DefaultExportFolder = value;
            PersistSettings();
            RaisePropertyChanged(nameof(CutOutsFolderHint));
            RefreshCommands();
        }
    }

    public string ExportNamingMode
    {
        get => _exportNamingMode;
        set
        {
            if (!SetProperty(ref _exportNamingMode, value))
            {
                return;
            }

            _settings.LastExportNamingMode = value;
            PersistSettings();
        }
    }

    public string ExportPrefix
    {
        get => _exportPrefix;
        set
        {
            if (!SetProperty(ref _exportPrefix, value))
            {
                return;
            }

            _settings.LastExportPrefix = value;
            PersistSettings();
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                RaisePropertyChanged(nameof(WindowTitle));
            }
        }
    }

    public CanvasTool ActiveTool
    {
        get => _activeTool;
        private set
        {
            if (SetProperty(ref _activeTool, value))
            {
                RaisePropertyChanged(nameof(IsSelectTool));
                RaisePropertyChanged(nameof(IsLassoTool));
                RaisePropertyChanged(nameof(IsPolygonTool));
                RaisePropertyChanged(nameof(IsShapeTool));
                RaisePropertyChanged(nameof(IsBrushAddTool));
                RaisePropertyChanged(nameof(IsBrushEraseTool));
                RaisePropertyChanged(nameof(CanCommitOrFinalizeSelection));
                RaisePropertyChanged(nameof(PreviewPrimaryStats));
            }
        }
    }

    public double CanvasScale
    {
        get => _canvasScale;
        private set
        {
            if (SetProperty(ref _canvasScale, value))
            {
                RaisePropertyChanged(nameof(ZoomText));
                RaisePropertyChanged(nameof(PreviewPrimaryStats));
            }
        }
    }

    public double PanX
    {
        get => _panX;
        private set => SetProperty(ref _panX, value);
    }

    public double PanY
    {
        get => _panY;
        private set => SetProperty(ref _panY, value);
    }

    public string WindowTitle => IsDirty
        ? $"Photo Cutter - {Project.ProjectName} *"
        : $"Photo Cutter - {Project.ProjectName}";

    public string SourceImageLabel => HasImage
        ? $"{Project.SourceImage.PixelWidth} x {Project.SourceImage.PixelHeight}"
        : "No source image loaded";

    public string SourceImagePathLabel => HasImage
        ? Project.SourceImage.Path
        : "Drop a PNG, JPG, BMP, GIF, or WEBP to begin.";

    public string ZoomText => CanvasScale > 0 ? $"{CanvasScale * 100:0}%" : "Fit";

    public string ActiveSelectionMode => HasActiveSelection ? ActiveSelection.Geometry.Mode : "none";

    public int ActiveSelectionPointCount => HasActiveSelection ? ActiveSelection.Geometry.Points.Count : 0;

    public string SelectedCutoutBounds => PrimarySelectedCutout?.BoundsLabel ?? "No cutout selected";

    public string SelectedCutoutSize => PrimarySelectedCutout?.SizeLabel ?? "";

    public string SelectedCutoutMode => PrimarySelectedCutout?.Geometry.Mode ?? "";

    public string SelectedCutoutConfidence => PrimarySelectedCutout?.ConfidenceLabel ?? "manual";

    public string CutOutsFolderHint => string.IsNullOrWhiteSpace(ExportFolder)
        ? "Set a base export folder. Files are always written into a cut outs subfolder."
        : $"Exports land in {Path.Combine(ExportFolder, "cut outs")}";

    public bool ShowCutoutsOverlay
    {
        get => Project.ShowCutoutsOverlay;
        set
        {
            if (Project.ShowCutoutsOverlay == value)
            {
                return;
            }

            Project.ShowCutoutsOverlay = value;
            RaisePropertyChanged();
            MarkDirty("Overlay visibility updated.");
        }
    }

    public bool IsSelectTool
    {
        get => ActiveTool == CanvasTool.Select;
        set
        {
            if (value)
            {
                SetActiveTool(CanvasTool.Select);
            }
        }
    }

    public bool IsLassoTool
    {
        get => ActiveTool == CanvasTool.Lasso;
        set
        {
            if (value)
            {
                SetActiveTool(CanvasTool.Lasso);
            }
        }
    }

    public bool IsPolygonTool
    {
        get => ActiveTool == CanvasTool.Polygon;
        set
        {
            if (value)
            {
                SetActiveTool(CanvasTool.Polygon);
            }
        }
    }


    public bool IsBrushAddTool
    {
        get => ActiveTool == CanvasTool.BrushAdd;
        set
        {
            if (value)
            {
                SetActiveTool(CanvasTool.BrushAdd);
            }
        }
    }

    public bool IsBrushEraseTool
    {
        get => ActiveTool == CanvasTool.BrushErase;
        set
        {
            if (value)
            {
                SetActiveTool(CanvasTool.BrushErase);
            }
        }
    }

    public string GetInitialProjectFolder()
    {
        if (!string.IsNullOrWhiteSpace(_settings.LastOpenFolder) && Directory.Exists(_settings.LastOpenFolder))
        {
            return _settings.LastOpenFolder;
        }

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ImageUiSlicer Projects");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public string GetInitialImageFolder()
    {
        if (!string.IsNullOrWhiteSpace(_settings.LastImageFolder) && Directory.Exists(_settings.LastImageFolder))
        {
            return _settings.LastImageFolder;
        }

        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return Directory.Exists(pictures) ? pictures : GetInitialProjectFolder();
    }

    public bool IsSupportedImage(string path) => _imageService.IsSupportedImage(path);

    public SKBitmap LoadBitmap(string path) => _imageService.LoadBitmap(path);

    public ProjectModel LoadProjectMetadata(string path) => _projectService.Load(path);

    public void ApplyImageProject(string imagePath, SKBitmap bitmap)
    {
        var project = new ProjectModel
        {
            ProjectName = Path.GetFileNameWithoutExtension(imagePath),
            SourceImage = new SourceImageModel
            {
                Path = imagePath,
                PixelWidth = bitmap.Width,
                PixelHeight = bitmap.Height,
            },
            Defaults = new ExportOptionsModel(),
            ShowCutoutsOverlay = true,
        };

        ApplyProject(project, bitmap, imagePath, isDirty: false);
        StatusText = $"Loaded image {Path.GetFileName(imagePath)}. Draw with Shapes, Lasso, or Polygon, then commit your cutout.";
    }

    public void ApplyLoadedProject(ProjectModel project, SKBitmap bitmap, string projectPath)
    {
        project.ProjectFilePath = projectPath;
        project.SourceImage.PixelWidth = bitmap.Width;
        project.SourceImage.PixelHeight = bitmap.Height;
        ApplyProject(project, bitmap, project.SourceImage.Path, isDirty: false);
        RememberProjectFolder(projectPath);
        StatusText = $"Opened project {Path.GetFileName(projectPath)}.";
    }

    public void SaveProject(string filePath)
    {
        _projectService.Save(Project, filePath);
        RememberProjectFolder(filePath);
        IsDirty = false;
        StatusText = $"Saved {Path.GetFileName(filePath)}.";
    }

    public void SetSelectedCutouts(IEnumerable<CutoutModel> cutouts, bool requestSync = false)
    {
        SelectedCutouts.Clear();
        foreach (var cutout in cutouts.Distinct())
        {
            SelectedCutouts.Add(cutout);
        }

        RaisePropertyChanged(nameof(PrimarySelectedCutout));
        RaisePropertyChanged(nameof(HasSelectedCutout));
        RaisePropertyChanged(nameof(SelectedCutoutBounds));
        RaisePropertyChanged(nameof(SelectedCutoutSize));
        RaisePropertyChanged(nameof(SelectedCutoutMode));
        RaisePropertyChanged(nameof(SelectedCutoutConfidence));
        RaisePropertyChanged(nameof(HasBrushTarget));
        RaisePropertyChanged(nameof(BrushTargetLabel));
        RaisePropertyChanged(nameof(HasReusableShape));
        RaisePropertyChanged(nameof(ShapeReuseStatus));
        RaisePropertyChanged(nameof(ShapeReuseHint));
                RaisePropertyChanged(nameof(HasSelectedSavedShape));
        RaisePropertyChanged(nameof(ExportScaleChoice));
        RaisePropertyChanged(nameof(ExportOutlineMode));
        RaisePropertyChanged(nameof(PreviewSecondaryStats));
        RefreshSelectedCutoutInspectorPreview();
        RefreshCommands();

        if (requestSync)
        {
            SelectionSyncRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SelectSingleCutout(CutoutModel? cutout)
    {
        if (cutout is null)
        {
            SetSelectedCutouts(Array.Empty<CutoutModel>(), requestSync: true);
            return;
        }

        SetSelectedCutouts(new[] { cutout }, requestSync: true);
    }

    public void SetActiveSelection(PathGeometryModel geometry)
    {
        if (!GeometryHelper.IsValidGeometry(geometry))
        {
            ClearActiveSelection();
            return;
        }

        ActiveSelection = new SelectionModel
        {
            Geometry = geometry,
            BBox = GeometryHelper.ComputeBBox(geometry.Points),
            HasSelection = true,
        };

        RaisePropertyChanged(nameof(HasActiveSelection));
        RaisePropertyChanged(nameof(HasReusableShape));
        RaisePropertyChanged(nameof(ShapeReuseStatus));
        RaisePropertyChanged(nameof(ShapeReuseHint));
                RaisePropertyChanged(nameof(HasSelectedSavedShape));
        RaisePropertyChanged(nameof(ActiveSelectionMode));
        RaisePropertyChanged(nameof(ActiveSelectionPointCount));
        RaisePropertyChanged(nameof(PreviewSecondaryStats));
        RefreshCommands();
    }

    public void ClearActiveSelection()
    {
        ActiveSelection = new SelectionModel();
        RefreshCommands();
    }

    public void SetCanvasView(double scale, double panX, double panY)
    {
        CanvasScale = scale;
        PanX = panX;
        PanY = panY;
    }

    public void ResetCanvasView()
    {
        CanvasScale = 0;
        PanX = 0;
        PanY = 0;
    }

    public void SetActiveTool(CanvasTool tool)
    {
        ActiveTool = tool;
        StatusText = tool switch
        {
            CanvasTool.Select => "Select tool ready.",
            CanvasTool.Lasso => "Freehand lasso ready.",
            CanvasTool.Polygon => "Polygon lasso ready.",
            CanvasTool.Shape => $"Shape tool ready ({SelectedShapePresetLabel}). Click a center point and drag outward.",
            CanvasTool.BrushAdd => "Refine Add brush ready.",
            CanvasTool.BrushErase => "Refine Erase brush ready.",
            _ => StatusText,
        };
    }

    public bool TryNudgeSelectedCutouts(int dx, int dy)
    {
        if (!HasImage || SelectedCutouts.Count == 0 || SourceBitmap is null)
        {
            return false;
        }

        var clamped = ClampDelta(SelectedCutouts, dx, dy, SourceBitmap.Width, SourceBitmap.Height);
        if (clamped.dx == 0 && clamped.dy == 0)
        {
            return false;
        }

        PushUndoSnapshot();
        foreach (var cutout in SelectedCutouts)
        {
            cutout.Geometry = GeometryHelper.Translate(cutout.Geometry, clamped.dx, clamped.dy);
            cutout.BBox = GeometryHelper.ComputeBBox(cutout.Geometry.Points);
        }

        MarkDirty($"Nudged {SelectedCutouts.Count} cutout(s).", touchProjectTimestamp: false);
        return true;
    }

    public bool BeginBrushRefineStroke(PointF center, float radius, bool addMode)
    {
        if (SourceBitmap is null || radius <= 0f || !TryResolveBrushRefineTarget(out var targetCutout, out _))
        {
            return false;
        }

        PushUndoSnapshot();
        _brushRefineStrokeActive = true;
        _brushRefineStrokeChanged = false;
        SetBrushRefineTarget(targetCutout);

        var changed = ApplyBrushRefine(center, radius, addMode);
        _brushRefineStrokeChanged = _brushRefineStrokeChanged || changed;
        return true;
    }

    public bool ContinueBrushRefineStroke(PointF center, float radius, bool addMode)
    {
        if (!_brushRefineStrokeActive || SourceBitmap is null || radius <= 0f)
        {
            return false;
        }

        var changed = ApplyBrushRefine(center, radius, addMode);
        _brushRefineStrokeChanged = _brushRefineStrokeChanged || changed;
        return true;
    }

    public void EndBrushRefineStroke(bool addMode)
    {
        if (!_brushRefineStrokeActive)
        {
            return;
        }

        _brushRefineStrokeActive = false;
        if (!_brushRefineStrokeChanged)
        {
            ClearBrushRefineTarget();
            return;
        }

        _brushRefineStrokeChanged = false;
        var modeLabel = addMode ? "add" : "erase";
        var targetLabel = GetBrushRefineTargetLabel();
        ClearBrushRefineTarget();
        MarkDirty($"Refined {targetLabel} using brush ({modeLabel}).", touchProjectTimestamp: false);
        RefreshSelectedCutoutInspectorPreview();
    }

    private bool ApplyBrushRefine(PointF center, float radius, bool addMode)
    {
        if (SourceBitmap is null || !TryGetBrushRefineGeometry(out var cutout, out var sourceGeometry))
        {
            return false;
        }

        using var basePath = GeometryHelper.BuildPath(sourceGeometry);
        using var brushPath = new SKPath();
        brushPath.AddCircle(center.X, center.Y, radius);
        using var resultPath = new SKPath();

        var pathOp = addMode ? SKPathOp.Union : SKPathOp.Difference;
        if (!resultPath.Op(basePath, pathOp, brushPath) || resultPath.IsEmpty)
        {
            return false;
        }

        var pointMinDistance = Math.Max(1.5f, radius * 0.35f);
        var refinedPoints = ExtractLargestContourPoints(resultPath, pointMinDistance);
        if (refinedPoints.Count < 3)
        {
            return false;
        }

        var clampedPoints = refinedPoints
            .Select(point => new PointF(
                Math.Clamp(point.X, 0, SourceBitmap.Width - 1),
                Math.Clamp(point.Y, 0, SourceBitmap.Height - 1)))
            .ToList();

        if (clampedPoints.Count < 3)
        {
            return false;
        }

        var geometry = new PathGeometryModel
        {
            Type = "path",
            Mode = addMode ? "refine-add" : "refine-erase",
            Closed = true,
            Points = clampedPoints,
        };

        ApplyBrushGeometryResult(cutout, geometry);
        return true;
    }

    private static List<PointF> ExtractLargestContourPoints(SKPath path, float minDistance)
    {
        var bestContour = new List<PointF>();
        var bestArea = 0f;

        using var measure = new SKPathMeasure(path, true);
        do
        {
            var length = measure.Length;
            if (length < 3f)
            {
                continue;
            }

            var sampleCount = Math.Clamp((int)Math.Ceiling(length / Math.Max(1f, minDistance)), 24, 260);
            var points = new List<PointF>(sampleCount);
            for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                var distance = (sampleIndex / (float)sampleCount) * length;
                if (measure.GetPosition(distance, out var position))
                {
                    points.Add(new PointF(position.X, position.Y));
                }
            }

            points = SimplifyPointsByDistance(points, minDistance);
            if (points.Count < 3)
            {
                continue;
            }

            var area = MathF.Abs(ComputePolygonArea(points));
            if (area > bestArea)
            {
                bestArea = area;
                bestContour = points;
            }
        }
        while (measure.NextContour());

        return bestContour;
    }

    private static List<PointF> SimplifyPointsByDistance(IReadOnlyList<PointF> points, float minDistance)
    {
        if (points.Count <= 3)
        {
            return points.ToList();
        }

        var minDistanceSq = minDistance * minDistance;
        var simplified = new List<PointF>(points.Count);
        foreach (var point in points)
        {
            if (simplified.Count == 0 || DistanceSquared(simplified[^1], point) >= minDistanceSq)
            {
                simplified.Add(point);
            }
        }

        if (simplified.Count >= 3 && DistanceSquared(simplified[0], simplified[^1]) < minDistanceSq)
        {
            simplified.RemoveAt(simplified.Count - 1);
        }

        return simplified;
    }

    private static float DistanceSquared(PointF a, PointF b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    private static float ComputePolygonArea(IReadOnlyList<PointF> points)
    {
        if (points.Count < 3)
        {
            return 0f;
        }

        var area = 0f;
        for (var index = 0; index < points.Count; index++)
        {
            var next = (index + 1) % points.Count;
            area += (points[index].X * points[next].Y) - (points[next].X * points[index].Y);
        }

        return area / 2f;
    }
    public void HandleProjectOrderChanged()
    {
        MarkDirty("Cutout order updated.", touchProjectTimestamp: false);
        RefreshCommands();
    }

    public void MarkDirty(string? statusText = null, bool touchProjectTimestamp = true)
    {
        if (_suspendDirtyTracking)
        {
            return;
        }

        if (touchProjectTimestamp)
        {
            Project.ModifiedUtc = DateTime.UtcNow;
        }

        IsDirty = true;
        if (!string.IsNullOrWhiteSpace(statusText))
        {
            StatusText = statusText;
        }
    }

    private void CommitSelection()
    {
        if (!HasActiveSelection)
        {
            return;
        }

        PushUndoSnapshot();
        var cutout = new CutoutModel
        {
            Name = $"Cutout {Project.Cutouts.Count + 1:000}",
            Geometry = ActiveSelection.Geometry.DeepClone(),
            BBox = ActiveSelection.BBox,
            Export = Project.Defaults.DeepClone(),
        };

        Project.Cutouts.Add(cutout);
        ClearActiveSelection();
        SetSelectedCutouts(new[] { cutout }, requestSync: true);
        MarkDirty($"Created {cutout.Name}.");
    }


    private void AutoDetectCutouts()
    {
        if (SourceBitmap is null || IsDetectionBusy)
        {
            return;
        }

        try
        {
            var suggestions = _autoCutoutService.Detect(SourceBitmap, new AutoCutoutService.DetectionOptions
            {
                Strength = (float)(AutoDetectStrength / 100.0),
                MaxResults = 28,
                DetectSections = true,
            });

            if (suggestions.Count == 0)
            {
                StatusText = "No strong auto-detect suggestions found. Raise strength or try manual tools.";
                return;
            }

            var generatedCutouts = new List<CutoutModel>();
            var nextIndex = Project.Cutouts.Count + 1;
            var sectionCount = 0;

            foreach (var suggestion in suggestions)
            {
                if (!GeometryHelper.IsValidGeometry(suggestion.Geometry))
                {
                    continue;
                }

                var isSection = string.Equals(suggestion.Kind, "section", StringComparison.OrdinalIgnoreCase);
                if (isSection)
                {
                    sectionCount++;
                }

                generatedCutouts.Add(new CutoutModel
                {
                    Name = isSection ? $"Section {nextIndex:000}" : $"Auto {nextIndex:000}",
                    Geometry = suggestion.Geometry,
                    BBox = GeometryHelper.ComputeBBox(suggestion.Geometry.Points),
                    Export = Project.Defaults.DeepClone(),
                    AutoConfidence = Math.Clamp(suggestion.Confidence, 0.0f, 1.0f),
                });
                nextIndex++;
            }

            if (generatedCutouts.Count == 0)
            {
                StatusText = "No usable auto-detect shapes found. Try manual Shapes, Lasso, or Polygon for this image.";
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

            if (sectionCount > 0)
            {
                MarkDirty($"Auto-detected {generatedCutouts.Count} cutout(s), including {sectionCount} section suggestion(s).", touchProjectTimestamp: false);
            }
            else
            {
                MarkDirty($"Auto-detected {generatedCutouts.Count} cutout suggestion(s).", touchProjectTimestamp: false);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Auto detect failed: {ex.Message}";
        }
    }

    private void DeleteSelectedCutouts()
    {
        if (SelectedCutouts.Count == 0)
        {
            return;
        }

        PushUndoSnapshot();
        var selected = SelectedCutouts.ToList();
        foreach (var cutout in selected)
        {
            Project.Cutouts.Remove(cutout);
        }

        SetSelectedCutouts(Array.Empty<CutoutModel>(), requestSync: true);
        MarkDirty($"Deleted {selected.Count} cutout(s).");
    }

    private void ExportAll()
    {
        if (SourceBitmap is null)
        {
            return;
        }

        try
        {
            var exported = 0;
            var index = 1;
            foreach (var cutout in Project.Cutouts)
            {
                _exportService.ExportCutout(SourceBitmap, cutout, ExportFolder, BuildFileName(cutout, index));
                exported++;
                index++;
            }

            StatusText = $"Exported {exported} cutout(s).";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    private void ExportSelected()
    {
        if (SourceBitmap is null)
        {
            return;
        }

        try
        {
            var exported = 0;
            var index = 1;
            foreach (var cutout in Project.Cutouts)
            {
                if (!SelectedCutouts.Contains(cutout))
                {
                    index++;
                    continue;
                }

                _exportService.ExportCutout(SourceBitmap, cutout, ExportFolder, BuildFileName(cutout, index));
                exported++;
                index++;
            }

            StatusText = $"Exported {exported} selected cutout(s).";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    private void RefreshSelectedCutoutInspectorPreview()
    {
        if (SourceBitmap is null || PrimarySelectedCutout is null || !GeometryHelper.IsValidGeometry(PrimarySelectedCutout.Geometry))
        {
            SelectedCutoutPreviewImage = null;
            SelectedCutoutPreviewCaption = "After";
            RaisePropertyChanged(nameof(SelectedCutoutConfidence));
            return;
        }

        SelectedCutoutPreviewImage = _cutoutPreviewFactory.BuildInspectorPreview(SourceBitmap, PrimarySelectedCutout, SplitPreviewEnabled, SplitPreviewRatio);
        SelectedCutoutPreviewCaption = SplitPreviewEnabled
            ? $"Split view ({SplitPreviewRatio * 100:0}% before / {100 - (SplitPreviewRatio * 100):0}% after)"
            : "After cutout";
        RaisePropertyChanged(nameof(SelectedCutoutConfidence));
    }

    private static string NormalizeExportPreset(string preset)
    {
        if (string.IsNullOrWhiteSpace(preset))
        {
            return "custom";
        }

        return preset.Trim() switch
        {
            "icons" => "icons",
            "sprites" => "sprites",
            "uiAssets" => "uiAssets",
            _ => "custom",
        };
    }

    private void ApplyExportPreset(string preset, bool markDirty, bool applyToExistingCutouts)
    {
        var normalized = NormalizeExportPreset(preset);
        if (normalized == "custom")
        {
            return;
        }

        var defaults = normalized switch
        {
            "icons" => new ExportOptionsModel { Scale = 1, TargetPixelSize = 256, Padding = 8, OutlineMode = "none" },
            "sprites" => new ExportOptionsModel { Scale = 1, TargetPixelSize = 128, Padding = 2, OutlineMode = "none" },
            "uiAssets" => new ExportOptionsModel { Scale = 1, TargetPixelSize = 256, Padding = 12, OutlineMode = "none" },
            _ => new ExportOptionsModel { Scale = 1, TargetPixelSize = 256, Padding = 0, OutlineMode = "none" },
        };

        var prefix = normalized switch
        {
            "icons" => "icon_",
            "sprites" => "spr_",
            "uiAssets" => "ui_",
            _ => string.Empty,
        };

        var naming = normalized == "sprites" ? "autoNumbered" : "cutoutNames";

        var previousSuspendState = _suspendDirtyTracking;
        _suspendDirtyTracking = true;

        _exportNamingMode = naming;
        RaisePropertyChanged(nameof(ExportNamingMode));
        _settings.LastExportNamingMode = naming;

        _exportPrefix = prefix;
        RaisePropertyChanged(nameof(ExportPrefix));
        _settings.LastExportPrefix = prefix;

        Project.Defaults.Scale = defaults.Scale;
        Project.Defaults.TargetPixelSize = defaults.TargetPixelSize;
        Project.Defaults.Padding = defaults.Padding;
        Project.Defaults.OutlineMode = defaults.OutlineMode;

        if (applyToExistingCutouts)
        {
            foreach (var cutout in Project.Cutouts)
            {
                cutout.Export.Scale = defaults.Scale;
                cutout.Export.TargetPixelSize = defaults.TargetPixelSize;
                cutout.Export.Padding = defaults.Padding;
                cutout.Export.OutlineMode = defaults.OutlineMode;
            }
        }

        _suspendDirtyTracking = previousSuspendState;

        PersistSettings();
        RaisePropertyChanged(nameof(ExportScaleChoice));
        RaisePropertyChanged(nameof(ExportOutlineMode));
        RefreshAllCutoutPreviews();
        RefreshSelectedCutoutInspectorPreview();

        if (markDirty)
        {
            MarkDirty($"Applied export preset: {normalized}.", touchProjectTimestamp: false);
        }
    }

    private string BuildFileName(CutoutModel cutout, int index)
    {
        var prefix = SanitizeForFileName(ExportPrefix);
        if (ExportNamingMode == "cutoutNames")
        {
            var name = SanitizeForFileName(cutout.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "cutout";
            }

            return prefix + name;
        }

        var digits = Math.Max(3, index.ToString().Length);
        return prefix + index.ToString().PadLeft(digits, '0');
    }

    private static string SanitizeForFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = value.Trim().Replace(' ', '_');
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalid.ToString(), string.Empty);
        }

        while (sanitized.Contains("__", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("__", "_", StringComparison.Ordinal);
        }

        return sanitized;
    }

    private void MoveUp()
    {
        var indexes = SelectedCutouts
            .Select(cutout => Project.Cutouts.IndexOf(cutout))
            .Where(index => index >= 0)
            .OrderBy(index => index)
            .ToList();

        if (indexes.Count == 0 || indexes[0] == 0)
        {
            return;
        }

        PushUndoSnapshot();
        foreach (var index in indexes)
        {
            Project.Cutouts.Move(index, index - 1);
        }

        HandleProjectOrderChanged();
    }

    private void MoveDown()
    {
        var indexes = SelectedCutouts
            .Select(cutout => Project.Cutouts.IndexOf(cutout))
            .Where(index => index >= 0)
            .OrderByDescending(index => index)
            .ToList();

        if (indexes.Count == 0 || indexes[0] == Project.Cutouts.Count - 1)
        {
            return;
        }

        PushUndoSnapshot();
        foreach (var index in indexes)
        {
            Project.Cutouts.Move(index, index + 1);
        }

        HandleProjectOrderChanged();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        _redoStack.Push(CaptureSnapshot());
        RestoreSnapshot(_undoStack.Pop());
        StatusText = "Undid the last change.";
    }

    private void Redo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }

        _undoStack.Push(CaptureSnapshot());
        RestoreSnapshot(_redoStack.Pop());
        StatusText = "Redid the last change.";
    }

    private void ApplyProject(ProjectModel project, SKBitmap bitmap, string imagePath, bool isDirty)
    {
        _suspendDirtyTracking = true;
        _sessionRevision++;

        _sourceBitmap?.Dispose();
        _sourceBitmap = bitmap;

        Project = project;
        Project.SourceImage.Path = imagePath;
        Project.SourceImage.PixelWidth = bitmap.Width;
        Project.SourceImage.PixelHeight = bitmap.Height;
        Project.ProjectName = string.IsNullOrWhiteSpace(Project.ProjectName)
            ? Path.GetFileNameWithoutExtension(imagePath)
            : Project.ProjectName;

        ClearActiveSelection();
        SetSelectedCutouts(Array.Empty<CutoutModel>(), requestSync: true);
        SyncSavedShapeSelectionWithProject();
        _undoStack.Clear();
        _redoStack.Clear();
        IsDirty = isDirty;
        ResetCanvasView();

        if (string.IsNullOrWhiteSpace(ExportFolder))
        {
            ExportFolder = Path.GetDirectoryName(imagePath) ?? string.Empty;
        }

        RefreshAllCutoutPreviews();
        RememberImageFolder(imagePath);
        RaisePropertyChanged(nameof(HasImage));
        RaisePropertyChanged(nameof(CanCommitOrFinalizeSelection));
        RaisePropertyChanged(nameof(HasBrushTarget));
        RaisePropertyChanged(nameof(BrushTargetLabel));
        RaisePropertyChanged(nameof(HasReusableShape));
        RaisePropertyChanged(nameof(ShapeReuseStatus));
        RaisePropertyChanged(nameof(ShapeReuseHint));
                RaisePropertyChanged(nameof(HasSelectedSavedShape));
        RaisePropertyChanged(nameof(ExportScaleChoice));
        RaisePropertyChanged(nameof(ExportOutlineMode));
        RaisePropertyChanged(nameof(SourceImageLabel));
        RaisePropertyChanged(nameof(SourceImagePathLabel));
        RaisePropertyChanged(nameof(WindowTitle));
        RefreshSelectedCutoutInspectorPreview();
        RefreshPreviewStats();

        _suspendDirtyTracking = false;
        RefreshCommands();
    }

    private void PersistSettings()
    {
        try
        {
            _settingsService.Save(_settings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Photo Cutter settings persist failed: {ex.Message}");
        }
    }

    private void RememberProjectFolder(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        _settings.LastOpenFolder = folder;
        PersistSettings();
    }

    private void RememberImageFolder(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        _settings.LastImageFolder = folder;
        PersistSettings();
    }

    private void AttachProject(ProjectModel project)
    {
        project.PropertyChanged += ProjectOnPropertyChanged;
        project.Cutouts.CollectionChanged += CutoutsOnCollectionChanged;
        foreach (var cutout in project.Cutouts)
        {
            cutout.PropertyChanged += CutoutOnPropertyChanged;
        }
    }

    private void DetachProject(ProjectModel project)
    {
        project.PropertyChanged -= ProjectOnPropertyChanged;
        project.Cutouts.CollectionChanged -= CutoutsOnCollectionChanged;
        foreach (var cutout in project.Cutouts)
        {
            cutout.PropertyChanged -= CutoutOnPropertyChanged;
        }
    }

    private void ProjectOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectModel.ProjectName))
        {
            RaisePropertyChanged(nameof(WindowTitle));
        }

        if (e.PropertyName is nameof(ProjectModel.ShowCutoutsOverlay))
        {
            RaisePropertyChanged(nameof(ShowCutoutsOverlay));
        }
    }

    private void CutoutsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (CutoutModel cutout in e.OldItems)
            {
                cutout.PropertyChanged -= CutoutOnPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (CutoutModel cutout in e.NewItems)
            {
                cutout.PropertyChanged += CutoutOnPropertyChanged;
                UpdateCutoutPreview(cutout, notifyGallery: false);
            }
        }

        UpdateComputedState();
        RequestGalleryRefresh();
    }

    private void CutoutOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suspendDirtyTracking)
        {
            return;
        }

        RaisePropertyChanged(nameof(SelectedCutoutBounds));
        RaisePropertyChanged(nameof(SelectedCutoutSize));
        RaisePropertyChanged(nameof(SelectedCutoutMode));
        RaisePropertyChanged(nameof(SelectedCutoutConfidence));
        RaisePropertyChanged(nameof(PreviewSecondaryStats));
        RefreshSelectedCutoutInspectorPreview();

        if (sender is CutoutModel cutout && e.PropertyName is nameof(CutoutModel.Geometry) or nameof(CutoutModel.BBox) or nameof(CutoutModel.Export))
        {
            UpdateCutoutPreview(cutout);
        }
        else if (e.PropertyName is nameof(CutoutModel.Name) or nameof(CutoutModel.IsVisible) or nameof(CutoutModel.AutoConfidence))
        {
            RequestGalleryRefresh();
        }

        if (e.PropertyName is nameof(CutoutModel.Name) or nameof(CutoutModel.IsVisible) or nameof(CutoutModel.BBox) or nameof(CutoutModel.Geometry) or nameof(CutoutModel.Notes))
        {
            MarkDirty(touchProjectTimestamp: false);
        }
    }

    private void RefreshAllCutoutPreviews()
    {
        if (SourceBitmap is null)
        {
            foreach (var cutout in Project.Cutouts)
            {
                cutout.PreviewImage = null;
            }

            RequestGalleryRefresh();
            return;
        }

        foreach (var cutout in Project.Cutouts)
        {
            UpdateCutoutPreview(cutout, notifyGallery: false);
        }

        RequestGalleryRefresh();
    }

    private void UpdateCutoutPreview(CutoutModel cutout, bool notifyGallery = true)
    {
        if (SourceBitmap is null)
        {
            cutout.PreviewImage = null;
            if (notifyGallery)
            {
                RequestGalleryRefresh();
            }

            return;
        }

        cutout.PreviewImage = _cutoutPreviewFactory.BuildPreviewImage(SourceBitmap, cutout);
        if (notifyGallery)
        {
            RequestGalleryRefresh();
        }
    }

    private void RequestGalleryRefresh()
    {
        GalleryRefreshRequested?.Invoke(this, EventArgs.Empty);
    }
    private void RefreshCommands()
    {
        ExportAllCommand.RaiseCanExecuteChanged();
        ExportSelectedCommand.RaiseCanExecuteChanged();
        AutoDetectCutoutsCommand.RaiseCanExecuteChanged();
        CommitSelectionCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
        DeleteSelectedCommand.RaiseCanExecuteChanged();
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
        OnRefreshCommands();
    }

    private void UpdateComputedState()
    {
        RaisePropertyChanged(nameof(HasSelectedCutout));
        RaisePropertyChanged(nameof(PrimarySelectedCutout));
        RaisePropertyChanged(nameof(SelectedCutoutBounds));
        RaisePropertyChanged(nameof(SelectedCutoutSize));
        RaisePropertyChanged(nameof(SelectedCutoutMode));
        RaisePropertyChanged(nameof(SelectedCutoutConfidence));
        RaisePropertyChanged(nameof(HasReusableShape));
        RaisePropertyChanged(nameof(ShapeReuseStatus));
        RaisePropertyChanged(nameof(ShapeReuseHint));
                RaisePropertyChanged(nameof(HasSelectedSavedShape));
        RefreshSelectedCutoutInspectorPreview();
        RefreshPreviewStats();
        RefreshCommands();
    }

    private void PushUndoSnapshot()
    {
        _undoStack.Push(CaptureSnapshot());
        _redoStack.Clear();
        RefreshCommands();
    }

    private EditorSnapshot CaptureSnapshot()
    {
        return new EditorSnapshot(
            Project.Cutouts.Select(cutout => cutout.DeepClone()).ToList(),
            SelectedCutouts.Select(cutout => cutout.Id).ToList(),
            HasActiveSelection ? ActiveSelection.Geometry.DeepClone() : null,
            ActiveSelection.BBox,
            HasActiveSelection);
    }

    private void RestoreSnapshot(EditorSnapshot snapshot)
    {
        _suspendDirtyTracking = true;

        Project.Cutouts.Clear();
        foreach (var cutout in snapshot.Cutouts.Select(item => item.DeepClone()))
        {
            Project.Cutouts.Add(cutout);
        }

        if (snapshot.HasActiveSelection && snapshot.ActiveSelectionGeometry is not null)
        {
            ActiveSelection = new SelectionModel
            {
                Geometry = snapshot.ActiveSelectionGeometry.DeepClone(),
                BBox = snapshot.ActiveSelectionBBox,
                HasSelection = true,
            };
        }
        else
        {
            ActiveSelection = new SelectionModel();
        }

        var restoredSelection = Project.Cutouts
            .Where(cutout => snapshot.SelectedCutoutIds.Contains(cutout.Id))
            .ToList();
        SetSelectedCutouts(restoredSelection, requestSync: true);

        RefreshAllCutoutPreviews();
        RefreshSelectedCutoutInspectorPreview();

        _suspendDirtyTracking = false;
        IsDirty = true;
        UpdateComputedState();
    }

    private static (int dx, int dy) ClampDelta(IEnumerable<CutoutModel> cutouts, int requestedDx, int requestedDy, int imageWidth, int imageHeight)
    {
        var allPoints = cutouts.SelectMany(cutout => cutout.Geometry.Points).ToList();
        if (allPoints.Count == 0)
        {
            return (0, 0);
        }

        var minX = allPoints.Min(point => point.X);
        var minY = allPoints.Min(point => point.Y);
        var maxX = allPoints.Max(point => point.X);
        var maxY = allPoints.Max(point => point.Y);

        var dx = requestedDx;
        var dy = requestedDy;

        if (minX + dx < 0)
        {
            dx = (int)Math.Ceiling(-minX);
        }
        else if (maxX + dx > imageWidth - 1)
        {
            dx = (int)Math.Floor((imageWidth - 1) - maxX);
        }

        if (minY + dy < 0)
        {
            dy = (int)Math.Ceiling(-minY);
        }
        else if (maxY + dy > imageHeight - 1)
        {
            dy = (int)Math.Floor((imageHeight - 1) - maxY);
        }

        return (dx, dy);
    }

    private sealed record EditorSnapshot(
        IReadOnlyList<CutoutModel> Cutouts,
        IReadOnlyList<string> SelectedCutoutIds,
        PathGeometryModel? ActiveSelectionGeometry,
        BBox ActiveSelectionBBox,
        bool HasActiveSelection);
}



























