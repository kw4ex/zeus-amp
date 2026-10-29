// SPDX-License-Identifier: GPL-2.0-or-later
//
// FlexControl Knob — Zeus workspace panel.
//
// Plain ESM with React.createElement (no build step). Zeus supplies `react`.
// Talks only to this feature's backend through callBackend. Styling uses only
// the public Zeus CSS tokens, scoped under the feature root class.
// Shows what the knob does right now (like the bridge's Thetis "Knob" window)
// and holds the settings: port, the function of every press, and connections.

import React from "react";

const { useState, useEffect, useRef, useCallback } = React;
const h = React.createElement;

const ROOT = "com-kw4ex-zeus-flexknob";
const PANEL_ID = "flexknob.main";

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
.${ROOT} .${ROOT}__sub{font-size:11px;color:var(--fg-2)}
.${ROOT} .${ROOT}__grow{flex:1 1 auto;min-width:0}
.${ROOT} .${ROOT}__pill{font-size:10px;font-weight:700;letter-spacing:.04em;padding:2px 7px;
  border-radius:var(--r-lg);border:1px solid var(--line-strong);color:var(--fg-0);background:var(--bg-2);white-space:nowrap}
.${ROOT} .${ROOT}__pill.${ROOT}__ok{color:var(--ok);border-color:var(--ok)}
.${ROOT} .${ROOT}__pill.${ROOT}__warn{color:var(--amber);border-color:var(--amber)}
.${ROOT} .${ROOT}__pill.${ROOT}__tx{color:var(--tx);border-color:var(--tx)}
.${ROOT} .${ROOT}__now{background:var(--bg-2);border:1px solid var(--line-strong);border-radius:var(--r-sm);
  padding:8px;display:flex;flex-direction:column;gap:4px}
.${ROOT} .${ROOT}__nowk{font-size:11px;font-weight:600;color:var(--fg-1);letter-spacing:.05em;text-transform:uppercase}
.${ROOT} .${ROOT}__nowv{font-size:16px;font-weight:700;color:var(--fg-0)}
.${ROOT} .${ROOT}__leds{display:flex;gap:6px;align-items:center}
.${ROOT} .${ROOT}__led{width:10px;height:10px;border-radius:50%;border:1px solid var(--line-strong);background:var(--bg-inset)}
.${ROOT} .${ROOT}__led.${ROOT}__on{background:var(--accent-bright);border-color:var(--accent)}
.${ROOT} .${ROOT}__lines{display:grid;grid-template-columns:minmax(60px,auto) 1fr;gap:3px 8px;align-items:baseline}
.${ROOT} .${ROOT}__lk{font-size:11px;font-weight:600;color:var(--fg-1)}
.${ROOT} .${ROOT}__lv{font-family:var(--font-mono);font-size:12px;color:var(--fg-0);white-space:pre-wrap}
.${ROOT} .${ROOT}__banner{border:1px solid var(--line-strong);border-left-width:4px;border-radius:var(--r-sm);
  padding:6px 8px;background:var(--bg-2);display:flex;flex-direction:column;gap:3px}
.${ROOT} .${ROOT}__banner.${ROOT}__warn{border-color:var(--amber)}
.${ROOT} .${ROOT}__banner.${ROOT}__tx{border-color:var(--tx)}
.${ROOT} .${ROOT}__banner strong{color:var(--fg-0)}
.${ROOT} .${ROOT}__group{border-top:1px solid var(--line-strong);padding-top:6px;display:flex;flex-direction:column;gap:5px}
.${ROOT} .${ROOT}__glab{font-size:11px;font-weight:700;color:var(--fg-1);letter-spacing:.05em;text-transform:uppercase}
.${ROOT} .${ROOT}__btns{display:flex;gap:4px;flex-wrap:wrap}
.${ROOT} button.${ROOT}__b{font-family:var(--font-sans);font-size:11px;font-weight:600;min-height:28px;
  padding:4px 11px;border-radius:var(--r-sm);border:1px solid var(--line-strong);background:var(--bg-3);
  color:var(--fg-0);cursor:pointer;transition:background var(--dur-fast) var(--ease-out)}
.${ROOT} button.${ROOT}__b:hover:not(:disabled){background:var(--bg-2)}
.${ROOT} button.${ROOT}__b:focus-visible,.${ROOT} .${ROOT}__in:focus-visible,.${ROOT} input[type=checkbox]:focus-visible{outline:2px solid var(--accent-bright);outline-offset:2px}
.${ROOT} button.${ROOT}__b:disabled{opacity:.75;border-style:dashed;cursor:not-allowed}
.${ROOT} button.${ROOT}__b.${ROOT}__good{border-color:var(--ok);color:var(--ok)}
.${ROOT} button.${ROOT}__b.${ROOT}__small{min-height:24px;padding:2px 8px}
.${ROOT} button.${ROOT}__icon{min-width:28px;padding:4px 7px}
.${ROOT} .${ROOT}__in{font-family:var(--font-mono);font-size:12px;background:var(--bg-inset);color:var(--fg-0);
  border:1px solid var(--line);border-radius:var(--r-xs);padding:4px 6px;min-height:28px;min-width:0;max-width:100%}
.${ROOT} select.${ROOT}__in{font-family:var(--font-sans)}
.${ROOT} .${ROOT}__fld{display:grid;grid-template-columns:minmax(110px,auto) 1fr;align-items:center;gap:6px}
.${ROOT} .${ROOT}__fld > label{color:var(--fg-1);font-size:12px}
.${ROOT} .${ROOT}__chk{display:flex;align-items:center;gap:6px;color:var(--fg-0);font-size:12px;min-height:24px}
.${ROOT} .${ROOT}__muted{font-size:12px;color:var(--fg-1)}
.${ROOT} .${ROOT}__err{font-size:11px;color:var(--tx)}
.${ROOT} .${ROOT}__msg{font-size:11px;color:var(--fg-0)}
.${ROOT} .${ROOT}__ftab{display:flex;flex-direction:column;gap:4px}
.${ROOT} .${ROOT}__frow{display:grid;grid-template-columns:minmax(120px,150px) minmax(0,1fr) auto;gap:6px;align-items:center;
  padding:3px 4px;border:1px solid var(--line);border-radius:var(--r-sm);background:var(--bg-1)}
.${ROOT} .${ROOT}__frow > span{color:var(--fg-0);font-size:12px}
.${ROOT} .${ROOT}__frow .${ROOT}__cust{grid-column:2 / span 2}
.${ROOT} .${ROOT}__log{background:var(--bg-inset);border:1px solid var(--line);border-radius:var(--r-xs);
  font-family:var(--font-mono);font-size:11px;line-height:1.5;max-height:160px;overflow:auto;padding:6px}
.${ROOT} .${ROOT}__ln{display:flex;gap:6px}
.${ROOT} .${ROOT}__ts{color:var(--fg-2)}
.${ROOT} .${ROOT}__k-warn{color:var(--amber)}
.${ROOT} .${ROOT}__k-ok{color:var(--ok)}
@media (max-width:420px){.${ROOT} .${ROOT}__frow{grid-template-columns:1fr auto}.${ROOT} .${ROOT}__frow > span{grid-column:1 / span 2}}
@media (prefers-reduced-motion: reduce){.${ROOT} button.${ROOT}__b{transition:none}}
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
  if (!res.ok) {
    const err = new Error((data && data.message) || (method + " " + path + " failed (" + res.status + ")"));
    err.status = res.status;
    throw err;
  }
  return data;
}

const LEDS = { vfoa: "000", vfob: "101", af: "100", agc: "010", rit: "001", xit: "011", step: "110", drive: "111" };
const mhz = (hz) => hz ? (hz / 1e6).toFixed(6).replace(/(\.\d{3})(\d{3})$/, "$1.$2") + " MHz" : "—";

function Pill(text, kind, title) {
  return h("span", { className: c("pill " + (kind || "")), title: title || "" }, text);
}

function Field(label, id, control) {
  return h("div", { className: c("fld") }, h("label", { htmlFor: id }, label), control);
}

function makePanel(callBackend) {
  return function FlexKnobPanel() {
    const [st, setSt] = useState(null);
    const [err, setErr] = useState("");
    const [log, setLog] = useState([]);
    const [showLog, setShowLog] = useState(false);
    const [settings, setSettings] = useState(false);
    const [msg, setMsg] = useState("");
    const alive = useRef(true);

    useEffect(() => {
      alive.current = true;
      try {
        if (typeof window !== "undefined" && window.location) {
          call(callBackend, "POST", "/hello", { origin: window.location.origin }).catch(() => {});
        }
      } catch (e) { /* not in a browser */ }
      let timer = null;
      const tick = async () => {
        try {
          const s = await call(callBackend, "GET", "/status");
          if (alive.current) { setSt(s); setErr(""); }
        } catch (e) { if (alive.current) setErr(e.message || String(e)); }
        if (alive.current) timer = setTimeout(tick, 700);
      };
      tick();
      return () => { alive.current = false; if (timer) clearTimeout(timer); };
    }, []);

    useEffect(() => {
      if (!showLog) return undefined;
      let live = true;
      const load = async () => {
        try { const l = await call(callBackend, "GET", "/log"); if (live) setLog(l || []); } catch (e) { /* keep */ }
      };
      load();
      const t = setInterval(load, 1500);
      return () => { live = false; clearInterval(t); };
    }, [showLog]);

    if (settings) {
      return h("div", { className: ROOT, role: "region", "aria-label": "FlexControl knob settings" },
        h(Settings, { callBackend, onClose: () => setSettings(false), onMsg: setMsg }));
    }

    const k = st && st.knob;
    const tci = st && st.tci;
    const amp = st && st.amp;
    const z = st && st.zeus;
    const leds = (k && LEDS[k.mode]) || "000";

    const knobPill = !k ? Pill("KNOB …") : k.port === "" ? Pill("KNOB OFF") :
      k.connected ? Pill("KNOB " + k.device, "ok") : Pill("KNOB —", "warn", k.error);
    const tciPill = !tci ? Pill("TCI …") : tci.connected ? Pill("TCI", "ok", tci.target) : Pill("TCI —", "warn", tci.error);
    const ampPill = !amp ? null : amp.reachable ? Pill("KPA1500" + (amp.operate === true ? " OPR" : amp.operate === false ? " STBY" : ""), amp.connected ? "ok" : "") :
      Pill("KPA1500 —", "", amp.error || "");

    const banners = [];
    if (err) banners.push(h("div", { key: "e", className: c("banner warn"), role: "alert" }, h("strong", null, "Feature not answering"), h("span", null, err)));
    if (k && k.port !== "" && !k.connected) {
      banners.push(h("div", { key: "k", className: c("banner warn") },
        h("strong", null, "FlexControl knob not connected"),
        h("span", null, k.error || "Looking for it…"),
        h("span", { className: c("muted") }, "Plug the knob into the computer that runs Zeus. If it still isn't found, choose its port in ⚙ Settings.")));
    }
    if (tci && !tci.connected) {
      banners.push(h("div", { key: "t", className: c("banner warn") },
        h("strong", null, "Zeus TCI not connected — only VFO A tuning works"),
        h("span", null, tci.error || "Connecting…"),
        h("span", { className: c("muted") }, "Volume, mute, AGC, RIT and the other functions need TCI: turn TCI on in Zeus (port 40001 normally) and check the port in ⚙ Settings.")));
    }
    if (k && k.tuneActive) banners.push(h("div", { key: "tu", className: c("banner tx"), role: "status" }, h("strong", null, "TUNE running (started from the knob)"), h("span", null, "Press the TUNE button again to stop.")));

    return h("div", { className: ROOT, role: "region", "aria-label": "FlexControl knob" },
      h("div", { className: c("row spread") },
        h("div", { className: c("row") },
          h("span", { className: c("title") }, "FlexControl Knob"),
          h("span", { className: c("sub") }, "KW4EX v" + ((st && st.version) || "…"))),
        h("div", { className: c("row") }, knobPill, tciPill, ampPill,
          h("button", { type: "button", className: c("b icon"), title: "Settings", "aria-label": "Settings", onClick: () => { setMsg(""); setSettings(true); } }, "⚙"))),
      banners,
      h("div", { className: c("now") },
        h("div", { className: c("row spread") },
          h("span", { className: c("nowk") }, "Turning the knob now does"),
          h("span", { className: c("leds"), "aria-label": "Button LEDs" },
            [0, 1, 2].map((i) => h("span", { key: i, className: c("led" + (leds[i] === "1" ? " on" : "")), title: "LED " + (i + 1) })))),
        h("span", { className: c("nowv") }, k ? k.modeName : "…"),
        z ? h("span", { className: c("muted") },
          mhz(z.frequencyHz) + "   step " + z.stepText + " (" + z.stepSource + ")" + (z.mox ? "   TX" : "")) : null),
      k ? h("div", { className: c("lines") },
        h("span", { className: c("lk") }, "Knob press"), h("span", { className: c("lv") }, k.lines.knob),
        h("span", { className: c("lk") }, "Button 1"), h("span", { className: c("lv") }, k.lines.b1),
        h("span", { className: c("lk") }, "Button 2"), h("span", { className: c("lv") }, k.lines.b2),
        h("span", { className: c("lk") }, "Button 3"), h("span", { className: c("lv") }, k.lines.b3)) : null,
      k && k.last ? h("div", { className: c("muted") }, "Last from the knob: " + k.lastText + "  (" + k.last + ", " + (k.lastAge != null ? k.lastAge + " s ago" : "") + ")") : null,
      msg ? h("div", { className: c("msg"), role: "status" }, msg) : null,
      h("div", { className: c("group") },
        h("label", { className: c("chk") },
          h("input", { type: "checkbox", checked: showLog, onChange: (e) => setShowLog(e.target.checked) }), "Event log"),
        showLog ? h("div", { className: c("log"), role: "log", "aria-live": "polite" },
          log.length === 0 ? h("div", { className: c("muted") }, "Nothing yet.") :
            log.map((e, i) => h("div", { key: i, className: c("ln") },
              h("span", { className: c("ts") }, e.ts),
              h("span", { className: c("k-" + e.kind) }, e.message)))) : null));
  };
}

function Settings(props) {
  const { callBackend, onClose, onMsg } = props;
  const [cat, setCat] = useState(null);
  const [draft, setDraft] = useState(null);
  const [ports, setPorts] = useState({ ports: [], found: null });
  const [saveErr, setSaveErr] = useState("");
  const [tryMsg, setTryMsg] = useState("");
  const [busy, setBusy] = useState(false);

  const loadPorts = useCallback(async () => {
    try { setPorts((await call(callBackend, "GET", "/ports")) || { ports: [], found: null }); } catch (e) { /* keep */ }
  }, [callBackend]);

  useEffect(() => {
    call(callBackend, "GET", "/catalog").then(setCat).catch((e) => setSaveErr(e.message));
    call(callBackend, "GET", "/config").then(setDraft).catch((e) => setSaveErr(e.message));
    loadPorts();
  }, []);

  if (!cat || !draft) return h("div", { className: c("muted") }, saveErr || "Loading settings…");

  const id = (n) => ROOT + "-cfg-" + n;
  const set = (key, val) => setDraft(Object.assign({}, draft, { [key]: val }));
  const setTable = (ev, act) => setDraft(Object.assign({}, draft, { table: Object.assign({}, draft.table, { [ev]: act }) }));
  const setCustom = (ev, txt) => setDraft(Object.assign({}, draft, { custom: Object.assign({}, draft.custom, { [ev]: txt }) }));
  const num = (key, min, max) => h("input", { id: id(key), className: c("in"), type: "number", min, max, value: draft[key],
    onChange: (e) => set(key, e.target.value === "" ? min : Number(e.target.value)) });

  const groups = [];
  cat.actions.forEach((a) => {
    let g = groups.find((x) => x.name === a.group);
    if (!g) { g = { name: a.group, items: [] }; groups.push(g); }
    g.items.push(a);
  });
  const actionSelect = (ev) => h("select", { id: id("act-" + ev), className: c("in"), value: draft.table[ev] || "none",
    "aria-label": "Function for " + ev, onChange: (e) => setTable(ev, e.target.value) },
    groups.map((g) => g.name === "" ? g.items.map((a) => h("option", { key: a.id, value: a.id }, a.name)) :
      h("optgroup", { key: g.name, label: g.name }, g.items.map((a) => h("option", { key: a.id, value: a.id }, a.name)))));

  const save = async () => {
    setBusy(true); setSaveErr("");
    try {
      await call(callBackend, "POST", "/config", draft);
      onMsg("Settings saved."); onClose();
    } catch (e) { setSaveErr(e.message || String(e)); }
    setBusy(false);
  };
  const defaults = async () => {
    setBusy(true); setSaveErr("");
    try { setDraft(await call(callBackend, "POST", "/defaults")); setTryMsg("Functions put back to the defaults (and saved)."); }
    catch (e) { setSaveErr(e.message || String(e)); }
    setBusy(false);
  };
  const tryIt = async (ev) => {
    setTryMsg("…");
    try { const r = await call(callBackend, "POST", "/try", { event: ev }); setTryMsg(r.message); }
    catch (e) { setTryMsg(e.message || String(e)); }
  };

  const portOptions = [h("option", { key: "auto", value: "auto" }, "Auto (find the FlexControl)" + (ports.found ? " — " + ports.found : "")),
    h("option", { key: "off", value: "" }, "Off (don't use the knob)")]
    .concat((ports.ports || []).map((p) => h("option", { key: p, value: p }, p)));
  if (draft.port && draft.port !== "auto" && !(ports.ports || []).includes(draft.port)) {
    portOptions.push(h("option", { key: "cur", value: draft.port }, draft.port + " (not present now)"));
  }

  return h("div", { className: c("ftab") },
    h("div", { className: c("row spread") },
      h("span", { className: c("title") }, "FlexControl Knob — settings"),
      h("button", { type: "button", className: c("b icon"), "aria-label": "Close settings", onClick: onClose }, "✕")),

    h("div", { className: c("group") },
      h("div", { className: c("glab") }, "Knob"),
      Field("USB port", id("port"), h("div", { className: c("row") },
        h("select", { id: id("port"), className: c("in grow"), value: draft.port, onChange: (e) => set("port", e.target.value) }, portOptions),
        h("button", { type: "button", className: c("b small"), onClick: loadPorts }, "Refresh"))),
      Field("Spin speed-up (1 = off)", id("speed"), num("speed", 1, 8)),
      Field("AF volume per click (dB)", id("afStep"), num("afStep", 1, 10)),
      Field("Back to VFO A after (s, 0 = never)", id("returnS"), num("returnS", 0, 600))),

    h("div", { className: c("group") },
      h("div", { className: c("glab") }, "What each press does"),
      cat.events.map((e) => h("div", { key: e.id, className: c("frow") },
        h("span", null, e.name),
        actionSelect(e.id),
        h("button", { type: "button", className: c("b small"), title: "Run it now (saved settings)", onClick: () => tryIt(e.id) }, "Try"),
        draft.table[e.id] === "custom" ? h("input", { className: c("in cust"), placeholder: "TCI command, e.g. rx_bin_enable:0,true;",
          "aria-label": "Custom command for " + e.name, value: (draft.custom && draft.custom[e.id]) || "",
          onChange: (ev) => setCustom(e.id, ev.target.value) }) : null)),
      h("div", { className: c("muted") }, "Try runs the function as saved — Save first after changing one. VOX is not offered: Zeus has no VOX control that other programs can reach."),
      tryMsg ? h("div", { className: c("msg"), role: "status" }, tryMsg) : null),

    h("div", { className: c("group") },
      h("div", { className: c("glab") }, "VFO tuning step"),
      Field("Step comes from", id("stepSource"), h("select", { id: id("stepSource"), className: c("in"), value: draft.stepSource, onChange: (e) => set("stepSource", e.target.value) },
        h("option", { value: "zeus" }, "Zeus's toolbar step (recommended)"),
        h("option", { value: "own" }, "This setting"))),
      draft.stepSource === "own" ? Field("Step", id("ownStepHz"), h("select", { id: id("ownStepHz"), className: c("in"), value: draft.ownStepHz,
        onChange: (e) => set("ownStepHz", Number(e.target.value)) }, cat.steps.map((s) => h("option", { key: s, value: s },
          s >= 1e6 ? s / 1e6 + " MHz" : s >= 1000 ? s / 1000 + " kHz" : s + " Hz")))) : null),

    h("div", { className: c("group") },
      h("div", { className: c("glab") }, "Safe TUNE (the TUNE function)"),
      Field("Stop TUNE after (s, 0 = never)", id("tuneTimeoutS"), num("tuneTimeoutS", 0, 600)),
      h("label", { className: c("chk") },
        h("input", { type: "checkbox", checked: !!draft.tuneNeedsAmp, onChange: (e) => set("tuneNeedsAmp", e.target.checked) }),
        "TUNE needs the KPA1500 feature (refuse if it can't put the amp in STANDBY first)")),

    h("div", { className: c("group") },
      h("div", { className: c("glab") }, "Connections"),
      Field("Zeus TCI host", id("tciHost"), h("input", { id: id("tciHost"), className: c("in"), value: draft.tciHost, onChange: (e) => set("tciHost", e.target.value) })),
      Field("Zeus TCI port", id("tciPort"), num("tciPort", 1, 65535)),
      Field("Receiver (0 = RX1)", id("tciRx"), num("tciRx", 0, 1)),
      Field("Zeus web address", id("zeusUrl"), h("input", { id: id("zeusUrl"), className: c("in"), placeholder: "empty = automatic", value: draft.zeusUrl, onChange: (e) => set("zeusUrl", e.target.value) })),
      Field("KPA1500 feature id", id("ampPluginId"), h("input", { id: id("ampPluginId"), className: c("in"), value: draft.ampPluginId, onChange: (e) => set("ampPluginId", e.target.value) }))),

    saveErr ? h("div", { className: c("err"), role: "alert" }, saveErr) : null,
    h("div", { className: c("btns") },
      h("button", { type: "button", className: c("b good"), disabled: busy, onClick: save }, "Save"),
      h("button", { type: "button", className: c("b"), disabled: busy, onClick: onClose }, "Cancel"),
      h("button", { type: "button", className: c("b"), disabled: busy, onClick: defaults }, "Default functions")));
}

export default function activate(api) {
  const { registerPanel, callBackend } = api;
  ensureStyle();
  registerPanel({ id: PANEL_ID, component: makePanel(callBackend) });
}
