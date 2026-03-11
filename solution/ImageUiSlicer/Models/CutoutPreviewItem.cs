using System.Windows.Media.Imaging;

namespace ImageUiSlicer.Models;

public sealed class CutoutPreviewItem
{
    public string Name { get; init; } = string.Empty;

    public string SizeLabel { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string ConfidenceLabel { get; init; } = string.Empty;

    public BitmapSource? PreviewImage { get; init; }
}