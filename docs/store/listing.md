# addons.mozilla.org listing

Copy these into the Developer Hub when submitting. Name, summary and permissions come from the
extension itself (`_locales`), the rest is filled in on the site.

- **Categories:** Photos, Music & Videos
- **Tags:** slowed, reverb, slowed and reverb, music, audio, speed, pitch, nightcore, spotify, youtube
- **Support site:** https://github.com/OWNER/nightverb/issues
- **Homepage:** https://github.com/OWNER/nightverb
- **License:** MIT
- **Screenshots:** `popup-ru.png`, `popup-en.png` (this folder)

---

## English

**Summary** (from the extension):
Slowed + reverb for any music in Firefox: Spotify, YouTube, SoundCloud, Yandex Music. Pitch-perfect slow-down and studio-grade reverb in one click. On Windows, reverb runs through the free, lightweight Nightverb audio component.

**Description:**

Turn anything you listen to in Firefox into a slowed + reverb track.

SLOW-DOWN IN REAL MUSICAL STEPS
• One click for 0.94x, 0.89x, 0.84x, 0.79x, 0.75x, 0.67x or 0.5x: each step is exactly one semitone (0.5x is one octave), so tracks stay in tune.
• Pitch goes down with the speed, like real slowed tracks, or switch to tempo-only.
• Players can't reset your speed when the next track starts.

STUDIO REVERB
• Presets: Slowed + Reverb, Vapor, Space, Cathedral, Hall, Lo-fi room, Room.
• Fine controls: amount, decay, size, brightness, pre-delay, low cut, tail highs, density, modulation, width.

WORKS EVERYWHERE
• Slow-down and reverb work right away on YouTube, SoundCloud, VK, Yandex Music and most sites, on any OS.
• Services that protect their audio (Spotify) don't let any extension process it. For them there is the free Nightverb audio component for Windows: the popup offers it only on such sites. Download, run once, done. It adds the reverb at the system level and doesn't bypass any protection.

THE REST
• Hotkeys: Alt+Shift+R on/off, Alt+Shift+Up/Down one semitone.
• Russian and English.
• No data collected, no account, no trial. Open source: https://github.com/OWNER/nightverb

---

## Русский

**Краткое описание** (из расширения):
Slowed + reverb для любой музыки в Firefox: Яндекс Музыка, Spotify, YouTube, SoundCloud. Замедление ровными музыкальными шагами и студийный реверб в один клик. Реверб в Windows работает через лёгкий бесплатный аудиокомпонент Nightverb.

**Описание:**

Превратите всё, что слушаете в Firefox, в slowed + reverb.

ЗАМЕДЛЕНИЕ РОВНЫМИ МУЗЫКАЛЬНЫМИ ШАГАМИ
• Один клик: 0.94, 0.89, 0.84, 0.79, 0.75, 0.67 или 0.5. Каждый шаг ровно на полутон (0.5 это октава), трек остаётся в тональности.
• Тон опускается вместе со скоростью, как в настоящих slowed, или меняется только темп.
• Плееры не сбрасывают скорость при смене трека.

СТУДИЙНЫЙ РЕВЕРБ
• Пресеты: Slowed + Reverb, Vapor, Космос, Собор, Зал, Lo-fi комната, Комната.
• Тонкая настройка: количество, хвост, размер, яркость, пауза перед эхом, срез низов, верха в хвосте, плотность, модуляция, ширина.

РАБОТАЕТ ВЕЗДЕ
• Замедление и реверб работают сразу на YouTube, SoundCloud, VK, Яндекс Музыке и большинстве сайтов, в любой ОС.
• Сервисы, которые защищают звук (Spotify), не дают расширениям его обрабатывать. Для них есть бесплатный аудиокомпонент Nightverb для Windows: окно расширения предложит его только на таких сайтах. Скачать, запустить один раз, готово. Он добавляет реверб на уровне системы и не обходит никакую защиту.

ЕЩЁ
• Горячие клавиши: Alt+Shift+R вкл/выкл, Alt+Shift+↑/↓ на полутон.
• Русский и английский язык.
• Ничего не собирает, без регистрации и пробных периодов. Открытый код: https://github.com/OWNER/nightverb

---

## Privacy policy

Nightverb does not collect, store or transmit any personal data or browsing data. Settings are kept
in the browser's local extension storage. Audio is processed locally, in the browser or by the
optional Nightverb audio component on the same computer, and is never recorded or sent anywhere.
The extension makes no network requests of its own; the only link it opens is the component's
download on GitHub, when the user clicks it.

---

## Notes for reviewers

- **No build step.** The submitted files are the source; nothing is minified or bundled.
- **`<all_urls>` host permission + content scripts:** needed to change playback speed and apply the
  reverb to media elements on whatever site the user listens to.
- **`page.js` runs in the MAIN world:** sites create players with `new Audio()` (never added to the
  DOM) and in closed shadow roots, and keep resetting `playbackRate`; only page-world code can see
  these elements and keep the chosen speed. It wraps `playbackRate`/`defaultPlaybackRate`/
  `preservesPitch`, `HTMLMediaElement.play`, `attachShadow`, `AudioContext.createMediaElementSource`/
  `createMediaStreamSource` and `AudioNode.connect`/`disconnect`, all as thin pass-throughs. The
  last ones let the reverb be inserted before the speakers on sites that use their own Web Audio
  graph. It reads no page content and talks to `content.js` only through two custom DOM events.
- **`nativeMessaging`:** optional Windows audio component (open source, same repository, `helper/`)
  for sites whose audio is DRM-protected. It captures Firefox's own audio output via the documented
  Windows process-loopback API and plays only a reverb signal; it doesn't decrypt, record or save
  anything. The extension works without it (slow-down everywhere, reverb on non-protected sites).
- **`alarms`:** a once-a-minute check that the optional component is still there, so the built-in
  reverb can take over if it was removed.
- **`storage`:** settings.
