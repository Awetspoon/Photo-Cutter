using System.Diagnostics;
using System.Text.Json;
using ImageUiSlicer.Models;

namespace ImageUiSlicer.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _root;
    private readonly string _fallbackRoot;

    public SettingsService()
    {
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ImageUiSlicer");
        _fallbackRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ImageUiSlicer");

        TryEnsureDirectory(_root);
        TryEnsureDirectory(_fallbackRoot);
    }

    public string SettingsPath => Path.Combine(_root, "settings.json");

    private string FallbackSettingsPath => Path.Combine(_fallbackRoot, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return TryLoadFallback();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), SerializerOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Photo Cutter settings load failed at '{SettingsPath}': {ex}");
            return TryLoadFallback();
        }
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, SerializerOptions);
        if (TryWriteFile(SettingsPath, json))
        {
            return;
        }

        if (TryWriteFile(FallbackSettingsPath, json))
        {
            return;
        }

        Debug.WriteLine("Photo Cutter settings save skipped because both settings locations are unavailable.");
    }

    private AppSettings TryLoadFallback()
    {
        try
        {
            if (File.Exists(FallbackSettingsPath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FallbackSettingsPath), SerializerOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Photo Cutter settings fallback load failed at '{FallbackSettingsPath}': {ex}");
        }

        return new AppSettings();
    }

    private static bool TryWriteFile(string path, string content)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, content);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Photo Cutter settings write failed at '{path}': {ex.Message}");
            return false;
        }
    }

    private static void TryEnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Photo Cutter settings directory unavailable '{path}': {ex.Message}");
        }
    }
}
