using System.Threading;
using LangWhisper.Audio;
using LangWhisper.Configuration;
using LangWhisper.Diagnostics;
using LangWhisper.Interop;

namespace LangWhisper.Monitoring;

/// <summary>
/// Announces the active keyboard language on these triggers:
/// <list type="bullet">
///   <item>focus lands on an editable text field (window switch or in-window focus),</item>
///   <item>the keyboard layout itself is switched (Alt+Shift, Win+Space, language bar),</item>
///   <item>optionally, the first keystroke after an idle focus.</item>
/// </list>
/// <para>
/// Architecture: the hooks (which need the UI thread's message pump) do nothing but
/// raise a flag and wake a single dedicated worker thread. The worker owns all state
/// and handles one thing at a time, so there are no timers, no debounce delay and no
/// cross-thread races.
/// </para>
/// <para>
/// Latency: a focus event is handled at once. Rather than waiting for the keyboard
/// layout to "settle" before speaking, the worker announces as soon as the reading
/// matches what it expects for that window, and keeps watching for a short while
/// afterwards; if Windows applies a different layout late, the cue is corrected.
/// </para>
/// </summary>
public sealed class FocusLanguageMonitor : IDisposable
{
    /// <summary>How long after a window/thread switch a late layout change counts as "settling", not a user switch.</summary>
    private const int SettleWindowMs = 600;

    /// <summary>Longest the announcement is held back when the first reading contradicts the expected language.</summary>
    private const int MaxSettleWaitMs = 250;

    /// <summary>Poll interval while a switch is settling.</summary>
    private const int FastPollMs = 40;

    /// <summary>Suppresses a repeat of the same cue from the burst of focus events one click can raise.</summary>
    private const int DuplicateGuardMs = 500;

    private const int MaxRememberedThreads = 256;

    private readonly AppSettings _settings;
    private readonly CuePlayer _player;

    // Keep field references so the delegates passed to the hooks are not collected.
    private readonly NativeMethods.WinEventProc _callback;
    private readonly NativeMethods.LowLevelKeyboardProc _keyboardCallback;

    private readonly AutoResetEvent _wake = new(false);
    private Thread? _worker;
    private volatile bool _stopping;

    private IntPtr _foregroundHook;
    private IntPtr _focusHook;
    private IntPtr _keyboardHook;
    private bool _started;

    // ---- Signals from the hook (UI) thread to the worker ----------------------
    private int _focusPending;         // a foreground/focus event arrived
    private long _typingIdleMs = -1;   // >= 0: a first keystroke after this much idle time
    private int _resetRequested;

    // Shared with the keyboard hook: set when an editable field gets focus,
    // cleared by the first keystroke or a non-editable focus.
    private int _typingArmed;
    private long _editableFocusedAt;

    // ---- Worker-owned state ---------------------------------------------------
    private IntPtr _lastForeground;
    private uint _lastInputThread;
    private bool _pendingWindowSwitch;
    private int _lastSeenLangId = -1;      // last language observed in the foreground, announced or not
    private int _lastAnnouncedLangId = -1; // last language actually spoken/beeped
    private long _lastAnnouncedAt;
    private long _switchAt;                // when the foreground window / input thread last changed
    private int _baselineLangId = -1;      // language in use just before that switch
    private bool _switchAnnounced;         // a cue was played since that switch
    private readonly Dictionary<uint, int> _threadLanguages = new(); // layout last seen per input thread

    public FocusLanguageMonitor(AppSettings settings, CuePlayer player)
    {
        _settings = settings;
        _player = player;
        _callback = OnWinEvent;
        _keyboardCallback = OnKeyboardHook;
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

        _worker = new Thread(RunWorker)
        {
            IsBackground = true,
            Name = "LangWhisper.Monitor",
        };
        _worker.SetApartmentState(ApartmentState.MTA); // UI Automation client calls are made from here.
        _worker.Start();

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

    /// <summary>Forget the last announced language so the next trigger re-announces.</summary>
    public void ResetLastAnnounced()
    {
        Interlocked.Exchange(ref _resetRequested, 1);
        _wake.Set();
    }

    // ---- Hook callbacks (UI thread): signal the worker and return -------------

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

        Interlocked.Exchange(ref _focusPending, 1);
        _wake.Set();
    }

    private IntPtr OnKeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Runs on the UI thread for every keystroke system-wide: keep it cheap.
        if (nCode >= 0
            && (wParam == NativeMethods.WM_KEYDOWN || wParam == NativeMethods.WM_SYSKEYDOWN)
            && Volatile.Read(ref _typingArmed) == 1
            && Volatile.Read(ref _focusPending) == 0)
        {
            var info = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if (IsTypingKey(info.vkCode) && Interlocked.Exchange(ref _typingArmed, 0) == 1)
            {
                long idleMs = Environment.TickCount64 - Interlocked.Read(ref _editableFocusedAt);
                if (_settings.Enabled && _settings.AnnounceOnTypingAfterIdle && idleMs >= _settings.TypingIdleSeconds * 1000L)
                {
                    Interlocked.Exchange(ref _typingIdleMs, idleMs);
                    _wake.Set();
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

    // ---- Worker ---------------------------------------------------------------

    private void RunWorker()
    {
        while (!_stopping)
        {
            int timeout = IsSettling() ? FastPollMs : Math.Max(120, _settings.LanguagePollMs);
            _wake.WaitOne(timeout);
            if (_stopping)
            {
                break;
            }

            try
            {
                if (Interlocked.Exchange(ref _resetRequested, 0) == 1)
                {
                    _lastAnnouncedLangId = -1;
                    _lastSeenLangId = -1;
                }

                long typingIdleMs = Interlocked.Exchange(ref _typingIdleMs, -1);
                bool focusEvent = Interlocked.Exchange(ref _focusPending, 0) == 1;

                if (!_settings.Enabled)
                {
                    continue;
                }

                if (focusEvent)
                {
                    // One click raises several events within a few ms; fold them into one pass.
                    int coalesceMs = Math.Clamp(_settings.FocusCoalesceMs, 0, 200);
                    if (coalesceMs > 0)
                    {
                        Thread.Sleep(coalesceMs);
                        Interlocked.Exchange(ref _focusPending, 0);
                    }

                    HandleFocusEvent();
                }
                else if (typingIdleMs >= 0)
                {
                    HandleTypingAfterIdle(typingIdleMs);
                }
                else
                {
                    HandleLayoutPoll();
                }
            }
            catch (Exception ex)
            {
                // A single failed read must never take the monitor down.
                Log.Write($"monitor: processing error: {ex.Message}");
            }
        }
    }

    private bool IsSettling() => Environment.TickCount64 - _switchAt < SettleWindowMs;

    /// <summary>Handles a foreground-window or focus change.</summary>
    private void HandleFocusEvent()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return;
        }

        uint inputThread = KeyboardLayoutReader.GetInputThreadId();

        // A switch often lands on a non-editable control first (a tab strip, a
        // toolbar) and reaches the text field only on a later focus event, so the
        // switch stays pending until an editable focus in that window consumes it.
        if (foreground != _lastForeground)
        {
            _pendingWindowSwitch = true;
        }

        if (foreground != _lastForeground || inputThread != _lastInputThread)
        {
            // While a previous switch is still settling, the last reading may be a
            // stale one, so keep the baseline taken before that switch.
            if (!IsSettling() || _baselineLangId <= 0)
            {
                _baselineLangId = _lastSeenLangId;
            }

            _switchAt = Environment.TickCount64;
            _switchAnnounced = false;
        }

        _lastForeground = foreground;
        _lastInputThread = inputThread;

        bool editable = EditableFocusDetector.IsEditable(foreground);
        long focusedAt = Environment.TickCount64;

        if (!editable)
        {
            // Nothing to say. If a switch is settling, the fast poll picks up the
            // window's layout silently, so the worker is free for the next event.
            Volatile.Write(ref _typingArmed, 0);
            if (!IsSettling())
            {
                Remember(inputThread, KeyboardLayoutReader.GetLanguageId(inputThread));
            }

            Log.Write("focus: not an editable field, ignoring");
            return;
        }

        Interlocked.Exchange(ref _editableFocusedAt, focusedAt);
        Volatile.Write(ref _typingArmed, 1);

        int langId = ReadLanguage(inputThread);

        // The user moved on while we were reading; the pending event handles it.
        if (Volatile.Read(ref _focusPending) == 1 && NativeMethods.GetForegroundWindow() != foreground)
        {
            Log.Write("focus: foreground changed while reading, deferring");
            return;
        }

        if (langId <= 0)
        {
            Log.Write("focus: editable field but keyboard language unknown");
            return;
        }

        Remember(inputThread, langId);

        var culture = KeyboardLayoutReader.TryGetCulture(langId);
        string name = culture?.EnglishName ?? $"LANGID 0x{langId:X4}";
        long now = Environment.TickCount64;

        bool announce;
        string reason;

        if (_pendingWindowSwitch && _settings.AnnounceOnWindowSwitch)
        {
            announce = true;
            reason = "window switch";
        }
        else if (langId != _lastAnnouncedLangId)
        {
            announce = true;
            reason = "focus change";
        }
        else if (!_settings.AnnounceOnlyOnChange && now - _lastAnnouncedAt >= DuplicateGuardMs)
        {
            announce = true;
            reason = "focus change";
        }
        else
        {
            announce = false;
            reason = "unchanged";
        }

        _pendingWindowSwitch = false;

        if (!announce)
        {
            Log.Write($"focus: editable field, language {name} ({reason}), staying quiet");
            return;
        }

        Announce(langId, null);
        Log.Write($"focus: editable field, announcing {name} ({reason}, {now - focusedAt} ms after detection)");
    }

    /// <summary>
    /// Reads the layout of the input thread. Right after a window/thread switch the
    /// reading can still be stale, because Windows applies the layout a moment after
    /// the window becomes foreground. So the reading is trusted at once when it
    /// matches what is expected for that thread; only when it contradicts the
    /// expectation is it re-read, briefly, until it matches or the budget runs out.
    /// A wrong guess is repaired afterwards by <see cref="HandleLayoutPoll"/>.
    /// </summary>
    private int ReadLanguage(uint inputThread)
    {
        int langId = KeyboardLayoutReader.GetLanguageId(inputThread);
        if (!IsSettling())
        {
            return langId;
        }

        int expected = ExpectedLanguage(inputThread);
        if (expected <= 0 || langId == expected)
        {
            return langId;
        }

        long deadline = Math.Min(_switchAt + SettleWindowMs, Environment.TickCount64 + MaxSettleWaitMs);
        while (langId != expected && Environment.TickCount64 < deadline && Volatile.Read(ref _focusPending) == 0)
        {
            Thread.Sleep(15);
            langId = KeyboardLayoutReader.GetLanguageId(inputThread);
        }

        return langId;
    }

    /// <summary>
    /// The language a thread should end up with after a switch: with Windows' default
    /// single input language for all apps it is the language in use just before; with
    /// "let me use a different input method for each app window" it is the one that
    /// thread had last time. 0 when unknown.
    /// </summary>
    private int ExpectedLanguage(uint inputThread)
    {
        if (KeyboardLayoutReader.IsPerWindowInput())
        {
            return _threadLanguages.TryGetValue(inputThread, out int remembered) ? remembered : 0;
        }

        return _baselineLangId;
    }

    /// <summary>Records a reading that is believed to be the thread's real, settled layout.</summary>
    private void Remember(uint inputThread, int langId)
    {
        if (langId <= 0)
        {
            return;
        }

        _lastSeenLangId = langId;

        if (_threadLanguages.Count >= MaxRememberedThreads && !_threadLanguages.ContainsKey(inputThread))
        {
            _threadLanguages.Clear();
        }

        _threadLanguages[inputThread] = langId;
    }

    private void Announce(int langId, CueMode? modeOverride)
    {
        _lastAnnouncedLangId = langId;
        _lastAnnouncedAt = Environment.TickCount64;
        _switchAnnounced = true;
        _player.Announce(KeyboardLayoutReader.TryGetCulture(langId), langId, modeOverride);
    }

    /// <summary>
    /// Periodic layout check. Finds a deliberate keyboard-layout switch (Windows raises
    /// no accessible event for it) and, right after a window switch, a layout that
    /// Windows applied late.
    /// </summary>
    private void HandleLayoutPoll()
    {
        // A different window (or a Notepad tab on another thread) now has the
        // keyboard: that's a focus change, not a layout switch. Leave it to the focus path.
        uint inputThread = KeyboardLayoutReader.GetInputThreadId();
        if (NativeMethods.GetForegroundWindow() != _lastForeground || inputThread != _lastInputThread)
        {
            return;
        }

        int langId = KeyboardLayoutReader.GetLanguageId(inputThread);
        if (langId <= 0)
        {
            return;
        }

        int previous = _lastSeenLangId;
        bool changed = previous > 0 && langId != previous;

        // An unchanged reading taken while a switch is settling may still be the
        // stale one, so it must not become what is expected of this thread next time.
        if (changed || !IsSettling())
        {
            Remember(inputThread, langId);
        }
        else
        {
            _lastSeenLangId = langId;
        }

        if (!changed)
        {
            // Nothing new (or the first observation, which sets the baseline silently).
            return;
        }

        var culture = KeyboardLayoutReader.TryGetCulture(langId);
        string name = culture?.EnglishName ?? $"LANGID 0x{langId:X4}";

        if (IsSettling())
        {
            // Windows applied this window's layout after we looked.
            if (_switchAnnounced && langId != _lastAnnouncedLangId)
            {
                Log.Write($"focus: layout settled late, correcting to {name}");
                Announce(langId, null);
            }
            else
            {
                Log.Write($"focus: layout settled to {name}");
            }

            return;
        }

        if (!_settings.AnnounceOnLanguageChange)
        {
            return;
        }

        // The layout was actually switched. The user is about to hear the
        // language, so restart the typing-idle clock.
        Interlocked.Exchange(ref _editableFocusedAt, Environment.TickCount64);

        Log.Write($"language switch: announcing {name} ({_settings.LanguageChangeSignal})");
        Announce(langId, _settings.LanguageChangeSignal);
    }

    private void HandleTypingAfterIdle(long idleMs)
    {
        if (NativeMethods.GetForegroundWindow() != _lastForeground)
        {
            return;
        }

        uint inputThread = KeyboardLayoutReader.GetInputThreadId();
        int langId = KeyboardLayoutReader.GetLanguageId(inputThread);
        if (langId <= 0)
        {
            return;
        }

        Remember(inputThread, langId);
        Log.Write($"typing: started after {idleMs / 1000}s idle, announcing {KeyboardLayoutReader.TryGetCulture(langId)?.EnglishName ?? $"LANGID 0x{langId:X4}"}");
        Announce(langId, null);
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _worker?.Join(TimeSpan.FromSeconds(1));

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
