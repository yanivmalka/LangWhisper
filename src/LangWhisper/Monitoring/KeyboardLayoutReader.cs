using System.Globalization;
using LangWhisper.Interop;

namespace LangWhisper.Monitoring;

/// <summary>Reads the keyboard layout (input language) of the foreground window's thread.</summary>
public static class KeyboardLayoutReader
{
    /// <summary>
    /// Returns the LANGID (low word of the HKL) currently active for the foreground
    /// window, or 0 if it cannot be determined.
    /// </summary>
    public static int GetForegroundLanguageId()
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return 0;
        }

        uint threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        if (threadId == 0)
        {
            return 0;
        }

        IntPtr hkl = NativeMethods.GetKeyboardLayout(threadId);
        return (int)((long)hkl & 0xFFFF);
    }

    /// <summary>Maps a LANGID to a <see cref="CultureInfo"/>, or null when it is not a known locale.</summary>
    public static CultureInfo? TryGetCulture(int langId)
    {
        if (langId <= 0)
        {
            return null;
        }

        try
        {
            return CultureInfo.GetCultureInfo(langId);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}
