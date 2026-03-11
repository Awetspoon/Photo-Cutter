using ImageUiSlicer.CanvasEngine;
using ImageUiSlicer.Infrastructure;
using ImageUiSlicer.Models;
using ImageUiSlicer.Services;

namespace ImageUiSlicer.ViewModels;

public sealed partial class MainViewModel
{
    private readonly AiCutoutDetectionService _aiCutoutDetectionService = new();
    private bool _isAiDetectBusy;
    private bool _hasAiApiKey;

    public RelayCommand AiDetectCutoutsCommand { get; private set; } = null!;

    public bool IsAiDetectBusy
    {
        get => _isAiDetectBusy;
        private set
        {
            if (SetProperty(ref _isAiDetectBusy, value))
            {
                RaisePropertyChanged(nameof(AiDetectButtonLabel));
                RaisePropertyChanged(nameof(AiDetectionBadge));
            }
        }
    }

    public bool HasAiApiKey
    {
        get => _hasAiApiKey;
        private set
        {
            if (SetProperty(ref _hasAiApiKey, value))
            {
                RaisePropertyChanged(nameof(AiDetectionBadge));
                RaisePropertyChanged(nameof(AiDetectionStatus));
                RaisePropertyChanged(nameof(AiDetectionHint));
            }
        }
    }

    public string AiDetectButtonLabel => IsAiDetectBusy ? "AI Working..." : "AI Detect";

    public string AiDetectionBadge => IsAiDetectBusy
        ? "Scanning"
        : HasAiApiKey ? "OpenAI Ready" : "Key Needed";

    public string AiDetectionStatus => HasAiApiKey
        ? "AI Detect can semantically split buttons, tabs, icons, and reusable UI chunks."
        : "AI Detect needs OPENAI_API_KEY before semantic slicing can run.";

    public string AiDetectionHint => HasAiApiKey
        ? "Quick Detect stays local. AI Detect uses vision for smarter proposals, then refines the shape inside Photo Cutter."
        : "Set OPENAI_API_KEY in Windows, restart Photo Cutter, and AI Detect will be ready here.";

    private void InitializeAiDetection()
    {
        RefreshAiAvailability();
        AiDetectCutoutsCommand = new RelayCommand(AiDetectCutouts, () => HasImage && !IsDetectionBusy);
    }

    private void RefreshAiAvailability()
    {
        HasAiApiKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
    }

    private void OnRefreshCommands()
    {
        AiDetectCutoutsCommand?.RaiseCanExecuteChanged();
        MatchSelectedCutoutCommand?.RaiseCanExecuteChanged();
        PasteSelectedShapeCommand?.RaiseCanExecuteChanged();
        SaveSelectedShapeCommand?.RaiseCanExecuteChanged();
        ApplySavedShapeCommand?.RaiseCanExecuteChanged();
        DeleteSavedShapeCommand?.RaiseCanExecuteChanged();
        DuplicateSelectedCutoutCommand?.RaiseCanExecuteChanged();
    }

    private async void AiDetectCutouts()
    {
        var sourceBitmapAtStart = SourceBitmap;
        if (sourceBitmapAtStart is null || IsDetectionBusy)
        {
            return;
        }

        RefreshAiAvailability();
        if (!HasAiApiKey)
        {
            StatusText = AiDetectionHint;
            return;
        }

        var sessionRevision = _sessionRevision;
        var projectAtStart = Project;

        try
        {
            IsAiDetectBusy = true;
            RefreshCommands();
            StatusText = "AI detect is scanning the image for exportable elements...";

            var suggestions = await _aiCutoutDetectionService.DetectAsync(sourceBitmapAtStart, new AiCutoutDetectionService.AiDetectionOptions
            {
                MaxResults = 12,
                Model = AiCutoutDetectionService.DefaultModel,
                Strength = (float)(AutoDetectStrength / 100.0),
            });

            if (!IsSessionCurrent(sessionRevision, projectAtStart, sourceBitmapAtStart))
            {
                return;
            }

            if (suggestions.Count == 0)
            {
                StatusText = "AI detect didn't find any usable cutouts. Try Quick Detect or draw with Lasso/Polygon.";
                return;
            }

            var generatedCutouts = BuildGeneratedCutoutsFromSuggestions(suggestions);
            if (generatedCutouts.Count == 0)
            {
                StatusText = "AI detect returned shapes, but none were usable as cutouts.";
                return;
            }

            PushUndoSnapshot();
            ClearActiveSelection();
            foreach (var cutout in generatedCutouts)
            {
                Project.Cutouts.Add(cutout);
            }

            SetSelectedCutouts(generatedCutouts, requestSync: true);
            IsSelectTool = true;
            MarkDirty($"AI detected {generatedCutouts.Count} cutout(s). Review and refine any edge cases with the brush tools.", touchProjectTimestamp: false);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message.StartsWith("AI Detect", StringComparison.OrdinalIgnoreCase)
                ? ex.Message
                : $"AI detect failed: {ex.Message}";
        }
        finally
        {
            IsAiDetectBusy = false;
            RefreshCommands();
        }
    }

    private List<CutoutModel> BuildGeneratedCutoutsFromSuggestions(IReadOnlyList<AutoCutoutService.AutoCutoutSuggestion> suggestions)
    {
        var usedNames = new HashSet<string>(Project.Cutouts.Select(cutout => cutout.Name), StringComparer.OrdinalIgnoreCase);
        var generatedCutouts = new List<CutoutModel>(suggestions.Count);
        var nextIndex = Project.Cutouts.Count + 1;

        foreach (var suggestion in suggestions)
        {
            if (!GeometryHelper.IsValidGeometry(suggestion.Geometry))
            {
                continue;
            }

            var fallbackPrefix = string.Equals(suggestion.Kind, "section", StringComparison.OrdinalIgnoreCase)
                ? "Section"
                : "AI";
            var name = BuildUniqueGeneratedName(suggestion.Label, fallbackPrefix, nextIndex, usedNames);

            generatedCutouts.Add(new CutoutModel
            {
                Name = name,
                Geometry = suggestion.Geometry.DeepClone(),
                BBox = GeometryHelper.ComputeBBox(suggestion.Geometry.Points),
                Export = Project.Defaults.DeepClone(),
                AutoConfidence = Math.Clamp(suggestion.Confidence, 0.0f, 1.0f),
            });

            nextIndex++;
        }

        return generatedCutouts;
    }

    private static string BuildUniqueGeneratedName(string? suggestedLabel, string fallbackPrefix, int index, ISet<string> usedNames)
    {
        var baseName = string.IsNullOrWhiteSpace(suggestedLabel)
            ? $"{fallbackPrefix} {index:000}"
            : suggestedLabel.Trim();

        if (usedNames.Add(baseName))
        {
            return baseName;
        }

        var suffix = 2;
        while (true)
        {
            var candidate = $"{baseName} {suffix:000}";
            if (usedNames.Add(candidate))
            {
                return candidate;
            }

            suffix++;
        }
    }
}

