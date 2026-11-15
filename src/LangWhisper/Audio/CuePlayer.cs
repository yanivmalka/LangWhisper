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
/// lives on one apartment.
/// <para>
/// The newest request always wins: a cue that is still waiting is dropped and one
/// that is still sounding is cut off, so what you hear is never a stale language and
/// a new cue never waits behind an old one. The voice is created and warmed up at
/// startup so the first whisper isn't delayed by loading the speech engine.
/// </para>
/// </summary>
public sealed class CuePlayer : IDisposable
{
    // SAPI SpeechVoiceSpeakFlags.
    private const int SpeakAsync = 1;
    private const int SpeakPurgeBeforeSpeak = 2;

    private readonly AppSettings _settings;
    private readonly BlockingCollection<Request> _queue = new(new ConcurrentQueue<Request>());
    private readonly Thread _worker;

    private dynamic? _voice; // SAPI.SpVoice, late-bound so there is no NuGet/interop dependency.
    private bool _voiceUnavailable;

    private long _latestRequest; // sequence number of the newest request
    private int _beepPlaying;

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
        Enqueue(culture, langId, modeOverride, null);
    }

    /// <summary>Plays a beep with explicit, possibly unsaved, sound settings (the settings dialog's preview).</summary>
    public void PreviewBeep(BeepSound sound, int frequencyHz, int volume)
    {
        Enqueue(null, 0, CueMode.Beep, new BeepSpec(sound, frequencyHz, volume));
    }

    private void Enqueue(CultureInfo? culture, int langId, CueMode? modeOverride, BeepSpec? beep)
    {
        if (_queue.IsAddingCompleted)
        {
            return;
        }

        long sequence = Interlocked.Increment(ref _latestRequest);

        // Cut off a beep that is still sounding so the worker gets to this request now.
        // Done before queueing, so it can only ever hit an older cue's beep.
        // (A whisper in progress is purged by the worker when it starts the new one.)
        if (Volatile.Read(ref _beepPlaying) == 1)
        {
            NativeMethods.PlaySoundMemory(IntPtr.Zero, IntPtr.Zero, 0);
        }

        try
        {
            _queue.Add(new Request(culture, langId, modeOverride, beep, sequence));
        }
        catch (InvalidOperationException)
        {
            // Shutting down.
        }
    }

    private void RunWorker()
    {
        WarmUpVoice();

        foreach (Request request in _queue.GetConsumingEnumerable())
        {
            // Superseded while it waited: only the newest language is worth announcing.
            if (request.Sequence != Interlocked.Read(ref _latestRequest))
            {
                continue;
            }

            try
            {
                StopSpeaking();

                CueMode mode = request.ModeOverride ?? _settings.Mode;
                string label = request.Culture?.EnglishName ?? $"LANGID 0x{request.LangId:X4}";

                if (mode is CueMode.Beep or CueMode.WhisperAndBeep)
                {
                    BeepSpec beep = request.Beep ?? ResolveBeep(request);
                    Log.Write($"cue: beep {beep.Sound} {beep.FrequencyHz} Hz for {label}");
                    PlayBeep(beep);
                }

                // A newer cue arrived during the beep; let it speak instead.
                if (request.Sequence != Interlocked.Read(ref _latestRequest))
                {
                    continue;
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

    private void PlayBeep(BeepSpec beep)
    {
        byte[] wav = ToneGenerator.CreateCueWav(beep.Sound, beep.FrequencyHz, _settings.BeepDurationMs, beep.Volume / 100.0);

        GCHandle handle = GCHandle.Alloc(wav, GCHandleType.Pinned);
        Volatile.Write(ref _beepPlaying, 1);
        try
        {
            NativeMethods.PlaySoundMemory(
                handle.AddrOfPinnedObject(),
                IntPtr.Zero,
                NativeMethods.SND_MEMORY | NativeMethods.SND_SYNC | NativeMethods.SND_NODEFAULT);
        }
        finally
        {
            Volatile.Write(ref _beepPlaying, 0);
            handle.Free();
        }
    }

    private BeepSpec ResolveBeep(Request request)
    {
        string? iso = request.Culture?.TwoLetterISOLanguageName;
        int seed = request.Culture?.LCID ?? request.LangId;
        return new BeepSpec(ResolveSound(_settings, iso), ResolveFrequency(_settings, iso, seed), _settings.BeepVolume);
    }

    public static BeepSound ResolveSound(AppSettings settings, string? iso) =>
        iso is not null && settings.BeepSounds.TryGetValue(iso, out BeepSound sound) ? sound : BeepSound.Tone;

    public static int ResolveFrequency(AppSettings settings, string? iso, int seed)
    {
        if (iso is not null && settings.BeepFrequencies.TryGetValue(iso, out int mapped))
        {
            return mapped;
        }

        // Deterministic fallback: spread unknown languages across a pleasant range.
        return 440 + Math.Abs(seed % 13) * 40; // 440-920 Hz
    }

    private bool EnsureVoice()
    {
        if (_voiceUnavailable)
        {
            return false;
        }

        if (_voice is null)
        {
            Type? type = Type.GetTypeFromProgID("SAPI.SpVoice");
            _voice = type is null ? null : Activator.CreateInstance(type);
            if (_voice is null)
            {
                _voiceUnavailable = true;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Creating the voice and its first utterance load the speech engine and open the
    /// audio device, which can take a noticeable moment. Pay that at startup, silently.
    /// </summary>
    private void WarmUpVoice()
    {
        try
        {
            if (EnsureVoice())
            {
                _voice!.Volume = 0;
                _voice.Speak(" ", 0);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"cue: voice warm-up failed: {ex.Message}");
        }
    }

    /// <summary>Cuts off a whisper that is still being spoken.</summary>
    private void StopSpeaking()
    {
        if (_voice is not null)
        {
            _voice.Speak(string.Empty, SpeakAsync | SpeakPurgeBeforeSpeak);
        }
    }

    private void Speak(Request request)
    {
        if (!EnsureVoice())
        {
            return;
        }

        _voice!.Volume = Math.Clamp(_settings.WhisperVolume, 0, 100);
        _voice.Rate = Math.Clamp(_settings.WhisperRate, -10, 10);

        // Asynchronous, so the worker is free to start (and this whisper to be cut
        // off by) the next cue straight away instead of waiting for speech to finish.
        _voice.Speak(SpokenName(request.Culture), SpeakAsync | SpeakPurgeBeforeSpeak);
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

    private readonly record struct Request(CultureInfo? Culture, int LangId, CueMode? ModeOverride, BeepSpec? Beep, long Sequence);

    private readonly record struct BeepSpec(BeepSound Sound, int FrequencyHz, int Volume);
}
