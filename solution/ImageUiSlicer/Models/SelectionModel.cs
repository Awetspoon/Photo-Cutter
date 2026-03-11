using ImageUiSlicer.Infrastructure;

namespace ImageUiSlicer.Models;

public sealed class SelectionModel : ObservableObject
{
    private PathGeometryModel _geometry = new();
    private BBox _bbox = new(0, 0, 0, 0);
    private bool _hasSelection;

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
                RaisePropertyChanged(nameof(BoundsLabel));
                RaisePropertyChanged(nameof(SizeLabel));
            }
        }
    }

    public bool HasSelection
    {
        get => _hasSelection;
        set => SetProperty(ref _hasSelection, value);
    }

    public string BoundsLabel => $"X {BBox.X}, Y {BBox.Y}, W {BBox.W}, H {BBox.H}";

    public string SizeLabel => $"{BBox.W} x {BBox.H}";
}
