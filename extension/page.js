// Nightverb in-page engine. Runs in the page's own JavaScript world (not the
// extension sandbox) so it sees every <audio>/<video> the site makes, including
// ones never added to the page (new Audio()) and ones hidden in shadow DOM.
//
// 1) Speed. Sites love to reset the speed (new track -> playbackRate = 1).
//    While Nightverb is active we keep the real speed ourselves and show the
//    site the value it last asked for, so it never notices and never fights back.
//
// 2) Built-in reverb (no Windows component needed), two ways:
//    a) Plain players: the media element is routed through our own Web Audio
//       graph (dry straight to the speakers + a convolution reverb).
//    b) Sites with their own Web Audio graph (Yandex Music does this): when the
//       site connects its graph to the speakers, the reverb is slipped in right
//       before them. The site's player keeps working exactly as before.
//    Protected (DRM) or cross-origin audio would go silent if routed, so a) never
//    touches it; live streams (calls) are never touched at all.
(() => {
  "use strict";

  const P = HTMLMediaElement.prototype;
  const desc = (name) => Object.getOwnPropertyDescriptor(P, name);
  const RATE = desc("playbackRate");
  const DEF_RATE = desc("defaultPlaybackRate");
  const PITCH = desc("preservesPitch");
  if (!RATE || !RATE.set || !DEF_RATE || !DEF_RATE.set) return;

  const nativePlay = P.play;
  const nativeAttachShadow = Element.prototype.attachShadow;
  const AC = window.AudioContext;
  const HAS_WEB_AUDIO = typeof AC === "function" && typeof AudioNode === "function";
  const nativeCreateMES = HAS_WEB_AUDIO ? AC.prototype.createMediaElementSource : null;
  const nativeCreateMSS = HAS_WEB_AUDIO ? AC.prototype.createMediaStreamSource : null;
  const nativeConnect = HAS_WEB_AUDIO ? AudioNode.prototype.connect : null;
  const nativeDisconnect = HAS_WEB_AUDIO ? AudioNode.prototype.disconnect : null;

  // active: speed is on; rate: playback speed; pitch: tone follows speed (real "slowed")
  let speed = { active: false, rate: 1, pitch: true };

  // Injected into a page that was already open (extension just installed or updated):
  // players created before us can't be found until they act, so the popup may ask for a reload.
  const lateStart = document.readyState !== "loading";

  const siteValues = new WeakMap(); // element -> what the site last set { playbackRate, defaultPlaybackRate, preservesPitch }
  const controlled = new WeakSet();  // elements whose real speed values are ours right now
  const known = new Set();           // WeakRefs to every media element we've met (incl. ones not in the page)
  const knownSet = new WeakSet();
  const shadowRoots = new Set();     // WeakRefs to shadow roots (players hide there)
  const encrypted = new WeakSet();   // elements that reported DRM-encrypted media

  function remember(el) {
    if (!knownSet.has(el)) {
      knownSet.add(el);
      known.add(new WeakRef(el));
    }
  }

  const MEDIA_EVENTS = ["play", "playing", "pause", "ended", "loadstart", "loadeddata", "ratechange", "encrypted"];

  // Calls (Meet, Discord, Telegram) play live streams; never touch those.
  function isLive(el) {
    try {
      return typeof MediaStream !== "undefined" && el.srcObject instanceof MediaStream;
    } catch (e) {
      return false;
    }
  }

  // ======================================================================
  // Speed
  // ======================================================================

  function siteVals(el) {
    let v = siteValues.get(el);
    if (!v) {
      v = {
        playbackRate: RATE.get.call(el),
        defaultPlaybackRate: DEF_RATE.get.call(el),
        preservesPitch: PITCH ? PITCH.get.call(el) : true,
      };
      siteValues.set(el, v);
    }
    return v;
  }

  function applySpeed(el) {
    try {
      if (speed.active && !isLive(el)) {
        siteVals(el);
        controlled.add(el);
        if (DEF_RATE.get.call(el) !== speed.rate) DEF_RATE.set.call(el, speed.rate);
        if (RATE.get.call(el) !== speed.rate) RATE.set.call(el, speed.rate);
        if (PITCH) {
          const keep = !speed.pitch;
          if (PITCH.get.call(el) !== keep) PITCH.set.call(el, keep);
        }
      } else if (controlled.has(el)) {
        // Hand control back: restore exactly what the site wanted.
        controlled.delete(el);
        const v = siteVals(el);
        DEF_RATE.set.call(el, v.defaultPlaybackRate);
        RATE.set.call(el, v.playbackRate);
        if (PITCH) PITCH.set.call(el, v.preservesPitch);
      }
    } catch (e) {
      /* a single odd element must never break the page */
    }
  }

  // The site reads/writes "its" values while we hold the real ones.
  function shim(name, original, isBool) {
    Object.defineProperty(P, name, {
      configurable: true,
      enumerable: original.enumerable,
      get() {
        return controlled.has(this) ? siteVals(this)[name] : original.get.call(this);
      },
      set(value) {
        if (controlled.has(this) && speed.active) {
          const v = isBool ? Boolean(value) : Number(value);
          if (!isBool && !Number.isFinite(v)) {
            throw new TypeError(`Failed to set the '${name}' property on 'HTMLMediaElement': The provided double value is non-finite.`);
          }
          siteVals(this)[name] = v;
          return;
        }
        original.set.call(this, value);
        if (siteValues.has(this)) siteVals(this)[name] = original.get.call(this);
      },
    });
  }

  shim("playbackRate", RATE, false);
  shim("defaultPlaybackRate", DEF_RATE, false);
  if (PITCH) shim("preservesPitch", PITCH, true);

  // ======================================================================
  // Built-in reverb: the effect chain (used in our own graph and in sites' graphs)
  // ======================================================================

  const RV = {
    on: false,
    params: null,
    ctx: null,               // our own context, for plain players
    ownChain: null,
    resuming: false,
    attached: new WeakSet(), // elements routed through our own context
    chains: new Set(),       // every live effect chain (ours + inserted into sites' graphs)
  };

  /**
   * Reverb chain inside `ctx`: in (mono) -> pre-delay -> low cut -> high cut ->
   * convolution -> wet gain -> limiter -> out. Only the reverb, the dry sound
   * goes around it.
   */
  function makeChain(ctx, out) {
    const input = ctx.createGain();
    input.channelCount = 1; // mono into the reverb, it spreads it back to stereo
    input.channelCountMode = "explicit";
    input.channelInterpretation = "speakers";
    const pre = ctx.createDelay(0.5);
    // Web Audio's lowpass/highpass Q is in dB: -3.01 dB = Butterworth (flat, no bump),
    // the same 12 dB/oct filters the Windows engine uses.
    const hp = ctx.createBiquadFilter();
    hp.type = "highpass";
    hp.Q.value = -3.0103;
    const lp = ctx.createBiquadFilter();
    lp.type = "lowpass";
    lp.Q.value = -3.0103;
    const conv = ctx.createConvolver();
    conv.normalize = false;
    const wet = ctx.createGain();
    wet.gain.value = 0;
    // safety limiter on the reverb only (the original sound is never compressed)
    const lim = ctx.createDynamicsCompressor();
    lim.threshold.value = -4;
    lim.knee.value = 3;
    lim.ratio.value = 20;
    lim.attack.value = 0.002;
    lim.release.value = 0.15;
    for (const [a, b] of [[input, pre], [pre, hp], [hp, lp], [lp, conv], [conv, wet], [wet, lim], [lim, out]]) nativeConnect.call(a, b);
    const chain = { ctx, input, pre, hp, lp, conv, wet, irKey: "", irTimer: 0 };
    RV.chains.add(chain);
    applyChain(chain, true);
    return chain;
  }

  function applyChain(c, now) {
    if (!RV.params || c.ctx.state === "closed") return;
    const p = RV.params;
    const t = c.ctx.currentTime;
    const w = p.wet / 100;
    c.wet.gain.setTargetAtTime(RV.on ? w * w * 2 : 0, t, 0.06);
    c.pre.delayTime.setTargetAtTime(Math.min(0.49, p.preDelay / 1000), t, 0.03);
    c.hp.frequency.setTargetAtTime(p.lowCut, t, 0.03);
    c.lp.frequency.setTargetAtTime(Math.min(p.highCut, c.ctx.sampleRate * 0.45), t, 0.03);

    const key = [p.decay, p.size, p.diffusion, p.damping, p.width].join("|");
    if (key !== c.irKey) {
      c.irKey = key;
      clearTimeout(c.irTimer);
      const build = () => {
        try {
          c.conv.buffer = buildImpulse(c.ctx, p);
        } catch (e) { /* out of memory or closed context */ }
      };
      if (now) build();
      else c.irTimer = setTimeout(build, 150);
    }
  }

  function applyReverbParams() {
    for (const c of RV.chains) {
      if (c.ctx.state === "closed") RV.chains.delete(c);
      else applyChain(c, false);
    }
  }

  // ---------------------------------------------------------------- a) our own graph

  /** ok | protected | blocked | live | idle */
  function classify(el) {
    if (isLive(el)) return "live";
    if (el.mediaKeys || encrypted.has(el)) return "protected";
    const src = el.currentSrc || el.src || "";
    if (!src) return "idle";
    if (/^(blob|data):/i.test(src)) return "ok";
    try {
      if (new URL(src, location.href).origin === location.origin) return "ok";
    } catch (e) { /* fall through */ }
    if (el.crossOrigin === "anonymous" || el.crossOrigin === "use-credentials") return "ok";
    return "blocked"; // cross-origin without CORS: routing it would output silence
  }

  function ownContext() {
    if (RV.ctx) return RV.ctx;
    if (!HAS_WEB_AUDIO) return null;
    RV.ctx = new AC();
    RV.ownChain = makeChain(RV.ctx, RV.ctx.destination);
    return RV.ctx;
  }

  function attachReverb(el) {
    if (!RV.on || !RV.params || RV.attached.has(el) || siteGraph.elements.has(el)) return;
    if (classify(el) !== "ok" || el.paused) return;
    const ctx = ownContext();
    if (!ctx) return;
    if (ctx.state !== "running") {
      // Never route audio into a context that isn't running: that would mute it.
      if (!RV.resuming) {
        RV.resuming = true;
        ctx.resume().then(
          () => { RV.resuming = false; sweep(); },
          () => { RV.resuming = false; }
        );
      }
      return;
    }
    try {
      const src = nativeCreateMES.call(ctx, el);
      nativeConnect.call(src, ctx.destination);   // dry, untouched
      nativeConnect.call(src, RV.ownChain.input); // into the reverb
      RV.attached.add(el);
    } catch (e) {
      /* already owned by another graph: leave it */
    }
    report();
  }

  // ---------------------------------------------------------------- b) the site's graph

  const siteGraph = {
    mediaCtx: new Set(),     // WeakRefs: site contexts that play media elements
    mediaCtxSet: new WeakSet(),
    liveCtx: new WeakSet(),  // contexts that play live streams (calls): never touched
    elements: new WeakMap(), // media element -> the site context it plays through
    links: new WeakMap(),    // ctx -> Set<WeakRef<AudioNode>> connected straight to the speakers
    inserts: new WeakMap(),  // ctx -> { input } where rerouted nodes now go
    rerouted: new WeakSet(), // nodes we redirected into an insert
  };

  function isSiteSpeakers(d) {
    return HAS_WEB_AUDIO && d instanceof AudioDestinationNode && d.context !== RV.ctx &&
      !(typeof OfflineAudioContext === "function" && d.context instanceof OfflineAudioContext);
  }

  function siteInsert(ctx) {
    let ins = siteGraph.inserts.get(ctx);
    if (!ins) {
      const input = ctx.createGain();                 // everything the site sends to the speakers
      nativeConnect.call(input, ctx.destination);     // dry, untouched
      const chain = makeChain(ctx, ctx.destination);
      nativeConnect.call(input, chain.input);         // plus the reverb
      ins = { input, chain };
      siteGraph.inserts.set(ctx, ins);
    }
    return ins;
  }

  function shouldInsert(ctx) {
    return RV.on && siteGraph.mediaCtxSet.has(ctx) && !siteGraph.liveCtx.has(ctx);
  }

  function reroute(node, ctx) {
    if (siteGraph.rerouted.has(node)) return;
    const ins = siteInsert(ctx);
    try {
      nativeDisconnect.call(node, ctx.destination);
    } catch (e) {
      return; // wasn't connected (any more)
    }
    nativeConnect.call(node, ins.input);
    siteGraph.rerouted.add(node);
  }

  function rerouteContext(ctx) {
    if (!shouldInsert(ctx)) return;
    const links = siteGraph.links.get(ctx);
    if (!links) return;
    for (const ref of links) {
      const node = ref.deref();
      if (node) reroute(node, ctx);
      else links.delete(ref);
    }
  }

  if (HAS_WEB_AUDIO) {
    AC.prototype.createMediaElementSource = function createMediaElementSource(el) {
      const node = nativeCreateMES.apply(this, arguments);
      if (this !== RV.ctx) {
        siteGraph.elements.set(el, this);
        if (!siteGraph.mediaCtxSet.has(this)) {
          siteGraph.mediaCtxSet.add(this);
          siteGraph.mediaCtx.add(new WeakRef(this));
        }
        rerouteContext(this);
        scheduleReport();
      }
      return node;
    };

    if (nativeCreateMSS) {
      AC.prototype.createMediaStreamSource = function createMediaStreamSource() {
        siteGraph.liveCtx.add(this);
        return nativeCreateMSS.apply(this, arguments);
      };
    }

    AudioNode.prototype.connect = function connect(dest) {
      if (isSiteSpeakers(dest)) {
        const ctx = dest.context;
        let links = siteGraph.links.get(ctx);
        if (!links) siteGraph.links.set(ctx, (links = new Set()));
        if (links.size > 256) {
          // games and apps can connect thousands of short sounds: drop the dead ones
          for (const ref of links) if (!ref.deref()) links.delete(ref);
        }
        links.add(new WeakRef(this));
        if (shouldInsert(ctx)) {
          const args = Array.from(arguments);
          args[0] = siteInsert(ctx).input;
          nativeConnect.apply(this, args);
          siteGraph.rerouted.add(this);
          return dest; // same return value as a real connect to the speakers
        }
      }
      return nativeConnect.apply(this, arguments);
    };

    AudioNode.prototype.disconnect = function disconnect(dest) {
      if (dest && isSiteSpeakers(dest) && siteGraph.rerouted.has(this)) {
        const ins = siteGraph.inserts.get(dest.context);
        if (ins) {
          const args = Array.from(arguments);
          args[0] = ins.input;
          siteGraph.rerouted.delete(this);
          return nativeDisconnect.apply(this, args);
        }
      }
      if (arguments.length === 0) siteGraph.rerouted.delete(this);
      return nativeDisconnect.apply(this, arguments);
    };
  }

  function rerouteAllSites() {
    for (const ref of siteGraph.mediaCtx) {
      const ctx = ref.deref();
      if (!ctx || ctx.state === "closed") siteGraph.mediaCtx.delete(ref);
      else rerouteContext(ctx);
    }
  }

  // ---------------------------------------------------------------- impulse response

  // Reverb impulse response: dense decorrelated noise with an exponential
  // decay (-60 dB at "decay"), a build-up that grows with room size and
  // diffusion, early reflections, and a tail that darkens over time (damping).
  // Its energy is normalized to match the Windows engine's loudness.
  function buildImpulse(ctx, p) {
    const sr = ctx.sampleRate;
    const decay = p.decay;
    const size = p.size / 100;
    const diff = p.diffusion / 100;
    const width = p.width / 100;
    const len = Math.max(1, Math.min(Math.ceil(sr * (decay * 1.25 + 0.2)), sr * 20));
    const buf = ctx.createBuffer(2, len, sr);
    const L = buf.getChannelData(0);
    const R = buf.getChannelData(1);

    let seed = 0x9e3779b9;
    const rnd = () => {
      seed = (seed + 0x6d2b79f5) | 0;
      let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
      t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
      return (((t ^ (t >>> 14)) >>> 0) / 4294967296) * 2 - 1;
    };

    const k = 6.907755 / decay;                          // ln(1000): -60 dB at t = decay
    const build = 0.003 + 0.045 * size * (0.35 + 0.65 * diff); // seconds to reach full density
    const fadeStart = Math.floor(len * 0.85);
    let zl = 0, zr = 0;

    for (let i = 0; i < len; i++) {
      const t = i / sr;
      let env = Math.exp(-k * t) * (1 - Math.exp(-t / build));
      if (i > fadeStart) env *= (len - i) / (len - fadeStart);
      const a = rnd() + rnd(), b = rnd() + rnd();       // roughly gaussian
      const m = (a + b) * 0.5, s = (a - b) * 0.5 * width;
      const l = (m + s) * env, r = (m - s) * env;
      // tail gets darker as it decays
      const fc = Math.max(250, p.damping * Math.exp((-1.3 * t) / decay));
      const c = Math.exp((-2 * Math.PI * Math.min(fc, 0.45 * sr)) / sr);
      zl = l + (zl - l) * c;
      zr = r + (zr - r) * c;
      L[i] = zl;
      R[i] = zr;
    }

    // early reflections: fewer and sharper at low diffusion, spread by size
    const taps = 6 + Math.round(10 * (1 - diff));
    for (let j = 0; j < taps; j++) {
      const tl = 0.003 + (0.012 + 0.07 * size) * ((j + 0.5 + rnd() * 0.4) / taps);
      const tr = tl * (1 + 0.07 * rnd());
      const amp = 0.9 * (1 - j / taps) * (1.1 - diff * 0.6) * Math.exp(-k * tl);
      const il = Math.floor(tl * sr), ir = Math.floor(tr * sr);
      if (il < len) L[il] += amp * (rnd() < 0 ? -1 : 1);
      if (ir < len) R[ir] += amp * (rnd() < 0 ? -1 : 1);
    }

    let e = 0;
    for (let i = 0; i < len; i++) e += L[i] * L[i] + R[i] * R[i];
    // Matched to the Windows engine (tools/calibrate-builtin.mjs): there, a darker tail
    // (lower damping) also comes out quieter, about 3.65 dB per octave of damping.
    const dampingTrim = Math.pow(10, (3.65 * (Math.log2(Math.max(500, p.damping)) - 12.69)) / 20);
    const target = 0.9 * Math.pow(decay / 2, 0.12) * Math.min(2, Math.max(0.5, dampingTrim));
    const g = e > 0 ? target / Math.sqrt(e / 2) : 0;
    for (let i = 0; i < len; i++) {
      L[i] *= g;
      R[i] *= g;
    }
    return buf;
  }

  // ======================================================================
  // Tracking elements
  // ======================================================================

  function touch(el) {
    if (!(el instanceof HTMLMediaElement)) return;
    applySpeed(el);
    attachReverb(el);
  }

  P.play = function play() {
    // Remember it even if everything is off right now: settings may arrive a moment
    // later, and a player that isn't in the page can't be found any other way.
    remember(this);
    if (speed.active) applySpeed(this);
    return nativePlay.apply(this, arguments);
  };

  function onMediaEvent(e) {
    const el = e.target;
    if (!(el instanceof HTMLMediaElement)) return;
    remember(el);
    if (e.type === "encrypted") encrypted.add(el);
    if (speed.active || controlled.has(el)) applySpeed(el);
    if (e.type === "playing") attachReverb(el);
    scheduleReport();
  }

  function listen(target) {
    for (const type of MEDIA_EVENTS) target.addEventListener(type, onMediaEvent, true);
  }

  if (typeof nativeAttachShadow === "function") {
    Element.prototype.attachShadow = function attachShadow() {
      const root = nativeAttachShadow.apply(this, arguments);
      try {
        shadowRoots.add(new WeakRef(root));
        listen(root);
      } catch (e) { /* ignore */ }
      return root;
    };
  }

  listen(window);

  function forEachMedia(fn) {
    const roots = [document];
    for (const ref of shadowRoots) {
      const r = ref.deref();
      if (r) roots.push(r);
      else shadowRoots.delete(ref);
    }
    for (const root of roots) {
      try {
        for (const el of root.querySelectorAll("audio, video")) remember(el);
      } catch (e) { /* ignore */ }
    }
    for (const ref of known) {
      const el = ref.deref();
      if (!el) {
        known.delete(ref);
        continue;
      }
      fn(el);
    }
  }

  function sweep() {
    forEachMedia(touch);
    report();
  }

  let timer = 0;
  function schedule() {
    const busy = speed.active || RV.on;
    if (busy && !timer) timer = setInterval(sweep, 1000);
    if (!busy && timer) {
      clearInterval(timer);
      timer = 0;
    }
  }

  // ======================================================================
  // Status for the popup: what kind of audio is playing on this page
  // ======================================================================

  let lastReport = "";
  let reportTimer = 0;

  function scheduleReport() {
    if (!reportTimer) reportTimer = setTimeout(report, 250);
  }

  function report() {
    clearTimeout(reportTimer);
    reportTimer = 0;
    const s = { playing: 0, builtin: 0, protected: 0, blocked: 0, siteGraph: 0, speedControlled: 0, late: lateStart };
    forEachMedia((el) => {
      if (controlled.has(el)) s.speedControlled++;
      if (el.paused || el.muted || isLive(el)) return;
      s.playing++;
      const siteCtx = siteGraph.elements.get(el);
      if (siteCtx) {
        // plays through the site's own graph: covered when our insert is in it
        s.siteGraph++;
        if (RV.on && siteGraph.inserts.has(siteCtx) && !siteGraph.liveCtx.has(siteCtx)) s.builtin++;
        return;
      }
      const kind = classify(el);
      if (kind === "protected") s.protected++;
      else if (kind === "blocked") s.blocked++;
      if (RV.on && RV.attached.has(el)) s.builtin++;
    });
    const json = JSON.stringify(s);
    if (json === lastReport) return;
    lastReport = json;
    document.dispatchEvent(new CustomEvent("nightverb:media", { detail: json }));
  }

  // ======================================================================
  // Settings from the extension (content.js)
  // ======================================================================

  document.addEventListener("nightverb:speed", (e) => {
    let s;
    try {
      s = JSON.parse(e.detail);
    } catch (err) {
      return;
    }
    const rate = Number(s.rate);
    speed = {
      active: Boolean(s.active),
      rate: Number.isFinite(rate) ? Math.min(4, Math.max(0.25, rate)) : 1,
      pitch: s.pitch !== false,
    };
    sweep();
    schedule();
  });

  document.addEventListener("nightverb:reverb", (e) => {
    let s;
    try {
      s = JSON.parse(e.detail);
    } catch (err) {
      return;
    }
    RV.on = Boolean(s.on);
    RV.params = s.params || RV.params;
    applyReverbParams();
    if (RV.on) rerouteAllSites();
    lastReport = "";
    sweep();
    schedule();
  });

  document.addEventListener("nightverb:report", () => {
    lastReport = "";
    report();
  });

  document.dispatchEvent(new CustomEvent("nightverb:hello"));
})();
