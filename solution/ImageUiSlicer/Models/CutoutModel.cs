using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using ImageUiSlicer.Infrastructure;

namespace ImageUiSlicer.Models;

public sealed class CutoutModel : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "Cutout";
    private bool _isVisible = true;
    private PathGeometryModel _geometry = new();
    private BBox _bbox = new(0, 0, 0, 0);
    private ExportOptionsModel _export = new();
    private string _notes = string.Empty;
    private BitmapSource? _previewImage;
    private double? _autoConfidence;

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public PathGeometryModel Geometry
    {
        get => _geometry;
        set => SetProperty(ref _geometry, value);
    }

    public BBox BBox
    {
        get => _bbox;
        set
        {
            if (SetProperty(ref _bbox, value))
            {
                RaisePropertyChanged(nameof(SizeLabel));
                RaisePropertyChanged(nameof(BoundsLabel));
            }
        }
    }

    public ExportOptionsModel Export
    {
        get => _export;
        set => SetProperty(ref _export, value);
    }

    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }

    public double? AutoConfidence
    {
        get => _autoConfidence;
        set
        {
            if (SetProperty(ref _autoConfidence, value))
            {
                RaisePropertyChanged(nameof(ConfidenceLabel));
                RaisePropertyChanged(nameof(HasAutoConfidence));
            }
        }
    }

    [JsonIgnore]
    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        set => SetProperty(ref _previewImage, value);
    }

    [JsonIgnore]
    public bool HasAutoConfidence => AutoConfidence.HasValue;

    [JsonIgnore]
    public string ConfidenceLabel => AutoConfidence.HasValue ? $"{AutoConfidence.Value * 100:0}% confidence" : "manual";

    public string SizeLabel => $"{BBox.W} x {BBox.H}";

    public string BoundsLabel => $"X {BBox.X}, Y {BBox.Y}, W {BBox.W}, H {BBox.H}";

    public CutoutModel DeepClone()
    {
        return new CutoutModel
        {
            Id = Id,
            Name = Name,
            IsVisible = IsVisible,
            Geometry = Geometry.DeepClone(),
            BBox = BBox,
            Export = Export.DeepClone(),
            Notes = Notes,
            AutoConfidence = AutoConfidence,
        };
    }
}