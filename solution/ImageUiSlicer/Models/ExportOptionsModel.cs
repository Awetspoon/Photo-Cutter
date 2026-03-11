using ImageUiSlicer.Infrastructure;

namespace ImageUiSlicer.Models;

public sealed class ExportOptionsModel : ObservableObject
{
    private string _format = "png";
    private int _scale = 1;
    private int _targetPixelSize;
    private int _padding;
    private string _outlineMode = "none";

    public string Format
    {
        get => _format;
        set => SetProperty(ref _format, value);
    }

    public int Scale
    {
        get => _scale;
        set => SetProperty(ref _scale, value);
    }

    public int TargetPixelSize
    {
        get => _targetPixelSize;
        set => SetProperty(ref _targetPixelSize, value);
    }

    public int Padding
    {
        get => _padding;
        set => SetProperty(ref _padding, value);
    }

    public string OutlineMode
    {
        get => _outlineMode;
        set => SetProperty(ref _outlineMode, value);
    }

    public ExportOptionsModel DeepClone()
    {
        return new ExportOptionsModel
        {
            Format = Format,
            Scale = Scale,
            TargetPixelSize = TargetPixelSize,
            Padding = Padding,
            OutlineMode = OutlineMode,
        };
    }
}
