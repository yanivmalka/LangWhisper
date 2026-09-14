using System.Globalization;
using System.Windows.Forms;
using LangWhisper.Audio;
using LangWhisper.Configuration;
using LangWhisper.Monitoring;

namespace LangWhisper.Tray;

/// <summary>A compact, code-built settings window. Writes back to the shared <see cref="AppSettings"/>.</summary>
public sealed class SettingsForm : Form
{
    private const string DefaultDescription = "Point at an option to see what it does.";
    private const int MaxVisibleLanguageRows = 5;

    private readonly AppSettings _settings;
    private readonly CuePlayer _player;
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 15000, InitialDelay = 400 };
    private readonly Label _description = new() { AutoSize = false, Text = DefaultDescription };
    private readonly List<LanguageBeepRow> _beepRows = new();

    private readonly RadioButton _modeWhisper = new() { Text = "Whisper (spoken)", AutoSize = true };
    private readonly RadioButton _modeBeep = new() { Text = "Beep (sound)", AutoSize = true };
    private readonly RadioButton _modeBoth = new() { Text = "Whisper + beep", AutoSize = true };

    private readonly CheckBox _onWindowSwitch = new() { Text = "Announce when switching to another window", AutoSize = true };
    private readonly CheckBox _onLanguageChange = new() { Text = "Announce when the keyboard language is switched", AutoSize = true };
    private readonly CheckBox _onlyOnChange = new() { Text = "Same window: only when the language changed", AutoSize = true };
    private readonly CheckBox _onTypingAfterIdle = new() { Text = "Announce when I start typing more than", AutoSize = true, Margin = new Padding(3, 6, 0, 3) };
    private readonly NumericUpDown _typingIdleSeconds = new() { Minimum = 1, Maximum = 600, Width = 56 };
    private readonly CheckBox _nativeName = new() { Text = "Speak the native language name", AutoSize = true };

    private readonly RadioButton _langChangeWhisper = new() { Text = "Whisper", AutoSize = true };
    private readonly RadioButton _langChangeBeep = new() { Text = "Beep", AutoSize = true };
    private readonly RadioButton _langChangeBoth = new() { Text = "Whisper + beep", AutoSize = true };

    private readonly TrackBar _whisperVolume = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, Width = 260 };
    private readonly TrackBar _whisperRate = new() { Minimum = -10, Maximum = 10, TickFrequency = 2, Width = 260 };
    private readonly TrackBar _beepVolume = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, Width = 260 };

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
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        BuildLayout();
        LoadFromSettings();
    }

    private void BuildLayout()
    {
        var root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
        };

        TabControl tabs = BuildTabs();
        root.Controls.Add(tabs);

        _description.Size = new System.Drawing.Size(tabs.Width, _description.Font.Height * 3 + 8);
        _description.Margin = new Padding(0, 8, 0, 0);
        _description.Padding = new Padding(4);
        _description.ForeColor = System.Drawing.SystemColors.GrayText;
        root.Controls.Add(_description);

        root.Controls.Add(BuildButtons());

        Controls.Add(root);
    }

    private TabControl BuildTabs()
    {
        var tabs = new TabControl();

        var general = NewTabPage("General");
        var generalContent = NewSectionPanel();
        generalContent.Controls.Add(SectionLabel("Signal (window / focus / typing)"));
        generalContent.Controls.Add(_modeWhisper);
        generalContent.Controls.Add(_modeBeep);
        generalContent.Controls.Add(_modeBoth);
        generalContent.Controls.Add(SectionLabel("Behaviour"));
        generalContent.Controls.Add(_nativeName);
        general.Controls.Add(generalContent);

        Describe(_modeWhisper, "Quietly speak the language name (for example \"Hebrew\") when you switch windows, click into a text field, or start typing after being idle.");
        Describe(_modeBeep, "Play a short sound instead of speaking. Each language can have its own sound and pitch on the Beep tab.");
        Describe(_modeBoth, "Play the language's beep, then whisper its name.");
        Describe(_nativeName, "Say the name in the language itself (\"עברית\") instead of in English (\"Hebrew\"). Needs a Windows voice for that language.");

        var triggers = NewTabPage("Triggers");
        var triggersContent = NewSectionPanel();
        triggersContent.Controls.Add(SectionLabel("When to announce"));
        triggersContent.Controls.Add(_onWindowSwitch);
        triggersContent.Controls.Add(_onLanguageChange);
        triggersContent.Controls.Add(_onlyOnChange);

        var typingRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(0),
        };
        var typingSuffix = new Label { Text = "seconds after focus", AutoSize = true, Margin = new Padding(3, 6, 0, 3) };
        typingRow.Controls.Add(_onTypingAfterIdle);
        typingRow.Controls.Add(_typingIdleSeconds);
        typingRow.Controls.Add(typingSuffix);
        _onTypingAfterIdle.CheckedChanged += (_, _) => _typingIdleSeconds.Enabled = _onTypingAfterIdle.Checked;
        triggersContent.Controls.Add(typingRow);

        triggersContent.Controls.Add(SectionLabel("Signal for a keyboard-language switch"));
        triggersContent.Controls.Add(_langChangeWhisper);
        triggersContent.Controls.Add(_langChangeBeep);
        triggersContent.Controls.Add(_langChangeBoth);
        triggers.Controls.Add(triggersContent);

        Describe(_onWindowSwitch, "Announce every time you switch to a window and land in a text field, even if the language is the same as before.");
        Describe(_onLanguageChange, "Announce when you change the keyboard layout yourself (Alt+Shift, Win+Space or the language bar).");
        Describe(_onlyOnChange, "When you move between text fields inside the same window, stay quiet unless the language differs from the last one announced.");
        const string typingText = "If a text field has been focused this long without typing, announce the language on your first keystroke. Keys are only timed, never recorded.";
        Describe(_onTypingAfterIdle, typingText);
        Describe(_typingIdleSeconds, typingText);
        Describe(typingSuffix, typingText);
        const string langChangeText = "Signal used when you switch the keyboard layout yourself. A beep confirms quickly without talking over your work.";
        Describe(_langChangeWhisper, langChangeText);
        Describe(_langChangeBeep, langChangeText);
        Describe(_langChangeBoth, langChangeText);

        var voice = NewTabPage("Voice");
        var voiceContent = NewSectionPanel();
        voiceContent.Controls.Add(SectionLabel("Whisper volume"));
        voiceContent.Controls.Add(_whisperVolume);
        voiceContent.Controls.Add(SectionLabel("Whisper rate (slow → fast)"));
        voiceContent.Controls.Add(_whisperRate);
        voice.Controls.Add(voiceContent);

        Describe(_whisperVolume, "How loud the spoken whisper is.");
        Describe(_whisperRate, "How fast the whisper speaks: left is slower, right is faster.");

        var beep = NewTabPage("Beep");
        var beepContent = NewSectionPanel();
        beepContent.Controls.Add(SectionLabel("Beep volume"));
        beepContent.Controls.Add(_beepVolume);
        beepContent.Controls.Add(SectionLabel("Sound for each language"));
        beepContent.Controls.Add(BuildLanguageBeepTable());
        beep.Controls.Add(beepContent);

        Describe(_beepVolume, "How loud beeps are. The ▶ buttons below preview at this volume.");

        tabs.TabPages.Add(general);
        tabs.TabPages.Add(triggers);
        tabs.TabPages.Add(voice);
        tabs.TabPages.Add(beep);

        // Size the strip to comfortably fit the largest tab's content so no
        // individual tab ever needs its own scrollbar.
        int maxWidth = 0;
        int maxHeight = 0;
        foreach (TabPage page in tabs.TabPages)
        {
            var preferred = page.Controls[0].GetPreferredSize(System.Drawing.Size.Empty);
            maxWidth = Math.Max(maxWidth, preferred.Width);
            maxHeight = Math.Max(maxHeight, preferred.Height);
        }

        foreach (TabPage page in tabs.TabPages)
        {
            page.Controls[0].Dock = DockStyle.Fill;
        }

        tabs.Size = new System.Drawing.Size(maxWidth + 8, maxHeight + tabs.ItemSize.Height + 24);
        return tabs;
    }

    private Control BuildLanguageBeepTable()
    {
        IReadOnlyList<CultureInfo> cultures = KeyboardLayoutReader.GetInstalledCultures();
        if (cultures.Count == 0)
        {
            return new Label { Text = "No keyboard languages found.", AutoSize = true };
        }

        var table = new TableLayoutPanel
        {
            ColumnCount = 5,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 2, 0, 0),
        };

        const string soundText = "Tone: one steady beep. Double beep: two quick beeps. Chime: two rising notes. Give each language a different sound to tell them apart.";
        const string pitchText = "Pitch of the sound in hertz. Higher numbers sound higher.";
        const string playText = "Hear this language's sound with the current (unsaved) settings.";

        foreach (CultureInfo culture in cultures)
        {
            string iso = culture.TwoLetterISOLanguageName;

            var name = new Label { Text = ShortName(culture), AutoSize = true, Margin = new Padding(0, 7, 8, 0) };

            var sound = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
            sound.Items.AddRange(new object[] { "Tone", "Double beep", "Chime" });
            sound.SelectedIndex = (int)CuePlayer.ResolveSound(_settings, iso);

            var pitch = new NumericUpDown
            {
                Minimum = 100,
                Maximum = 3000,
                Increment = 20,
                Width = 64,
                Value = Math.Clamp(CuePlayer.ResolveFrequency(_settings, iso, culture.LCID), 100, 3000),
            };

            var hz = new Label { Text = "Hz", AutoSize = true, Margin = new Padding(2, 7, 6, 0) };

            var play = new Button { Text = "▶", Width = 32, Height = pitch.Height + 2 };
            play.Click += (_, _) => _player.PreviewBeep((BeepSound)sound.SelectedIndex, (int)pitch.Value, _beepVolume.Value);

            Describe(name, soundText);
            Describe(sound, soundText);
            Describe(pitch, pitchText);
            Describe(hz, pitchText);
            Describe(play, playText);

            table.Controls.Add(name);
            table.Controls.Add(sound);
            table.Controls.Add(pitch);
            table.Controls.Add(hz);
            table.Controls.Add(play);

            _beepRows.Add(new LanguageBeepRow(iso, sound, pitch));
        }

        if (cultures.Count <= MaxVisibleLanguageRows)
        {
            return table;
        }

        // Rare: many languages installed. Keep the tab compact and let just this list scroll.
        int rowHeight = table.GetPreferredSize(System.Drawing.Size.Empty).Height / cultures.Count;
        var scroller = new Panel
        {
            AutoScroll = true,
            Size = new System.Drawing.Size(
                table.GetPreferredSize(System.Drawing.Size.Empty).Width + SystemInformation.VerticalScrollBarWidth,
                rowHeight * MaxVisibleLanguageRows),
        };
        scroller.Controls.Add(table);
        return scroller;
    }

    private FlowLayoutPanel BuildButtons()
    {
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
        };

        var test = new Button { Text = "Test", AutoSize = true };
        test.Click += (_, _) =>
        {
            ApplyToSettings();
            int langId = KeyboardLayoutReader.GetForegroundLanguageId();
            _player.Announce(KeyboardLayoutReader.TryGetCulture(langId) ?? CultureInfo.CurrentCulture, langId);
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

        Describe(test, "Announce the current keyboard language using the settings as they are now in this window.");
        Describe(ok, "Save the settings and close.");
        Describe(cancel, "Close without saving.");

        buttons.Controls.Add(test);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;
        return buttons;
    }

    private void Describe(Control control, string text)
    {
        _toolTip.SetToolTip(control, text);
        control.MouseEnter += (_, _) => _description.Text = text;
        control.Enter += (_, _) => _description.Text = text;
    }

    private static string ShortName(CultureInfo culture)
    {
        string name = culture.EnglishName;
        int paren = name.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? name[..paren] : name;
    }

    private static TabPage NewTabPage(string text) => new(text) { Padding = new Padding(0) };

    private static FlowLayoutPanel NewSectionPanel() => new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(10),
    };

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
        _onTypingAfterIdle.Checked = _settings.AnnounceOnTypingAfterIdle;
        _typingIdleSeconds.Value = Math.Clamp(_settings.TypingIdleSeconds, 1, 600);
        _typingIdleSeconds.Enabled = _onTypingAfterIdle.Checked;

        _langChangeWhisper.Checked = _settings.LanguageChangeSignal == CueMode.Whisper;
        _langChangeBeep.Checked = _settings.LanguageChangeSignal == CueMode.Beep;
        _langChangeBoth.Checked = _settings.LanguageChangeSignal == CueMode.WhisperAndBeep;

        _whisperVolume.Value = Math.Clamp(_settings.WhisperVolume, 0, 100);
        _whisperRate.Value = Math.Clamp(_settings.WhisperRate, -10, 10);
        _beepVolume.Value = Math.Clamp(_settings.BeepVolume, 0, 100);
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
        _settings.AnnounceOnTypingAfterIdle = _onTypingAfterIdle.Checked;
        _settings.TypingIdleSeconds = (int)_typingIdleSeconds.Value;

        _settings.WhisperVolume = _whisperVolume.Value;
        _settings.WhisperRate = _whisperRate.Value;
        _settings.BeepVolume = _beepVolume.Value;

        foreach (LanguageBeepRow row in _beepRows)
        {
            _settings.BeepSounds[row.Iso] = (BeepSound)row.Sound.SelectedIndex;
            _settings.BeepFrequencies[row.Iso] = (int)row.Pitch.Value;
        }
    }

    private sealed record LanguageBeepRow(string Iso, ComboBox Sound, NumericUpDown Pitch);
}
