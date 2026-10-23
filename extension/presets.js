"use strict";

// Shared between background, popup and content scripts.

// ---------- language: follows Firefox's UI language (ru or en, en otherwise) ----------

function nvT(key, subs) {
  try {
    return browser.i18n.getMessage(key, subs) || key;
  } catch (e) {
    return key;
  }
}

function nvLang() {
  try {
    return browser.i18n.getUILanguage() || "en";
  } catch (e) {
    return "en";
  }
}

function nvNum(v, digits) {
  return new Intl.NumberFormat(nvLang(), { maximumFractionDigits: digits, minimumFractionDigits: 0 }).format(v);
}

// With "tone follows speed" on, speed and pitch are locked together:
// rate = 2^(semitones / 12). One semitone down = x0.944, an octave down = x0.5.
// Speeds picked in whole semitones keep the track in a real key.
function nvRateFromSemitones(st) {
  return Math.pow(2, st / 12);
}

function nvSemitones(rate) {
  return 12 * Math.log2(rate);
}

// `speed` is optional: presets that have it also set the playback speed.
const NV_PRESETS = [
  {
    id: "slowed", nameKey: "presetSlowed", speed: nvRateFromSemitones(-4),
    params: { wet: 45, size: 130, decay: 4.5, preDelay: 20, diffusion: 75, lowCut: 180, highCut: 6500, damping: 5000, modulation: 50, width: 100 },
  },
  {
    id: "vapor", nameKey: "presetVapor", speed: nvRateFromSemitones(-3),
    params: { wet: 55, size: 150, decay: 6, preDelay: 10, diffusion: 80, lowCut: 300, highCut: 4000, damping: 3000, modulation: 100, width: 120 },
  },
  {
    id: "dream", nameKey: "presetDream", speed: nvRateFromSemitones(-5),
    params: { wet: 60, size: 200, decay: 15, preDelay: 60, diffusion: 90, lowCut: 250, highCut: 5500, damping: 4000, modulation: 80, width: 130 },
  },
  {
    id: "cathedral", nameKey: "presetCathedral",
    params: { wet: 40, size: 185, decay: 8, preDelay: 45, diffusion: 85, lowCut: 150, highCut: 7500, damping: 5500, modulation: 40, width: 110 },
  },
  {
    id: "hall", nameKey: "presetHall",
    params: { wet: 35, size: 100, decay: 2.6, preDelay: 25, diffusion: 70, lowCut: 120, highCut: 9000, damping: 7000, modulation: 30, width: 100 },
  },
  {
    id: "lofi", nameKey: "presetLofi",
    params: { wet: 35, size: 60, decay: 1.4, preDelay: 5, diffusion: 70, lowCut: 300, highCut: 3500, damping: 2500, modulation: 60, width: 70 },
  },
  {
    id: "room", nameKey: "presetRoom",
    params: { wet: 30, size: 45, decay: 0.9, preDelay: 8, diffusion: 60, lowCut: 200, highCut: 7000, damping: 5000, modulation: 20, width: 80 },
  },
];

const NV_SPEED_MIN = 0.5;
const NV_SPEED_MAX = 1.5;

// Quick buttons in exact musical steps (semitones; 0 = original), slowest on
// the left and 1x on the right, the same direction as the speed slider.
const NV_SEMITONE_STEPS = [-12, -7, -5, -4, -3, -2, -1, 0].map((st) => ({ st, hintKey: `stepHint${-st}` }));

/** Short button label: 1x, 0.94, 0.89 ... 0.5 */
function nvStepLabel(rate) {
  if (Math.abs(rate - 1) < 0.001) return "1x";
  return rate.toFixed(2).replace(/0$/, "");
}

const NV_DEFAULTS = {
  enabled: true,        // master switch (header power button, Alt+Shift+R)
  reverbEnabled: true,
  preset: "slowed",
  params: { ...NV_PRESETS[0].params },
  speedEnabled: true,
  speed: nvRateFromSemitones(-4),
  pitch: true,          // tone follows speed, like real slowed tracks
  useComponent: true,   // prefer the Windows audio component when it's installed
  engineAvailable: false, // last known: is the component installed and reachable
};

// Where the popup sends people to get the Windows audio component
// (always the newest GitHub release; built by .github/workflows/build.yml).
const NV_COMPONENT_URL = "https://github.com/prkldev/nightverb/releases/latest/download/nightverb.exe";

const NV_KEYS = Object.keys(NV_DEFAULTS);

// Kept to 4 decimals so semitone speeds stay exact (0.7937, not 0.79).
function nvClampSpeed(v) {
  const n = Number(v);
  if (!Number.isFinite(n)) return 1;
  return Math.round(Math.min(NV_SPEED_MAX, Math.max(NV_SPEED_MIN, n)) * 10000) / 10000;
}

/** Next whole semitone up (dir 1) or down (dir -1): from -3.9 down goes to -4, from -4 to -5. */
function nvStepSemitone(rate, dir) {
  const st = nvSemitones(rate);
  const next = dir > 0 ? Math.floor(st + 1.05) : Math.ceil(st - 1.05);
  return nvClampSpeed(nvRateFromSemitones(next));
}

/** "-4 полутона" / "-4 semitones", "-3,9 полутона", "исходный тон" / "original pitch". */
function nvFormatSemitones(rate) {
  const st = Math.round(nvSemitones(rate) * 10) / 10;
  if (Math.abs(st) < 0.05) return nvT("pitchOriginal");
  const abs = Math.abs(st);
  let form = "other";
  try {
    form = new Intl.PluralRules(nvLang()).select(abs);
  } catch (e) { /* keep "other" */ }
  const text = `${st > 0 ? "+" : "-"}${nvNum(abs, 1)}`;
  return nvT(`semitone_${form}`, [text]);
}

async function nvLoadSettings() {
  const s = await browser.storage.local.get(NV_KEYS);
  const bool = (k) => (typeof s[k] === "boolean" ? s[k] : NV_DEFAULTS[k]);
  return {
    enabled: bool("enabled"),
    reverbEnabled: bool("reverbEnabled"),
    preset: typeof s.preset === "string" ? s.preset : NV_DEFAULTS.preset,
    params: { ...NV_DEFAULTS.params, ...(s.params || {}) },
    speedEnabled: bool("speedEnabled"),
    speed: s.speed === undefined ? NV_DEFAULTS.speed : nvClampSpeed(s.speed),
    pitch: bool("pitch"),
    useComponent: bool("useComponent"),
    engineAvailable: bool("engineAvailable"),
  };
}

function nvSaveSettings(settings) {
  const out = {};
  for (const k of NV_KEYS) out[k] = settings[k];
  return browser.storage.local.set(out);
}

/** What page.js needs to know. */
function nvSpeedState(s) {
  const rate = nvClampSpeed(s.speed);
  return {
    active: Boolean(s.enabled && s.speedEnabled && Math.abs(rate - 1) > 0.001),
    rate,
    pitch: s.pitch !== false,
  };
}

/** Reverb switched on at all (either engine). */
function nvReverbOn(s) {
  return Boolean(s.enabled && s.reverbEnabled);
}

/** The Windows component does the reverb (it hears all of Firefox, protected sites too). */
function nvEngineReverbOn(s) {
  return nvReverbOn(s) && Boolean(s.useComponent);
}

/** The in-page reverb runs only when the component isn't doing it, so there's never double reverb. */
function nvBuiltinReverbOn(s) {
  return nvReverbOn(s) && !(s.useComponent && s.engineAvailable);
}

function nvSetEngineAvailable(v) {
  return browser.storage.local.set({ engineAvailable: Boolean(v) });
}

function nvFormatSpeed(v) {
  return `${nvClampSpeed(v).toFixed(2).replace(/0$/, "")}x`;
}
