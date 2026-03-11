using SkiaSharp;

namespace ImageUiSlicer.Services;

public sealed class ImageService
{
    private static readonly HashSet<string> SupportedExtensions =
    [
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".gif",
        ".webp",
    ];

    public bool IsSupportedImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    }

    public SKBitmap LoadBitmap(string path)
    {
        var bitmap = SKBitmap.Decode(path);
        if (bitmap is null)
        {
            throw new InvalidOperationException($"Unable to decode image '{path}'.");
        }

        return bitmap;
    }
}
