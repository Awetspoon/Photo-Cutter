using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using ImageUiSlicer.Infrastructure;

namespace ImageUiSlicer.Models;

public sealed class ProjectModel : ObservableObject
{
    private string _version = "0.2";
    private string _projectName = "Untitled";
    private DateTime _createdUtc = DateTime.UtcNow;
    private DateTime _modifiedUtc = DateTime.UtcNow;
    private SourceImageModel _sourceImage = new();
    private ExportOptionsModel _defaults = new();
    private bool _showCutoutsOverlay = true;
    private ObservableCollection<CutoutModel> _cutouts = new();
    private ObservableCollection<SavedShapeModel> _savedShapes = new();
    private string? _projectFilePath;

    public string Version
    {
        get => _version;
        set => SetProperty(ref _version, value);
    }

    public string ProjectName
    {
        get => _projectName;
        set => SetProperty(ref _projectName, value);
    }

    public DateTime CreatedUtc
    {
        get => _createdUtc;
        set => SetProperty(ref _createdUtc, value);
    }

    public DateTime ModifiedUtc
    {
        get => _modifiedUtc;
        set => SetProperty(ref _modifiedUtc, value);
    }

    public SourceImageModel SourceImage
    {
        get => _sourceImage;
        set => SetProperty(ref _sourceImage, value);
    }

    public ExportOptionsModel Defaults
    {
        get => _defaults;
        set => SetProperty(ref _defaults, value);
    }

    public bool ShowCutoutsOverlay
    {
        get => _showCutoutsOverlay;
        set => SetProperty(ref _showCutoutsOverlay, value);
    }

    public ObservableCollection<CutoutModel> Cutouts
    {
        get => _cutouts;
        set => SetProperty(ref _cutouts, value);
    }

    public ObservableCollection<SavedShapeModel> SavedShapes
    {
        get => _savedShapes;
        set => SetProperty(ref _savedShapes, value);
    }

    [JsonIgnore]
    public string? ProjectFilePath
    {
        get => _projectFilePath;
        set => SetProperty(ref _projectFilePath, value);
    }
}

