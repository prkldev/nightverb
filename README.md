# Nightverb

**Slowed + reverb for any music in Firefox.** Slow tracks down in exact musical steps and add a
lush studio reverb, on YouTube, SoundCloud, VK, Yandex Music, Spotify and pretty much any site.

*Русская версия ниже.*

## What it does

- **Slow-down** in exact semitone steps (0.94x = one semitone down, 0.5x = one octave), with the
  pitch going down too, like real slowed tracks, or tempo only. Players can't reset it: they keep
  seeing their own values while Nightverb holds the real speed. Calls (Meet, Discord) are never touched.
- **Reverb** with 7 presets (Slowed + Reverb, Vapor, Space, Cathedral, Hall, Lo-fi room, Room) and
  fine controls (amount, decay, size, brightness, pre-delay, low cut, damping, density, modulation, width).
- Works out of the box on most sites, on any OS. Sites that protect their audio with DRM (Spotify)
  get reverb through the free **Nightverb audio component** for Windows; the popup offers it only there.
- Russian and English, picked from Firefox's language. Hotkeys: Alt+Shift+R (on/off), Alt+Shift+↑/↓ (one semitone).
- Collects nothing, sends nothing, no accounts, no trial, open source.

## How it works

```
             ┌──────────── extension (any OS) ─────────────┐
 site audio ─┤ page.js: speed shims on <audio>/<video>      │
             │ built-in reverb (Web Audio convolution):     ├──► speakers
             │   a) plain players: routed through our graph │
             │   b) sites with their own Web Audio graph    │
             │      (Yandex Music): reverb slipped in       │
             │      right before the speakers               │
             └──────────────────────────────────────────────┘
 DRM audio (Spotify) ─────────────────────────────────────────► speakers
        └─ Windows process loopback ─► nightverb.exe reverb ──► speakers (reverb only)
```

- Protected (DRM) and cross-origin audio without CORS would go **silent** if routed through Web Audio,
  so the built-in reverb never touches it. That's what the Windows component is for: it listens to
  Firefox's output through the standard Windows process-loopback API (no DRM is bypassed, nothing is
  recorded) and plays only the reverb tail on top.
- When the component is installed it does the reverb everywhere and the built-in one switches off,
  so there's never double reverb. If the component disappears, the built-in reverb takes over within
  a minute (background health check). Both are calibrated to the same loudness (within ~1 dB).
- The component runs per user, no admin rights: `%LOCALAPPDATA%\Nightverb`, HKCU registry only,
  uninstall from Windows Settings → Apps. It lives while Firefox is open and exits after it closes.

## Repository

| Path | What |
|---|---|
| `extension/` | Firefox extension (MV3): popup, presets, `page.js` (speed + built-in reverb), `content.js`, `_locales/` (ru, en) |
| `helper/` | Windows audio component, C# / .NET 8: installer, native-messaging bridge, WASAPI process-loopback engine, FDN reverb |
| `tools/` | Tests and diagnostics (see below) |
| `docs/store/` | Add-ons store listing texts and screenshots |
| `.github/workflows/` | CI build and releases |

### Tests

| Tool | Checks |
|---|---|
| `nightverb.exe --selftest` | Reverb DSP: RT60 accuracy, levels, stability at extremes, no denormals, CPU |
| `tools/speed-test.html` | 6 kinds of players (in page, `new Audio()`, shadow DOM, one that forces 1x, track reload, video), speed on and off |
| `tools/reverb-test.html` | Built-in reverb: own file, blob, cross-origin with and without CORS (the last must not be touched) |
| `tools/webaudio-test.html` | A site with its own Web Audio graph (like Yandex Music): reverb inserted, site's analyser still works |
| `tools/conflict-test.ps1` | Built-in vs component: no double reverb, takeover when the component vanishes |
| `tools/calibrate-builtin.mjs` | Built-in vs component loudness per preset |
| `tools/bidi-check.mjs <url>` | Opens any site in a driven Firefox with Nightverb and reports what kind of audio plays there |

Test pages are served by `node tools/serve.mjs` (http://127.0.0.1:8737).

### Build

```powershell
cd helper
dotnet publish -c Release -o ..\dist      # single self-contained exe, ~13 MB
..\dist\nightverb.exe --selftest
cd ..
npx web-ext lint  --source-dir extension
npx web-ext build --source-dir extension --artifacts-dir dist
```

Command line of the component:

```
nightverb.exe                     install / update (silent, a note closes by itself)
nightverb.exe --uninstall         remove
nightverb.exe --stop              stop the engine
nightverb.exe --selftest [dir]    DSP checks; with a dir also writes demo-*.wav
nightverb.exe --probe [s] [--pid] show the audio level of Firefox (or any process)
```

Log: `%LOCALAPPDATA%\Nightverb\nightverb.log`.

### Limits

- The Windows component needs Windows 10 2004+ or Windows 11. Without it, DRM sites get slow-down only.
- The component applies reverb to all of Firefox (all tabs) and serves one Firefox profile at a time.

---

## По-русски

**Nightverb** замедляет любую музыку в Firefox ровными музыкальными шагами и добавляет студийный реверб:
YouTube, SoundCloud, VK, Яндекс Музыка, Spotify и почти любые сайты.

- Замедление по полутонам (0.94 = на полутон ниже, 0.5 = на октаву), с понижением тона, как в настоящих
  slowed, или только темп. Плееры не могут сбросить скорость. Звонки не трогаются.
- Реверб: 7 пресетов и тонкая настройка.
- Работает сразу на большинстве сайтов и любой ОС. Для сайтов с защитой звука (Spotify) есть бесплатный
  аудиокомпонент Nightverb для Windows, окошко предлагает его только там.
- Язык берётся из Firefox (русский или английский). Ничего не собирает и не отправляет. Открытый код.

Подробности устройства, тесты и сборка описаны выше, в английской части.
