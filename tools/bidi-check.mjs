// Opens a site in a throwaway Firefox (driven over WebDriver BiDi) with Nightverb installed,
// optionally clicks a play button, then asks Nightverb what kind of audio is playing there.
//
//   node tools/bidi-check.mjs <url> [button text/aria-label to click] [seconds to wait]
//
// Prints Nightverb's report ({ playing, builtin, protected, blocked }) and the page's media
// elements; saves screenshots next to the system temp dir.
import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const [url, clickText = "", waitArg = "8"] = process.argv.slice(2);
if (!url) { console.log("usage: node tools/bidi-check.mjs <url> [click text] [wait s]"); process.exit(2); }
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const FIREFOX = "C:\\Program Files\\Mozilla Firefox\\firefox.exe";
const PORT = 9333;
const outDir = path.join(os.tmpdir(), "nightverb-bidi");
fs.mkdirSync(outDir, { recursive: true });

const profile = fs.mkdtempSync(path.join(os.tmpdir(), "nv-ff-"));
fs.writeFileSync(path.join(profile, "user.js"), [
  'user_pref("media.autoplay.default", 0);',          // allow sound without a real click
  'user_pref("browser.shell.checkDefaultBrowser", false);',
  'user_pref("datareporting.policy.dataSubmissionEnabled", false);',
  'user_pref("intl.locale.requested", "ru");',
].join("\n"));

const ff = spawn(FIREFOX, ["-no-remote", "-profile", profile, `--remote-debugging-port=${PORT}`, "about:blank"], { stdio: "ignore" });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let ws;
for (let i = 0; i < 60 && !ws; i++) {
  await sleep(500);
  try {
    ws = await new Promise((resolve, reject) => {
      const s = new WebSocket(`ws://127.0.0.1:${PORT}/session`);
      s.onopen = () => resolve(s);
      s.onerror = reject;
    });
  } catch (e) { ws = null; }
}
if (!ws) { console.log("could not connect to Firefox"); ff.kill(); process.exit(1); }

let nextId = 1;
const pending = new Map();
ws.onmessage = (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id && pending.has(m.id)) {
    const { resolve, reject } = pending.get(m.id);
    pending.delete(m.id);
    m.type === "error" ? reject(new Error(`${m.error}: ${m.message}`)) : resolve(m.result);
  }
};
const cmd = (method, params = {}) => new Promise((resolve, reject) => {
  const id = nextId++;
  pending.set(id, { resolve, reject });
  ws.send(JSON.stringify({ id, method, params }));
});
const evaluate = async (context, expression) => {
  const r = await cmd("script.evaluate", { expression, target: { context }, awaitPromise: true, userActivation: true });
  return r.result && r.result.value;
};
const shot = async (context, name) => {
  const r = await cmd("browsingContext.captureScreenshot", { context });
  const file = path.join(outDir, name);
  fs.writeFileSync(file, Buffer.from(r.data, "base64"));
  return file;
};

try {
  await cmd("session.new", { capabilities: {} });
  const ext = await cmd("webExtension.install", { extensionData: { type: "path", path: path.join(root, "extension") } });
  console.log("extension installed:", ext.extension);
  const tree = await cmd("browsingContext.getTree", {});
  const context = tree.contexts[0].context;
  await cmd("browsingContext.navigate", { context, url, wait: "complete" });
  await sleep(4000);
  console.log("screenshot:", await shot(context, "1-loaded.png"));

  // collect Nightverb's reports from now on
  await evaluate(context, `document.addEventListener("nightverb:media", e => { window.__nvMedia = e.detail; }); true`);

  if (clickText) {
    const clicked = await evaluate(context, `(() => {
      const want = ${JSON.stringify(clickText.toLowerCase())};
      const els = [...document.querySelectorAll("button, [role=button], a")];
      const el = els.find(b => ((b.getAttribute("aria-label") || "") + " " + (b.textContent || "")).toLowerCase().includes(want));
      if (!el) return "no match among " + els.length + " buttons";
      el.click();
      return "clicked: " + (el.getAttribute("aria-label") || el.textContent.trim()).slice(0, 80);
    })()`);
    console.log(clicked);
  }

  await sleep(Number(waitArg) * 1000);
  await evaluate(context, `document.dispatchEvent(new CustomEvent("nightverb:report")); true`);
  await sleep(500);
  const report = await evaluate(context, `window.__nvMedia || "no report"`);
  const elements = await evaluate(context, `JSON.stringify([...document.querySelectorAll("audio, video")].map(e => ({
    tag: e.tagName, paused: e.paused, muted: e.muted, src: (e.currentSrc || "").slice(0, 60),
    crossOrigin: e.crossOrigin, drm: !!e.mediaKeys, t: Math.round(e.currentTime * 10) / 10 })))`);
  console.log("nightverb report:", report);
  console.log("media in page:", elements);
  console.log("screenshot:", await shot(context, "2-after.png"));
} catch (e) {
  console.log("ERROR", e.message);
} finally {
  try { await cmd("session.end", {}); } catch (e) { /* closing */ }
  ff.kill();
  await sleep(1500);
  fs.rmSync(profile, { recursive: true, force: true });
  process.exit(0);
}
