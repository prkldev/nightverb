// Tiny server for tools/ test pages: node tools/serve.mjs [port]
//   /<file>          files from tools/
//   /delay?ms=N      answers after N ms (holds a page's load event, for timed screenshots)
//   /click.wav       20 s: a short quiet click every 2 s, silence between (reverb tails show up there)
//   /silence.wav     20 s of silence
//   add ?cors=1 to a .wav to send Access-Control-Allow-Origin: *
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const dir = path.dirname(fileURLToPath(import.meta.url));
const port = Number(process.argv[2] || 8737);

function wav(seconds, clicks) {
  const sr = 48000, n = sr * seconds;
  const buf = Buffer.alloc(44 + n * 2);
  buf.write("RIFF", 0); buf.writeUInt32LE(36 + n * 2, 4); buf.write("WAVE", 8); buf.write("fmt ", 12);
  buf.writeUInt32LE(16, 16); buf.writeUInt16LE(1, 20); buf.writeUInt16LE(1, 22);
  buf.writeUInt32LE(sr, 24); buf.writeUInt32LE(sr * 2, 28); buf.writeUInt16LE(2, 32); buf.writeUInt16LE(16, 34);
  buf.write("data", 36); buf.writeUInt32LE(n * 2, 40);
  if (clicks) {
    for (let start = 0; start < n; start += sr * 2) {
      for (let i = 0; i < 240 && start + i < n; i++) {
        const v = Math.sin((2 * Math.PI * 1000 * i) / sr) * Math.exp(-i / 60) * 0.3;
        buf.writeInt16LE(Math.round(v * 32767), 44 + (start + i) * 2);
      }
    }
  }
  return buf;
}
const CLICK = wav(20, true), SILENCE = wav(20, false);

http.createServer((req, res) => {
  const u = new URL(req.url, "http://x");
  const cors = u.searchParams.get("cors") === "1" ? { "access-control-allow-origin": "*" } : {};
  if (u.pathname === "/delay") {
    setTimeout(() => res.writeHead(200, { "content-type": "text/plain" }).end("ok"), Number(u.searchParams.get("ms") || 1000));
    return;
  }
  if (u.pathname === "/click.wav" || u.pathname === "/silence.wav") {
    res.writeHead(200, { "content-type": "audio/wav", ...cors }).end(u.pathname === "/click.wav" ? CLICK : SILENCE);
    return;
  }
  const name = path.basename(decodeURIComponent(u.pathname)) || "speed-test.html";
  const file = path.join(dir, name);
  if (!file.startsWith(dir) || !fs.existsSync(file)) {
    res.writeHead(404).end("not found");
    return;
  }
  res.writeHead(200, { "content-type": name.endsWith(".html") ? "text/html; charset=utf-8" : "application/octet-stream" });
  fs.createReadStream(file).pipe(res);
}).listen(port, "127.0.0.1", () => console.log(`http://127.0.0.1:${port}/speed-test.html`));
