// Renders extension/icons/icon.svg to transparent PNGs (48, 96, 128) with Firefox itself,
// over WebDriver BiDi: the SVG is drawn on a canvas and read back with toDataURL.
//   node tools/render-icons.mjs
import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const svg = fs.readFileSync(path.join(root, "extension/icons/icon.svg"), "utf8");
const SIZES = [48, 96, 128];
const PORT = 9334;

const profile = fs.mkdtempSync(path.join(os.tmpdir(), "nv-icons-"));
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
const cmd = (method, params = {}) => new Promise((res) => { const i = ++id; pending.set(i, res); ws.send(JSON.stringify({ id: i, method, params })); });

try {
  await cmd("session.new", { capabilities: {} });
  const ctx = (await cmd("browsingContext.getTree", {})).result.contexts[0].context;
  const expr = `(async () => {
    const img = new Image();
    img.src = "data:image/svg+xml;base64," + ${JSON.stringify(Buffer.from(svg).toString("base64"))};
    await img.decode();
    const out = {};
    for (const s of ${JSON.stringify(SIZES)}) {
      const c = document.createElement("canvas");
      c.width = c.height = s;
      c.getContext("2d").drawImage(img, 0, 0, s, s);
      out[s] = c.toDataURL("image/png");
    }
    return JSON.stringify(out);
  })()`;
  const r = await cmd("script.evaluate", { expression: expr, target: { context: ctx }, awaitPromise: true });
  const urls = JSON.parse(r.result.result.value);
  for (const s of SIZES) {
    const file = path.join(root, "extension/icons", `icon-${s}.png`);
    fs.writeFileSync(file, Buffer.from(urls[s].split(",")[1], "base64"));
    console.log(file, fs.statSync(file).size, "bytes");
  }
} finally {
  try { await cmd("session.end", {}); } catch (e) { /* closing */ }
  ff.kill();
  await sleep(1500);
  try { fs.rmSync(profile, { recursive: true, force: true }); } catch (e) { /* Firefox may still hold files */ }
  process.exit(0);
}
