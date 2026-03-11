using ImageUiSlicer.Models;
using SkiaSharp;

namespace ImageUiSlicer.Services;

public sealed class ExportService
{
    private readonly CutoutRenderService _cutoutRenderService = new();

    public static string CutOutsFolder(string baseFolder) => Path.Combine(baseFolder, "cut outs");

    public static void Ensure(string baseFolder) => Directory.CreateDirectory(CutOutsFolder(baseFolder));

    public string ExportCutout(SKBitmap sourceBitmap, CutoutModel cutout, string baseFolder, string fileNoExt)
    {
        Ensure(baseFolder);

        var outputPath = GetUniqueOutputPath(baseFolder, fileNoExt);
        using var rendered = _cutoutRenderService.RenderCutoutBitmap(sourceBitmap, cutout);
        using var image = SKImage.FromBitmap(rendered);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        data.SaveTo(stream);
        return outputPath;
    }

    public SKBitmap RenderCutoutBitmap(SKBitmap sourceBitmap, CutoutModel cutout)
    {
        return _cutoutRenderService.RenderCutoutBitmap(sourceBitmap, cutout);
    }

    private static string GetUniqueOutputPath(string baseFolder, string fileNoExt)
    {
        var folder = CutOutsFolder(baseFolder);
        var sanitized = string.IsNullOrWhiteSpace(fileNoExt) ? "cutout" : fileNoExt;
        var candidate = Path.Combine(folder, sanitized + ".png");
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        for (var index = 1; index < 10000; index++)
        {
            candidate = Path.Combine(folder, $"{sanitized}_{index:00}.png");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Unable to find a unique export filename.");
    }
}
