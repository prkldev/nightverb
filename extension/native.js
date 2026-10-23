"use strict";

// Connection to the Nightverb helper (native messaging host "nightverb").
// The helper starts the audio engine on first contact; the engine keeps
// running on its own until Firefox closes.

const NV_HOST = "nightverb";

class NvConnection {
  constructor({ onMessage, onDisconnect } = {}) {
    this.onMessage = onMessage || (() => {});
    this.onDisconnect = onDisconnect || (() => {});
    this.port = null;
  }

  open() {
    this.port = browser.runtime.connectNative(NV_HOST);
    this.port.onMessage.addListener((m) => this.onMessage(m));
    this.port.onDisconnect.addListener((p) => {
      const err = (p && p.error && p.error.message) || (browser.runtime.lastError && browser.runtime.lastError.message) || "";
      this.port = null;
      this.onDisconnect(err);
    });
    return this;
  }

  send(msg) {
    if (!this.port) return false;
    try {
      this.port.postMessage(msg);
      return true;
    } catch (e) {
      return false;
    }
  }

  close() {
    if (this.port) {
      try { this.port.disconnect(); } catch (e) { /* already gone */ }
      this.port = null;
    }
  }
}

/** One-shot: send messages, resolve with the first engine state (or reject). */
function nvTalk(messages, timeoutMs = 8000) {
  return new Promise((resolve, reject) => {
    let done = false;
    const conn = new NvConnection({
      onMessage: (m) => {
        if (done) return;
        if (m && m.type === "state") {
          done = true;
          clearTimeout(timer);
          conn.close();
          resolve(m);
        } else if (m && m.type === "error") {
          done = true;
          clearTimeout(timer);
          conn.close();
          reject(new Error(m.error || "engine error"));
        }
      },
      onDisconnect: (err) => {
        if (done) return;
        done = true;
        clearTimeout(timer);
        reject(new Error(err || "disconnected"));
      },
    });
    const timer = setTimeout(() => {
      if (done) return;
      done = true;
      conn.close();
      reject(new Error("timeout"));
    }, timeoutMs);
    try {
      conn.open();
      for (const m of messages) conn.send(m);
    } catch (e) {
      done = true;
      clearTimeout(timer);
      reject(e);
    }
  });
}

function nvIsMissingHost(err) {
  const s = String((err && err.message) || err || "").toLowerCase();
  return s.includes("no such native application") || s.includes("not found") || s.includes("native application");
}
