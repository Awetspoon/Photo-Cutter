namespace ImageUiSlicer.Models;

public sealed class AppSettings
{
    public string Version { get; set; } = "0.2";

    public string DefaultExportFolder { get; set; } = string.Empty;

    public string LastExportNamingMode { get; set; } = "autoNumbered";

    public string LastExportPrefix { get; set; } = string.Empty;

    public string LastExportPreset { get; set; } = "custom";

    public double LastAutoDetectStrength { get; set; } = 65;

    public string LastOpenFolder { get; set; } = string.Empty;

    public string LastImageFolder { get; set; } = string.Empty;

    public double LastLeftPaneWidth { get; set; } = 228;

    public double LastRightPaneWidth { get; set; } = 258;

    public bool LastPreviewFocusMode { get; set; }

    public bool LastShowCanvasHud { get; set; } = true;
}
