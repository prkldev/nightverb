// What happens when Nightverb is installed while music already plays in a tab?
// Opens a page with a playing player FIRST, installs the extension AFTER, then reads
// Nightverb's report from the page. The popup asks for a reload when it sees
// late=true with speedControlled=0 (or no report at all).
//   node tools/serve.mjs   (in another window)
//   node tools/late-install-test.mjs
import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const PORT = 9336;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const profile = fs.mkdtempSync(path.join(os.tmpdir(), "nv-late-"));
fs.writeFileSync(path.join(profile, "user.js"), 'user_pref("media.autoplay.default", 0);\n');
const ff = spawn("C:\\Program Files\\Mozilla Firefox\\firefox.exe",
  ["-no-remote", "-profile", profile, "--headless", `--remote-debugging-port=${PORT}`, "about:blank"], { stdio: "ignore" });

let ws;
for (let i = 0; i < 60 && !ws; i++) {
  await sleep(500);
  try { ws = await new Promise((res, rej) => { const s = new WebSocket(`ws://127.0.0.1:${PORT}/session`); s.onopen = () => res(s); s.onerror = rej; }); } catch (e) { ws = null; }
}
let id = 0;
const pending = new Map();
ws.onmessage = (ev) => { const m = JSON.parse(ev.data); if (pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } };
const cmd = (method, params = {}) => new Promise((res, rej) => {
  const i = ++id;
  pending.set(i, (m) => (m.type === "error" ? rej(new Error(`${m.error}: ${m.message}`)) : res(m.result)));
  ws.send(JSON.stringify({ id: i, method, params }));
});
const evaluate = async (context, expression) =>
  (await cmd("script.evaluate", { expression, target: { context }, awaitPromise: true })).result.value;

// page with one player, either kept off the page (like Yandex) or in the page (like YouTube)
const pageFor = (offDom) => "data:text/html," + encodeURIComponent(`<!doctype html><title>late</title><script>
  const a = new Audio("http://127.0.0.1:8737/silence.wav"); a.loop = true;
  ${offDom ? "" : "document.documentElement.appendChild(a);"}
  a.play();
  window.__a = a;
</script>`);

try {
  await cmd("session.new", { capabilities: {} });
  const tree = await cmd("browsingContext.getTree", {});
  const ctxA = tree.contexts[0].context;
  const ctxB = (await cmd("browsingContext.create", { type: "tab" })).context;
  await cmd("browsingContext.navigate", { context: ctxA, url: pageFor(true), wait: "complete" });
  await cmd("browsingContext.navigate", { context: ctxB, url: pageFor(false), wait: "complete" });
  await sleep(1000);

  await cmd("webExtension.install", { extensionData: { type: "path", path: path.join(root, "extension") } });
  await sleep(3000);

  for (const [name, ctx] of [["off-page player (like Yandex)", ctxA], ["in-page player (like YouTube)", ctxB]]) {
    const r = await evaluate(ctx, `new Promise((resolve) => {
      let got = null;
      document.addEventListener("nightverb:media", (e) => { got = e.detail; });
      document.dispatchEvent(new CustomEvent("nightverb:report"));
      setTimeout(() => resolve(JSON.stringify({ report: got, realRate: window.__a.playbackRate, playing: !window.__a.paused })), 300);
    })`);
    console.log(`${name}: ${r}`);
  }
} catch (e) {
  console.log("ERROR", e.message);
} finally {
  try { await cmd("session.end", {}); } catch (e) { /* closing */ }
  ff.kill();
  await sleep(1500);
  try { fs.rmSync(profile, { recursive: true, force: true }); } catch (e) { /* locked */ }
  process.exit(0);
}
