"use strict";

// Toolbar badge, waking the Windows audio component when Firefox starts,
// remembering whether it's installed, and hotkeys.

async function updateBadge(s) {
  const speed = nvSpeedState(s);
  let text = "";
  if (s.enabled) {
    if (speed.active) text = nvFormatSpeed(speed.rate).replace(/x$/, "");
    else if (nvReverbOn(s)) text = "ON";
  }
  await browser.action.setBadgeBackgroundColor({ color: "#8b5cf6" });
  if (browser.action.setBadgeTextColor) await browser.action.setBadgeTextColor({ color: "#ffffff" });
  await browser.action.setBadgeText({ text });
}

/** Tell the component what to do; also learns whether it's installed. */
async function pushToEngine(s) {
  try {
    await nvTalk([{ type: "set", enabled: nvEngineReverbOn(s), params: s.params }]);
    if (!s.engineAvailable) await nvSetEngineAvailable(true);
  } catch (e) {
    if (nvIsMissingHost(e) && s.engineAvailable) await nvSetEngineAvailable(false);
  }
}

async function wake() {
  const s = await nvLoadSettings();
  await updateBadge(s);
  // Check for the component even if reverb is off right now, so the right
  // engine is picked the moment it's switched on.
  if (s.useComponent && (nvReverbOn(s) || !s.engineAvailable)) await pushToEngine(s);
}

// Health check once a minute while reverb is on and the component is wanted:
//  - component vanished (uninstalled)   -> engineAvailable=false, built-in reverb takes over
//  - component installed in the meantime -> engineAvailable=true, it takes over
//  - its engine crashed                  -> talking to it starts a new one
// A failed check costs nothing (Firefox just reports the host is missing).
async function health() {
  const s = await nvLoadSettings();
  if (s.useComponent && nvReverbOn(s)) await pushToEngine(s);
}

async function startup() {
  browser.alarms.create("nv-health", { periodInMinutes: 1 });
  await wake();
}

browser.runtime.onStartup.addListener(startup);
browser.runtime.onInstalled.addListener(startup);
browser.alarms.onAlarm.addListener((a) => {
  if (a.name === "nv-health") health();
});

browser.commands.onCommand.addListener(async (command) => {
  const s = await nvLoadSettings();
  const engineBefore = nvEngineReverbOn(s);

  if (command === "toggle-all") {
    s.enabled = !s.enabled;
  } else if (command === "speed-down" || command === "speed-up") {
    const base = s.enabled && s.speedEnabled ? s.speed : 1;
    s.speed = nvStepSemitone(base, command === "speed-up" ? 1 : -1);
    s.speedEnabled = true;
    s.enabled = true;
  } else {
    return;
  }

  await nvSaveSettings(s);
  if (s.engineAvailable && nvEngineReverbOn(s) !== engineBefore) await pushToEngine(s);
});

browser.storage.onChanged.addListener(async (changes, area) => {
  if (area !== "local") return;
  if (["enabled", "reverbEnabled", "speedEnabled", "speed"].some((k) => k in changes)) {
    updateBadge(await nvLoadSettings());
  }
});
