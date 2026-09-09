using System.IO;
using System.Text;
using LangWhisper.Configuration;

namespace LangWhisper.Diagnostics;

/// <summary>
/// Minimal append-only file logger. A tray app has no console and no window, so this
/// is the only way to see what it decided at runtime. Self-capping; never throws.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1_000_000;

    private static readonly object Gate = new();

    public static bool Enabled { get; set; } = true;

    public static string FilePath { get; } = Path.Combine(SettingsStore.Directory, "langwhisper.log");

    public static void Write(string message)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.Directory);

                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                {
                    File.WriteAllText(FilePath, string.Empty);
                }

                File.AppendAllText(
                    FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never disturb the app.
        }
    }
}
