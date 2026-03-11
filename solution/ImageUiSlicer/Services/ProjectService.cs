using System.Text.Json;
using ImageUiSlicer.CanvasEngine;
using ImageUiSlicer.Models;

namespace ImageUiSlicer.Services;

public sealed class ProjectService
{
    private const int DefaultExportPixelSize = 256;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public ProjectModel Load(string path)
    {
        var json = File.ReadAllText(path);
        var project = JsonSerializer.Deserialize<ProjectModel>(json, SerializerOptions)
            ?? throw new InvalidOperationException("Project file was empty.");

        project.ProjectFilePath = path;
        project.Cutouts ??= new();
        project.SavedShapes ??= new();
        project.SourceImage ??= new();
        project.Defaults ??= new();

        project.Defaults.Padding = Math.Max(0, project.Defaults.Padding);
        project.Defaults.OutlineMode = NormalizeOutlineMode(project.Defaults.OutlineMode);
        project.Defaults.TargetPixelSize = project.Defaults.TargetPixelSize > 0 ? project.Defaults.TargetPixelSize : DefaultExportPixelSize;
        project.Defaults.Scale = 1;

        foreach (var cutout in project.Cutouts)
        {
            cutout.Id = string.IsNullOrWhiteSpace(cutout.Id) ? Guid.NewGuid().ToString("N") : cutout.Id;
            cutout.Geometry ??= new PathGeometryModel();
            cutout.Export ??= new ExportOptionsModel();
            cutout.Export.Padding = Math.Max(0, cutout.Export.Padding);
            cutout.Export.OutlineMode = NormalizeOutlineMode(cutout.Export.OutlineMode);
            cutout.BBox = GeometryHelper.IsValidGeometry(cutout.Geometry)
                ? GeometryHelper.ComputeBBox(cutout.Geometry.Points)
                : new BBox(0, 0, 0, 0);

            cutout.Export.TargetPixelSize = cutout.Export.TargetPixelSize > 0
                ? cutout.Export.TargetPixelSize
                : ResolveLegacyTargetPixelSize(cutout);
            cutout.Export.Scale = 1;
        }

        foreach (var shape in project.SavedShapes)
        {
            shape.Id = string.IsNullOrWhiteSpace(shape.Id) ? Guid.NewGuid().ToString("N") : shape.Id;
            shape.Geometry ??= new PathGeometryModel();
            shape.Name = string.IsNullOrWhiteSpace(shape.Name) ? "Custom Shape" : shape.Name.Trim();

            if (!GeometryHelper.IsValidGeometry(shape.Geometry))
            {
                shape.Geometry = new PathGeometryModel();
                shape.BBox = new BBox(0, 0, 0, 0);
                continue;
            }

            var bounds = GeometryHelper.ComputeBBox(shape.Geometry.Points);
            // Store saved shapes in normalized local space so reusing is predictable.
            shape.Geometry = GeometryHelper.Translate(shape.Geometry, -bounds.X, -bounds.Y);
            shape.BBox = GeometryHelper.ComputeBBox(shape.Geometry.Points);
        }

        if (string.IsNullOrWhiteSpace(project.ProjectName))
        {
            project.ProjectName = Path.GetFileNameWithoutExtension(path);
        }

        return project;
    }

    public void Save(ProjectModel project, string path)
    {
        project.ProjectFilePath = path;
        project.ModifiedUtc = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(project, SerializerOptions);
        File.WriteAllText(path, json);
    }

    private static int ResolveLegacyTargetPixelSize(CutoutModel cutout)
    {
        var padding = Math.Max(0, cutout.Export.Padding);
        var bbox = cutout.BBox;
        var width = Math.Max(1, bbox.W + (padding * 2));
        var height = Math.Max(1, bbox.H + (padding * 2));
        var longestSide = Math.Max(width, height);
        var scale = Math.Max(1, cutout.Export.Scale);
        return Math.Max(1, longestSide * scale);
    }

    private static string NormalizeOutlineMode(string? outlineMode)
    {
        return outlineMode?.Trim().ToLowerInvariant() switch
        {
            "white" => "white",
            "black" => "black",
            _ => "none",
        };
    }
}
