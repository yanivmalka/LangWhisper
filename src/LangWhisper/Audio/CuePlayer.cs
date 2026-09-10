using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using LangWhisper.Configuration;
using LangWhisper.Diagnostics;
using LangWhisper.Interop;

namespace LangWhisper.Audio;

/// <summary>
/// Plays the audible cue for a language. All playback happens on a single dedicated
/// STA worker thread so beeps and whispers never overlap and the SAPI voice object
/// lives on one apartment. Requests are queued; if the user is switching windows
/// fast the queue is capped so cues don't pile up.
/// </summary>
public sealed class CuePlayer : IDisposable
{
    private const int MaxQueued = 2;

    private readonly AppSettings _settings;
    private readonly BlockingCollection<Request> _queue = new(new ConcurrentQueue<Request>());
    private readonly Thread _worker;

    private dynamic? _voice; // SAPI.SpVoice, late-bound so there is no NuGet/interop dependency.
    private bool _voiceUnavailable;

    public CuePlayer(AppSettings settings)
    {
        _settings = settings;
        _worker = new Thread(RunWorker)
        {
            IsBackground = true,
            Name = "LangWhisper.Audio",
        };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    /// <summary>
    /// Queue an announcement for the given culture (may be null if the locale is
    /// unknown). Pass <paramref name="modeOverride"/> to force a specific signal
    /// instead of the configured default <see cref="AppSettings.Mode"/>.
    /// </summary>
    public void Announce(CultureInfo? culture, int langId, CueMode? modeOverride = null)
    {
        if (_queue.IsAddingCompleted || _queue.Count > MaxQueued)
        {
            return;
        }

        _queue.Add(new Request(culture, langId, modeOverride));
    }

    private void RunWorker()
    {
        foreach (Request request in _queue.GetConsumingEnumerable())
        {
            try
            {
                CueMode mode = request.ModeOverride ?? _settings.Mode;
                string label = request.Culture?.EnglishName ?? $"LANGID 0x{request.LangId:X4}";

                if (mode is CueMode.Beep or CueMode.WhisperAndBeep)
                {
                    double hz = ResolveFrequency(request);
                    Log.Write($"cue: beep {hz:0} Hz for {label}");
                    PlayBeep(request);
                }

                if (mode is CueMode.Whisper or CueMode.WhisperAndBeep)
                {
                    Log.Write($"cue: whisper \"{SpokenName(request.Culture)}\"");
                    Speak(request);
                }
            }
            catch (Exception ex)
            {
                // Audio glitches are never fatal.
                Log.Write($"cue: playback error: {ex.Message}");
            }
        }
    }

    private void PlayBeep(Request request)
    {
        double frequency = ResolveFrequency(request);
        byte[] wav = ToneGenerator.CreateSineWav(frequency, _settings.BeepDurationMs, _settings.BeepVolume / 100.0);

        GCHandle handle = GCHandle.Alloc(wav, GCHandleType.Pinned);
        try
        {
            NativeMethods.PlaySoundMemory(
                handle.AddrOfPinnedObject(),
                IntPtr.Zero,
                NativeMethods.SND_MEMORY | NativeMethods.SND_SYNC | NativeMethods.SND_NODEFAULT);
        }
        finally
        {
            handle.Free();
        }
    }

    private double ResolveFrequency(Request request)
    {
        string? iso = request.Culture?.TwoLetterISOLanguageName;
        if (iso is not null && _settings.BeepFrequencies.TryGetValue(iso, out int mapped))
        {
            return mapped;
        }

        // Deterministic fallback: spread unknown languages across a pleasant range.
        int seed = request.Culture?.LCID ?? request.LangId;
        return 440 + Math.Abs(seed % 13) * 40; // 440-920 Hz
    }

    private void Speak(Request request)
    {
        if (_voiceUnavailable)
        {
            return;
        }

        if (_voice is null)
        {
            Type? type = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (type is null)
            {
                _voiceUnavailable = true;
                return;
            }

            _voice = Activator.CreateInstance(type);
            if (_voice is null)
            {
                _voiceUnavailable = true;
                return;
            }
        }

        _voice.Volume = Math.Clamp(_settings.WhisperVolume, 0, 100);
        _voice.Rate = Math.Clamp(_settings.WhisperRate, -10, 10);

        // 0 == SVSFDefault: speak synchronously so the worker serialises cues.
        _voice.Speak(SpokenName(request.Culture), 0);
    }

    private string SpokenName(CultureInfo? culture)
    {
        if (culture is null)
        {
            return "Unknown layout";
        }

        string name = _settings.SpeakNativeName ? culture.NativeName : culture.EnglishName;
        int paren = name.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? name[..paren] : name;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(1));

        if (_voice is not null)
        {
            try
            {
                Marshal.FinalReleaseComObject(_voice);
            }
            catch
            {
                // ignore
            }

            _voice = null;
        }

        _queue.Dispose();
    }

    private readonly record struct Request(CultureInfo? Culture, int LangId, CueMode? ModeOverride);
}
