using System.Globalization;
using System.Text;
using System.Windows.Forms;
using LangWhisper.Audio;
using LangWhisper.Configuration;

namespace LangWhisper.Tray;

/// <summary>A compact, code-built settings window. Writes back to the shared <see cref="AppSettings"/>.</summary>
public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly CuePlayer _player;

    private readonly RadioButton _modeWhisper = new() { Text = "Whisper (spoken)", AutoSize = true };
    private readonly RadioButton _modeBeep = new() { Text = "Beep (tone)", AutoSize = true };
    private readonly RadioButton _modeBoth = new() { Text = "Whisper + beep", AutoSize = true };

    private readonly CheckBox _onWindowSwitch = new() { Text = "Announce when switching to another window", AutoSize = true };
    private readonly CheckBox _onLanguageChange = new() { Text = "Announce when the keyboard language is switched", AutoSize = true };
    private readonly CheckBox _onlyOnChange = new() { Text = "Same window: only when the language changed", AutoSize = true };
    private readonly CheckBox _nativeName = new() { Text = "Speak the native language name", AutoSize = true };

    private readonly RadioButton _langChangeWhisper = new() { Text = "Whisper", AutoSize = true };
    private readonly RadioButton _langChangeBeep = new() { Text = "Beep", AutoSize = true };
    private readonly RadioButton _langChangeBoth = new() { Text = "Whisper + beep", AutoSize = true };

    private readonly TrackBar _whisperVolume = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, Width = 260 };
    private readonly TrackBar _whisperRate = new() { Minimum = -10, Maximum = 10, TickFrequency = 2, Width = 260 };
    private readonly TrackBar _beepVolume = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, Width = 260 };

    private readonly TextBox _beepMap = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        Width = 260,
        Height = 90,
        Font = new System.Drawing.Font(FontFamily.GenericMonospace, 9f),
    };

    public SettingsForm(AppSettings settings, CuePlayer player)
    {
        _settings = settings;
        _player = player;

        Text = "LangWhisper settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new System.Drawing.Size(320, 520);

        BuildLayout();
        LoadFromSettings();
    }

    private void BuildLayout()
    {
        var root = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(12),
        };

        root.Controls.Add(SectionLabel("Signal"));
        root.Controls.Add(_modeWhisper);
        root.Controls.Add(_modeBeep);
        root.Controls.Add(_modeBoth);

        root.Controls.Add(SectionLabel("When to announce"));
        root.Controls.Add(_onWindowSwitch);
        root.Controls.Add(_onLanguageChange);
        root.Controls.Add(_onlyOnChange);

        root.Controls.Add(SectionLabel("Signal for a keyboard-language switch"));
        var langChangeGroup = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(0),
        };
        langChangeGroup.Controls.Add(_langChangeWhisper);
        langChangeGroup.Controls.Add(_langChangeBeep);
        langChangeGroup.Controls.Add(_langChangeBoth);
        root.Controls.Add(langChangeGroup);

        root.Controls.Add(SectionLabel("Behaviour"));
        root.Controls.Add(_nativeName);

        root.Controls.Add(SectionLabel("Whisper volume"));
        root.Controls.Add(_whisperVolume);
        root.Controls.Add(SectionLabel("Whisper rate (slow → fast)"));
        root.Controls.Add(_whisperRate);

        root.Controls.Add(SectionLabel("Beep volume"));
        root.Controls.Add(_beepVolume);
        root.Controls.Add(SectionLabel("Beep frequencies  (iso=Hz, one per line)"));
        root.Controls.Add(_beepMap);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(0, 12, 0, 0),
        };

        var test = new Button { Text = "Test", AutoSize = true };
        test.Click += (_, _) =>
        {
            ApplyToSettings();
            int langId = Monitoring.KeyboardLayoutReader.GetForegroundLanguageId();
            _player.Announce(Monitoring.KeyboardLayoutReader.TryGetCulture(langId) ?? CultureInfo.CurrentCulture, langId);
        };

        var ok = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.OK };
        ok.Click += (_, _) =>
        {
            ApplyToSettings();
            SettingsStore.Save(_settings);
            _settings.RaiseChanged();
            Close();
        };

        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        cancel.Click += (_, _) => Close();

        buttons.Controls.Add(test);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static Label SectionLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, 10, 0, 2),
        Font = new System.Drawing.Font(System.Drawing.SystemFonts.DefaultFont, System.Drawing.FontStyle.Bold),
    };

    private void LoadFromSettings()
    {
        _modeWhisper.Checked = _settings.Mode == CueMode.Whisper;
        _modeBeep.Checked = _settings.Mode == CueMode.Beep;
        _modeBoth.Checked = _settings.Mode == CueMode.WhisperAndBeep;

        _onWindowSwitch.Checked = _settings.AnnounceOnWindowSwitch;
        _onLanguageChange.Checked = _settings.AnnounceOnLanguageChange;
        _onlyOnChange.Checked = _settings.AnnounceOnlyOnChange;
        _nativeName.Checked = _settings.SpeakNativeName;

        _langChangeWhisper.Checked = _settings.LanguageChangeSignal == CueMode.Whisper;
        _langChangeBeep.Checked = _settings.LanguageChangeSignal == CueMode.Beep;
        _langChangeBoth.Checked = _settings.LanguageChangeSignal == CueMode.WhisperAndBeep;

        _whisperVolume.Value = Math.Clamp(_settings.WhisperVolume, 0, 100);
        _whisperRate.Value = Math.Clamp(_settings.WhisperRate, -10, 10);
        _beepVolume.Value = Math.Clamp(_settings.BeepVolume, 0, 100);

        var sb = new StringBuilder();
        foreach (var pair in _settings.BeepFrequencies)
        {
            sb.AppendLine($"{pair.Key}={pair.Value}");
        }

        _beepMap.Text = sb.ToString().TrimEnd();
    }

    private void ApplyToSettings()
    {
        _settings.Mode = _modeBeep.Checked ? CueMode.Beep
            : _modeBoth.Checked ? CueMode.WhisperAndBeep
            : CueMode.Whisper;

        _settings.LanguageChangeSignal = _langChangeWhisper.Checked ? CueMode.Whisper
            : _langChangeBoth.Checked ? CueMode.WhisperAndBeep
            : CueMode.Beep;

        _settings.AnnounceOnWindowSwitch = _onWindowSwitch.Checked;
        _settings.AnnounceOnLanguageChange = _onLanguageChange.Checked;
        _settings.AnnounceOnlyOnChange = _onlyOnChange.Checked;
        _settings.SpeakNativeName = _nativeName.Checked;

        _settings.WhisperVolume = _whisperVolume.Value;
        _settings.WhisperRate = _whisperRate.Value;
        _settings.BeepVolume = _beepVolume.Value;

        var parsed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in _beepMap.Lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = line[..eq].Trim();
            if (int.TryParse(line[(eq + 1)..].Trim(), out int hz) && hz is > 50 and < 12000)
            {
                parsed[key] = hz;
            }
        }

        if (parsed.Count > 0)
        {
            _settings.BeepFrequencies = parsed;
        }
    }
}
