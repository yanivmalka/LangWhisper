using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LangWhisper.Configuration;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON in the per-user app-data folder.</summary>
public static class SettingsStore
{
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LangWhisper");

    public static string FilePath { get; } = Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable file: fall back to defaults rather than crash on startup.
        }

        var fresh = new AppSettings();
        Save(fresh);
        return fresh;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Non-fatal: settings just won't persist this session.
        }
    }
}
