using System.IO;
using System.Windows.Media.Imaging;
using ImageUiSlicer.CanvasEngine;
using ImageUiSlicer.Models;
using SkiaSharp;

namespace ImageUiSlicer.Services;

public sealed class CutoutPreviewFactory
{
    private readonly CutoutRenderService _cutoutRenderService = new();

    public BitmapSource? BuildPreviewImage(SKBitmap sourceBitmap, CutoutModel cutout)
    {
        if (!GeometryHelper.IsValidGeometry(cutout.Geometry))
        {
            return null;
        }

        using var rendered = _cutoutRenderService.RenderCutoutBitmap(sourceBitmap, cutout);
        return ToBitmapSource(rendered);
    }

    public BitmapSource? BuildInspectorPreview(SKBitmap sourceBitmap, CutoutModel cutout, bool splitPreview, double splitRatio)
    {
        using var preview = _cutoutRenderService.RenderInspectorPreviewBitmap(sourceBitmap, cutout, splitPreview, splitRatio);
        if (preview is null)
        {
            return null;
        }

        return ToBitmapSource(preview);
    }

    public IReadOnlyList<CutoutPreviewItem> BuildItems(SKBitmap sourceBitmap, IEnumerable<CutoutModel> cutouts)
    {
        var items = new List<CutoutPreviewItem>();
        foreach (var cutout in cutouts)
        {
            var preview = BuildPreviewImage(sourceBitmap, cutout);
            if (preview is null)
            {
                continue;
            }

            items.Add(new CutoutPreviewItem
            {
                Name = string.IsNullOrWhiteSpace(cutout.Name) ? "Cutout" : cutout.Name,
                SizeLabel = cutout.SizeLabel,
                Mode = cutout.Geometry.Mode,
                ConfidenceLabel = cutout.ConfidenceLabel,
                PreviewImage = preview,
            });
        }

        return items;
    }

    private static BitmapSource ToBitmapSource(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());

        var preview = new BitmapImage();
        preview.BeginInit();
        preview.CacheOption = BitmapCacheOption.OnLoad;
        preview.StreamSource = stream;
        preview.EndInit();
        preview.Freeze();
        return preview;
    }
}
