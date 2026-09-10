using System.Collections.Generic;

namespace LangWhisper.Configuration;

/// <summary>How the active keyboard language is signalled when a text field gains focus.</summary>
public enum CueMode
{
    Whisper,
    Beep,
    WhisperAndBeep,
}

/// <summary>
/// User-configurable settings. Persisted as JSON under
/// <c>%APPDATA%\LangWhisper\settings.json</c>. A single instance of this object is
/// shared live between the tray menu, the settings form, the focus monitor and the
/// cue player, so mutating a property takes effect immediately; call
/// <see cref="RaiseChanged"/> afterwards to trigger a save and menu refresh.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Master on/off switch. When false nothing is announced.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whisper, beep, or both. Used for window switches and in-window focus changes.</summary>
    public CueMode Mode { get; set; } = CueMode.Whisper;

    /// <summary>
    /// Signal used specifically when the keyboard layout is switched (Alt+Shift,
    /// Win+Space). Defaults to a beep so a deliberate layout toggle gets a quick,
    /// non-spoken confirmation.
    /// </summary>
    public CueMode LanguageChangeSignal { get; set; } = CueMode.Beep;

    /// <summary>
    /// For a focus change *within the same window* (clicking from one text box to
    /// another): when true (default) the cue plays only if the language differs
    /// from the one last announced; when false it plays on every text-field focus.
    /// Window switches and keyboard-layout changes are governed by the two options
    /// below, not by this one.
    /// </summary>
    public bool AnnounceOnlyOnChange { get; set; } = true;

    /// <summary>
    /// Announce the language whenever you move to a different window that has a
    /// text field, even if the language has not changed. Default on.
    /// </summary>
    public bool AnnounceOnWindowSwitch { get; set; } = true;

    /// <summary>
    /// Announce the new language whenever the keyboard layout is switched
    /// (Alt+Shift, Win+Space, the language bar), regardless of focus. Default on.
    /// </summary>
    public bool AnnounceOnLanguageChange { get; set; } = true;

    /// <summary>
    /// How often (ms) to check whether the keyboard layout changed. Only used when
    /// <see cref="AnnounceOnLanguageChange"/> is on.
    /// </summary>
    public int LanguagePollMs { get; set; } = 350;

    /// <summary>Speak the language's native name ("עברית") instead of its English name ("Hebrew").</summary>
    public bool SpeakNativeName { get; set; }

    /// <summary>SAPI volume for the spoken whisper, 0-100.</summary>
    public int WhisperVolume { get; set; } = 80;

    /// <summary>SAPI speaking rate for the whisper, -10 (slow) to 10 (fast).</summary>
    public int WhisperRate { get; set; } = 2;

    /// <summary>Amplitude of the beep tone, 0-100.</summary>
    public int BeepVolume { get; set; } = 75;

    /// <summary>Length of the beep tone in milliseconds.</summary>
    public int BeepDurationMs { get; set; } = 320;

    /// <summary>
    /// Delay (ms) after the last focus event before the language is read and the cue
    /// played. Coalesces the burst of events Windows fires during a window switch and
    /// gives the foreground window's remembered keyboard layout time to be applied
    /// (the read is then settle-sampled on top of this).
    /// </summary>
    public int DebounceMs { get; set; } = 250;

    /// <summary>
    /// Beep frequency (Hz) per two-letter ISO language code. Languages not listed here
    /// get a deterministic fallback pitch derived from their locale id.
    /// </summary>
    public Dictionary<string, int> BeepFrequencies { get; set; } = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = 880,
        ["he"] = 587,
        ["ru"] = 659,
        ["ar"] = 494,
        ["fr"] = 784,
        ["de"] = 698,
        ["es"] = 740,
        ["it"] = 831,
    };

    // Events are not serialised by System.Text.Json.
    public event Action? Changed;

    public void RaiseChanged() => Changed?.Invoke();
}
