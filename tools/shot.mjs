// Crisp screenshots with Firefox over WebDriver BiDi (real device-pixel ratio, unlike --screenshot).
//   node tools/shot.mjs <url> <width> <height> <dpr> <out.png> [wait ms]
import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const [url, w, h, dpr, out, waitMs = "1500"] = process.argv.slice(2);
if (!out) { console.log("usage: node tools/shot.mjs <url> <width> <height> <dpr> <out.png> [wait ms]"); process.exit(2); }
const PORT = 9335;
const profile = fs.mkdtempSync(path.join(os.tmpdir(), "nv-shot-"));
const ff = spawn("C:\\Program Files\\Mozilla Firefox\\firefox.exe",
  ["-no-remote", "-profile", profile, "--headless", `--remote-debugging-port=${PORT}`, "about:blank"], { stdio: "ignore" });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let ws;
for (let i = 0; i < 60 && !ws; i++) {
  await sleep(500);
  try {
    ws = await new Promise((res, rej) => { const s = new WebSocket(`ws://127.0.0.1:${PORT}/session`); s.onopen = () => res(s); s.onerror = rej; });
  } catch (e) { ws = null; }
}
let id = 0;
const pending = new Map();
ws.onmessage = (ev) => { const m = JSON.parse(ev.data); if (pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } };
const cmd = (method, params = {}) => new Promise((res, rej) => {
  const i = ++id;
  pending.set(i, (m) => (m.type === "error" ? rej(new Error(`${m.error}: ${m.message}`)) : res(m.result)));
  ws.send(JSON.stringify({ id: i, method, params }));
});

try {
  await cmd("session.new", { capabilities: {} });
  const ctx = (await cmd("browsingContext.getTree", {})).contexts[0].context;
  await cmd("browsingContext.setViewport", { context: ctx, viewport: { width: Number(w), height: Number(h) }, devicePixelRatio: Number(dpr) });
  await cmd("browsingContext.navigate", { context: ctx, url, wait: "complete" });
  await sleep(Number(waitMs));
  const shot = await cmd("browsingContext.captureScreenshot", { context: ctx, origin: "viewport" });
  fs.writeFileSync(out, Buffer.from(shot.data, "base64"));
  console.log(out);
} catch (e) {
  console.log("ERROR", e.message);
} finally {
  try { await cmd("session.end", {}); } catch (e) { /* closing */ }
  ff.kill();
  await sleep(1500);
  try { fs.rmSync(profile, { recursive: true, force: true }); } catch (e) { /* still locked */ }
  process.exit(0);
}
