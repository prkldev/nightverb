"use strict";

// Extension side of the in-page engine: hands settings to page.js (which runs
// inside the page) through DOM events, and answers the popup's "what's playing
// here?" question.
(() => {
  let speedState = null;
  let reverbState = null;
  let media = null; // last report from page.js

  function send() {
    if (speedState) document.dispatchEvent(new CustomEvent("nightverb:speed", { detail: JSON.stringify(speedState) }));
    if (reverbState) document.dispatchEvent(new CustomEvent("nightverb:reverb", { detail: JSON.stringify(reverbState) }));
  }

  async function refresh() {
    try {
      const s = await nvLoadSettings();
      speedState = nvSpeedState(s);
      reverbState = { on: nvBuiltinReverbOn(s), params: s.params };
      send();
    } catch (e) {
      /* extension reloaded or storage unavailable */
    }
  }

  // page.js may start before or after us; it says hello when ready.
  document.addEventListener("nightverb:hello", () => (speedState ? send() : refresh()));

  document.addEventListener("nightverb:media", (e) => {
    try {
      media = JSON.parse(e.detail);
    } catch (err) { /* ignore */ }
  });

  browser.storage.onChanged.addListener((changes, area) => {
    if (area === "local" && NV_KEYS.some((k) => k in changes)) refresh();
  });

  // Only frames that actually play something answer, so the popup gets the player's frame.
  browser.runtime.onMessage.addListener((msg) => {
    if (!msg || msg.type !== "nv-media") return undefined;
    document.dispatchEvent(new CustomEvent("nightverb:report"));
    if (media && media.playing > 0) return Promise.resolve(media);
    return undefined;
  });

  refresh();
})();
