# LangWhisper

A tiny Windows 11 tray assistant. When you move focus to a window and land on a
**text-editing cursor** (an editable field with a caret), it tells you which
**keyboard input language** is currently active — either by quietly *whispering*
the language name or by playing a *soft beep* whose pitch is mapped to that
language.

It announces on three triggers:

- **Switching to another window** that has a text field — always, even if the
  language did not change (*whisper* by default).
- **Focusing a text field within the same window** — only if the language
  differs from the last announced one (configurable).
- **Switching the keyboard layout** yourself (Alt+Shift, Win+Space, language
  bar) — a *beep* by default, so a deliberate toggle gets a quick confirmation.
- **Starting to type after an idle focus** (optional, off by default) — the
  first keystroke in a text field that has been focused for more than X seconds
  re-announces the language, in case you forgot it while away.

Each trigger can be turned off, and the window/focus signal and the
language-switch signal are chosen independently (whisper / beep / both).

## Why

If you juggle two or more keyboard layouts (say English + Hebrew), you have
probably typed a whole sentence into the wrong one. LangWhisper gives you a
half-second cue the moment your cursor enters a text box, before you start
typing.

## How it works

| Concern | Mechanism |
| --- | --- |
| Notice focus/window changes | `SetWinEventHook` for `EVENT_SYSTEM_FOREGROUND` and `EVENT_OBJECT_FOCUS` |
| Notice typing after an idle focus | `WH_KEYBOARD_LL` hook, installed only while that option is on |
| Notice a keyboard-layout switch | light poll (~350 ms) of `GetKeyboardLayout`; Windows raises no accessible event for it |
| Is there a typing cursor? | `GetGUIThreadInfo` caret check, then UI Automation (`ControlType.Edit` / `Document` / `TextPattern`, honouring read-only) |
| Which language? | `GetKeyboardLayout` of the thread owning the focused control (`GetGUIThreadInfo`) → LANGID → `CultureInfo`. Not the top-level window's thread: Windows 11 Notepad runs each tab's editor on its own thread with its own layout |
| Whisper | Late-bound `SAPI.SpVoice` (no NuGet dependency) |
| Beep | Sine tone synthesised in memory, played via `winmm` `PlaySound` |

### Latency

The cue is meant to be heard the moment the cursor lands in a text field:

- The hooks only raise a flag; a single worker thread owns all state and handles
  the event at once (no debounce timer; a ~15 ms pause folds the burst of events
  one click raises into one pass — `FocusCoalesceMs` in `settings.json`).
- Moving between fields of the same window reads the layout and announces
  immediately.
- After a window switch Windows applies the window's layout a moment *after* it
  becomes foreground, so an immediate read can be stale. Instead of waiting for
  it to settle, the reading is announced at once when it matches what is
  expected (the language in use just before, or — with per-window input
  languages turned on in Windows — the one that window had last time). Only a
  reading that contradicts the expectation is re-read, for at most 250 ms.
- For 600 ms after the switch the layout is re-checked every 40 ms; if Windows
  applies a different layout late, the cue is corrected.
- The SAPI voice is created and warmed up at startup, whispers are spoken
  asynchronously, and a newer cue cuts off an older one rather than queueing
  behind it.

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
- **Signal (window / focus)** — Whisper / Beep / Whisper + beep
- **Signal (language switch)** — Whisper / Beep / Whisper + beep (default Beep)
- **Announce when switching windows**
- **Announce when keyboard language changes**
- **Same window: only when language changed**
- **Announce when typing after Xs idle** (X is set in Settings → Triggers)
- **Speak native language name** (`עברית` instead of `Hebrew`)
- **Test cue**
- **Settings…** — volumes, whisper rate, and a sound (tone / double beep /
  chime), pitch and preview button for each installed keyboard language
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
    FocusLanguageMonitor.cs      hooks → single worker thread, orchestration
    EditableFocusDetector.cs     "is this a typing cursor?" logic
    KeyboardLayoutReader.cs      LANGID → CultureInfo
  Audio/
    CuePlayer.cs                 STA worker: newest request wins → beep and/or whisper
    ToneGenerator.cs             in-memory WAV sine tone
  Configuration/                 AppSettings + JSON store
  Startup/AutoStart.cs           HKCU Run key
  Tray/
    TrayApplicationContext.cs    NotifyIcon + menu
    SettingsForm.cs              settings window
```
