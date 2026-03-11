using ImageUiSlicer.Infrastructure;

namespace ImageUiSlicer.Models;

public sealed class SourceImageModel : ObservableObject
{
    private string _path = string.Empty;
    private int _pixelWidth;
    private int _pixelHeight;

    public string Path
    {
        get => _path;
        set => SetProperty(ref _path, value);
    }

    public int PixelWidth
    {
        get => _pixelWidth;
        set => SetProperty(ref _pixelWidth, value);
    }

    public int PixelHeight
    {
        get => _pixelHeight;
        set => SetProperty(ref _pixelHeight, value);
    }
}
