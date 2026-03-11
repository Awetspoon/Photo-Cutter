using ImageUiSlicer.Infrastructure;

namespace ImageUiSlicer.Models;

public sealed class SavedShapeModel : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "Custom Shape";
    private PathGeometryModel _geometry = new();
    private BBox _bbox = new(0, 0, 0, 0);
    private bool _allowGrow;

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

    public PathGeometryModel Geometry
    {
        get => _geometry;
        set => SetProperty(ref _geometry, value);
    }

    public BBox BBox
    {
        get => _bbox;
        set => SetProperty(ref _bbox, value);
    }

    public bool AllowGrow
    {
        get => _allowGrow;
        set => SetProperty(ref _allowGrow, value);
    }

    public override string ToString() => Name;

    public SavedShapeModel DeepClone()
    {
        return new SavedShapeModel
        {
            Id = Id,
            Name = Name,
            Geometry = Geometry.DeepClone(),
            BBox = BBox,
            AllowGrow = AllowGrow,
        };
    }
}


