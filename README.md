# LangWhisper

A tiny Windows 11 tray assistant. When you move focus to a window and land on a
**text-editing cursor** (an editable field with a caret), it tells you which
**keyboard input language** is currently active — either by quietly *whispering*
the language name or by playing a *soft beep* whose pitch is mapped to that
language.

By default it only speaks up when the language differs from the one it last
announced, so it stays quiet while you work in a single language.

## Why

If you juggle two or more keyboard layouts (say English + Hebrew), you have
probably typed a whole sentence into the wrong one. LangWhisper gives you a
half-second cue the moment your cursor enters a text box, before you start
typing.

## How it works

| Concern | Mechanism |
| --- | --- |
| Notice focus/window changes | `SetWinEventHook` for `EVENT_SYSTEM_FOREGROUND` and `EVENT_OBJECT_FOCUS` |
| Is there a typing cursor? | `GetGUIThreadInfo` caret check, then UI Automation (`ControlType.Edit` / `Document` / `TextPattern`, honouring read-only) |
| Which language? | `GetKeyboardLayout` of the foreground thread → LANGID → `CultureInfo` |
| Whisper | Late-bound `SAPI.SpVoice` (no NuGet dependency) |
| Beep | Sine tone synthesised in memory, played via `winmm` `PlaySound` |

Events are debounced (~150 ms) so the burst Windows fires during a window switch
collapses into a single announcement.

## Build & run

Requires the .NET 10 SDK (Windows).

```powershell
dotnet build LangWhisper.sln -c Release
dotnet run --project src/LangWhisper/LangWhisper.csproj
```

Single-file self-contained publish:

```powershell
dotnet publish src/LangWhisper/LangWhisper.csproj -c Release -r win-x64 `
  --self-contained -p:PublishSingleFile=true -o publish
```

## Using it

The app lives in the system tray (no window). Right-click the icon for:

- **Enabled** — master switch
- **Signal** — Whisper / Beep / Whisper + beep
- **Only announce on language change**
- **Speak native language name** (`עברית` instead of `Hebrew`)
- **Test cue**
- **Settings…** — volumes, whisper rate, per-language beep frequencies
- **Start with Windows**
- **Open config folder** — `%APPDATA%\LangWhisper\settings.json`

## Known limitations

- Runs unelevated, so it cannot observe focus inside **elevated (admin)**
  windows. Running LangWhisper as admin fixes that but is not recommended.
- A few custom-drawn editors expose no caret and no UI Automation text pattern;
  those focus events are missed.
- The whisper uses whatever SAPI voice Windows has installed for that language;
  install the matching language pack for best pronunciation.

## Project layout

```
src/LangWhisper/
  Program.cs                     entry point + single-instance guard
  Interop/NativeMethods.cs       P/Invoke surface
  Monitoring/
    FocusLanguageMonitor.cs      hooks, debounce, orchestration
    EditableFocusDetector.cs     "is this a typing cursor?" logic
    KeyboardLayoutReader.cs      LANGID → CultureInfo
  Audio/
    CuePlayer.cs                 STA worker: queue → beep and/or whisper
    ToneGenerator.cs             in-memory WAV sine tone
  Configuration/                 AppSettings + JSON store
  Startup/AutoStart.cs           HKCU Run key
  Tray/
    TrayApplicationContext.cs    NotifyIcon + menu
    SettingsForm.cs              settings window
```
