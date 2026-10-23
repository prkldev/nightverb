// Stand-in for the WebExtension API so popup.html can be rendered as a plain page
// (layout previews / screenshots). Simulates a running engine with moving meters.
(() => {
  const store = {};
  const noopEvent = { addListener() {}, removeListener() {} };
  // Set window.NV_FAKE_MESSAGES (a _locales messages.json object) and NV_FAKE_LANG before this script.
  const messages = window.NV_FAKE_MESSAGES || {};
  window.browser = {
    i18n: {
      getUILanguage: () => window.NV_FAKE_LANG || "ru",
      getMessage(key, subs) {
        const m = messages[key];
        if (!m) return "";
        const list = subs === undefined ? [] : [].concat(subs);
        return m.message.replace(/\$(\d)/g, (_, n) => (list[n - 1] !== undefined ? String(list[n - 1]) : ""));
      },
    },
    storage: {
      local: {
        async get() { return { ...store }; },
        async set(o) { Object.assign(store, o); },
      },
      onChanged: noopEvent,
    },
    permissions: {
      async contains() { return !/noperm/.test(location.hash); },
      async request() { return true; },
    },
    runtime: {
      lastError: null,
      connectNative() {
        const msgL = [], discL = [];
        const missing = /nohelper/.test(location.hash);
        setTimeout(() => {
          if (missing) { discL.forEach((f) => f({ error: { message: "No such native application nightverb" } })); return; }
          msgL.forEach((f) => f({
            type: "state", status: "running", enabled: true, device: window.NV_FAKE_LANG === "en" ? "Speakers" : "Динамики",
            sampleRate: 48000, latencyMs: 44, firefoxPid: 1,
          }));
          setInterval(() => msgL.forEach((f) => f({ type: "meter", in: 0.3 + Math.random() * 0.5, out: 0.05 + Math.random() * 0.1 })), 50);
        }, 50);
        return {
          onMessage: { addListener: (f) => msgL.push(f) },
          onDisconnect: { addListener: (f) => discL.push(f) },
          postMessage() {},
          disconnect() {},
        };
      },
    },
  };
})();
