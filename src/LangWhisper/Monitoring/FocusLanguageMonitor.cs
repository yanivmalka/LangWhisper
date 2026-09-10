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

    private IntPtr _foregroundHook;
    private IntPtr _focusHook;
    private int _processing;

    private IntPtr _lastForeground;
    private int _lastSeenLangId = -1;      // last language observed in the foreground, announced or not
    private int _lastAnnouncedLangId = -1; // last language actually spoken/beeped
    private bool _started;

    public FocusLanguageMonitor(AppSettings settings, CuePlayer player)
    {
        _settings = settings;
        _player = player;
        _callback = OnWinEvent;

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

        _languagePoll.Start();
        _started = true;
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
        _ = Task.Run(ProcessFocusEvent);
    }

    private void OnLanguagePollTick(object? sender, EventArgs e)
    {
        if (!_settings.Enabled || !_settings.AnnounceOnLanguageChange)
        {
            return;
        }

        // A focus/window event is mid-flight; it will pick up the current layout itself.
        if (Volatile.Read(ref _processing) == 1)
        {
            return;
        }

        _ = Task.Run(ProcessLanguagePoll);
    }

    /// <summary>Handles a foreground-window or focus change.</summary>
    private void ProcessFocusEvent()
    {
        if (Interlocked.Exchange(ref _processing, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_settings.Enabled)
            {
                return;
            }

            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero)
            {
                return;
            }

            bool windowChanged = foreground != _lastForeground;
            bool editable = EditableFocusDetector.IsEditable(foreground);

            int langId = ReadSettledLanguageId();

            // Track state regardless of whether we announce, so later triggers compare fairly.
            _lastForeground = foreground;
            if (langId > 0)
            {
                _lastSeenLangId = langId;
            }

            if (!editable)
            {
                Log.Write("focus: not an editable field, ignoring");
                return;
            }

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
    /// moment later, so an immediate read frequently returns the language of the
    /// window the user just left. We sample repeatedly and only trust a value
    /// once two consecutive reads agree (or we run out of attempts, in which case
    /// the freshest read wins).
    /// </summary>
    private static int ReadSettledLanguageId()
    {
        const int maxAttempts = 5;
        const int gapMs = 120;

        int previous = KeyboardLayoutReader.GetForegroundLanguageId();

        for (int attempt = 1; attempt < maxAttempts; attempt++)
        {
            Thread.Sleep(gapMs);
            int current = KeyboardLayoutReader.GetForegroundLanguageId();

            if (current > 0 && current == previous)
            {
                return current;
            }

            previous = current;
        }

        return previous;
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
    }
}
