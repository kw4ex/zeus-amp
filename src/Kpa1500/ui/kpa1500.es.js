// SPDX-License-Identifier: GPL-2.0-or-later
//
// Elecraft KPA1500 — Zeus workspace panel.
//
// Plain ESM with React.createElement (no build step). Zeus supplies `react`.
// Talks only to this feature's backend through callBackend. Styling uses only
// the public Zeus CSS tokens, scoped under the feature root class.

import React from "react";

const { useState, useEffect, useRef, useCallback } = React;
const h = React.createElement;

const ROOT = "com-kw4ex-zeus-kpa1500";
const PANEL_ID = "kpa1500.main";
const BANDS = ["160m", "80m", "60m", "40m", "30m", "20m", "17m", "15m", "12m", "10m", "6m"];
const BAUDS = [4800, 9600, 19200, 38400, 57600, 115200, 230400];
const PEAK_HOLD_MS = 2000;

// Secondary class names always carry the feature prefix.
const c = (names) => String(names || "").split(/\s+/).filter(Boolean)
  .map((n) => ROOT + "__" + n).join(" ");

const CSS = `
.${ROOT}{font-family:var(--font-sans);color:var(--fg-1);background:var(--bg-1);
  border:1px solid var(--panel-border);border-radius:var(--r-md);padding:8px;
  display:flex;flex-direction:column;gap:8px;box-sizing:border-box;width:100%;height:100%;
  overflow:auto;font-size:12px;position:relative;min-width:0}
.${ROOT} *{box-sizing:border-box}
.${ROOT} .${ROOT}__row{display:flex;align-items:center;gap:6px;flex-wrap:wrap;min-width:0}
.${ROOT} .${ROOT}__spread{justify-content:space-between}
.${ROOT} .${ROOT}__title{font-size:13px;font-weight:700;color:var(--fg-0);letter-spacing:.02em}
.${ROOT} .${ROOT}__sub{font-size:12px;color:var(--fg-1);overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.${ROOT} .${ROOT}__grow{flex:1 1 auto;min-width:0}
.${ROOT} .${ROOT}__pill{font-size:10px;font-weight:700;letter-spacing:.04em;padding:2px 7px;
  border-radius:var(--r-lg);border:1px solid var(--line-strong);color:var(--fg-0);background:var(--bg-2);white-space:nowrap}
.${ROOT} .${ROOT}__pill.${ROOT}__ok{color:var(--ok);border-color:var(--ok)}
.${ROOT} .${ROOT}__pill.${ROOT}__warn{color:var(--amber);border-color:var(--amber)}
.${ROOT} .${ROOT}__pill.${ROOT}__tx{color:var(--tx);border-color:var(--tx)}
.${ROOT} .${ROOT}__pill.${ROOT}__acc{color:var(--accent-bright);border-color:var(--accent)}
.${ROOT} .${ROOT}__banner{border:1px solid var(--line-strong);border-left-width:4px;border-radius:var(--r-sm);
  padding:6px 8px;background:var(--bg-2);display:flex;align-items:center;gap:8px;flex-wrap:wrap}
.${ROOT} .${ROOT}__banner.${ROOT}__tx{border-color:var(--tx)}
.${ROOT} .${ROOT}__banner.${ROOT}__warn{border-color:var(--amber)}
.${ROOT} .${ROOT}__banner.${ROOT}__acc{border-color:var(--accent)}
.${ROOT} .${ROOT}__banner strong{color:var(--fg-0)}
.${ROOT} .${ROOT}__meters{display:flex;flex-direction:column;gap:5px}
.${ROOT} .${ROOT}__meter{display:grid;grid-template-columns:minmax(52px,auto) 1fr minmax(58px,auto);align-items:center;gap:8px}
.${ROOT} .${ROOT}__mk{font-size:11px;font-weight:600;color:var(--fg-1);letter-spacing:.05em;text-transform:uppercase}
.${ROOT} .${ROOT}__mv{font-family:var(--font-mono);font-size:12px;color:var(--fg-0);text-align:right;white-space:nowrap}
.${ROOT} .${ROOT}__track{position:relative;height:10px;background:var(--bg-inset);border:1px solid var(--line-strong);border-radius:var(--r-xs);overflow:hidden}
.${ROOT} .${ROOT}__fill{position:absolute;left:0;top:0;bottom:0;background:var(--accent);transition:width var(--dur-fast) var(--ease-out)}
.${ROOT} .${ROOT}__fill.${ROOT}__warn{background:var(--amber)}
.${ROOT} .${ROOT}__fill.${ROOT}__tx{background:var(--tx)}
.${ROOT} .${ROOT}__peak{position:absolute;top:0;bottom:0;width:2px;background:var(--fg-0)}
.${ROOT} .${ROOT}__tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(78px,1fr));gap:5px}
.${ROOT} .${ROOT}__tile{background:var(--bg-2);border:1px solid var(--line-strong);border-radius:var(--r-sm);padding:4px 6px}
.${ROOT} .${ROOT}__tile.${ROOT}__warn{border-color:var(--amber)}
.${ROOT} .${ROOT}__tile .${ROOT}__mv{text-align:left;font-size:13px}
.${ROOT} .${ROOT}__group{border-top:1px solid var(--line-strong);padding-top:6px;display:flex;flex-direction:column;gap:5px}
.${ROOT} .${ROOT}__glab{font-size:11px;font-weight:700;color:var(--fg-1);letter-spacing:.05em;text-transform:uppercase}
.${ROOT} .${ROOT}__btns{display:flex;gap:4px;flex-wrap:wrap}
.${ROOT} button.${ROOT}__b{font-family:var(--font-sans);font-size:11px;font-weight:600;min-height:28px;
  padding:4px 11px;border-radius:var(--r-sm);border:1px solid var(--line-strong);background:var(--bg-3);
  color:var(--fg-0);cursor:pointer;transition:background var(--dur-fast) var(--ease-out)}
.${ROOT} button.${ROOT}__b:hover:not(:disabled){background:var(--bg-2)}
.${ROOT} button.${ROOT}__b:focus-visible,.${ROOT} .${ROOT}__in:focus-visible,.${ROOT} input[type=checkbox]:focus-visible,
.${ROOT} input[type=radio]:focus-visible{outline:2px solid var(--accent-bright);outline-offset:2px}
.${ROOT} button.${ROOT}__b:disabled{opacity:.75;border-style:dashed;cursor:not-allowed}
.${ROOT} button.${ROOT}__b[aria-pressed=true]{background:var(--accent);border-color:var(--accent);color:var(--bg-0);font-weight:700}
.${ROOT} button.${ROOT}__b.${ROOT}__litok[aria-pressed=true]{background:var(--ok);border-color:var(--ok)}
.${ROOT} button.${ROOT}__b.${ROOT}__litamber[aria-pressed=true]{background:var(--amber);border-color:var(--amber)}
.${ROOT} button.${ROOT}__b.${ROOT}__faint{opacity:.5}
.${ROOT} button.${ROOT}__b.${ROOT}__faint:hover:not(:disabled){opacity:.85}
.${ROOT} .${ROOT}__bands{display:grid;grid-template-columns:repeat(auto-fill,minmax(44px,1fr));gap:4px}
.${ROOT} .${ROOT}__bands button.${ROOT}__b{padding:4px 2px}
.${ROOT} button.${ROOT}__b.${ROOT}__danger{border-color:var(--tx);color:var(--tx)}
.${ROOT} button.${ROOT}__b.${ROOT}__good{border-color:var(--ok);color:var(--ok)}
.${ROOT} button.${ROOT}__icon{min-width:28px;padding:4px 7px}
.${ROOT} .${ROOT}__in{font-family:var(--font-mono);font-size:12px;background:var(--bg-inset);color:var(--fg-0);
  border:1px solid var(--line);border-radius:var(--r-xs);padding:4px 6px;min-height:28px;min-width:0}
.${ROOT} .${ROOT}__fld{display:grid;grid-template-columns:minmax(96px,auto) 1fr;align-items:center;gap:6px}
.${ROOT} .${ROOT}__fld > label{color:var(--fg-1);font-size:12px}
.${ROOT} .${ROOT}__chk{display:flex;align-items:center;gap:6px;color:var(--fg-0);font-size:12px;min-height:24px}
.${ROOT} .${ROOT}__checks{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:2px 10px}
.${ROOT} .${ROOT}__muted{font-size:12px;color:var(--fg-1)}
.${ROOT} .${ROOT}__err{font-size:11px;color:var(--tx)}
.${ROOT} .${ROOT}__log{background:var(--bg-inset);border:1px solid var(--line);border-radius:var(--r-xs);
  font-family:var(--font-mono);font-size:11px;line-height:1.5;max-height:160px;overflow:auto;padding:6px}
.${ROOT} .${ROOT}__ln{display:flex;gap:6px}
.${ROOT} .${ROOT}__ts{color:var(--fg-2)}
.${ROOT} .${ROOT}__k-warn{color:var(--amber)}
.${ROOT} .${ROOT}__k-tx{color:var(--tx)}
.${ROOT} .${ROOT}__k-ok{color:var(--ok)}
.${ROOT} .${ROOT}__k-cmd{color:var(--accent-bright)}
.${ROOT} .${ROOT}__overlay{position:absolute;inset:0;background:var(--bg-inset);display:flex;align-items:center;
  justify-content:center;padding:12px;border-radius:var(--r-md);z-index:3}
.${ROOT} .${ROOT}__card{background:var(--bg-1);border:1px solid var(--line-strong);border-radius:var(--r-md);
  padding:12px;display:flex;flex-direction:column;gap:8px;max-width:320px}
.${ROOT} .${ROOT}__card h3{margin:0;font-size:13px;color:var(--fg-0)}
.${ROOT} .${ROOT}__ntab{width:100%;border-collapse:collapse;font-size:12px}
.${ROOT} .${ROOT}__ntab th{text-align:left;color:var(--fg-1);font-weight:600;padding:3px 4px;border-bottom:1px solid var(--line-strong)}
.${ROOT} .${ROOT}__ntab td{padding:2px 4px;border-bottom:1px solid var(--line);color:var(--fg-0)}
.${ROOT} .${ROOT}__ntab tr.${ROOT}__cur td{background:var(--bg-2)}
.${ROOT} .${ROOT}__ntab input{width:72px}
.${ROOT} .${ROOT}__compact .${ROOT}__glab{display:none}
.${ROOT}.${ROOT}__compact{gap:5px;padding:6px}
.${ROOT}.${ROOT}__compact .${ROOT}__meter{grid-template-columns:44px 1fr 52px}
@media (prefers-reduced-motion: reduce){.${ROOT} button.${ROOT}__b,.${ROOT} .${ROOT}__fill{transition:none}}
`;

function ensureStyle() {
  if (typeof document === "undefined" || document.getElementById(ROOT + "-style")) return;
  const s = document.createElement("style");
  s.id = ROOT + "-style";
  s.textContent = CSS;
  document.head.appendChild(s);
}

async function call(callBackend, method, path, body) {
  const res = await callBackend(method, path, body);
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
  if (!res.ok && res.status !== 202) {
    const err = new Error((data && data.message) || (method + " " + path + " failed (" + res.status + ")"));
    err.status = res.status;
    throw err;
  }
  return data;
}

const mhz = (khz) => khz == null ? "—" : (khz / 1000).toFixed(3) + " MHz";
const SOURCE_TEXT = { "default": "default", "manual": "typed", "learned-floor": "learned (antenna)",
  "learned-equipment": "learned (no antenna)", "auto": "auto-learning" };
const num = (v, d) => (v === null || v === undefined) ? "—" : (d === undefined ? String(v) : Number(v).toFixed(d));
const tempText = (cdeg, unit) => cdeg == null ? "—"
  : unit === "F" ? Math.round(cdeg * 9 / 5 + 32) + " °F" : cdeg + " °C";

function Meter(props) {
  const { label, value, max, text, level, peak } = props;
  const pct = value == null ? 0 : Math.max(0, Math.min(100, (value / max) * 100));
  const peakPct = peak == null ? null : Math.max(0, Math.min(100, (peak / max) * 100));
  return h("div", { className: c("meter") },
    h("span", { className: c("mk") }, label),
    h("div", { className: c("track"), role: "meter", "aria-label": label,
      "aria-valuemin": 0, "aria-valuemax": max, "aria-valuenow": value == null ? undefined : value,
      "aria-valuetext": text },
      h("div", { className: c("fill " + (level || "")), style: { width: pct + "%" } }),
      peakPct != null && peakPct > pct ? h("div", { className: c("peak"), style: { left: "calc(" + peakPct + "% - 1px)" } }) : null),
    h("span", { className: c("mv") }, text));
}

function Tile(label, text, warn) {
  return h("div", { className: c("tile" + (warn ? " warn" : "")), key: label },
    h("div", { className: c("mk") }, label),
    h("div", { className: c("mv") }, text, warn ? " ⚠" : ""));
}

function Check(cfg, setCfg, key, label) {
  return h("label", { className: c("chk"), key },
    h("input", { type: "checkbox", checked: !!cfg[key], onChange: (e) => setCfg({ ...cfg, [key]: e.target.checked }) }),
    label);
}

function Field(label, id, control) {
  return h("div", { className: c("fld"), key: id },
    h("label", { htmlFor: id }, label), control);
}

function makePanel(callBackend) {
  return function Kpa1500Panel() {
    const [status, setStatus] = useState(null);
    const [cfg, setCfgSaved] = useState(null);
    const [draft, setDraft] = useState(null);
    const [loadErr, setLoadErr] = useState(null);
    const [actErr, setActErr] = useState(null);
    const [busy, setBusy] = useState(false);
    const [showSettings, setShowSettings] = useState(false);
    const [saveErr, setSaveErr] = useState(null);
    const [ports, setPorts] = useState([]);
    const [log, setLog] = useState([]);
    const [confirm, setConfirm] = useState(null); // "on" | "off" | "tune"
    const [peak, setPeak] = useState({ w: null, at: 0 });
    const [found, setFound] = useState(null);      // null | { searching } | { error } | { searched, found }
    const alive = useRef(true);

    // Status poll.
    useEffect(() => {
      alive.current = true;
      let timer = null;
      const tick = async () => {
        try {
          const s = await call(callBackend, "GET", "/status");
          if (!alive.current) return;
          setStatus(s);
          setLoadErr(null);
          const w = s && s.amp ? s.amp.forwardW : null;
          setPeak((p) => {
            const now = Date.now();
            if (w != null && (p.w == null || w >= p.w || now - p.at > PEAK_HOLD_MS)) return { w, at: now };
            return p;
          });
        } catch (e) {
          if (alive.current) setLoadErr(e.message || String(e));
        }
        if (alive.current) timer = setTimeout(tick, 300);
      };
      tick();
      call(callBackend, "GET", "/config").then((x) => { if (alive.current && x) setCfgSaved(x); }).catch(() => {});
      return () => { alive.current = false; if (timer) clearTimeout(timer); };
    }, []);

    // Log poll, only while the log is visible.
    const logVisible = !!(cfg && cfg.showLog);
    useEffect(() => {
      if (!logVisible) return undefined;
      let timer = null;
      let live = true;
      const tick = async () => {
        try { const l = await call(callBackend, "GET", "/log"); if (live) setLog(l || []); } catch (e) { /* shown via status */ }
        if (live) timer = setTimeout(tick, 2000);
      };
      tick();
      return () => { live = false; if (timer) clearTimeout(timer); };
    }, [logVisible]);

    const act = useCallback(async (path, body) => {
      setBusy(true);
      setActErr(null);
      try {
        const r = await call(callBackend, "POST", path, body);
        if (r && r.message && r.ok === true && path === "/tune-guard") setActErr(null);
      } catch (e) {
        setActErr(e.message || String(e));
      } finally {
        setBusy(false);
      }
    }, []);

    const openSettings = async () => {
      setSaveErr(null);
      setDraft(cfg ? { ...cfg } : null);
      setShowSettings(true);
      try { setPorts((await call(callBackend, "GET", "/ports")) || []); } catch (e) { setPorts([]); }
    };

    const saveSettings = async () => {
      setSaveErr(null);
      try {
        const saved = await call(callBackend, "POST", "/config", draft);
        setCfgSaved(saved);
        setShowSettings(false);
      } catch (e) {
        setSaveErr(e.message || String(e));
      }
    };

    // ── loading / unavailable ──────────────────────────────────────────────
    if (!status || !cfg) {
      return h("div", { className: ROOT, role: "region", "aria-label": "KPA1500" },
        h("div", { className: c("title") }, "Elecraft KPA1500"),
        loadErr
          ? h("div", { className: c("banner warn"), role: "alert" }, h("strong", null, "Feature backend unavailable. "), loadErr)
          : h("div", { className: c("muted"), role: "status" }, "Loading…"));
    }

    const amp = status.amp || {};
    const conn = status.connection || {};
    const connected = conn.state === "open";
    const on = connected && amp.power === "on";
    const tx = !!status.tx;
    const fault = amp.fault && amp.fault.active ? amp.fault : null;
    const tuning = amp.tuning === true;
    const guard = status.tuneGuard || { phase: "idle" };
    const relayLock = !on || busy || tx || tuning;
    const lockTip = !connected ? "Not connected" : !on ? "Amplifier is off" : tx ? "Not while transmitting" : tuning ? "Not while the ATU is tuning" : undefined;
    const hasTarget = cfg.transport === "serial" ? !!(cfg.serialPort || "").trim() : !!(cfg.host || "").trim();
    const tci = status.tci || { enabled: false };

    // ── header ──
    const pills = [];
    if (!connected) pills.push(h("span", { key: "c", className: c("pill " + (conn.state === "opening" ? "warn" : "")) },
      conn.state === "opening" ? "OPENING" : "OFFLINE"));
    if (connected) pills.push(h("span", { key: "p", className: c("pill " + (on ? "ok" : amp.power === "off" ? "" : "warn")) },
      amp.powerOnInProgress ? "WAKING…" : on ? "ON" : amp.power === "off" ? "OFF" : "NO REPLY"));
    if (tci.enabled) pills.push(h("span", { key: "tci", className: c("pill " + (tci.connected ? "ok" : "warn")), title: "TCI " + tci.target },
      tci.connected ? (tci.tune ? "TCI TUNE" : "TCI") : "TCI WAIT"));
    if (on && amp.operate != null) pills.push(h("span", { key: "o", className: c("pill " + (amp.operate ? "acc" : "")) }, amp.operate ? "OPERATE" : "STANDBY"));
    if (tx) pills.push(h("span", { key: "t", className: c("pill tx") }, "TX"));
    if (tuning) pills.push(h("span", { key: "u", className: c("pill warn") }, "ATU TUNING"));
    if (fault) pills.push(h("span", { key: "f", className: c("pill tx") }, "FAULT " + fault.code));

    const header = h("div", { className: c("row spread") },
      h("div", { className: c("grow") },
        h("div", { className: c("title") }, "Elecraft KPA1500"),
        h("div", { className: c("sub") },
          "v" + (status.version || "?") + " · " + (conn.target || "no connection set") +
          (amp.firmware ? " · fw " + amp.firmware : "") +
          (amp.band ? " · " + amp.band : "") +
          (on && amp.antenna ? " · ANT" + amp.antenna : ""))),
      h("div", { className: c("row") }, pills,
        h("button", { className: c("b icon"), type: "button", "aria-label": "Settings", title: "Settings", onClick: openSettings }, "⚙")));

    const banners = [];
    if (!connected) {
      banners.push(h("div", { key: "dc", className: c("banner warn"), role: "status" },
        h("span", { className: c("grow") },
          !hasTarget ? "Set the amplifier's IP address or serial port in Settings."
            : conn.state === "opening" ? "Opening " + conn.target + "…"
            : "Not connected." + (conn.error ? " " + conn.error : "")),
        hasTarget && conn.state !== "opening"
          ? h("button", { className: c("b"), type: "button", disabled: busy, onClick: () => act("/connect") }, "Connect")
          : h("button", { className: c("b"), type: "button", onClick: openSettings }, "Settings")));
    }
    if (connected && amp.power === "unknown" && !amp.powerOnInProgress) {
      banners.push(h("div", { key: "nr", className: c("banner warn"), role: "status" },
        h("span", { className: c("grow") }, "No reply from the KPA1500 at " + conn.target + ". It may be off or asleep, or the address is wrong."),
        cfg.ctlPower ? h("button", { className: c("b good"), type: "button", disabled: busy, onClick: () => act("/power", { on: true }) }, "Power on") : null));
    }
    if (connected && on && amp.wakeOnLan === false && cfg.transport !== "serial") {
      banners.push(h("div", { key: "wl", className: c("banner acc"), role: "status" },
        h("span", { className: c("grow") }, "Wake-on-LAN is off in the amp, so it can't be powered on over the network."),
        h("button", { className: c("b"), type: "button", disabled: busy, onClick: () => act("/wol/enable") }, "Enable Wake-on-LAN")));
    }
    if (fault) {
      banners.push(h("div", { key: "f", className: c("banner tx"), role: "alert" },
        h("span", { className: c("grow") }, h("strong", null, "FAULT " + fault.code + " "), fault.name),
        cfg.ctlClearFault ? h("button", { className: c("b danger"), type: "button", disabled: busy, onClick: () => act("/fault/clear") }, "Clear fault") : null));
    }
    if (status.pendingBand) {
      banners.push(h("div", { key: "pb", className: c("banner acc"), role: "status" },
        "Band change to " + status.pendingBand + " waits until TX drops."));
    }
    if (guard.phase !== "idle") {
      const msg = guard.phase === "armed" ? "Standby-on-Tune armed — press TUNE on the radio."
        : guard.phase === "active" ? "Tune carrier present — amp held in STANDBY."
        : "Tune finished — restoring…";
      banners.push(h("div", { key: "tg", className: c("banner acc"), role: "status" },
        h("span", { className: c("grow") }, msg, guard.restore ? " OPERATE returns afterwards." : ""),
        h("button", { className: c("b"), type: "button", onClick: () => act("/tune-guard", { arm: false }) }, "Cancel")));
    }
    const na = status.noAnt || { state: "off" };
    if (cfg.noAntEnabled && (na.state === "low" || na.state === "tx")) {
      const txNow = na.state === "tx";
      banners.push(h("div", { key: "na", className: c("banner " + (txNow ? "tx" : "warn")), role: "alert" },
        h("span", { className: c("grow") },
          h("strong", null, txNow ? "TRANSMITTING — NO ANTENNA? " : "NO ANTENNA? "),
          "RX " + Math.round(na.dbm) + " dBm on " + na.band + " (limit " + na.limit + " dBm). " +
          (txNow ? "Check the antenna / dummy load!" : "Check the antenna.") +
          (na.hold ? " Amp held in STANDBY." : "")),
        h("button", { className: c("b"), type: "button", disabled: busy, title: "A dummy load is connected on purpose: silence the warning on this band",
          onClick: () => act("/noant/dummy", { on: true }) }, "DUMMY OK")));
    } else if (cfg.noAntEnabled && na.state === "dummy") {
      banners.push(h("div", { key: "nd", className: c("banner acc"), role: "status" },
        h("span", { className: c("grow") }, "DUMMY OK on " + na.band + " — no-antenna warning silenced until a normal level returns."),
        h("button", { className: c("b"), type: "button", onClick: () => act("/noant/dummy", { on: false }) }, "Undo")));
    }
    if (na.scanning) {
      banners.push(h("div", { key: "sc", className: c("banner acc"), role: "status" },
        h("span", { className: c("grow") }, h("strong", null, "Band scan: "), na.scanStatus || "…"),
        h("button", { className: c("b"), type: "button", onClick: () => act("/noant/scan", { on: false }) }, "Stop")));
    }
    if (actErr) banners.push(h("div", { key: "e", className: c("banner warn"), role: "alert" }, actErr));

    // ── telemetry ──
    const swrLevel = amp.swr == null ? "" : amp.swr >= Math.max(cfg.swrWarn * 1.5, 3) ? "tx" : amp.swr >= cfg.swrWarn ? "warn" : "";
    const meters = [];
    if (cfg.showForward) meters.push(h(Meter, { key: "fw", label: "Fwd", value: amp.forwardW, max: cfg.meterMaxWatts,
      peak: peak.w, text: amp.forwardW == null ? "—" : amp.forwardW + " W",
      level: amp.forwardW != null && amp.forwardW > cfg.meterMaxWatts ? "tx" : "" }));
    if (cfg.showSwr) meters.push(h(Meter, { key: "swr", label: "SWR", value: amp.swr == null ? null : amp.swr - 1, max: 4,
      text: amp.swr == null ? "—" : num(amp.swr, 1) + ":1", level: swrLevel }));
    if (cfg.showReflected) meters.push(h(Meter, { key: "rf", label: "Refl", value: amp.reflectedW, max: Math.max(100, Math.round(cfg.meterMaxWatts / 10)),
      text: amp.reflectedW == null ? "—" : amp.reflectedW + " W" }));
    if (cfg.showInput) meters.push(h(Meter, { key: "in", label: "Drive", value: amp.inputW, max: 100,
      text: amp.inputW == null ? "—" : amp.inputW + " W", level: amp.inputW != null && amp.inputW > 90 ? "warn" : "" }));
    if (cfg.showDissipation) meters.push(h(Meter, { key: "pd", label: "Diss", value: amp.dissipationW, max: cfg.meterMaxWatts,
      text: amp.dissipationW == null ? "—" : amp.dissipationW + " W" }));

    const tiles = [];
    const hot = amp.tempC != null && amp.tempC >= cfg.tempWarnC;
    if (cfg.showTemperature) tiles.push(Tile("Temp", tempText(amp.tempC, cfg.tempUnit), hot));
    if (cfg.showVoltage) tiles.push(Tile("PA volts", amp.volts == null ? "—" : num(amp.volts, 1) + " V"));
    if (cfg.showCurrent) tiles.push(Tile("PA amps", amp.amps == null ? "—" : num(amp.amps, 0) + " A"));
    if (cfg.showAmpFreq) {
      const vfoKhz = status.radio && status.radio.frequencyHz ? Math.round(status.radio.frequencyHz / 1000) : null;
      const off = amp.freqKhz != null && vfoKhz != null && Math.abs(amp.freqKhz - vfoKhz) > 10 && cfg.sendFrequency;
      tiles.push(Tile(off ? "Amp freq ≠ VFO" : "Amp freq", mhz(amp.freqKhz), off));
    }
    if (cfg.showRxLevel) {
      const na = status.noAnt || {};
      const bad = na.state === "low" || na.state === "tx";
      const text = !cfg.tciEnabled ? "TCI off"
        : !tci.connected ? "TCI wait"
        : na.dbm == null ? "—" : Math.round(na.dbm) + " dBm";
      tiles.push(Tile("RX level", text, bad));
    }
    if (cfg.showFan) tiles.push(Tile("Fan", amp.fan == null ? "—" : String(amp.fan) + " / 5"));
    if (cfg.showAtuStatus) {
      tiles.push(Tile("ATU", amp.atuInline == null ? "—" : tuning ? "TUNING" : amp.atuInline ? "IN-LINE" : "BYPASS"));
      tiles.push(Tile("Byp SWR", amp.bypassSwr == null ? "—" : num(amp.bypassSwr, 1) + ":1"));
    }

    const telemetry = (meters.length || tiles.length) ? h("div", { className: c("group") },
      h("div", { className: c("glab") }, "Telemetry"),
      meters.length ? h("div", { className: c("meters") }, meters) : null,
      tiles.length ? h("div", { className: c("tiles") }, tiles) : null) : null;

    // ── controls ──
    // Every two-state control is one toggle that is filled (lit) when active.
    // Pairs/sets (ANT 1/2, bands) light the active one and fade the others.
    // Dashed outline = locked (transmitting / tuning / amp off), never "inactive".
    const toggle = (key, label, lit, opts) => h("button", { key, type: "button",
      className: c("b " + (opts.variant || "") + (opts.faint ? " faint" : "")),
      "aria-pressed": !!lit, disabled: !!opts.disabled, title: opts.title, onClick: opts.onClick }, label);

    const ampBtns = [];
    if (cfg.ctlPower) {
      ampBtns.push(toggle("pw", amp.powerOnInProgress ? "WAKING…" : "POWER", on || amp.powerOnInProgress, {
        variant: "litok",
        disabled: !connected || busy || amp.powerOnInProgress || (on && (tx || tuning)),
        title: on ? (tx ? "Not while transmitting" : "Amplifier is ON — click to turn it off") : "Amplifier is off — click to turn it on",
        onClick: () => {
          const want = on ? "off" : "on";
          if ((want === "off" && cfg.confirmPowerOff) || (want === "on" && cfg.confirmPowerOn)) setConfirm(want);
          else act("/power", { on: want === "on" });
        } }));
    }
    if (cfg.ctlOperate) {
      const operating = on && amp.operate === true;
      ampBtns.push(toggle("op", "OPERATE", operating, {
        // Going to STANDBY is always allowed (even under RF); going to OPERATE is locked under RF.
        disabled: operating ? (!on || busy) : relayLock,
        title: !on ? "Amplifier is off" : operating ? "In OPERATE — click for STANDBY" : (lockTip || "In STANDBY — click for OPERATE"),
        onClick: () => act("/operate", { operate: !operating }) }));
    }
    if (cfg.standbyOnTune && cfg.ctlTuneGuard) {
      const armed = guard.phase !== "idle";
      ampBtns.push(toggle("tg", "TUNE STBY", armed, {
        variant: "litamber", disabled: !on || busy,
        title: armed ? "Standby-on-Tune armed — click to cancel" : "Click, then press TUNE on the radio: the amp goes to STANDBY for the tune and back to OPERATE after",
        onClick: () => act("/tune-guard", { arm: !armed }) }));
    }

    const groups = [];
    if (ampBtns.length) groups.push(h("div", { key: "a", className: c("group") },
      h("div", { className: c("glab") }, "Amplifier"), h("div", { className: c("btns") }, ampBtns)));

    if (cfg.ctlBand) {
      const radioText = status.radio && status.radio.available && status.radio.band
        ? (status.radio.source || "Zeus") + ": " + status.radio.band + " · " + (status.radio.frequencyHz / 1000).toFixed(1) + " kHz"
        : "Zeus radio state not available";
      groups.push(h("div", { key: "b", className: c("group") },
        h("div", { className: c("glab") }, "Band" + (cfg.sendFrequency ? " · amp follows the VFO" : "")),
        h("div", { className: c("bands"), role: "group", "aria-label": "Amplifier band" },
          BANDS.map((b) => {
            const active = on && amp.band === b;
            return toggle("bd" + b, b.replace("m", ""), active, {
              faint: on && amp.band != null && !active, disabled: relayLock && !active,
              title: active ? "Amp is on " + b : (lockTip || "Set the amp to " + b),
              onClick: () => { if (!active) act("/band", { band: b }); } });
          })),
        h("div", { className: c("muted") }, radioText)));
    }

    if (cfg.ctlAntenna || cfg.ctlAtu || cfg.ctlTune) {
      const atuBtns = [];
      if (cfg.ctlAntenna) [1, 2].forEach((a) => {
        const active = on && amp.antenna === a;
        atuBtns.push(toggle("a" + a, "ANT " + a, active, {
          faint: on && amp.antenna != null && !active, disabled: relayLock && !active,
          title: active ? "Antenna " + a + " in use" : (lockTip || "Switch to antenna " + a),
          onClick: () => { if (!active) act("/antenna", { antenna: a }); } }));
      });
      if (cfg.ctlAtu) {
        const inline = on && amp.atuInline === true;
        atuBtns.push(toggle("atu", "ATU", inline, {
          disabled: relayLock,
          title: lockTip || (inline ? "ATU in-line — click to bypass" : "ATU bypassed — click to put it in-line"),
          onClick: () => act("/atu", { inline: !inline }) }));
      }
      if (cfg.ctlTune) atuBtns.push(toggle("tn", tuning ? "TUNING…" : "ATU TUNE", tuning, {
        variant: "litamber",
        disabled: tuning ? busy : relayLock,
        title: tuning ? "ATU is tuning — click to cancel" : (lockTip || "Start a full-search ATU tune (you supply the carrier)"),
        onClick: () => tuning ? act("/atu/cancel") : setConfirm("tune") }));
      groups.push(h("div", { key: "t", className: c("group") },
        h("div", { className: c("glab") }, "Antenna & ATU"), h("div", { className: c("btns") }, atuBtns)));
    }

    if (cfg.ctlFan) {
      const fm = amp.fanMinimum;
      groups.push(h("div", { key: "fan", className: c("group") },
        h("div", { className: c("glab") }, "Fan"),
        h("div", { className: c("row") },
          h("span", { className: c("muted") }, "Minimum speed"),
          h("button", { type: "button", className: c("b icon"), "aria-label": "Lower minimum fan speed", disabled: !on || busy || fm == null || fm <= 0,
            onClick: () => act("/fan", { speed: fm - 1 }) }, "−"),
          h("span", { className: c("mv"), "aria-live": "polite" }, fm == null ? "—" : String(fm)),
          h("button", { type: "button", className: c("b icon"), "aria-label": "Raise minimum fan speed", disabled: !on || busy || fm == null || fm >= 5,
            onClick: () => act("/fan", { speed: fm + 1 }) }, "+"),
          h("span", { className: c("muted") }, "now " + (amp.fan == null ? "—" : amp.fan) + " / 5"))));
    }

    const logView = cfg.showLog ? h("div", { className: c("group") },
      h("div", { className: c("glab") }, "Event log"),
      h("div", { className: c("log"), role: "log", "aria-live": "off" },
        (log.length ? log : [{ ts: "", kind: "info", message: "No events yet." }]).map((e, i) =>
          h("div", { className: c("ln"), key: i },
            h("span", { className: c("ts") }, e.ts),
            h("span", { className: c("k-" + (e.kind || "info")) }, e.message))))) : null;

    // ── dialogs ──
    let overlay = null;
    if (confirm) {
      const isTune = confirm === "tune";
      const title = isTune ? "Start an ATU full-search tune?" : confirm === "off" ? "Turn the KPA1500 off?" : "Turn the KPA1500 on?";
      const text = isTune
        ? "This arms the KPA1500's tuner. It needs a carrier: after pressing Start, key a low-power carrier (or TUNE) on the radio. The amp bypasses itself while tuning. This feature never keys the radio."
        : confirm === "off" ? "The amplifier powers down and stops amplifying until it is turned back on."
        : "Sends a Wake-on-LAN packet (when the amp's MAC is known) and then ^ON1; once a second for up to 15 seconds. The amp's NET WAKE on LAN menu must be ON.";
      overlay = h("div", { className: c("overlay") },
        h("div", { className: c("card"), role: "dialog", "aria-modal": "true", "aria-label": title },
          h("h3", null, title),
          h("div", { className: c("muted") }, text),
          h("div", { className: c("btns") },
            h("button", { type: "button", className: c("b " + (confirm === "off" ? "danger" : "good")), autoFocus: true,
              onClick: () => {
                const k = confirm; setConfirm(null);
                if (k === "tune") act("/atu/tune"); else act("/power", { on: k === "on" });
              } }, isTune ? "Start" : confirm === "off" ? "Power off" : "Power on"),
            h("button", { type: "button", className: c("b"), onClick: () => setConfirm(null) }, "Cancel"))));
    } else if (showSettings && draft) {
      overlay = h("div", { className: c("overlay"), style: { alignItems: "flex-start", overflow: "auto" } },
        Settings({ draft, setDraft, ports, saveErr, connected, busy, found, mac: amp.mac, callBackend,
          onFind: async () => {
            setFound({ searching: true });
            try { setFound(await call(callBackend, "POST", "/discover")); }
            catch (e) { setFound({ error: e.message || String(e) }); }
          },
          onCancel: () => setShowSettings(false), onSave: saveSettings,
          onConnect: () => act("/connect"), onDisconnect: () => act("/disconnect"),
          onRefreshPorts: async () => { try { setPorts((await call(callBackend, "GET", "/ports")) || []); } catch (e) { setPorts([]); } } }));
    }

    return h("div", { className: ROOT + (cfg.compact ? " " + c("compact") : ""), role: "region", "aria-label": "Elecraft KPA1500" },
      header, banners, telemetry, groups, logView, overlay);
  };
}

// Live per-band limits for the "No antenna?" check, inside Settings.
function NoAntBands(props) {
  const { callBackend, below, above } = props;
  const [data, setData] = useState(null);
  const [msg, setMsg] = useState(null);
  const [edits, setEdits] = useState({});
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let live = true;
    let timer = null;
    const tick = async () => {
      try { const d = await call(callBackend, "GET", "/noant"); if (live) setData(d); } catch (e) { /* keep last */ }
      if (live) timer = setTimeout(tick, 1000);
    };
    tick();
    return () => { live = false; if (timer) clearTimeout(timer); };
  }, []);

  const post = async (path, body) => {
    setBusy(true);
    try { const r = await call(callBackend, "POST", path, body); setMsg(r && r.message ? r.message : "Done."); }
    catch (e) { setMsg(e.message || String(e)); }
    finally { setBusy(false); }
  };

  if (!data) return h("div", { className: c("muted") }, "Loading band limits…");
  const cur = data.current || {};
  const levelText = cur.dbm == null ? "no reading yet" : Math.round(cur.dbm) + " dBm";

  const commit = (band, text) => {
    const t = String(text).trim();
    setEdits((e) => { const n = { ...e }; delete n[band]; return n; });
    if (t === "") return post("/noant/band", { band, limit: null, reset: false });
    const v = Number(t);
    if (!Number.isFinite(v)) { setMsg("Type a number such as -105, or leave it empty for no check."); return null; }
    return post("/noant/band", { band, limit: Math.round(v), reset: false });
  };

  return h("div", { className: c("group"), style: { borderTop: "none", paddingTop: 0 } },
    h("div", { className: c("muted") },
      cur.band ? ("Now on " + cur.band + ": receive level " + levelText + (cur.limit != null ? ", limit " + cur.limit + " dBm" : ", no check on this band")) + "."
        : "The radio is not on a ham band."),
    h("div", { className: c("btns") },
      h("button", { type: "button", className: c("b"), disabled: busy || !cur.band,
        title: "Antenna connected: limit = this band's noise floor − " + below + " dB",
        onClick: () => post("/noant/learn", { antennaConnected: true }) }, "Learn this band (antenna connected)"),
      h("button", { type: "button", className: c("b"), disabled: busy || !cur.band,
        title: "Antenna disconnected: limit = equipment noise + " + above + " dB",
        onClick: () => post("/noant/learn", { antennaConnected: false }) }, "Learn this band (antenna disconnected)")),
    h("div", { className: c("btns") },
      cur.scanning
        ? h("button", { type: "button", className: c("b danger"), onClick: () => post("/noant/scan", { on: false }) }, "Stop band scan")
        : h("button", { type: "button", className: c("b"), disabled: busy || !cur.canScan,
            title: cur.canScan ? "Tunes the radio to the middle of each band's phone segment, measures the noise, sets the limit, then restores your frequency"
              : "Zeus has not given this feature permission to tune the radio",
            onClick: () => post("/noant/scan", { on: true }) }, "Scan all bands now (antenna connected)"),
      h("button", { type: "button", className: c("b"), disabled: busy, onClick: () => post("/noant/defaults") }, "Default limits")),
    cur.scanStatus ? h("div", { className: c("muted"), role: "status" }, "Scan: " + cur.scanStatus) : null,
    msg ? h("div", { className: c("muted"), role: "status" }, msg) : null,
    h("table", { className: c("ntab") },
      h("thead", null, h("tr", null,
        h("th", null, "Band"), h("th", null, "Limit (dBm)"), h("th", null, "How it was set"), h("th", null, "Noise floor"), h("th", null, ""))),
      h("tbody", null, (data.bands || []).map((b) => {
        const editing = Object.prototype.hasOwnProperty.call(edits, b.band);
        return h("tr", { key: b.band, className: b.band === cur.band ? c("cur") : undefined },
          h("td", null, b.band),
          h("td", null, h("input", { className: c("in"), type: "text", inputMode: "numeric", "aria-label": b.band + " limit in dBm, empty for no check",
            value: editing ? edits[b.band] : (b.limit == null ? "" : String(b.limit)), placeholder: "off",
            onChange: (e) => { const v = e.target.value; setEdits((x) => ({ ...x, [b.band]: v })); },
            onBlur: (e) => { if (editing) commit(b.band, e.target.value); },
            onKeyDown: (e) => { if (e.key === "Enter") e.target.blur(); } })),
          h("td", null, SOURCE_TEXT[b.source] || b.source),
          h("td", null, b.floor == null ? "—" : Math.round(b.floor) + " dBm"),
          h("td", null, h("button", { type: "button", className: c("b"), disabled: busy || b.source === "default",
            "aria-label": "Reset " + b.band + " to the default limit", onClick: () => post("/noant/band", { band: b.band, limit: null, reset: true }) }, "Reset")));
      }))),
    h("div", { className: c("muted") },
      "Empty = no check on that band. A limit you type is never changed automatically. " +
      "Things that lower the receive level and can cause a false warning: the attenuator, preamp off, a very narrow filter, or a dead band at night."));
}

function Settings(p) {
  const { draft, setDraft, ports, saveErr, connected, busy } = p;
  const set = (k, v) => setDraft({ ...draft, [k]: v });
  const id = (k) => ROOT + "-cfg-" + k;
  const numIn = (k, min, max, step) => h("input", { id: id(k), className: c("in"), type: "number", min, max, step: step || 1,
    value: draft[k], onChange: (e) => set(k, e.target.value === "" ? "" : Number(e.target.value)) });
  const portList = ports.slice();
  if (draft.serialPort && portList.indexOf(draft.serialPort) < 0) portList.unshift(draft.serialPort);

  return h("div", { className: c("card"), role: "dialog", "aria-modal": "true", "aria-label": "KPA1500 settings", style: { maxWidth: "560px", width: "100%" } },
    h("h3", null, "KPA1500 settings"),

    h("div", { className: c("glab") }, "Connection"),
    h("div", { className: c("row"), role: "radiogroup", "aria-label": "Connection type" },
      h("label", { className: c("chk") }, h("input", { type: "radio", name: id("tr"), checked: draft.transport !== "serial", onChange: () => set("transport", "udp") }), "Network (UDP)"),
      h("label", { className: c("chk") }, h("input", { type: "radio", name: id("tr"), checked: draft.transport === "serial", onChange: () => set("transport", "serial") }), "Serial port (USB or virtual)")),
    draft.transport !== "serial" ? [
      Field("Amp IP address", id("host"), h("div", { className: c("row") },
        h("input", { id: id("host"), className: c("in grow"), value: draft.host, placeholder: "192.168.0.175",
          onChange: (e) => set("host", e.target.value) }),
        h("button", { type: "button", className: c("b"), disabled: !!(p.found && p.found.searching), onClick: p.onFind },
          p.found && p.found.searching ? "Searching…" : "Find amplifier"))),
      p.found && !p.found.searching ? h("div", { key: "found", className: c("muted"), role: "status" },
        p.found.error ? p.found.error
          : p.found.found && p.found.found.length
            ? p.found.found.map((f) => h("div", { key: f.ip, className: c("row") },
                h("span", { className: c("grow") }, f.ip + (f.serial ? " · serial " + f.serial : "") + (f.firmware ? " · fw " + f.firmware : "") + (f.boot ? " · boot block" : "")),
                h("button", { type: "button", className: c("b"), onClick: () => setDraft({ ...draft, host: f.ip, ampMac: draft.ampMac || f.mac || "" }) }, "Use")))
            : "No KPA1500 answered (searched " + (p.found.searched || "?") + ").") : null,
      Field("UDP port", id("netPort"), numIn("netPort", 1, 65535)),
      h("div", { key: "udpnote", className: c("muted") }, "UDP keeps no session, so the Elecraft utility and other programs can talk to the amp at the same time."),
    ] : [
      Field("Serial port", id("serialPort"), h("div", { className: c("row") },
        h("select", { id: id("serialPort"), className: c("in grow"), value: draft.serialPort, onChange: (e) => set("serialPort", e.target.value) },
          [h("option", { key: "", value: "" }, portList.length ? "choose…" : "no ports found")].concat(portList.map((n) => h("option", { key: n, value: n }, n)))),
        h("button", { type: "button", className: c("b"), onClick: p.onRefreshPorts }, "Refresh"))),
      Field("Baud", id("baud"), h("select", { id: id("baud"), className: c("in"), value: draft.baud, onChange: (e) => set("baud", Number(e.target.value)) },
        BAUDS.map((b) => h("option", { key: b, value: b }, String(b))))),
      h("div", { key: "sernote", className: c("muted") }, "Any serial port works (COM3 on Windows, /dev/cu.usbserial-… on a Mac), including a virtual serial-port emulator. Only one program can open a serial port at a time; use a port splitter to share it."),
    ],
    Field("Poll interval (ms)", id("pollMs"), numIn("pollMs", 50, 2000, 10)),
    h("div", { className: c("checks") }, Check(draft, setDraft, "autoConnect", "Connect automatically")),
    h("div", { className: c("btns") },
      h("button", { type: "button", className: c("b"), disabled: busy, onClick: p.onConnect }, "Connect"),
      h("button", { type: "button", className: c("b"), disabled: busy || !connected, onClick: p.onDisconnect }, "Disconnect"),
      h("span", { className: c("muted") }, "Save first if you changed the connection.")),

    h("div", { className: c("glab") }, "Remote power-on"),
    Field("Amp MAC address", id("ampMac"), h("input", { id: id("ampMac"), className: c("in"), value: draft.ampMac,
      placeholder: p.mac || "learned when the amp is on", onChange: (e) => set("ampMac", e.target.value) })),
    Field("Wake-on-LAN port", id("wolPort"), numIn("wolPort", 1, 65535)),

    h("div", { className: c("glab") }, "Behaviour"),
    h("div", { className: c("checks") },
      Check(draft, setDraft, "sendFrequency", "Amp follows the VFO (^FR: band + ATU recall; VFO B in split via TCI)"),
      Check(draft, setDraft, "bandFollow", "Also send band (^BN)"),
      Check(draft, setDraft, "standbyOnConnect", "Standby on connect"),
      Check(draft, setDraft, "confirmPowerOff", "Confirm power off"),
      Check(draft, setDraft, "confirmPowerOn", "Confirm power on")),
    Field("Frequency step (kHz)", id("frequencyStepKhz"), numIn("frequencyStepKhz", 1, 100)),

    h("div", { className: c("glab") }, "Standby on Tune"),
    h("div", { className: c("checks") },
      Check(draft, setDraft, "standbyOnTune", "Enable Standby-on-Tune"),
      Check(draft, setDraft, "restoreOperateAfterTune", "Return to OPERATE afterwards"),
      Check(draft, setDraft, "tciEnabled", "Use Zeus TCI for TUNE, split VFO and receive level (like the Thetis bridge)"),
      Check(draft, setDraft, "autoTuneDetect", "Also trigger when Zeus mode reports TUNE")),
    draft.tciEnabled ? [
      Field("TCI host", id("tciHost"), h("input", { id: id("tciHost"), className: c("in"), value: draft.tciHost, onChange: (e) => set("tciHost", e.target.value) })),
      Field("TCI port", id("tciPort"), numIn("tciPort", 1, 65535)),
      Field("TCI receiver", id("tciReceiver"), numIn("tciReceiver", 0, 7)),
    ] : null,
    Field("Tune button waits (s)", id("tuneTimeoutS"), numIn("tuneTimeoutS", 5, 120)),

    h("div", { className: c("glab") }, "No antenna? check"),
    h("div", { className: c("checks") },
      Check(draft, setDraft, "noAntEnabled", "Warn when the receive level says no antenna"),
      Check(draft, setDraft, "noAntAutoLearn", "Auto-learn each band's noise floor while listening")),
    Field("When it warns", id("noAntAction"), h("select", { id: id("noAntAction"), className: c("in"), value: draft.noAntAction,
      onChange: (e) => set("noAntAction", e.target.value) },
      h("option", { value: "warn" }, "Warn only"),
      h("option", { value: "standby" }, "Warn and keep the amp in STANDBY"))),
    Field("Limit below noise floor (dB)", id("noAntBelowFloorDb"), numIn("noAntBelowFloorDb", 1, 30)),
    Field("Limit above no-antenna noise (dB)", id("noAntAboveEquipDb"), numIn("noAntAboveEquipDb", 1, 30)),
    draft.tciEnabled ? null : h("div", { key: "natci", className: c("muted") },
      "This check needs the receive level from Zeus: tick \"Use Zeus TCI\" above and set Zeus's TCI port (Zeus's default is 40001)."),
    h(NoAntBands, { key: "nab", callBackend: p.callBackend, below: draft.noAntBelowFloorDb, above: draft.noAntAboveEquipDb }),

    h("div", { className: c("glab") }, "Telemetry shown"),
    h("div", { className: c("checks") },
      Check(draft, setDraft, "showForward", "Forward power"),
      Check(draft, setDraft, "showSwr", "SWR"),
      Check(draft, setDraft, "showReflected", "Reflected power"),
      Check(draft, setDraft, "showInput", "Drive (input) power"),
      Check(draft, setDraft, "showDissipation", "PA dissipation"),
      Check(draft, setDraft, "showTemperature", "Temperature"),
      Check(draft, setDraft, "showVoltage", "PA voltage"),
      Check(draft, setDraft, "showCurrent", "PA current"),
      Check(draft, setDraft, "showFan", "Fan speed"),
      Check(draft, setDraft, "showAtuStatus", "ATU status"),
      Check(draft, setDraft, "showAmpFreq", "Amp frequency"),
      Check(draft, setDraft, "showRxLevel", "Receive level (no-antenna check)"),
      Check(draft, setDraft, "showLog", "Event log")),

    h("div", { className: c("glab") }, "Controls shown"),
    h("div", { className: c("checks") },
      Check(draft, setDraft, "ctlPower", "Power on/off"),
      Check(draft, setDraft, "ctlOperate", "Operate / Standby"),
      Check(draft, setDraft, "ctlTuneGuard", "Tune (STBY) button"),
      Check(draft, setDraft, "ctlClearFault", "Clear fault"),
      Check(draft, setDraft, "ctlBand", "Band select"),
      Check(draft, setDraft, "ctlAntenna", "Antenna 1/2"),
      Check(draft, setDraft, "ctlAtu", "ATU in-line / bypass"),
      Check(draft, setDraft, "ctlTune", "ATU tune"),
      Check(draft, setDraft, "ctlFan", "Fan minimum speed")),

    h("div", { className: c("glab") }, "Display"),
    h("div", { className: c("checks") }, Check(draft, setDraft, "compact", "Compact layout")),
    Field("Temperature unit", id("tempUnit"), h("select", { id: id("tempUnit"), className: c("in"), value: draft.tempUnit, onChange: (e) => set("tempUnit", e.target.value) },
      h("option", { value: "C" }, "°C"), h("option", { value: "F" }, "°F"))),
    Field("Power meter full scale (W)", id("meterMaxWatts"), numIn("meterMaxWatts", 100, 3000, 50)),
    Field("SWR warning", id("swrWarn"), numIn("swrWarn", 1.1, 10, 0.1)),
    Field("Temperature warning (°C)", id("tempWarnC"), numIn("tempWarnC", 30, 100)),

    saveErr ? h("div", { className: c("err"), role: "alert" }, saveErr) : null,
    h("div", { className: c("btns") },
      h("button", { type: "button", className: c("b good"), onClick: p.onSave }, "Save"),
      h("button", { type: "button", className: c("b"), onClick: p.onCancel }, "Cancel")));
}

export default function activate(api) {
  const { registerPanel, callBackend } = api;
  ensureStyle();
  registerPanel({ id: PANEL_ID, component: makePanel(callBackend) });
}
