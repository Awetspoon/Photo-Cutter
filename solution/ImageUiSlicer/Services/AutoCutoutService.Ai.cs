using ImageUiSlicer.CanvasEngine;
using ImageUiSlicer.Models;
using SkiaSharp;

namespace ImageUiSlicer.Services;

public sealed partial class AutoCutoutService
{
    public AutoCutoutSuggestion CreateSuggestionFromHint(
        SKBitmap bitmap,
        BBox hintedBounds,
        string kind,
        float confidence,
        string mode = "ai",
        string? label = null,
        PathGeometryModel? preferredGeometry = null,
        float strength = 0.5f)
    {
        var imageBounds = new BBox(0, 0, bitmap.Width, bitmap.Height);
        var clippedBounds = ClipBounds(hintedBounds, imageBounds);
        if (clippedBounds.W <= 1 || clippedBounds.H <= 1)
        {
            clippedBounds = hintedBounds;
        }

        var resolvedBounds = clippedBounds;
        var geometry = TryBuildGeometryFromHint(bitmap, clippedBounds, mode, strength, out resolvedBounds)
            ?? CloneGeometry(preferredGeometry, mode, out resolvedBounds)
            ?? BuildRectGeometry(clippedBounds, mode);

        return new AutoCutoutSuggestion
        {
            Bounds = resolvedBounds,
            Confidence = Math.Clamp(confidence, 0.05f, 0.99f),
            Geometry = geometry,
            Kind = kind,
            Label = label,
        };
    }

    private static PathGeometryModel? TryBuildGeometryFromHint(SKBitmap bitmap, BBox hintBounds, string mode, float strength, out BBox resolvedBounds)
    {
        resolvedBounds = hintBounds;
        if (hintBounds.W < 6 || hintBounds.H < 6)
        {
            return null;
        }

        var mask = SmoothMask(BuildForegroundMask(bitmap, Math.Clamp(strength, 0.2f, 0.9f)), bitmap.Width, bitmap.Height, iterations: 1);
        var searchBounds = ExpandBounds(
            hintBounds,
            padX: Math.Max(6, hintBounds.W / 9),
            padY: Math.Max(6, hintBounds.H / 7),
            clip: new BBox(0, 0, bitmap.Width, bitmap.Height));

        if (!TryExtractComponentForHint(mask, bitmap.Width, searchBounds, hintBounds, out resolvedBounds, out var boundaryPoints))
        {
            return null;
        }

        return BuildGeometryFromBoundary(boundaryPoints, resolvedBounds, mode, maxPoints: 56);
    }

    private static PathGeometryModel? CloneGeometry(PathGeometryModel? geometry, string mode, out BBox resolvedBounds)
    {
        resolvedBounds = default;
        if (geometry is null || !GeometryHelper.IsValidGeometry(geometry))
        {
            return null;
        }

        var clone = geometry.DeepClone();
        clone.Mode = mode;
        resolvedBounds = GeometryHelper.ComputeBBox(clone.Points);
        return clone;
    }

    private static BBox ClipBounds(BBox bounds, BBox clip)
    {
        var left = Math.Clamp(bounds.X, clip.X, Math.Max(clip.X, clip.Right - 1));
        var top = Math.Clamp(bounds.Y, clip.Y, Math.Max(clip.Y, clip.Bottom - 1));
        var right = Math.Clamp(bounds.Right, left + 1, clip.Right);
        var bottom = Math.Clamp(bounds.Bottom, top + 1, clip.Bottom);
        return new BBox(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static bool TryExtractComponentForHint(
        bool[] mask,
        int imageWidth,
        BBox searchBounds,
        BBox hintBounds,
        out BBox bestBounds,
        out List<IntPoint> bestBoundaryPoints)
    {
        bestBounds = default;
        bestBoundaryPoints = new List<IntPoint>();

        if (searchBounds.W <= 0 || searchBounds.H <= 0)
        {
            return false;
        }

        var visited = new bool[searchBounds.W * searchBounds.H];
        var queue = new Queue<(int x, int y)>();
        var bestScore = 0f;
        var hintArea = Math.Max(1, hintBounds.W * hintBounds.H);
        var hintCenterX = hintBounds.X + (hintBounds.W / 2f);
        var hintCenterY = hintBounds.Y + (hintBounds.H / 2f);

        for (var y = searchBounds.Y; y < searchBounds.Bottom; y++)
        {
            for (var x = searchBounds.X; x < searchBounds.Right; x++)
            {
                var localIndex = ((y - searchBounds.Y) * searchBounds.W) + (x - searchBounds.X);
                if (visited[localIndex])
                {
                    continue;
                }

                visited[localIndex] = true;
                var globalIndex = (y * imageWidth) + x;
                if (!mask[globalIndex])
                {
                    continue;
                }

                queue.Clear();
                queue.Enqueue((x, y));

                var area = 0;
                var overlapPixels = 0;
                var minX = int.MaxValue;
                var minY = int.MaxValue;
                var maxX = int.MinValue;
                var maxY = int.MinValue;
                var boundaryPoints = new List<IntPoint>();

                while (queue.Count > 0)
                {
                    var (currentX, currentY) = queue.Dequeue();
                    area++;

                    minX = Math.Min(minX, currentX);
                    minY = Math.Min(minY, currentY);
                    maxX = Math.Max(maxX, currentX);
                    maxY = Math.Max(maxY, currentY);

                    if (currentX >= hintBounds.X && currentX < hintBounds.Right && currentY >= hintBounds.Y && currentY < hintBounds.Bottom)
                    {
                        overlapPixels++;
                    }

                    var isBoundary = false;
                    foreach (var (nextX, nextY) in EnumerateNeighbors(currentX, currentY))
                    {
                        if (nextX < searchBounds.X || nextX >= searchBounds.Right || nextY < searchBounds.Y || nextY >= searchBounds.Bottom)
                        {
                            isBoundary = true;
                            continue;
                        }

                        var nextGlobalIndex = (nextY * imageWidth) + nextX;
                        if (!mask[nextGlobalIndex])
                        {
                            isBoundary = true;
                            continue;
                        }

                        var nextLocalIndex = ((nextY - searchBounds.Y) * searchBounds.W) + (nextX - searchBounds.X);
                        if (visited[nextLocalIndex])
                        {
                            continue;
                        }

                        visited[nextLocalIndex] = true;
                        queue.Enqueue((nextX, nextY));
                    }

                    if (isBoundary)
                    {
                        boundaryPoints.Add(new IntPoint(currentX, currentY));
                    }
                }

                if (area < 24 || boundaryPoints.Count < 3)
                {
                    continue;
                }

                var componentBounds = new BBox(minX, minY, Math.Max(1, (maxX - minX) + 1), Math.Max(1, (maxY - minY) + 1));
                var coverage = overlapPixels / (float)hintArea;
                var purity = overlapPixels / (float)Math.Max(1, area);
                var iou = IoU(componentBounds, hintBounds);
                var centerInside = hintCenterX >= componentBounds.X && hintCenterX <= componentBounds.Right &&
                                   hintCenterY >= componentBounds.Y && hintCenterY <= componentBounds.Bottom;
                var score = (coverage * 0.46f) + (purity * 0.28f) + (iou * 0.20f) + (centerInside ? 0.10f : 0f);

                if (score <= bestScore || (overlapPixels == 0 && !centerInside))
                {
                    continue;
                }

                bestScore = score;
                bestBounds = componentBounds;
                bestBoundaryPoints = boundaryPoints;
            }
        }

        return bestScore >= 0.14f && bestBoundaryPoints.Count >= 3;
    }

    private static IEnumerable<(int x, int y)> EnumerateNeighbors(int x, int y)
    {
        yield return (x - 1, y);
        yield return (x + 1, y);
        yield return (x, y - 1);
        yield return (x, y + 1);
    }
}

