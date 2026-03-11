using ImageUiSlicer.CanvasEngine;

namespace ImageUiSlicer.Models;

public sealed class ShapePresetOption
{
    public ShapePresetOption(ShapeCutoutPreset value, string label)
    {
        Value = value;
        Label = label;
    }

    public ShapeCutoutPreset Value { get; }

    public string Label { get; }

    public override string ToString() => Label;
}
