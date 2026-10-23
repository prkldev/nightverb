"use strict";

// ---------- reverb slider definitions ----------

const fmtPct = (v) => `${Math.round(v)}%`;
const fmtHz = (v) => (v >= 1000 ? nvT("unitKHz", [nvNum(v / 1000, v >= 10000 ? 0 : 1)]) : nvT("unitHz", [String(Math.round(v))]));
const fmtMs = (v) => nvT("unitMs", [String(Math.round(v))]);
const fmtSec = (v) => nvT("unitSec", [v < 10 ? nvNum(v, 1) : String(Math.round(v))]);

const SLIDERS = [
  { key: "wet", labelKey: "sliderWet", min: 0, max: 100, log: false, fmt: fmtPct, main: true },
  { key: "decay", labelKey: "sliderDecay", min: 0.3, max: 30, log: true, fmt: fmtSec, main: true },
  { key: "size", labelKey: "sliderSize", min: 30, max: 200, log: false, fmt: fmtPct, main: true },
  { key: "highCut", labelKey: "sliderHighCut", min: 1000, max: 20000, log: true, fmt: fmtHz, main: true },
  { key: "preDelay", labelKey: "sliderPreDelay", min: 0, max: 300, log: false, fmt: fmtMs },
  { key: "lowCut", labelKey: "sliderLowCut", min: 20, max: 1000, log: true, fmt: fmtHz },
  { key: "damping", labelKey: "sliderDamping", min: 500, max: 20000, log: true, fmt: fmtHz },
  { key: "diffusion", labelKey: "sliderDiffusion", min: 0, max: 100, log: false, fmt: fmtPct },
  { key: "modulation", labelKey: "sliderModulation", min: 0, max: 100, log: false, fmt: fmtPct },
  { key: "width", labelKey: "sliderWidth", min: 0, max: 150, log: false, fmt: fmtPct },
];

// Engine error codes -> message keys (anything else is shown as is).
const ENGINE_ERRORS = {
  "device-lost": "errDeviceLost",
  "access-denied": "errAccessDenied",
  "firefox-not-found": "errFirefoxNotFound",
};

function applyI18n() {
  document.documentElement.lang = nvLang();
  for (const el of document.querySelectorAll("[data-i18n]")) el.textContent = nvT(el.dataset.i18n);
  for (const el of document.querySelectorAll("[data-i18n-title]")) el.title = nvT(el.dataset.i18nTitle);
  for (const el of document.querySelectorAll("[data-i18n-aria]")) el.setAttribute("aria-label", nvT(el.dataset.i18nAria));
}

const STEPS = 1000;
const toPos = (d, v) => {
  const t = d.log ? Math.log(v / d.min) / Math.log(d.max / d.min) : (v - d.min) / (d.max - d.min);
  return Math.round(Math.min(1, Math.max(0, t)) * STEPS);
};
const fromPos = (d, p) => {
  const t = p / STEPS;
  const v = d.log ? d.min * Math.pow(d.max / d.min, t) : d.min + (d.max - d.min) * t;
  if (d.key === "decay") return Math.round(v * 10) / 10;
  if (d.log) return Math.round(v / (v >= 1000 ? 50 : 5)) * (v >= 1000 ? 50 : 5);
  return Math.round(v);
};

// ---------- state ----------

let settings = null;   // the extension's storage is the source of truth
let engine = null;     // last "state" message from the reverb engine
let conn = null;
let helperMissing = false;
let lastError = "";
let lastPresetId = "slowed";
let hasSitePermission = true;
let tabMedia = null;        // what's playing in the active tab (from content.js)
let activeTab = null;
let isWindows = true;
let downloadClicked = false;

const $ = (id) => document.getElementById(id);
const els = {};

// ---------- building ----------

function buildPresets() {
  const box = $("presets");
  box.textContent = "";
  for (const p of NV_PRESETS) {
    const b = document.createElement("button");
    b.type = "button";
    b.className = "chip";
    b.textContent = nvT(p.nameKey);
    b.dataset.id = p.id;
    b.setAttribute("role", "radio");
    if (p.speed) b.title = nvT("presetSetsSpeed", [`${nvFormatSpeed(p.speed)} (${nvFormatSemitones(p.speed)})`]);
    b.addEventListener("click", () => applyPreset(p.id));
    box.appendChild(b);
  }
  const c = document.createElement("span");
  c.className = "chip custom";
  c.textContent = nvT("presetCustom");
  c.dataset.id = "custom";
  c.setAttribute("role", "radio");
  box.appendChild(c);
}

function buildSliders() {
  for (const d of SLIDERS) {
    const row = document.createElement("div");
    row.className = "slider";
    const id = `s-${d.key}`;
    const label = document.createElement("label");
    label.htmlFor = id;
    label.textContent = nvT(d.labelKey);
    const out = document.createElement("output");
    out.htmlFor = id;
    const input = document.createElement("input");
    input.type = "range";
    input.id = id;
    input.min = "0";
    input.max = String(STEPS);
    input.step = "1";
    input.addEventListener("input", () => {
      const v = fromPos(d, Number(input.value));
      settings.params[d.key] = v;
      out.value = d.fmt(v);
      settings.preset = "custom";
      renderPresets();
      reverbChanged();
    });
    // double-click snaps a slider back to the preset's value
    input.addEventListener("dblclick", () => {
      settings.params[d.key] = basePreset().params[d.key];
      renderSliders();
      reverbChanged();
    });
    row.append(label, out, input);
    (d.main ? $("main-sliders") : $("adv-sliders")).appendChild(row);
    els[d.key] = { input, out };
  }
}

function buildSpeedSteps() {
  const box = $("speed-steps");
  for (const { st, hintKey } of NV_SEMITONE_STEPS) {
    const rate = nvRateFromSemitones(st);
    const b = document.createElement("button");
    b.type = "button";
    b.className = "step";
    b.dataset.st = String(st);
    b.textContent = nvStepLabel(rate);
    b.title = st === 0 ? nvT(hintKey) : `${nvT(hintKey)} (${nvFormatSemitones(rate)})`;
    b.addEventListener("click", () => setSpeed(rate, true));
    box.appendChild(b);
  }
}

// Slider: 1% steps, but a value within 0.15 semitone of a whole note snaps to it.
function sliderToSpeed(pos) {
  const rate = pos / 100;
  const st = nvSemitones(rate);
  return Math.abs(st - Math.round(st)) < 0.15 ? nvRateFromSemitones(Math.round(st)) : rate;
}

function basePreset() {
  return NV_PRESETS.find((p) => p.id === settings.preset) || NV_PRESETS.find((p) => p.id === lastPresetId) || NV_PRESETS[0];
}

// ---------- rendering ----------

function renderPresets() {
  for (const el of $("presets").children) el.setAttribute("aria-checked", String(el.dataset.id === settings.preset));
}

function renderSliders() {
  for (const d of SLIDERS) {
    const { input, out } = els[d.key];
    const v = settings.params[d.key];
    input.value = String(toPos(d, v));
    out.value = d.fmt(v);
  }
}

function renderSpeed() {
  $("speed-on").checked = settings.speedEnabled;
  $("pitch").checked = settings.pitch;
  $("speed-range").value = String(Math.round(settings.speed * 100));
  $("speed-val").value = nvFormatSpeed(settings.speed);
  const pct = Math.round((settings.speed - 1) * 100);
  $("speed-st").textContent = settings.pitch
    ? nvFormatSemitones(settings.speed)
    : pct === 0 ? nvT("tempoOriginal") : nvT("tempoChange", [`${pct > 0 ? "+" : ""}${pct}`]);
  const st = nvSemitones(settings.speed);
  for (const b of $("speed-steps").children) {
    b.setAttribute("aria-pressed", String(Math.abs(Number(b.dataset.st) - st) < 0.02));
  }
  $("speed-card").classList.toggle("dim", !settings.speedEnabled);
  $("perm").hidden = hasSitePermission;
  $("reload").hidden = !needsReload();
}

// The tab is playing sound, speed is on, but Nightverb couldn't reach its player:
// the tab was open before Nightverb was installed/updated. One reload fixes it.
function needsReload() {
  if (!activeTab || !activeTab.audible || !nvSpeedState(settings).active || !hasSitePermission) return false;
  let url;
  try { url = new URL(activeTab.url || ""); } catch (e) { return false; }
  if (!/^https?:$/.test(url.protocol)) return false;
  if (/(^|\.)(addons\.mozilla\.org|accounts\.firefox\.com)$/.test(url.hostname)) return false; // extensions can't run there
  if (!tabMedia) return true; // no Nightverb script in the tab at all
  return Boolean(tabMedia.late) && !tabMedia.speedControlled;
}

function renderPower() {
  $("power").setAttribute("aria-pressed", String(settings.enabled));
  document.body.classList.toggle("off", !settings.enabled);
  $("reverb-on").checked = settings.reverbEnabled;
  $("reverb-card").classList.toggle("dim", !settings.reverbEnabled);
}

function setStatus(kind, text) {
  const s = $("status");
  s.className = `status ${kind}`;
  s.querySelector("span").textContent = text;
}

// The Windows component is doing the reverb right now (otherwise the built-in one is).
function engineMode() {
  return Boolean(settings.useComponent && engine);
}

// Built-in reverb can't reach this tab's audio (DRM or the site forbids it).
function needsComponentHere() {
  return !engineMode() && Boolean(tabMedia) && tabMedia.builtin === 0 && (tabMedia.protected > 0 || tabMedia.blocked > 0);
}

function renderNeed() {
  const show = settings.reverbEnabled && needsComponentHere();
  $("need").hidden = !show;
  if (!show) return;
  $("need-title").textContent = nvT(tabMedia.protected > 0 ? "needProtectedTitle" : "needBlockedTitle");
  const canDownload = isWindows && Boolean(NV_COMPONENT_URL);
  $("need-download").hidden = !canDownload || downloadClicked;
  $("need-after").hidden = !downloadClicked;
  const note = !isWindows ? nvT("needWindowsOnly") : !NV_COMPONENT_URL ? nvT("needSoon") : "";
  $("need-note").textContent = note;
  $("need-note").hidden = !note;
}

function renderComponentRow() {
  $("component-row").hidden = !isWindows;
  $("use-component").checked = settings.useComponent;
  $("component-state").textContent = nvT(settings.engineAvailable || engine ? "componentInstalled" : "componentMissing");
}

function renderStatus() {
  renderNeed();
  renderComponentRow();
  $("meters").hidden = !engineMode();

  const speedOn = nvSpeedState(settings).active;
  if (!settings.enabled) return setStatus("", nvT("statusOff"));
  if (!settings.reverbEnabled) return setStatus(speedOn ? "ok" : "", nvT(speedOn ? "statusSpeedOnly" : "statusReverbOff"));

  if (settings.useComponent && lastError) return setStatus("err", lastError);

  if (engineMode()) {
    switch (engine.status) {
      case "running": return setStatus("ok", nvT(speedOn ? "statusRunningBoth" : "statusRunning"));
      case "starting":
      case "off": return setStatus("warn", nvT("statusStarting"));
      case "waiting": return setStatus("warn", nvT("statusWaiting"));
      default: {
        const e = engine.error;
        return setStatus("err", e ? nvT("statusError", [ENGINE_ERRORS[e] ? nvT(ENGINE_ERRORS[e]) : e]) : nvT("statusEngineError"));
      }
    }
  }

  if (needsComponentHere()) return setStatus("warn", nvT("statusNeedsComponent"));
  if (conn && !helperMissing && settings.engineAvailable) return setStatus("", nvT("statusConnecting"));
  return setStatus("ok", nvT(speedOn ? "statusRunningBoth" : "statusRunning"));
}

function renderInfo() {
  if (engineMode() && engine.device) {
    const khz = engine.sampleRate ? nvT("unitKHz", [nvNum(engine.sampleRate / 1000, 1)]) : "";
    $("info").textContent = [engine.device, khz].filter(Boolean).join(" · ");
    $("latency-hint").textContent = engine.latencyMs ? nvT("latencyHint", [String(engine.latencyMs)]) : "";
  } else {
    $("info").textContent = nvReverbOn(settings) ? nvT("infoBuiltin") : "";
    $("latency-hint").textContent = "";
  }
  $("info").title = $("info").textContent;
}

// ---------- meters ----------

const meter = { in: 0, out: 0, shownIn: 0, shownOut: 0 };

function levelToWidth(peak) {
  if (!(peak > 0)) return 0;
  return Math.max(0, Math.min(1, (20 * Math.log10(peak) + 54) / 54));
}

function animateMeters() {
  meter.shownIn = Math.max(levelToWidth(meter.in), meter.shownIn * 0.86);
  meter.shownOut = Math.max(levelToWidth(meter.out), meter.shownOut * 0.86);
  meter.in = 0;
  meter.out = 0;
  $("m-in").style.width = `${(meter.shownIn * 100).toFixed(1)}%`;
  $("m-out").style.width = `${(meter.shownOut * 100).toFixed(1)}%`;
  requestAnimationFrame(animateMeters);
}

// ---------- saving and engine traffic ----------

let sendTimer = null;
let saveTimer = null;
let saveDue = 0;

// Speed changes go through storage (every tab listens), so save quickly.
function save(delay) {
  const due = Date.now() + delay;
  if (saveTimer && saveDue <= due) return;
  clearTimeout(saveTimer);
  saveDue = due;
  saveTimer = setTimeout(() => {
    saveTimer = null;
    nvSaveSettings(settings);
  }, delay);
}

function reverbChanged() {
  if (!sendTimer) {
    sendTimer = setTimeout(() => {
      sendTimer = null;
      pushToEngine();
    }, 35);
  }
  save(80); // the built-in reverb listens to storage, keep it snappy
}

function pushToEngine() {
  if (conn) conn.send({ type: "set", enabled: nvEngineReverbOn(settings), params: settings.params });
}

function setEngineAvailable(v) {
  if (settings.engineAvailable === v) return;
  settings.engineAvailable = v;
  nvSetEngineAvailable(v); // content scripts switch the built-in reverb off/on
}

function connect() {
  helperMissing = false;
  lastError = "";

  conn = new NvConnection({
    onMessage(m) {
      if (!m || typeof m !== "object") return;
      if (m.type === "state") {
        engine = m;
        lastError = "";
        setEngineAvailable(true);
        stopPolling();
        renderStatus();
        renderInfo();
      } else if (m.type === "meter") {
        meter.in = Math.max(meter.in, Number(m.in) || 0);
        meter.out = Math.max(meter.out, Number(m.out) || 0);
      } else if (m.type === "error") {
        lastError = m.error === "engine-unreachable" ? nvT("errEngineUnreachable") : nvT("statusError", [m.error]);
        renderStatus();
      }
    },
    onDisconnect(err) {
      conn = null;
      engine = null;
      if (nvIsMissingHost(err)) {
        helperMissing = true;
        setEngineAvailable(false);
      } else if (!lastError) {
        lastError = nvT("errHelperDisconnected");
      }
      renderStatus();
      renderInfo();
    },
  }).open();

  pushToEngine();
  conn.send({ type: "meters", on: true });
  renderStatus();
}

// Talk to the component only when it's wanted: reverb on, or we don't know yet
// whether it's installed.
function ensureEngine() {
  const want = settings.useComponent && (nvReverbOn(settings) || !settings.engineAvailable);
  if (want && !conn) connect();
  else if (conn) pushToEngine();
  renderStatus();
  renderInfo();
}

// After "Download", keep checking quietly until the component shows up.
let pollTimer = 0;
function startPolling() {
  if (pollTimer) return;
  pollTimer = setInterval(() => {
    if (!conn && settings.useComponent) connect();
  }, 2500);
}
function stopPolling() {
  clearInterval(pollTimer);
  pollTimer = 0;
}

async function loadTabMedia() {
  try {
    const [tab] = await browser.tabs.query({ active: true, currentWindow: true });
    activeTab = tab || null;
    tabMedia = tab ? (await browser.tabs.sendMessage(tab.id, { type: "nv-media" })) || null : null;
  } catch (e) {
    tabMedia = null; // no content script there (about: pages, the add-ons store, tabs opened before install)
  }
  renderStatus();
  renderSpeed();
}

// ---------- actions ----------

function applyPreset(id) {
  const p = NV_PRESETS.find((x) => x.id === id);
  if (!p) return;
  settings.preset = id;
  lastPresetId = id;
  settings.params = { ...p.params };
  if (p.speed) {
    settings.speed = p.speed;
    settings.speedEnabled = true;
    renderSpeed();
  }
  renderPresets();
  renderSliders();
  reverbChanged();
  save(30);
}

function setSpeed(v, enable) {
  settings.speed = nvClampSpeed(v);
  if (enable) settings.speedEnabled = true;
  renderSpeed();
  renderStatus();
  save(40);
}

function togglePower() {
  settings.enabled = !settings.enabled;
  renderPower();
  ensureEngine();
  save(0);
}

async function checkPermission() {
  try {
    hasSitePermission = await browser.permissions.contains({ origins: ["<all_urls>"] });
  } catch (e) {
    hasSitePermission = true;
  }
  renderSpeed();
}

// ---------- init ----------

async function init() {
  settings = await nvLoadSettings();
  if (settings.preset !== "custom") lastPresetId = settings.preset;

  applyI18n();
  buildPresets();
  buildSliders();
  buildSpeedSteps();
  renderPresets();
  renderSliders();
  renderSpeed();
  renderPower();

  $("power").addEventListener("click", togglePower);

  $("speed-on").addEventListener("change", (e) => {
    settings.speedEnabled = e.target.checked;
    renderSpeed();
    renderStatus();
    save(0);
  });
  $("speed-range").addEventListener("input", (e) => setSpeed(sliderToSpeed(Number(e.target.value)), false));
  $("speed-range").addEventListener("dblclick", () => setSpeed(1, false));
  $("pitch").addEventListener("change", (e) => {
    settings.pitch = e.target.checked;
    renderSpeed();
    save(0);
  });
  $("perm-grant").addEventListener("click", async () => {
    try {
      await browser.permissions.request({ origins: ["<all_urls>"] });
    } catch (e) { /* user said no */ }
    checkPermission();
  });

  $("reverb-on").addEventListener("change", (e) => {
    settings.reverbEnabled = e.target.checked;
    renderPower();
    ensureEngine();
    save(0);
  });
  $("reset").addEventListener("click", () => applyPreset(basePreset().id));

  $("use-component").addEventListener("change", (e) => {
    settings.useComponent = e.target.checked;
    if (!settings.useComponent && conn) {
      pushToEngine(); // the component goes quiet, the built-in reverb takes over
      engine = null;
      conn.close();
      conn = null;
    }
    save(0);
    ensureEngine();
  });

  $("reload-tab").addEventListener("click", async () => {
    if (activeTab) await browser.tabs.reload(activeTab.id);
    window.close();
  });

  $("need-download").addEventListener("click", () => {
    browser.tabs.create({ url: NV_COMPONENT_URL, active: false });
    downloadClicked = true;
    renderStatus();
    startPolling();
  });

  try {
    isWindows = (await browser.runtime.getPlatformInfo()).os === "win";
  } catch (e) { /* assume Windows */ }

  checkPermission();
  renderInfo();
  ensureEngine();
  loadTabMedia();
  requestAnimationFrame(animateMeters);
}

window.addEventListener("unload", () => {
  if (sendTimer) { clearTimeout(sendTimer); pushToEngine(); }
  if (saveTimer) { clearTimeout(saveTimer); nvSaveSettings(settings); }
  if (conn) conn.close();
});

init();
