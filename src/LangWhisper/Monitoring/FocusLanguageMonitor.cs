using System.Threading;
using System.Threading.Tasks;
using LangWhisper.Audio;
using LangWhisper.Configuration;
using LangWhisper.Diagnostics;
using LangWhisper.Interop;
using Timer = System.Windows.Forms.Timer;

namespace LangWhisper.Monitoring;

/// <summary>
/// Announces the active keyboard language on three triggers:
/// <list type="bullet">
///   <item>focus lands on an editable text field (window switch or in-window focus),</item>
///   <item>the foreground window changes to another editable window,</item>
///   <item>the keyboard layout itself is switched (Alt+Shift, Win+Space, language bar).</item>
/// </list>
/// The first two are event-driven (WinEvent hooks + debounce); the layout switch is
/// found by a light poll because Windows raises no accessible event for it.
/// </summary>
public sealed class FocusLanguageMonitor : IDisposable
{
    private readonly AppSettings _settings;
    private readonly CuePlayer _player;

    // Keep a field reference so the delegate passed to SetWinEventHook is not collected.
    private readonly NativeMethods.WinEventProc _callback;
    private readonly Timer _debounce;
    private readonly Timer _languagePoll;

    private readonly NativeMethods.LowLevelKeyboardProc _keyboardCallback;

    private IntPtr _foregroundHook;
    private IntPtr _focusHook;
    private IntPtr _keyboardHook;
    private int _processing;

    // Set when an editable field gets focus; cleared by the first keystroke or a non-editable focus.
    private int _typingArmed;
    private long _editableFocusedAt;

    private IntPtr _lastForeground;
    private uint _lastInputThread;
    private bool _pendingWindowSwitch;
    private int _lastSeenLangId = -1;      // last language observed in the foreground, announced or not
    private int _lastAnnouncedLangId = -1; // last language actually spoken/beeped
    private bool _started;

    public FocusLanguageMonitor(AppSettings settings, CuePlayer player)
    {
        _settings = settings;
        _player = player;
        _callback = OnWinEvent;
        _keyboardCallback = OnKeyboardHook;

        _debounce = new Timer { Interval = Math.Max(30, _settings.DebounceMs) };
        _debounce.Tick += OnDebounceElapsed;

        _languagePoll = new Timer { Interval = Math.Max(120, _settings.LanguagePollMs) };
        _languagePoll.Tick += OnLanguagePollTick;
    }

    /// <summary>Installs the hooks. Must be called on the UI thread (it owns the message pump).</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _callback,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        _focusHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_FOCUS,
            NativeMethods.EVENT_OBJECT_FOCUS,
            IntPtr.Zero,
            _callback,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        _lastForeground = NativeMethods.GetForegroundWindow();
        _lastInputThread = KeyboardLayoutReader.GetInputThreadId();
        _languagePoll.Start();
        _started = true;
        UpdateKeyboardHook();
    }

    /// <summary>
    /// Installs the keyboard hook only while the typing-after-idle option is on, so the
    /// app doesn't watch keystrokes it doesn't need. Call on the UI thread after settings change.
    /// </summary>
    public void UpdateKeyboardHook()
    {
        bool wanted = _started && _settings.AnnounceOnTypingAfterIdle;

        if (wanted && _keyboardHook == IntPtr.Zero)
        {
            _keyboardHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                _keyboardCallback,
                NativeMethods.GetModuleHandle(null),
                0);

            if (_keyboardHook == IntPtr.Zero)
            {
                Log.Write($"typing: could not install keyboard hook (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            }
        }
        else if (!wanted && _keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private IntPtr OnKeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Runs on the UI thread for every keystroke system-wide: keep it cheap.
        if (nCode >= 0
            && (wParam == NativeMethods.WM_KEYDOWN || wParam == NativeMethods.WM_SYSKEYDOWN)
            && Volatile.Read(ref _typingArmed) == 1
            && !_debounce.Enabled
            && Volatile.Read(ref _processing) == 0)
        {
            var info = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if (IsTypingKey(info.vkCode) && Interlocked.Exchange(ref _typingArmed, 0) == 1)
            {
                long idleMs = Environment.TickCount64 - Interlocked.Read(ref _editableFocusedAt);
                if (_settings.Enabled && _settings.AnnounceOnTypingAfterIdle && idleMs >= _settings.TypingIdleSeconds * 1000L)
                {
                    _ = Task.Run(() => AnnounceTypingAfterIdle(idleMs));
                }
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private static bool IsTypingKey(uint vk)
    {
        bool printable = vk is >= 0x30 and <= 0x39   // digits
            or >= 0x41 and <= 0x5A                   // letters
            or >= 0x60 and <= 0x6F                   // numpad
            or >= 0xBA and <= 0xC0                   // ;=,-./`
            or >= 0xDB and <= 0xDF                   // [\]'
            or 0xE2;                                 // <> on ISO keyboards
        if (!printable)
        {
            return false;
        }

        // Shortcuts (Ctrl+C, Alt+Tab, Win+Space) are not typing.
        return !IsDown(NativeMethods.VK_CONTROL) && !IsDown(NativeMethods.VK_MENU)
            && !IsDown(NativeMethods.VK_LWIN) && !IsDown(NativeMethods.VK_RWIN);

        static bool IsDown(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;
    }

    private void AnnounceTypingAfterIdle(long idleMs)
    {
        try
        {
            if (NativeMethods.GetForegroundWindow() != _lastForeground)
            {
                return;
            }

            int langId = KeyboardLayoutReader.GetForegroundLanguageId();
            if (langId <= 0)
            {
                return;
            }

            _lastAnnouncedLangId = langId;
            var culture = KeyboardLayoutReader.TryGetCulture(langId);
            Log.Write($"typing: started after {idleMs / 1000}s idle, announcing {culture?.EnglishName ?? $"LANGID 0x{langId:X4}"}");
            _player.Announce(culture, langId);
        }
        catch (Exception ex)
        {
            Log.Write($"typing: announce error: {ex.Message}");
        }
    }

    /// <summary>Forget the last announced language so the next trigger re-announces.</summary>
    public void ResetLastAnnounced()
    {
        Interlocked.Exchange(ref _lastAnnouncedLangId, -1);
        Interlocked.Exchange(ref _lastSeenLangId, -1);
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (!_settings.Enabled)
        {
            return;
        }

        // Restart the debounce window; the burst of events during a switch collapses to one run.
        _debounce.Stop();
        _debounce.Interval = Math.Max(30, _settings.DebounceMs);
        _debounce.Start();
    }

    private void OnDebounceElapsed(object? sender, EventArgs e)
    {
        _debounce.Stop();

        // Claim the slot on the UI thread so a poll tick can't slip in before the task starts.
        if (Interlocked.Exchange(ref _processing, 1) == 1)
        {
            _debounce.Start();
            return;
        }

        _ = Task.Run(ProcessFocusEvent);
    }

    private void OnLanguagePollTick(object? sender, EventArgs e)
    {
        if (!_settings.Enabled || !_settings.AnnounceOnLanguageChange)
        {
            return;
        }

        // A focus/window event is pending or mid-flight; it will pick up the current layout itself.
        if (_debounce.Enabled || Volatile.Read(ref _processing) == 1)
        {
            return;
        }

        _ = Task.Run(ProcessLanguagePoll);
    }

    /// <summary>Handles a foreground-window or focus change.</summary>
    private void ProcessFocusEvent()
    {
        try
        {
            if (!_settings.Enabled)
            {
                return;
            }

            long focusedAt = Environment.TickCount64;
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero)
            {
                return;
            }

            // A switch often lands on a non-editable control first (a tab strip, a
            // toolbar) and reaches the text field only on a later focus event, so the
            // switch stays pending until an editable focus in that window consumes it.
            if (foreground != _lastForeground)
            {
                _pendingWindowSwitch = true;
            }

            bool editable = EditableFocusDetector.IsEditable(foreground);

            int langId = ReadSettledLanguageId(_lastSeenLangId);

            // Track state regardless of whether we announce, so later triggers compare fairly.
            _lastForeground = foreground;
            _lastInputThread = KeyboardLayoutReader.GetInputThreadId();
            if (langId > 0)
            {
                _lastSeenLangId = langId;
            }

            if (!editable)
            {
                Volatile.Write(ref _typingArmed, 0);
                Log.Write("focus: not an editable field, ignoring");
                return;
            }

            Interlocked.Exchange(ref _editableFocusedAt, focusedAt);
            Volatile.Write(ref _typingArmed, 1);

            if (langId <= 0)
            {
                Log.Write("focus: editable field but keyboard language unknown");
                return;
            }

            // If the user has already moved on to another window while we were
            // waiting for the layout to settle, let the pending event handle it.
            if (NativeMethods.GetForegroundWindow() != foreground)
            {
                Log.Write("focus: foreground changed while settling, deferring");
                return;
            }

            var culture = KeyboardLayoutReader.TryGetCulture(langId);
            string name = culture?.EnglishName ?? $"LANGID 0x{langId:X4}";

            bool windowChanged = _pendingWindowSwitch;
            _pendingWindowSwitch = false;

            bool announce;
            string reason;

            if (windowChanged && _settings.AnnounceOnWindowSwitch)
            {
                announce = true;
                reason = "window switch";
            }
            else if (!_settings.AnnounceOnlyOnChange || langId != _lastAnnouncedLangId)
            {
                announce = true;
                reason = "focus change";
            }
            else
            {
                announce = false;
                reason = "unchanged";
            }

            if (!announce)
            {
                Log.Write($"focus: editable field, language {name} ({reason}), staying quiet");
                return;
            }

            _lastAnnouncedLangId = langId;
            Log.Write($"focus: editable field, announcing {name} ({reason})");
            _player.Announce(culture, langId);
        }
        catch (Exception ex)
        {
            // A single failed read must never take the monitor down.
            Log.Write($"focus: processing error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _processing, 0);
        }
    }

    /// <summary>Handles a keyboard-layout switch detected by polling.</summary>
    private void ProcessLanguagePoll()
    {
        if (Interlocked.Exchange(ref _processing, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_settings.Enabled || !_settings.AnnounceOnLanguageChange)
            {
                return;
            }

            // A different window (or a Notepad tab on another thread) now has the
            // keyboard: that's a focus change, not a layout switch. Leave both the
            // announcement and the baseline to the focus path.
            if (NativeMethods.GetForegroundWindow() != _lastForeground
                || KeyboardLayoutReader.GetInputThreadId() != _lastInputThread)
            {
                return;
            }

            int langId = KeyboardLayoutReader.GetForegroundLanguageId();
            if (langId <= 0)
            {
                return;
            }

            if (_lastSeenLangId <= 0)
            {
                // First observation: establish the baseline silently.
                _lastSeenLangId = langId;
                return;
            }

            if (langId == _lastSeenLangId)
            {
                return;
            }

            // The layout was actually switched.
            _lastSeenLangId = langId;
            _lastAnnouncedLangId = langId;

            // The user just heard the language; restart the typing-idle clock.
            Interlocked.Exchange(ref _editableFocusedAt, Environment.TickCount64);

            var culture = KeyboardLayoutReader.TryGetCulture(langId);
            string name = culture?.EnglishName ?? $"LANGID 0x{langId:X4}";
            Log.Write($"language switch: announcing {name} ({_settings.LanguageChangeSignal})");
            _player.Announce(culture, langId, _settings.LanguageChangeSignal);
        }
        catch (Exception ex)
        {
            Log.Write($"language poll error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _processing, 0);
        }
    }

    /// <summary>
    /// Reads the foreground keyboard language, waiting for it to stop changing.
    /// When a window becomes foreground Windows applies its remembered layout a
    /// moment later, so an immediate read frequently still returns the language
    /// of the window the user just left. A naive "two consecutive reads agree"
    /// check gets fooled by this: the first two samples are often both the stale
    /// value, taken before Windows has even started applying the new one, so
    /// they "agree" while still being wrong. Instead, once we know what the
    /// previous window's language was (<paramref name="baselineLangId"/>), we
    /// only trust an early result once the reading has actually moved away from
    /// that baseline and then held steady for one more sample; if the language
    /// genuinely didn't change, we fall back to the last sample once the full
    /// settle budget is spent.
    /// </summary>
    private static int ReadSettledLanguageId(int baselineLangId)
    {
        const int maxAttempts = 5;
        const int gapMs = 120;

        int last = KeyboardLayoutReader.GetForegroundLanguageId();

        for (int attempt = 1; attempt < maxAttempts; attempt++)
        {
            Thread.Sleep(gapMs);
            int current = KeyboardLayoutReader.GetForegroundLanguageId();

            bool stable = current > 0 && current == last;
            bool trustworthy = baselineLangId <= 0 || current != baselineLangId || attempt == maxAttempts - 1;

            if (stable && trustworthy)
            {
                return current;
            }

            last = current;
        }

        return last;
    }

    public void Dispose()
    {
        _debounce.Stop();
        _debounce.Tick -= OnDebounceElapsed;
        _debounce.Dispose();

        _languagePoll.Stop();
        _languagePoll.Tick -= OnLanguagePollTick;
        _languagePoll.Dispose();

        if (_foregroundHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        if (_focusHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_focusHook);
            _focusHook = IntPtr.Zero;
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }
}
