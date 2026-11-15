using System.Globalization;
using LangWhisper.Interop;

namespace LangWhisper.Monitoring;

/// <summary>Reads the keyboard layout (input language) that keystrokes in the foreground window will use.</summary>
public static class KeyboardLayoutReader
{
    /// <summary>
    /// Returns the LANGID (low word of the HKL) currently active for the focused
    /// control of the foreground window, or 0 if it cannot be determined.
    /// </summary>
    public static int GetForegroundLanguageId() => GetLanguageId(GetInputThreadId());

    /// <summary>Returns the LANGID active for the given thread, or 0 if it cannot be determined.</summary>
    public static int GetLanguageId(uint threadId)
    {
        if (threadId == 0)
        {
            return 0;
        }

        IntPtr hkl = NativeMethods.GetKeyboardLayout(threadId);
        return (int)((long)hkl & 0xFFFF);
    }

    /// <summary>
    /// The thread that receives keystrokes in the foreground window. Layouts are per
    /// thread, and some apps host their editor on a different thread than the
    /// top-level window (Windows 11 Notepad runs each tab's text box on its own
    /// thread), so the focused window's thread is used rather than the top-level one.
    /// </summary>
    public static uint GetInputThreadId()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return 0;
        }

        uint foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        if (foregroundThread == 0)
        {
            return 0;
        }

        var info = new NativeMethods.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
        if (NativeMethods.GetGUIThreadInfo(foregroundThread, ref info) && info.hwndFocus != IntPtr.Zero)
        {
            uint focusThread = NativeMethods.GetWindowThreadProcessId(info.hwndFocus, out _);
            if (focusThread != 0)
            {
                return focusThread;
            }
        }

        return foregroundThread;
    }

    /// <summary>
    /// True when Windows keeps a separate input language per app window (Settings →
    /// "Let me use a different input method for each app window"); false for the
    /// default, where one input language follows the user across windows.
    /// </summary>
    public static bool IsPerWindowInput()
    {
        bool perWindow = false;
        return NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETTHREADLOCALINPUTSETTINGS, 0, ref perWindow, 0)
            && perWindow;
    }

    /// <summary>The distinct input languages installed for the user, in language-bar order.</summary>
    public static IReadOnlyList<CultureInfo> GetInstalledCultures()
    {
        int count = NativeMethods.GetKeyboardLayoutList(0, null);
        var layouts = new IntPtr[Math.Max(0, count)];
        count = NativeMethods.GetKeyboardLayoutList(layouts.Length, layouts);

        var result = new List<CultureInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            var culture = TryGetCulture((int)((long)layouts[i] & 0xFFFF));
            if (culture is not null && seen.Add(culture.TwoLetterISOLanguageName))
            {
                result.Add(culture);
            }
        }

        return result;
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
