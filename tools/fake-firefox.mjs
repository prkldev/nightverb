// Talks to the installed native host exactly like Firefox does (length-prefixed JSON on stdio).
// Usage: node fake-firefox.mjs [seconds] [wet]
import { spawn } from "node:child_process";
import os from "node:os";
import path from "node:path";

const seconds = Number(process.argv[2] || 4);
const wet = Number(process.argv[3] || 2);
const dir = path.join(process.env.LOCALAPPDATA || os.homedir(), "Nightverb");
const exe = path.join(dir, "nightverb.exe");

const child = spawn(exe, [path.join(dir, "nightverb.json"), "nightverb@reverb.fx"], { stdio: ["pipe", "pipe", "inherit"] });

function send(obj) {
  const body = Buffer.from(JSON.stringify(obj), "utf8");
  const len = Buffer.alloc(4);
  len.writeUInt32LE(body.length);
  child.stdin.write(Buffer.concat([len, body]));
}

let buf = Buffer.alloc(0);
let maxIn = 0, maxOut = 0, meters = 0, lastState = null, underruns = 0;
child.stdout.on("data", (d) => {
  buf = Buffer.concat([buf, d]);
  while (buf.length >= 4) {
    const n = buf.readUInt32LE(0);
    if (buf.length < 4 + n) break;
    const msg = JSON.parse(buf.subarray(4, 4 + n).toString("utf8"));
    buf = buf.subarray(4 + n);
    if (msg.type === "meter") {
      meters++;
      maxIn = Math.max(maxIn, msg.in);
      maxOut = Math.max(maxOut, msg.out);
      underruns = msg.underruns;
    } else {
      if (msg.type === "state") lastState = msg;
      console.log("<-", JSON.stringify(msg));
    }
  }
});

const t0 = Date.now();
// Optional 4th arg: capture this PID instead of Firefox (for testing with any app that plays sound).
const targetPid = Number(process.argv[4] || 0);
send(targetPid ? { type: "hello", pid: targetPid } : { type: "hello" });
send({ type: "set", enabled: true, params: { wet } });
send({ type: "meters", on: true });

setTimeout(() => {
  console.log(`meters: ${meters} msgs in ${(Date.now() - t0) / 1000}s, max in ${maxIn}, max out ${maxOut}, underruns ${underruns}`);
  console.log(`final status: ${lastState && lastState.status}`);
  child.stdin.end();
  setTimeout(() => process.exit(0), 300);
}, seconds * 1000);
