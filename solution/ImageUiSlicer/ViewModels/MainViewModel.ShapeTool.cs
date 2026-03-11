using ImageUiSlicer.CanvasEngine;
using ImageUiSlicer.Models;

namespace ImageUiSlicer.ViewModels;

public sealed partial class MainViewModel
{
    private ShapeCutoutPreset _selectedShapePreset = ShapeCutoutPreset.RoundedRectangle;

    public IReadOnlyList<ShapePresetOption> ShapePresetOptions { get; } =
    [
        new(ShapeCutoutPreset.Rectangle, "Rectangle"),
        new(ShapeCutoutPreset.RoundedRectangle, "Rounded Rect"),
        new(ShapeCutoutPreset.Circle, "Circle"),
        new(ShapeCutoutPreset.Ellipse, "Ellipse"),
        new(ShapeCutoutPreset.Diamond, "Diamond"),
        new(ShapeCutoutPreset.Triangle, "Triangle"),
        new(ShapeCutoutPreset.Hexagon, "Hexagon"),
        new(ShapeCutoutPreset.Octagon, "Octagon"),
        new(ShapeCutoutPreset.Capsule, "Capsule"),
        new(ShapeCutoutPreset.Star, "Star"),
    ];

    public ShapeCutoutPreset SelectedShapePreset
    {
        get => _selectedShapePreset;
        set
        {
            if (!SetProperty(ref _selectedShapePreset, value))
            {
                return;
            }

            RaisePropertyChanged(nameof(SelectedShapePresetLabel));
            if (ActiveTool == CanvasTool.Shape)
            {
                StatusText = $"Shape tool ready ({SelectedShapePresetLabel}). Click a center point and drag outward.";
            }
        }
    }

    public string SelectedShapePresetLabel => ShapePresetOptions.FirstOrDefault(option => option.Value == SelectedShapePreset)?.Label ?? "Rounded Rect";

    public bool IsShapeTool
    {
        get => ActiveTool == CanvasTool.Shape;
        set
        {
            if (value)
            {
                SetActiveTool(CanvasTool.Shape);
            }
        }
    }
}
