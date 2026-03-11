namespace ImageUiSlicer.Models;

public sealed class PathGeometryModel
{
    public string Type { get; set; } = "path";

    public string Mode { get; set; } = "freehand";

    public bool Closed { get; set; } = true;

    public List<PointF> Points { get; set; } = new();

    public PathGeometryModel DeepClone()
    {
        return new PathGeometryModel
        {
            Type = Type,
            Mode = Mode,
            Closed = Closed,
            Points = Points.Select(point => new PointF(point.X, point.Y)).ToList(),
        };
    }
}
