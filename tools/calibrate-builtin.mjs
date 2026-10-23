// Compares the browser's built-in reverb loudness with the Windows engine, per preset.
// Usage: node tools/calibrate-builtin.mjs   (needs dist/nightverb.exe for the engine side)
//
// For a (centred) white-noise input, the built-in reverb's output level is
//   wetGain * || highpass(lowpass(impulse)) ||
// so no audio rendering is needed: the impulse is built with the exact code from page.js.
import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const SR = 48000;

// ---- pull buildImpulse() out of page.js ----
const page = fs.readFileSync(path.join(root, "extension/page.js"), "utf8");
const start = page.indexOf("  function buildImpulse(");
const end = page.indexOf("    return buf;\n  }", start) + "    return buf;\n  }".length;
const buildImpulse = vm.runInNewContext(`(${page.slice(start, end).trim()})`, { Math });

const ctx = {
  sampleRate: SR,
  createBuffer(ch, len) {
    const data = Array.from({ length: ch }, () => new Float32Array(len));
    return { getChannelData: (i) => data[i] };
  },
};

// Web Audio biquads (spec formulas), Q in dB
function biquad(type, f0, qDb) {
  const w0 = (2 * Math.PI * f0) / SR, cos = Math.cos(w0);
  const alpha = Math.sin(w0) / (2 * Math.pow(10, qDb / 20));
  const a0 = 1 + alpha, a1 = -2 * cos, a2 = 1 - alpha;
  const [b0, b1, b2] = type === "lowpass" ? [(1 - cos) / 2, 1 - cos, (1 - cos) / 2] : [(1 + cos) / 2, -(1 + cos), (1 + cos) / 2];
  return (x) => {
    const y = new Float32Array(x.length);
    let x1 = 0, x2 = 0, y1 = 0, y2 = 0;
    for (let i = 0; i < x.length; i++) {
      const v = (b0 * x[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2) / a0;
      x2 = x1; x1 = x[i]; y2 = y1; y1 = v; y[i] = v;
    }
    return y;
  };
}

const presets = fs.readFileSync(path.join(root, "extension/presets.js"), "utf8");
const list = [...presets.matchAll(/id: "(\w+)",[^\n]*\n\s*params: (\{[^}]+\})/g)].map((m) => ({ id: m[1], p: vm.runInNewContext(`(${m[2]})`) }));

const engine = Object.fromEntries(
  execFileSync(path.join(root, "dist/nightverb.exe"), ["--level-table"], { encoding: "utf8" })
    .trim().split(/\r?\n/).map((l) => l.trim().split(/\s+/)).map(([k, v]) => [k, Number(v)])
);

let sum = 0;
console.log("preset      engine   built-in   diff");
for (const { id, p } of list) {
  const ir = buildImpulse(ctx, p);
  const w = p.wet / 100, wetGain = w * w * 2;
  let e = 0;
  for (const c of [0, 1]) {
    const f = biquad("highpass", p.lowCut, -3.0103)(biquad("lowpass", p.highCut, -3.0103)(ir.getChannelData(c)));
    for (const v of f) e += v * v;
  }
  const builtin = 20 * Math.log10(wetGain * Math.sqrt(e / 2));
  const diff = builtin - engine[id];
  sum += diff;
  console.log(`${id.padEnd(10)} ${engine[id].toFixed(1).padStart(6)} ${builtin.toFixed(1).padStart(9)} ${diff.toFixed(1).padStart(7)}`);
}
console.log(`mean diff ${(sum / list.length).toFixed(2)} dB`);
