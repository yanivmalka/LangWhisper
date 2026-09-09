using Microsoft.Win32;

namespace LangWhisper.Startup;

/// <summary>Toggles "start when I sign in" via the per-user Run registry key.</summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LangWhisper";

    private static string ExecutablePath =>
        Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

    public static bool IsEnabled
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string existing &&
                   string.Equals(Trim(existing), ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (enabled)
        {
            key.SetValue(ValueName, $"\"{ExecutablePath}\"");
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static string Trim(string value) => value.Trim().Trim('"');
}
