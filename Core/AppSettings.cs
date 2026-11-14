using System.IO;
using System.Text.Json;

namespace HardwareCenter.Core;

/// <summary>User preferences, stored as JSON under %LOCALAPPDATA%\HardwareCenter.</summary>
public sealed class AppSettings
{
    public int IntervalMs { get; set; } = 1000;
    public bool UseFahrenheit { get; set; }
    public bool AlwaysRunAsAdministrator { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool ShowInactiveAdapters { get; set; }
    public bool MinimizeToTray { get; set; } = true;

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HardwareCenter");

    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or damaged settings file just means defaults.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences are a convenience; failing to persist them must not interrupt the app.
        }
    }
}
