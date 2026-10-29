// SPDX-License-Identifier: GPL-2.0-or-later
//
// What the knob does. Each message from the knob arrives here; turning is
// handled according to the current knob mode (VFO A, AF volume, AGC ...) and
// each press runs the function assigned to it in the table. This is the
// bridge's knob_code / knob_turn / knob_action / knob_watchdog, with Thetis
// CAT commands replaced by Zeus calls.
//
// Messages are handled one at a time on a worker, in order, so a press that
// has to ask Zeus for a value never holds up the serial reader. Turn
// messages that pile up while Zeus is busy are added together, so a fast
// spin never lags behind.

using System.Globalization;
using System.Threading.Channels;
using Zeus.Plugins.Contracts;

namespace Zeus.Community.FlexKnob;

/// <summary>Zeus's tuning step (from its toolbar) and web address.</summary>
public interface IZeusWeb
{
    int? StepHz { get; }
    string? BaseUrl { get; }
}

public sealed class KnobController : IAsyncDisposable
{
    private readonly EventLog _log;
    private readonly Func<ITciLink?> _tci;
    private readonly IAmpLink _amp;
    private readonly Func<IRadioStateReader?> _radio;
    private readonly Func<IRadioController?> _rc;
    private readonly IZeusWeb _web;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly Timer _watchdog;
    private readonly object _lock = new();

    private KnobConfig _cfg;
    private string _mode = "vfoa";
    private readonly Dictionary<string, (long Value, long Ticks)> _vals = new();
    private readonly Dictionary<int, long> _bandMemory = new();
    private long _lastUse = Environment.TickCount64;
    private (string Code, string Text, long Ticks) _last = ("", "", 0);
    private int? _stepOverride;
    private long _vfoTarget;
    private long _vfoTargetTicks;
    private long _vfobTarget;
    private long _vfobTargetTicks;
    private long _tuneStartTicks;       // knob-started TUNE running since (0 = none)
    private bool _lockWarned;

    /// <summary>Where LED patterns go (the knob's serial port).</summary>
    public Action<string>? Leds { get; set; }

    public KnobController(KnobConfig cfg, EventLog log, Func<ITciLink?> tci, IAmpLink amp,
        Func<IRadioStateReader?> radio, Func<IRadioController?> rc, IZeusWeb web)
    {
        _cfg = cfg;
        _log = log;
        _tci = tci;
        _amp = amp;
        _radio = radio;
        _rc = rc;
        _web = web;
        _worker = Task.Run(() => WorkAsync(_cts.Token));
        _watchdog = new Timer(_ => _ = WatchdogAsync(), null, 500, 500);
    }

    public KnobConfig Config { get { lock (_lock) return _cfg; } }
    public void ApplyConfig(KnobConfig cfg) { lock (_lock) _cfg = cfg; }

    public string Mode { get { lock (_lock) return _mode; } }
    public string ModeName => KnobCatalog.ModeMap[Mode].Name;
    public (string Code, string Text, double? AgeS) Last
    {
        get
        {
            lock (_lock)
                return (_last.Code, _last.Text, _last.Ticks == 0 ? null : Math.Round((Environment.TickCount64 - _last.Ticks) / 1000.0, 1));
        }
    }

    private int Rx => Config.TciRx;
    private string R(string template) => KnobCatalog.Rx(template, Rx);

    /// <summary>The tuning step in use and where it comes from.</summary>
    public (int Hz, string Source) Step
    {
        get
        {
            var cfg = Config;
            lock (_lock)
            {
                if (_stepOverride is { } o) return (o, "knob");
            }
            if (cfg.StepSource == "own") return (cfg.OwnStepHz, "setting");
            return _web.StepHz is { } z and > 0 ? (z, "Zeus") : (500, "Zeus (not read yet)");
        }
    }

    public bool KnobTuneActive => Interlocked.Read(ref _tuneStartTicks) != 0;

    /// <summary>A message from the knob (called on the serial thread).</summary>
    public void Post(string code) => _queue.Writer.TryWrite(code);

    // ── worker ─────────────────────────────────────────────────────────────

    private async Task WorkAsync(CancellationToken ct)
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var code))
                {
                    if (code == "__connected__") { SetMode(Mode, ""); continue; }
                    var p = KnobCatalog.Parse(code);
                    if (p.Kind == "none") continue;
                    lock (_lock) _lastUse = Environment.TickCount64;
                    if (p.Kind == "turn")
                    {
                        // Add up turns already waiting behind this one.
                        long steps = Accel(p.Turn), raw = p.Turn;
                        while (reader.TryPeek(out var next))
                        {
                            var q = KnobCatalog.Parse(next);
                            if (q.Kind != "turn") break;
                            reader.TryRead(out _);
                            steps += Accel(q.Turn);
                            raw += q.Turn;
                        }
                        lock (_lock) _last = (code, $"turn {(raw > 0 ? "up" : "down")} {Math.Abs(raw)}", Environment.TickCount64);
                        await Guarded(() => TurnAsync(steps, raw, ct)).ConfigureAwait(false);
                    }
                    else
                    {
                        var act = Config.Table.GetValueOrDefault(p.Event, "none");
                        lock (_lock) _last = (code, KnobCatalog.EventName(p.Event), Environment.TickCount64);
                        var (ok, msg) = await RunGuardedAsync(p.Event, act, ct).ConfigureAwait(false);
                        if (act != "none")
                            _log.Add(ok ? "info" : "warn", $"Knob {KnobCatalog.EventName(p.Event)} → {(ok ? "" : "REFUSED: ")}{msg}");
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task Guarded(Func<Task> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await work().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.Add("warn", "Knob: " + ex.Message); }
        finally { _gate.Release(); }
    }

    private async Task<(bool, string)> RunGuardedAsync(string ev, string act, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await ActionAsync(ev, act, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return (false, ex.Message); }
        finally { _gate.Release(); }
    }

    /// <summary>The panel's Try button: run what is assigned to an event, as if pressed.</summary>
    public Task<(bool Ok, string Message)> TryAsync(string ev, CancellationToken ct) =>
        RunGuardedAsync(ev, Config.Table.GetValueOrDefault(ev, "none"), ct);

    /// <summary>Spin acceleration, as in the bridge: 3+ clicks in one message × Speed.</summary>
    private int Accel(int n) => Math.Abs(n) < 3 ? n : n * Config.Speed;

    // ── turning ────────────────────────────────────────────────────────────

    private async Task TurnAsync(long steps, long raw, CancellationToken ct)
    {
        var mode = KnobCatalog.ModeMap[Mode];
        switch (mode.Kind)
        {
            case ModeKind.Vfo: await TuneVfoAAsync(steps, ct).ConfigureAwait(false); return;
            case ModeKind.TciVfo: await TuneTciVfoAsync(mode, steps, ct).ConfigureAwait(false); return;
            case ModeKind.StepList: ChangeStep(raw > 0 ? 1 : -1); return;
            default: await TurnValueAsync(mode, raw, ct).ConfigureAwait(false); return;
        }
    }

    public static long RoundToStep(long hz, int step)
    {
        if (step <= 1) return hz;
        var q = Math.DivRem(hz, step, out var r);
        return (r * 2 >= step ? q + 1 : q) * step;
    }

    private bool VfoLocked()
    {
        var locked = _tci()?.State.GetBool(R("lock:{rx}")) == true;
        if (locked && !_lockWarned) { _log.Add("warn", "Knob: the VFO is locked in Zeus — tuning ignored (unlock it, or press the Lock function)."); }
        _lockWarned = locked;
        return locked;
    }

    private async Task TuneVfoAAsync(long steps, CancellationToken ct)
    {
        if (VfoLocked()) return;
        var step = Step.Hz;
        steps = Math.Clamp(steps, -400, 400);
        long baseHz;
        lock (_lock)
            baseHz = _vfoTarget > 0 && Environment.TickCount64 - _vfoTargetTicks < 600 ? _vfoTarget : 0;
        if (baseHz == 0) baseHz = _radio()?.FrequencyHz ?? 0;
        if (baseHz <= 0) baseHz = TciState.AsLong(_tci()?.State.Get(R("vfo:{rx},0"))) ?? 0;
        if (baseHz <= 0) { _log.Add("warn", "Knob: Zeus has not reported the frequency yet."); return; }
        var target = RoundToStep(baseHz + steps * step, step);
        if (target < 10_000 || target > 1_000_000_000) return;
        lock (_lock) { _vfoTarget = target; _vfoTargetTicks = Environment.TickCount64; }
        if (_rc() is { } rc) await rc.SetFrequencyAsync(target, ct).ConfigureAwait(false);
        else if (_tci() is { Connected: true } t) await t.SendAsync(R("vfo:{rx},0,") + target.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
    }

    private async Task TuneTciVfoAsync(KnobMode mode, long steps, CancellationToken ct)
    {
        if (_tci() is not { Connected: true } t) return;
        if (VfoLocked()) return;
        var step = Step.Hz;
        long baseHz;
        lock (_lock)
            baseHz = _vfobTarget > 0 && Environment.TickCount64 - _vfobTargetTicks < 600 ? _vfobTarget : 0;
        if (baseHz == 0) baseHz = TciState.AsLong(t.State.Get(R(mode.Key))) ?? 0;
        if (baseHz <= 0)
            baseHz = TciState.AsLong(await QueryAsync(R(mode.Get), R(mode.Key), ct).ConfigureAwait(false)) ?? 0;
        if (baseHz <= 0) { _log.Add("warn", "Knob: Zeus did not tell the VFO B frequency."); return; }
        var target = RoundToStep(baseHz + Math.Clamp(steps, -400, 400) * step, step);
        lock (_lock) { _vfobTarget = target; _vfobTargetTicks = Environment.TickCount64; }
        await t.SendAsync(string.Format(CultureInfo.InvariantCulture, R(mode.Set), target), ct).ConfigureAwait(false);
    }

    private async Task TurnValueAsync(KnobMode mode, long raw, CancellationToken ct)
    {
        if (_tci() is not { Connected: true } t) return;
        var now = Environment.TickCount64;
        long? cur = null;
        lock (_lock)
            if (_vals.TryGetValue(mode.Id, out var v) && now - v.Ticks < 3000) cur = v.Value;
        if (cur is null)
        {
            // Not turned lately: ask Zeus for the value now (someone may have moved the slider).
            cur = TciState.AsLong(await QueryAsync(R(mode.Get), R(mode.Key), ct).ConfigureAwait(false))
                  ?? TciState.AsLong(t.State.Get(R(mode.Key)));
            if (cur is null) { _log.Add("warn", $"Knob: Zeus did not tell the {mode.Name} — try again."); return; }
        }
        var per = mode.Id == "af" ? Config.AfStep : mode.PerClick;
        var next = Math.Clamp(cur.Value + Math.Sign(raw) * Math.Min(Math.Abs(raw), 10) * per, mode.Min, mode.Max);
        lock (_lock) _vals[mode.Id] = (next, Environment.TickCount64);
        t.State.Put(R(mode.Key), next.ToString(CultureInfo.InvariantCulture));
        await t.SendAsync(string.Format(CultureInfo.InvariantCulture, R(mode.Set), next), ct).ConfigureAwait(false);
    }

    private string ChangeStep(int dir)
    {
        var cur = Step.Hz;
        var list = KnobCatalog.Steps;
        var i = 0;
        while (i < list.Count - 1 && list[i] < cur) i++;          // first listed step at or above
        if (list[i] == cur) i = Math.Clamp(i + dir, 0, list.Count - 1);
        else if (list[i] > cur && dir < 0) i = Math.Max(0, i - 1); // between two steps: the one below
        // between two steps going up: list[i] is already the one above
        lock (_lock) _stepOverride = list[i];
        return "tuning step " + StepText(list[i]);
    }

    public static string StepText(int hz) => hz >= 1_000_000 ? $"{hz / 1_000_000} MHz" : hz >= 1000 ? $"{hz / 1000.0:0.#} kHz" : $"{hz} Hz";

    // ── modes ──────────────────────────────────────────────────────────────

    private void SetMode(string mode, string why)
    {
        if (!KnobCatalog.ModeMap.TryGetValue(mode, out var m)) return;
        bool changed;
        lock (_lock)
        {
            changed = _mode != mode;
            _mode = mode;
        }
        if (changed) _log.Add("info", $"Knob: {m.Name}{why}");
        Leds?.Invoke($"I{m.Leds};");
    }

    // ── presses ────────────────────────────────────────────────────────────

    private async Task<(bool Ok, string Message)> ActionAsync(string ev, string act, CancellationToken ct)
    {
        if (!KnobCatalog.ActionMap.TryGetValue(act, out var spec) || act == "none") return (true, "nothing assigned");
        var how = spec.How;
        if (how.StartsWith("toggle_mode:", StringComparison.Ordinal))
        {
            var m = how["toggle_mode:".Length..];
            SetMode(Mode == m ? "vfoa" : m, "");
            return (true, "knob now: " + ModeName);
        }
        if (how.StartsWith("mode:", StringComparison.Ordinal))
        {
            SetMode(how["mode:".Length..], "");
            return (true, "knob now: " + ModeName);
        }
        if (how == "cycle")
        {
            var i = KnobCatalog.Cycle.ToList().IndexOf(Mode);
            SetMode(KnobCatalog.Cycle[(i + 1) % KnobCatalog.Cycle.Count], "");
            return (true, "knob now: " + ModeName);
        }
        if (how.StartsWith("step:", StringComparison.Ordinal))
        {
            var w = how["step:".Length..];
            if (w == "zeus")
            {
                lock (_lock) _stepOverride = null;
                return (true, "tuning step follows Zeus again (" + StepText(Step.Hz) + ")");
            }
            return (true, ChangeStep(w == "up" ? 1 : -1));
        }
        if (how.StartsWith("band:", StringComparison.Ordinal)) return await BandAsync(how.EndsWith("up", StringComparison.Ordinal) ? 1 : -1, ct).ConfigureAwait(false);
        if (how == "mox") return await MoxAsync(ct).ConfigureAwait(false);
        if (how == "tune") return await TuneAsync(ct).ConfigureAwait(false);
        if (how.StartsWith("amp:", StringComparison.Ordinal)) return await AmpAsync(how["amp:".Length..], ct).ConfigureAwait(false);

        if (_tci() is not { Connected: true } t) return (false, "Zeus TCI is not connected (turn TCI on in Zeus)");
        if (how.StartsWith("tci_toggle:", StringComparison.Ordinal))
        {
            var parts = how["tci_toggle:".Length..].Split('|');
            var (get, key, set) = (R(parts[0]), R(parts[1]), R(parts[2]));
            bool? cur = null;
            if (get.Length > 0) cur = TciState.AsBool(await QueryAsync(get, key, ct).ConfigureAwait(false));
            cur ??= t.State.GetBool(key);
            if (cur is null && get.Length > 0) return (false, $"Zeus did not answer {get}");
            var next = !(cur ?? false);
            var b = next ? "true" : "false";
            if (!await t.SendAsync(string.Format(CultureInfo.InvariantCulture, set, b), ct).ConfigureAwait(false))
                return (false, "could not send to Zeus");
            t.State.Put(key, b);
            return (true, $"{spec.Name.Replace(" on / off", "", StringComparison.Ordinal)} {(next ? "ON" : "OFF")}");
        }
        if (how.StartsWith("tci:", StringComparison.Ordinal))
            return await t.SendAsync(R(how["tci:".Length..]), ct).ConfigureAwait(false) ? (true, spec.Name) : (false, "could not send to Zeus");
        if (how == "custom")
        {
            var c = Config.Custom.GetValueOrDefault(ev, "");
            if (c.Length == 0) return (false, "no custom command typed for this event");
            return await t.SendAsync(c, ct).ConfigureAwait(false) ? (true, "custom: " + c) : (false, "could not send to Zeus");
        }
        return (false, "unknown function");
    }

    /// <summary>Ask Zeus for a value (TCI get) and wait briefly for the answer; null if none.</summary>
    private async Task<string?> QueryAsync(string get, string key, CancellationToken ct, int ms = 600)
    {
        if (_tci() is not { Connected: true } t) return null;
        var wait = t.State.Next(key);
        if (!await t.SendAsync(get, ct).ConfigureAwait(false)) { t.State.CancelWait(key); return null; }
        var done = await Task.WhenAny(wait, Task.Delay(ms, ct)).ConfigureAwait(false);
        if (done == wait && wait.IsCompletedSuccessfully) return wait.Result;
        t.State.CancelWait(key);
        return null;
    }

    private bool Transmitting()
    {
        var st = _tci()?.State;
        return _radio()?.Mox == true || st?.GetBool(R("trx:{rx}")) == true || st?.GetBool(R("tune:{rx}")) == true;
    }

    private async Task<(bool, string)> BandAsync(int dir, CancellationToken ct)
    {
        if (Transmitting()) return (false, "Band change refused while transmitting");
        var hz = _radio()?.FrequencyHz ?? 0;
        if (hz <= 0) hz = TciState.AsLong(_tci()?.State.Get(R("vfo:{rx},0"))) ?? 0;
        if (hz <= 0) return (false, "Zeus has not reported the frequency yet");
        var here = KnobCatalog.BandIndex(hz);
        var to = KnobCatalog.NextBand(hz, dir);
        long target;
        lock (_lock)
        {
            if (here >= 0) _bandMemory[here] = hz;                    // come back here later
            target = _bandMemory.TryGetValue(to, out var m) ? m : KnobCatalog.Bands[to].Home;
            _vfoTarget = 0;
        }
        if (_rc() is { } rc) await rc.SetFrequencyAsync(target, ct).ConfigureAwait(false);
        else if (_tci() is { Connected: true } t) await t.SendAsync(R("vfo:{rx},0,") + target.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        else return (false, "Zeus radio control is not available");
        return (true, $"{KnobCatalog.Bands[to].Name} ({target / 1000.0:0.000} kHz)");
    }

    private async Task<(bool, string)> MoxAsync(CancellationToken ct)
    {
        if (_rc() is not { } rc) return (false, "Zeus radio control is not available");
        var on = _radio()?.Mox == true;
        if (!on)
        {
            var a = await _amp.StatusAsync(ct).ConfigureAwait(false);
            if (a?.NoAntState == "low") return (false, "MOX refused: no antenna? (press DUMMY OK if a dummy load is connected)");
        }
        await rc.SetMoxAsync(!on, ct).ConfigureAwait(false);
        return (true, "MOX " + (on ? "OFF" : "ON"));
    }

    /// <summary>
    /// TUNE the safe way, as the bridge did: put the amp in STANDBY and wait
    /// until it confirms, then start the radio's TUNE. Pressing again stops
    /// it. The KPA1500 feature's Standby-on-Tune restores OPERATE afterwards.
    /// </summary>
    private async Task<(bool, string)> TuneAsync(CancellationToken ct)
    {
        if (_tci() is not { Connected: true } t) return (false, "Refused: Zeus TCI is not connected - nothing keyed");
        var tuneKey = R("tune:{rx}");
        if (KnobTuneActive || t.State.GetBool(tuneKey) == true)
        {
            Interlocked.Exchange(ref _tuneStartTicks, 0);
            var sent = await t.SendAsync(R("tune:{rx},false"), ct).ConfigureAwait(false);
            return (true, "TUNE stopped from the knob" + (sent ? "" : " (Zeus not reachable)"));
        }
        if (Transmitting()) return (false, "Refused: the radio is already transmitting");

        var cfg = Config;
        var a = await _amp.StatusAsync(ct).ConfigureAwait(false);
        string how;
        if (a is null)
        {
            if (cfg.TuneNeedsAmp)
                return (false, "Refused: the KPA1500 feature could not be reached (" + _amp.LastError + ") - nothing keyed. " +
                               "Untick 'TUNE needs the KPA1500 feature' in the knob settings to tune without it.");
            how = " - KPA1500 feature not reachable: amp not checked";
        }
        else if (a.NoAntState == "low")
            return (false, "Refused: no antenna? Press DUMMY OK first if a dummy load is connected.");
        else if (a.Connected && a.Power == "on" && a.Operate == true)
        {
            var (ok, msg) = await _amp.PostAsync("tune-guard", new { arm = true }, ct).ConfigureAwait(false);
            if (!ok && msg.Contains("switched off", StringComparison.OrdinalIgnoreCase))
                how = " - Standby-on-Tune is OFF: amp left as it is";
            else if (!ok)
                return (false, "Refused: " + msg + " - nothing keyed");
            else
            {
                // Wait until the amp itself reports STANDBY.
                var confirmed = false;
                var end = Environment.TickCount64 + 2500;
                while (Environment.TickCount64 < end && !ct.IsCancellationRequested)
                {
                    await Task.Delay(150, ct).ConfigureAwait(false);
                    var s = await _amp.StatusAsync(ct).ConfigureAwait(false);
                    if (s?.Operate == false) { confirmed = true; break; }
                }
                if (!confirmed)
                {
                    await _amp.PostAsync("tune-guard", new { arm = false }, ct).ConfigureAwait(false);
                    return (false, "Refused: the amp did not confirm STANDBY - nothing keyed");
                }
                await Task.Delay(100, ct).ConfigureAwait(false);   // let the amp's relays settle before RF
                how = " - amp in STANDBY first (CONFIRMED)";
            }
        }
        else if (a.Connected && a.Power == "on" && a.Operate == false) how = " - amp was already in STANDBY";
        else how = " - amp is off or not connected";

        Interlocked.Exchange(ref _tuneStartTicks, Environment.TickCount64);
        if (!await t.SendAsync(R("tune:{rx},true"), ct).ConfigureAwait(false))
        {
            Interlocked.Exchange(ref _tuneStartTicks, 0);
            return (false, "Could not reach Zeus - tune not started");
        }
        return (true, "TUNE started from the knob" + how +
                      (cfg.TuneTimeoutS > 0 ? $", auto-stop after {cfg.TuneTimeoutS} s" : ""));
    }

    private async Task<(bool, string)> AmpAsync(string what, CancellationToken ct)
    {
        if (what == "sot") return await _amp.ToggleStandbyOnTuneAsync(ct).ConfigureAwait(false);
        if (what == "atu_tune") return await _amp.PostAsync("atu/tune", null, ct).ConfigureAwait(false);
        var a = await _amp.StatusAsync(ct).ConfigureAwait(false);
        if (a is null) return (false, "KPA1500 feature not reachable: " + _amp.LastError);
        return what switch
        {
            "operate" => await _amp.PostAsync("operate", new { operate = a.Operate != true }, ct).ConfigureAwait(false),
            "antenna" => await _amp.PostAsync("antenna", new { antenna = a.Antenna == 2 ? 1 : 2 }, ct).ConfigureAwait(false),
            "dummy" => await _amp.PostAsync("noant/dummy", new { on = a.NoAntState != "dummy" }, ct).ConfigureAwait(false),
            _ => (false, "unknown amplifier function"),
        };
    }

    // ── watchdog: TUNE time limit, back to VFO A ───────────────────────────

    private int _watching;

    private async Task WatchdogAsync()
    {
        if (Interlocked.Exchange(ref _watching, 1) == 1) return;
        try
        {
            var cfg = Config;
            var start = Interlocked.Read(ref _tuneStartTicks);
            var t = _tci();
            if (start != 0)
            {
                var running = t?.State.GetBool(R("tune:{rx}"));
                var age = Environment.TickCount64 - start;
                if (running == false && age > 1500) Interlocked.Exchange(ref _tuneStartTicks, 0);   // stopped in Zeus
                else if (cfg.TuneTimeoutS > 0 && age > cfg.TuneTimeoutS * 1000L)
                {
                    Interlocked.Exchange(ref _tuneStartTicks, 0);
                    if (t is { Connected: true }) await t.SendAsync(R("tune:{rx},false")).ConfigureAwait(false);
                    _log.Add("warn", $"Knob TUNE auto-stopped after {cfg.TuneTimeoutS} s.");
                }
            }
            long idle;
            lock (_lock) idle = Environment.TickCount64 - _lastUse;
            if (cfg.ReturnS > 0 && Mode != "vfoa" && idle > cfg.ReturnS * 1000L)
                SetMode("vfoa", $" (back after {cfg.ReturnS} s)");
        }
        catch (Exception) { }
        finally { Interlocked.Exchange(ref _watching, 0); }
    }

    // ── short names for the panel ("tap AF  2x Mute  hold VFO A") ──────────

    public string Short(string ev)
    {
        var cfg = Config;
        var act = cfg.Table.GetValueOrDefault(ev, "none");
        if (!KnobCatalog.ActionMap.TryGetValue(act, out var spec)) return "-";
        var how = spec.How;
        var mode = Mode;
        if (how.StartsWith("toggle_mode:", StringComparison.Ordinal))
        {
            var m = how["toggle_mode:".Length..];
            return ">" + KnobCatalog.ModeMap[mode == m ? "vfoa" : m].Short;
        }
        if (how.StartsWith("mode:", StringComparison.Ordinal)) return ">" + KnobCatalog.ModeMap[how["mode:".Length..]].Short;
        if (how == "cycle")
        {
            var i = KnobCatalog.Cycle.ToList().IndexOf(mode);
            return ">" + KnobCatalog.ModeMap[KnobCatalog.Cycle[(i + 1) % KnobCatalog.Cycle.Count]].Short;
        }
        if (act == "custom")
        {
            var c = cfg.Custom.GetValueOrDefault(ev, "");
            return c.Length > 0 ? "C:" + c.TrimEnd(';')[..Math.Min(9, c.TrimEnd(';').Length)] : "C:?";
        }
        return spec.Short;
    }

    public string Line(string prefix) => $"tap {Short(prefix + "_tap")}   2x {Short(prefix + "_double")}   hold {Short(prefix + "_long")}";

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _cts.Cancel();
        await _watchdog.DisposeAsync().ConfigureAwait(false);
        try { await _worker.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
