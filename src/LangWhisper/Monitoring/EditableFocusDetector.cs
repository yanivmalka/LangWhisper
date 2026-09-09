using System.Runtime.InteropServices;
using System.Windows.Automation;
using LangWhisper.Interop;

namespace LangWhisper.Monitoring;

/// <summary>
/// Decides whether the currently focused UI element is a text-editing surface with a
/// caret (a place the user would actually type), as opposed to a button, list, etc.
/// </summary>
public static class EditableFocusDetector
{
    public static bool IsEditable(IntPtr foregroundWindow)
    {
        // Fast path: classic Win32 controls expose a real caret window.
        if (HasCaretWindow(foregroundWindow))
        {
            return true;
        }

        // Modern apps (WPF, WinUI, Chromium/Electron, browsers) usually have no caret
        // HWND, so fall back to UI Automation on the focused element.
        try
        {
            return CheckViaUiAutomation();
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasCaretWindow(IntPtr hwnd)
    {
        uint threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        if (threadId == 0)
        {
            return false;
        }

        var info = new NativeMethods.GUITHREADINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>(),
        };

        return NativeMethods.GetGUIThreadInfo(threadId, ref info) && info.hwndCaret != IntPtr.Zero;
    }

    private static bool CheckViaUiAutomation()
    {
        AutomationElement? focused = AutomationElement.FocusedElement;
        if (focused is null)
        {
            return false;
        }

        ControlType controlType = focused.Current.ControlType;
        bool looksTextual = controlType == ControlType.Edit || controlType == ControlType.Document;

        if (!looksTextual)
        {
            // Rich editors (browser content, some custom controls) advertise TextPattern.
            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out _))
            {
                return false;
            }

            looksTextual = true;
        }

        // If the element can tell us it's read-only, respect that.
        if (focused.TryGetCurrentPattern(ValuePattern.Pattern, out object valueObj) &&
            valueObj is ValuePattern valuePattern)
        {
            return !valuePattern.Current.IsReadOnly;
        }

        return looksTextual;
    }
}
