using ImageUiSlicer.CanvasEngine;
using ImageUiSlicer.Models;
using SkiaSharp;

namespace ImageUiSlicer.Services;

public sealed class CutoutRenderService
{
    public SKBitmap RenderCutoutBitmap(SKBitmap sourceBitmap, CutoutModel cutout)
    {
        var geometry = cutout.Geometry;
        if (!GeometryHelper.IsValidGeometry(geometry))
        {
            throw new InvalidOperationException("Cannot render a cutout without at least three points.");
        }

        var padding = Math.Max(0, cutout.Export.Padding);
        var scale = Math.Max(1, cutout.Export.Scale);
        var targetPixelSize = Math.Max(0, cutout.Export.TargetPixelSize);
        var points = geometry.Points;

        var minX = points.Min(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxX = points.Max(point => point.X);
        var maxY = points.Max(point => point.Y);

        var left = Math.Max(0, (int)Math.Floor(minX) - padding);
        var top = Math.Max(0, (int)Math.Floor(minY) - padding);
        var right = Math.Min(sourceBitmap.Width, (int)Math.Ceiling(maxX) + padding);
        var bottom = Math.Min(sourceBitmap.Height, (int)Math.Ceiling(maxY) + padding);

        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);
        var renderScale = ResolveRenderScale(width, height, scale, targetPixelSize, out var targetWidth, out var targetHeight);
        var targetInfo = new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        var target = new SKBitmap(targetInfo);

        using var canvas = new SKCanvas(target);
        using var path = GeometryHelper.BuildPath(geometry);
        canvas.Clear(SKColors.Transparent);

        canvas.Save();
        canvas.Scale(renderScale);
        canvas.Translate(-left, -top);
        canvas.ClipPath(path, SKClipOperation.Intersect, antialias: true);
        canvas.DrawBitmap(sourceBitmap, 0, 0);
        canvas.Restore();

        DrawOutlineIfNeeded(
            canvas,
            path,
            cutout.Export.OutlineMode,
            renderScale,
            left,
            top,
            Math.Max(1.35f / Math.Max(renderScale, 0.18f), 0.45f),
            translateForCrop: true);

        canvas.Flush();
        return target;
    }

    public SKBitmap? RenderInspectorPreviewBitmap(SKBitmap sourceBitmap, CutoutModel cutout, bool splitPreview, double splitRatio)
    {
        if (!GeometryHelper.IsValidGeometry(cutout.Geometry) || !TryGetCropRect(cutout.Geometry, sourceBitmap, out var cropRect))
        {
            return null;
        }

        splitRatio = Math.Clamp(splitRatio, 0.05, 0.95);

        using var before = RenderSourceCrop(sourceBitmap, cropRect);
        using var after = RenderCutoutCrop(sourceBitmap, cutout, cropRect);

        if (!splitPreview)
        {
            var preview = new SKBitmap(after.Info);
            after.CopyTo(preview);
            return preview;
        }

        var merged = new SKBitmap(before.Width, before.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(merged);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(before, 0, 0);

        var splitX = (float)(before.Width * splitRatio);
        using (var clip = new SKPath())
        {
            clip.AddRect(SKRect.Create(0, 0, splitX, before.Height));
            canvas.Save();
            canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
            canvas.DrawBitmap(after, 0, 0);
            canvas.Restore();
        }

        using var divider = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(255, 151, 90, 230),
            StrokeWidth = Math.Max(1f, before.Width / 240f),
        };
        canvas.DrawLine(splitX, 0, splitX, before.Height, divider);
        canvas.Flush();

        return merged;
    }

    private static SKBitmap RenderSourceCrop(SKBitmap sourceBitmap, SKRectI cropRect)
    {
        var before = new SKBitmap(cropRect.Width, cropRect.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(before);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(sourceBitmap, cropRect, new SKRectI(0, 0, cropRect.Width, cropRect.Height));
        canvas.Flush();
        return before;
    }

    private static SKBitmap RenderCutoutCrop(SKBitmap sourceBitmap, CutoutModel cutout, SKRectI cropRect)
    {
        var after = new SKBitmap(cropRect.Width, cropRect.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(after);
        using var clipPath = GeometryHelper.BuildPath(cutout.Geometry);
        canvas.Clear(SKColors.Transparent);

        canvas.Save();
        canvas.Translate(-cropRect.Left, -cropRect.Top);
        canvas.ClipPath(clipPath, SKClipOperation.Intersect, antialias: true);
        canvas.DrawBitmap(sourceBitmap, 0, 0);
        canvas.Restore();

        DrawOutlineIfNeeded(
            canvas,
            clipPath,
            cutout.Export.OutlineMode,
            scale: 1f,
            offsetX: cropRect.Left,
            offsetY: cropRect.Top,
            outlineStrokeWidth: Math.Max(1.15f, cropRect.Width / 220f),
            translateForCrop: true);

        canvas.Flush();
        return after;
    }

    private static void DrawOutlineIfNeeded(
        SKCanvas canvas,
        SKPath path,
        string? outlineMode,
        float scale,
        int offsetX,
        int offsetY,
        float outlineStrokeWidth,
        bool translateForCrop)
    {
        var outlineColor = ResolveOutlineColor(outlineMode);
        if (!outlineColor.HasValue)
        {
            return;
        }

        canvas.Save();
        if (Math.Abs(scale - 1f) > 0.0001f)
        {
            canvas.Scale(scale);
        }

        if (translateForCrop)
        {
            canvas.Translate(-offsetX, -offsetY);
        }

        using var outline = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = outlineColor.Value,
            StrokeWidth = outlineStrokeWidth,
            IsAntialias = true,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeCap = SKStrokeCap.Round,
        };
        canvas.DrawPath(path, outline);
        canvas.Restore();
    }

    private static float ResolveRenderScale(int width, int height, int scale, int targetPixelSize, out int targetWidth, out int targetHeight)
    {
        targetPixelSize = Math.Max(0, targetPixelSize);
        scale = Math.Max(1, scale);
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (targetPixelSize > 0)
        {
            var longestSide = Math.Max(width, height);
            var renderScale = targetPixelSize / (float)longestSide;
            targetWidth = Math.Max(1, (int)Math.Round(width * renderScale));
            targetHeight = Math.Max(1, (int)Math.Round(height * renderScale));
            return renderScale;
        }

        targetWidth = width * scale;
        targetHeight = height * scale;
        return scale;
    }

    private static bool TryGetCropRect(PathGeometryModel geometry, SKBitmap sourceBitmap, out SKRectI cropRect)
    {
        cropRect = default;
        if (geometry.Points.Count < 3)
        {
            return false;
        }

        var minX = (int)Math.Floor(geometry.Points.Min(point => point.X));
        var minY = (int)Math.Floor(geometry.Points.Min(point => point.Y));
        var maxX = (int)Math.Ceiling(geometry.Points.Max(point => point.X));
        var maxY = (int)Math.Ceiling(geometry.Points.Max(point => point.Y));

        minX = Math.Clamp(minX, 0, sourceBitmap.Width - 1);
        minY = Math.Clamp(minY, 0, sourceBitmap.Height - 1);
        maxX = Math.Clamp(maxX, minX + 1, sourceBitmap.Width);
        maxY = Math.Clamp(maxY, minY + 1, sourceBitmap.Height);

        cropRect = new SKRectI(minX, minY, maxX, maxY);
        return cropRect.Width > 0 && cropRect.Height > 0;
    }

    private static SKColor? ResolveOutlineColor(string? outlineMode)
    {
        return outlineMode?.Trim().ToLowerInvariant() switch
        {
            "white" => SKColors.White,
            "black" => SKColors.Black,
            _ => null,
        };
    }
}
