using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using LangWhisper.Audio;
using LangWhisper.Configuration;
using LangWhisper.Monitoring;
using LangWhisper.Startup;

namespace LangWhisper.Tray;

/// <summary>Owns the tray icon, the focus monitor and the cue player for the app's lifetime.</summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly CuePlayer _player;
    private readonly FocusLanguageMonitor _monitor;
    private readonly NotifyIcon _tray;

    private SettingsForm? _settingsForm;

    public TrayApplicationContext()
    {
        _settings = SettingsStore.Load();
        _settings.Changed += OnSettingsChanged;

        _player = new CuePlayer(_settings);
        _monitor = new FocusLanguageMonitor(_settings, _player);

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "LangWhisper — keyboard language cue",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowSettings();

        RebuildMenu();
        _monitor.Start();
    }

    private void OnSettingsChanged()
    {
        SettingsStore.Save(_settings);
        _monitor.ResetLastAnnounced();
        if (_tray.ContextMenuStrip is not null)
        {
            RebuildMenu();
        }
    }

    private void RebuildMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(Check("Enabled", _settings.Enabled, () =>
        {
            _settings.Enabled = !_settings.Enabled;
            _settings.RaiseChanged();
        }));

        menu.Items.Add(new ToolStripSeparator());

        var signal = new ToolStripMenuItem("Signal (window / focus)");
        signal.DropDownItems.Add(Radio("Whisper", _settings.Mode == CueMode.Whisper, () => SetMode(CueMode.Whisper)));
        signal.DropDownItems.Add(Radio("Beep", _settings.Mode == CueMode.Beep, () => SetMode(CueMode.Beep)));
        signal.DropDownItems.Add(Radio("Whisper + beep", _settings.Mode == CueMode.WhisperAndBeep, () => SetMode(CueMode.WhisperAndBeep)));
        menu.Items.Add(signal);

        var langSignal = new ToolStripMenuItem("Signal (language switch)");
        langSignal.DropDownItems.Add(Radio("Whisper", _settings.LanguageChangeSignal == CueMode.Whisper, () => SetLanguageSignal(CueMode.Whisper)));
        langSignal.DropDownItems.Add(Radio("Beep", _settings.LanguageChangeSignal == CueMode.Beep, () => SetLanguageSignal(CueMode.Beep)));
        langSignal.DropDownItems.Add(Radio("Whisper + beep", _settings.LanguageChangeSignal == CueMode.WhisperAndBeep, () => SetLanguageSignal(CueMode.WhisperAndBeep)));
        menu.Items.Add(langSignal);

        menu.Items.Add(Check("Announce when switching windows", _settings.AnnounceOnWindowSwitch, () =>
        {
            _settings.AnnounceOnWindowSwitch = !_settings.AnnounceOnWindowSwitch;
            _settings.RaiseChanged();
        }));

        menu.Items.Add(Check("Announce when keyboard language changes", _settings.AnnounceOnLanguageChange, () =>
        {
            _settings.AnnounceOnLanguageChange = !_settings.AnnounceOnLanguageChange;
            _settings.RaiseChanged();
        }));

        menu.Items.Add(Check("Same window: only when language changed", _settings.AnnounceOnlyOnChange, () =>
        {
            _settings.AnnounceOnlyOnChange = !_settings.AnnounceOnlyOnChange;
            _settings.RaiseChanged();
        }));

        menu.Items.Add(Check("Speak native language name", _settings.SpeakNativeName, () =>
        {
            _settings.SpeakNativeName = !_settings.SpeakNativeName;
            _settings.RaiseChanged();
        }));

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(Item("Test cue", () =>
        {
            int langId = KeyboardLayoutReader.GetForegroundLanguageId();
            _player.Announce(KeyboardLayoutReader.TryGetCulture(langId) ?? CultureInfo.CurrentCulture, langId);
        }));

        menu.Items.Add(Item("Settings…", ShowSettings));

        menu.Items.Add(Check("Start with Windows", SafeIsAutoStart(), () =>
        {
            try
            {
                AutoStart.SetEnabled(!AutoStart.IsEnabled);
            }
            catch (Exception ex)
            {
                ShowError("Could not update the startup entry.", ex);
            }

            RebuildMenu();
        }));

        menu.Items.Add(Item("Open config folder", () =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", SettingsStore.Directory) { UseShellExecute = true });
            }
            catch
            {
                // ignore
            }
        }));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Exit", ExitApp));

        ContextMenuStrip? old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    private void SetMode(CueMode mode)
    {
        _settings.Mode = mode;
        _settings.RaiseChanged();
    }

    private void SetLanguageSignal(CueMode mode)
    {
        _settings.LanguageChangeSignal = mode;
        _settings.RaiseChanged();
    }

    private static bool SafeIsAutoStart()
    {
        try
        {
            return AutoStart.IsEnabled;
        }
        catch
        {
            return false;
        }
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_settings, _player);
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void ShowError(string message, Exception ex) =>
        MessageBox.Show($"{message}\n\n{ex.Message}", "LangWhisper", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void ExitApp()
    {
        _tray.Visible = false;
        _monitor.Dispose();
        _player.Dispose();
        _tray.Dispose();
        _settingsForm?.Close();
        ExitThread();
    }

    private static ToolStripMenuItem Item(string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => onClick();
        return item;
    }

    private static ToolStripMenuItem Check(string text, bool isChecked, Action onClick)
    {
        var item = Item(text, onClick);
        item.CheckOnClick = false;
        item.Checked = isChecked;
        return item;
    }

    private static ToolStripMenuItem Radio(string text, bool isChecked, Action onClick)
    {
        var item = Item(text, onClick);
        item.Checked = isChecked;
        return item;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _monitor.Dispose();
            _player.Dispose();
            _tray.Dispose();
        }

        base.Dispose(disposing);
    }
}
