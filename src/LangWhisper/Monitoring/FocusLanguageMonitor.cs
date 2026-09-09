using System.Threading;
using System.Threading.Tasks;
using LangWhisper.Audio;
using LangWhisper.Configuration;
using LangWhisper.Diagnostics;
using LangWhisper.Interop;
using Timer = System.Windows.Forms.Timer;

namespace LangWhisper.Monitoring;

/// <summary>
/// Watches for foreground-window and focus changes. When focus lands on an editable
/// text field it reads the active keyboard language and asks the <see cref="CuePlayer"/>
/// to announce it (subject to the "only on change" setting).
/// </summary>
public sealed class FocusLanguageMonitor : IDisposable
{
    private readonly AppSettings _settings;
    private readonly CuePlayer _player;

    // Keep a field reference so the delegate passed to SetWinEventHook is not collected.
    private readonly NativeMethods.WinEventProc _callback;
    private readonly Timer _debounce;

    private IntPtr _foregroundHook;
    private IntPtr _focusHook;
    private int _processing;
    private int _lastAnnouncedLangId = -1;
    private bool _started;

    public FocusLanguageMonitor(AppSettings settings, CuePlayer player)
    {
        _settings = settings;
        _player = player;
        _callback = OnWinEvent;
        _debounce = new Timer { Interval = Math.Max(30, _settings.DebounceMs) };
        _debounce.Tick += OnDebounceElapsed;
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

        _started = true;
    }

    /// <summary>Forget the last announced language so the next focus re-announces.</summary>
    public void ResetLastAnnounced() => Interlocked.Exchange(ref _lastAnnouncedLangId, -1);

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
        _ = Task.Run(Process);
    }

    private void Process()
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

            if (!EditableFocusDetector.IsEditable(foreground))
            {
                Log.Write("focus: not an editable field, ignoring");
                return;
            }

            int langId = KeyboardLayoutReader.GetForegroundLanguageId();
            if (langId <= 0)
            {
                Log.Write("focus: editable field but keyboard language unknown");
                return;
            }

            var culture = KeyboardLayoutReader.TryGetCulture(langId);
            string name = culture?.EnglishName ?? $"LANGID 0x{langId:X4}";

            if (_settings.AnnounceOnlyOnChange && langId == _lastAnnouncedLangId)
            {
                Log.Write($"focus: editable field, language {name} unchanged, staying quiet");
                return;
            }

            _lastAnnouncedLangId = langId;
            Log.Write($"focus: editable field, announcing {name}");
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

    public void Dispose()
    {
        _debounce.Stop();
        _debounce.Tick -= OnDebounceElapsed;
        _debounce.Dispose();

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
