using ImageUiSlicer.Models;
using SkiaSharp;

namespace ImageUiSlicer.CanvasEngine;

public static class GeometryHelper
{
    public static bool IsValidGeometry(PathGeometryModel geometry)
    {
        return geometry.Points.Count >= 3;
    }

    public static BBox ComputeBBox(IReadOnlyList<PointF> points)
    {
        if (points.Count == 0)
        {
            return new BBox(0, 0, 0, 0);
        }

        var minX = points.Min(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxX = points.Max(point => point.X);
        var maxY = points.Max(point => point.Y);

        return new BBox(
            (int)Math.Floor(minX),
            (int)Math.Floor(minY),
            Math.Max(1, (int)Math.Ceiling(maxX - minX)),
            Math.Max(1, (int)Math.Ceiling(maxY - minY)));
    }

    public static SKPath BuildPath(PathGeometryModel geometry)
    {
        var path = new SKPath();
        if (geometry.Points.Count == 0)
        {
            return path;
        }

        path.MoveTo(geometry.Points[0].X, geometry.Points[0].Y);
        for (var index = 1; index < geometry.Points.Count; index++)
        {
            path.LineTo(geometry.Points[index].X, geometry.Points[index].Y);
        }

        if (geometry.Closed)
        {
            path.Close();
        }

        return path;
    }

    public static PathGeometryModel Translate(PathGeometryModel geometry, float dx, float dy)
    {
        return new PathGeometryModel
        {
            Type = geometry.Type,
            Mode = geometry.Mode,
            Closed = geometry.Closed,
            Points = geometry.Points.Select(point => new PointF(point.X + dx, point.Y + dy)).ToList(),
        };
    }
}
