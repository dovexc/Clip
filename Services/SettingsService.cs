using System.IO;
using System.Text.Json;
using ClipperApp.Models;

namespace ClipperApp.Services;

public static class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClipperApp", "settings.json");

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch { settings = new AppSettings(); }

        // Migrate: old default was 10 s, which causes up to 10 s of missing footage per clip.
        // Force it to 2 s so the open-segment gap is at most ~2 s.
        if (settings.SegmentDurationSeconds >= 5)
            settings.SegmentDurationSeconds = 2;

        return settings;
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
