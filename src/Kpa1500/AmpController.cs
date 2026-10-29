// SPDX-License-Identifier: GPL-2.0-or-later
//
// Owns the link to the KPA1500 (UDP port 1500, or USB as a fallback), the poll
// loop, frequency tracking from Zeus, Standby-on-Tune, operator commands and
// the safety rules. Behaviour follows the earlier, hardware-proven
// kpa1500_thetis_bridge.py (v2.9) wherever that script had an answer.
//
// Safety rules (deliberate — keep them):
//   • This code never keys the transmitter. ATU TUNE (^FT;) only starts the
//     amp's full-search tune; the operator supplies the carrier.
//   • Nothing that switches relays happens under RF: band, antenna, ATU
//     in-line/bypass, ATU tune, OPERATE and power-off are refused (409) while
//     the radio or amp is transmitting or the ATU is tuning. Frequency/band
//     tracking waits for unkey. STANDBY (amp bypassed) is always allowed.
//   • Automatic actions only ever move the amp toward STANDBY. The single
//     exception is Standby-on-Tune returning the amp to OPERATE after TUNE,
//     and only when it was confirmed in OPERATE before the tune started.
//   • If the tune signal is lost mid-tune (TCI drops), the amp is left in
//     STANDBY — fail-safe, as in the bridge.
//   • No network/serial I/O happens on a Zeus radio-event thread: handlers
//     record the value and wake the poll loop.

using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;

namespace Zeus.Community.Kpa1500;

public sealed record CommandResult(bool Ok, int Status, string Message)
{
    public static CommandResult Done(string msg) => new(true, 200, msg);
    public static CommandResult Accepted(string msg) => new(true, 202, msg);
    public static CommandResult Bad(string msg) => new(false, 400, msg);
    public static CommandResult Conflict(string msg) => new(false, 409, msg);
    public static CommandResult Unavailable(string msg) => new(false, 503, msg);
}

/// <summary>Point-in-time copy of everything the panel shows.</summary>
public sealed class AmpState
{
    public string Connection { get; set; } = "closed";   // closed | opening | open
    public string? ConnectionError { get; set; }
    public string Target { get; set; } = "";
    public string Power { get; set; } = "unknown";      // unknown (no reply) | on | off
    public bool? Operate { get; set; }
    public string? FaultCode { get; set; }
    public int? BandCode { get; set; }
    public int? Antenna { get; set; }
    public bool? AtuInline { get; set; }
    public bool? Tuning { get; set; }
    public int? ForwardW { get; set; }
    public int? ReflectedW { get; set; }
    public int? InputW { get; set; }
    public int? DissipationW { get; set; }
    public double? Swr { get; set; }
    public double? BypassSwr { get; set; }
    public int? TempC { get; set; }
    public double? Volts { get; set; }
    public double? Amps { get; set; }
    public int? Fan { get; set; }
    public int? FanMinimum { get; set; }
    public int? AmpFreqKhz { get; set; }
    public bool? WakeOnLan { get; set; }
    public string? Mac { get; set; }
    public string? Firmware { get; set; }
    public string? SerialNumber { get; set; }
    public bool PowerOnInProgress { get; set; }

    public AmpState Copy() => (AmpState)MemberwiseClone();

    public void ClearTelemetry()
    {
        Operate = null; FaultCode = null; BandCode = null; Antenna = null; AtuInline = null;
        Tuning = null; ForwardW = null; ReflectedW = null; InputW = null; DissipationW = null;
        Swr = null; BypassSwr = null; TempC = null; Volts = null; Amps = null; Fan = null; FanMinimum = null; AmpFreqKhz = null;
    }
}

public sealed class AmpController : IAsyncDisposable
{
    /// <summary>Forward power at or above this counts as RF present (bridge: fwd &gt; 5 W).</summary>
    public const int RfPresentWatts = 6;

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);

    private static readonly string[] SlowQueries = { "PWI", "PWR", "TM", "VI", "AM", "AN", "BN", "FS", "FC", "PWD", "SB" };

    private readonly ILogger _log;
    private readonly EventLog _events;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Func<PluginConfig, CancellationToken, Task<KpaLink>> _open;

    private PluginConfig _cfg = new();
    private AmpState _state = new();
    private KpaLink? _link;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private TciClient? _tci;

    // Radio state (written from Zeus event threads, read on the poll thread).
    private IRadioStateReader? _radio;
    private long _radioHz;
    private volatile bool _radioMox;
    private volatile string _radioMode = "";

    // Poll-thread bookkeeping.
    private bool _wantOpen;
    private DateTime _nextOpenAttempt = DateTime.MinValue;
    private int _slowIndex;
    private int _cycle;
    private int _misses;
    private bool _ampAnswering;
    private int _bandPending = -1;
    private int _lastBandSent = -1;
    private long _lastFreqSentKhz = -1;
    private bool _settlePending;
    private bool _prevTuneSignal;
    private readonly TuneGuard _tune = new();
    private int _powerOnBusy;

    // "No antenna?" check.
    private readonly NoAntennaMonitor _noAnt = new();
    private bool _noAntHold;          // amp put in STANDBY by the check (option "standby")
    private NoAntState _noAntLast = NoAntState.Off;
    private CancellationTokenSource? _scanCts;
    private volatile bool _scanning;
    private volatile string _scanStatus = "";

    public NoAntennaMonitor NoAntenna => _noAnt;

    /// <summary>Raised (poll thread) when a new amp MAC is learned, so it can be saved.</summary>
    public event Action<string>? MacLearned;

    public AmpController(ILogger log, EventLog events,
        Func<PluginConfig, CancellationToken, Task<KpaLink>>? open = null)
    {
        _log = log;
        _events = events;
        _open = open ?? DefaultOpenAsync;
    }

    // ── lifecycle / config ──────────────────────────────────────────────────

    public void Start(PluginConfig cfg)
    {
        lock (_gate)
        {
            _cfg = cfg;
            ApplyNoAntConfig(cfg);
            _wantOpen = cfg.AutoConnect && HasTarget(cfg);
            _state.Mac = cfg.AmpMac == "" ? null : cfg.AmpMac;
        }
        RestartTci(cfg);
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public PluginConfig Config { get { lock (_gate) return _cfg.Clone(); } }

    public void ApplyConfig(PluginConfig next)
    {
        bool reopen, tciChanged;
        lock (_gate)
        {
            reopen = next.Transport != _cfg.Transport || next.Host != _cfg.Host ||
                     next.NetPort != _cfg.NetPort || next.SerialPort != _cfg.SerialPort ||
                     next.Baud != _cfg.Baud;
            tciChanged = next.TciEnabled != _cfg.TciEnabled || next.TciHost != _cfg.TciHost ||
                         next.TciPort != _cfg.TciPort || next.TciReceiver != _cfg.TciReceiver;
            _cfg = next;
            ApplyNoAntConfig(next);
            if (next.AmpMac != "") _state.Mac = next.AmpMac;
            if (reopen)
            {
                _wantOpen = HasTarget(next) && (next.AutoConnect || _link is not null);
                _nextOpenAttempt = DateTime.MinValue;
            }
        }
        if (reopen) _ = DropLinkAsync("connection settings changed");
        if (tciChanged) RestartTci(next);
        Wake();
    }

    private void ApplyNoAntConfig(PluginConfig c)
    {
        _noAnt.Enabled = c.NoAntEnabled;
        _noAnt.AutoLearn = c.NoAntAutoLearn;
        _noAnt.BelowFloorDb = c.NoAntBelowFloorDb;
        _noAnt.AboveEquipDb = c.NoAntAboveEquipDb;
    }

    private void OnRxLevel(double dbm) => _noAnt.AddLevel(dbm, DateTime.UtcNow);

    private void RestartTci(PluginConfig cfg)
    {
        var old = Interlocked.Exchange(ref _tci, null);
        if (old is not null)
        {
            old.Changed -= Wake;
            old.RxLevel -= OnRxLevel;
            _ = old.DisposeAsync().AsTask();
        }
        if (!cfg.TciEnabled) return;
        var t = new TciClient(cfg.TciHost, cfg.TciPort, cfg.TciReceiver, Note);
        t.Changed += Wake;
        t.RxLevel += OnRxLevel;
        _tci = t;
    }

    private static bool HasTarget(PluginConfig c) =>
        c.Transport == "serial" ? !string.IsNullOrWhiteSpace(c.SerialPort) : !string.IsNullOrWhiteSpace(c.Host);

    private static string Describe(PluginConfig c) =>
        c.Transport == "serial" ? $"{c.SerialPort} @ {c.Baud}" : $"{c.Host} UDP {c.NetPort}";

    private static Task<KpaLink> DefaultOpenAsync(PluginConfig c, CancellationToken ct) =>
        c.Transport == "serial"
            ? Task.Run(() => Transports.OpenSerial(c.SerialPort.Trim(), c.Baud), ct)
            : Transports.OpenUdpAsync(c.Host.Trim(), c.NetPort, ct);

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        }
        var tci = Interlocked.Exchange(ref _tci, null);
        if (tci is not null) await tci.DisposeAsync().ConfigureAwait(false);
        await DropLinkAsync(null).ConfigureAwait(false);
        _cts?.Dispose();
    }

    // ── radio state (Zeus threads: record + wake only) ──────────────────────

    public bool RadioAttached => _radio is not null;

    public void AttachRadio(IRadioStateReader radio)
    {
        if (_radio is not null) return;
        _radio = radio;
        Interlocked.Exchange(ref _radioHz, radio.FrequencyHz);
        _radioMox = radio.Mox;
        _radioMode = radio.Mode ?? "";
        radio.FrequencyChanged += OnFrequency;
        radio.MoxChanged += OnMox;
        radio.ModeChanged += OnMode;
        Note("info", $"Following Zeus radio state ({radio.FrequencyHz / 1000.0:0.0} kHz).");
    }

    public void DetachRadio()
    {
        if (_radio is null) return;
        _radio.FrequencyChanged -= OnFrequency;
        _radio.MoxChanged -= OnMox;
        _radio.ModeChanged -= OnMode;
        _radio = null;
    }

    private void OnFrequency(long hz) { Interlocked.Exchange(ref _radioHz, hz); Wake(); }
    private void OnMox(bool tx) { _radioMox = tx; Wake(); }
    private void OnMode(string mode) { _radioMode = mode ?? ""; Wake(); }

    private void Wake()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    // ── status ──────────────────────────────────────────────────────────────

    public AmpState State { get { lock (_gate) return _state.Copy(); } }
    public long RadioHz => Interlocked.Read(ref _radioHz);
    public bool RadioMox => _radioMox;
    public string RadioMode => _radioMode;
    public int PendingBand => _bandPending;
    public (bool Enabled, bool Connected, bool Tune, string Target, string Error, long Levels, double? LastLevel, int? LevelChannel) Tci
    {
        get
        {
            var t = _tci;
            return t is null ? (false, false, false, "", "", 0, null, null)
                : (true, t.Connected, t.Tune, t.Target, t.LastError, t.LevelCount, t.LastLevel, t.LevelChannel);
        }
    }

    public (TunePhase Phase, string Trigger, bool Restore) TuneGuardState
    {
        get { lock (_gate) return (_tune.Phase, _tune.Trigger, _tune.RestoreOperate); }
    }

    /// <summary>RF present per Zeus MOX, TCI trx, or the amp's own forward-power reading.</summary>
    public bool Transmitting
    {
        get { lock (_gate) return TxLocked(); }
    }

    private bool TxLocked() => _radioMox || (_tci?.Trx ?? false) || (_state.ForwardW ?? 0) >= RfPresentWatts;

    private bool RelayBusy(out string why)
    {
        lock (_gate)
        {
            if (TxLocked()) { why = "Refused: transmitting — not allowed under RF."; return true; }
            if (_state.Tuning == true) { why = "Refused: the ATU is tuning."; return true; }
        }
        why = "";
        return false;
    }

    private bool TuneSignal(PluginConfig cfg) =>
        (cfg.TciEnabled && (_tci?.Tune ?? false)) ||
        (cfg.AutoTuneDetect && TuneGuard.IsTuneMode(_radioMode));

    // ── poll loop ───────────────────────────────────────────────────────────

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            PluginConfig cfg;
            lock (_gate) cfg = _cfg;
            var delay = cfg.PollMs;
            try
            {
                var link = _link;
                if (link is null || !link.IsAlive)
                {
                    if (link is not null) await DropLinkAsync("link closed").ConfigureAwait(false);
                    bool want;
                    lock (_gate) want = _wantOpen && HasTarget(_cfg);
                    if (want && DateTime.UtcNow >= _nextOpenAttempt)
                        await OpenCoreAsync(cfg, ct).ConfigureAwait(false);
                    delay = 500;
                }
                else
                {
                    delay = await CycleAsync(link, cfg, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "KPA1500 poll cycle failed");
                await DropLinkAsync(ex.Message).ConfigureAwait(false);
            }

            try { await _wake.WaitAsync(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task OpenCoreAsync(PluginConfig cfg, CancellationToken ct)
    {
        var target = Describe(cfg);
        lock (_gate) { _state.Connection = "opening"; _state.Target = target; _state.ConnectionError = null; }
        try
        {
            _link = await _open(cfg, ct).ConfigureAwait(false);
            _settlePending = true;
            _misses = 0;
            _ampAnswering = false;
            _lastBandSent = -1;
            _lastFreqSentKhz = -1;
            lock (_gate) { _state.Connection = "open"; _state.Power = "unknown"; }
            Note("info", $"Talking to the KPA1500 at {target}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _nextOpenAttempt = DateTime.UtcNow + RetryInterval;
            lock (_gate) { _state.Connection = "closed"; _state.ConnectionError = ex.Message; }
            Note("warn", $"Could not open {target}: {ex.Message}");
        }
    }

    private async Task DropLinkAsync(string? reason)
    {
        var link = Interlocked.Exchange(ref _link, null);
        bool hadGuard;
        lock (_gate)
        {
            _state.Connection = "closed";
            _state.Power = "unknown";
            _state.ClearTelemetry();
            if (reason is not null) _state.ConnectionError = reason;
            hadGuard = _tune.Phase != TunePhase.Idle;
            _tune.Cancel();
        }
        if (hadGuard) Note("warn", "Link lost during Standby-on-Tune — check the amp before transmitting.");
        _bandPending = -1;
        _nextOpenAttempt = DateTime.UtcNow + RetryInterval;
        if (link is null) return;
        if (reason is not null) Note("warn", $"Link closed: {reason}.");
        await link.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<string?> Q(KpaLink link, string letters, CancellationToken ct)
    {
        var r = await link.QueryAsync(letters, QueryTimeout, ct).ConfigureAwait(false);
        if (r is null)
        {
            if (++_misses == 5 && _ampAnswering)
            {
                _ampAnswering = false;
                Note("warn", "Amplifier not responding.");
            }
        }
        else
        {
            if (!_ampAnswering) Note("ok", "Amplifier responding.");
            _misses = 0;
            _ampAnswering = true;
        }
        return r;
    }

    /// <summary>One poll pass. Returns the delay before the next pass.</summary>
    private async Task<int> CycleAsync(KpaLink link, PluginConfig cfg, CancellationToken ct)
    {
        _cycle++;
        var on = await Q(link, "ON", ct).ConfigureAwait(false);
        var power = KpaProtocol.Bool01(on ?? "");
        if (power != true)
        {
            var label = power == false ? "off" : "unknown";
            bool changed, guard;
            lock (_gate)
            {
                changed = _state.Power != label;
                _state.Power = label;
                _state.ClearTelemetry();
                guard = _tune.Phase != TunePhase.Idle;
                _tune.Cancel();
            }
            if (guard) Note("warn", "Amp stopped answering during Standby-on-Tune — guard cancelled.");
            if (changed && power == false) Note("info", "Amplifier is off.");
            _prevTuneSignal = TuneSignal(cfg);
            return 1000;
        }

        bool cameOn;
        lock (_gate) { cameOn = _state.Power != "on"; _state.Power = "on"; }
        if (cameOn)
        {
            Note("ok", "Amplifier is on.");
            _lastBandSent = -1;
            _lastFreqSentKhz = -1;
            var sn = await Q(link, "SN", ct).ConfigureAwait(false);
            var rv = await Q(link, "RV", ct).ConfigureAwait(false);
            lock (_gate) { _state.SerialNumber = sn ?? _state.SerialNumber; _state.Firmware = rv ?? _state.Firmware; }
        }

        // Standby-on-Tune gets first call on every pass so TUNE is answered fast.
        await StepTuneGuardAsync(link, cfg, ct).ConfigureAwait(false);

        var ws = await Q(link, "WS", ct).ConfigureAwait(false);
        var os = await Q(link, "OS", ct).ConfigureAwait(false);
        var fl = await Q(link, "FL", ct).ConfigureAwait(false);
        var tp = await Q(link, "TP", ct).ConfigureAwait(false);
        var fr = await Q(link, "FR", ct).ConfigureAwait(false);
        var meter = ws is null ? null : KpaProtocol.ParseWs(ws);
        // While transmitting, read drive and reflected power on every pass.
        bool txNow;
        lock (_gate) txNow = TxLocked() || (meter is { Watts: >= RfPresentWatts });
        var pwiFast = txNow ? await Q(link, "PWI", ct).ConfigureAwait(false) : null;
        var pwrFast = txNow ? await Q(link, "PWR", ct).ConfigureAwait(false) : null;

        var slow = new List<(string Cmd, string? Val)>();
        for (var i = 0; i < 2; i++)
        {
            var cmd = SlowQueries[_slowIndex];
            _slowIndex = (_slowIndex + 1) % SlowQueries.Length;
            slow.Add((cmd, await Q(link, cmd, ct).ConfigureAwait(false)));
        }
        string? ma = null, wl = null;
        if (_cycle % 60 == 5)
        {
            bool needMac, needWl;
            lock (_gate) { needMac = _state.Mac is null; needWl = _state.WakeOnLan is null; }
            if (needMac) ma = await Q(link, "MA", ct).ConfigureAwait(false);
            if (needWl) wl = await Q(link, "WL", ct).ConfigureAwait(false);
        }

        string? prevFault, learnedMac = null;
        bool? prevOperate, prevWl;
        lock (_gate)
        {
            prevFault = _state.FaultCode;
            prevOperate = _state.Operate;
            prevWl = _state.WakeOnLan;
            if (meter is { } m) { _state.ForwardW = m.Watts; _state.Swr = m.Swr; }
            if (os is not null) _state.Operate = KpaProtocol.Bool01(os);
            if (fl is not null) _state.FaultCode = KpaProtocol.ParseFault(fl);
            if (tp is not null) _state.Tuning = KpaProtocol.Bool01(tp);
            if (fr is not null) _state.AmpFreqKhz = KpaProtocol.Int(fr) is int k and > 0 ? k : null;
            if (pwiFast is not null) _state.InputW = KpaProtocol.Int(pwiFast);
            if (pwrFast is not null) _state.ReflectedW = KpaProtocol.Int(pwrFast);
            foreach (var (cmd, val) in slow)
            {
                if (val is null) continue;
                switch (cmd)
                {
                    case "BN": _state.BandCode = KpaProtocol.Int(val); break;
                    case "TM": _state.TempC = KpaProtocol.Int(val); break;
                    case "VI":
                        if (KpaProtocol.ParseVi(val) is { } vi) { _state.Volts = vi.Volts; _state.Amps = vi.Amps; }
                        break;
                    case "AM": _state.AtuInline = KpaProtocol.ParseAtuMode(val); break;
                    case "AN": _state.Antenna = KpaProtocol.Int(val); break;
                    case "PWI": _state.InputW = KpaProtocol.Int(val); break;
                    case "PWR": _state.ReflectedW = KpaProtocol.Int(val); break;
                    case "PWD": _state.DissipationW = KpaProtocol.Int(val); break;
                    case "FS": _state.Fan = KpaProtocol.Int(val); break;
                    case "FC": _state.FanMinimum = KpaProtocol.Int(val); break;
                    case "SB": _state.BypassSwr = KpaProtocol.Tenths(val); break;
                }
            }
            if (ma is not null && PluginConfig.NormalizeMac(ma) is { Length: > 0 } mac && mac != _state.Mac)
            {
                _state.Mac = mac;
                learnedMac = mac;
            }
            if (wl is not null) _state.WakeOnLan = KpaProtocol.Bool01(wl);
        }

        if (learnedMac is not null)
        {
            Note("info", $"Amp MAC address learned: {learnedMac} (used for remote power-on).");
            MacLearned?.Invoke(learnedMac);
        }
        bool? nowWl;
        lock (_gate) nowWl = _state.WakeOnLan;
        if (prevWl is null && nowWl == false)
            Note("warn", "Amp menu NET WAKE on LAN is OFF — remote power-on over the LAN needs it ON (Settings → Enable Wake-on-LAN).");

        string? fault;
        bool? operate;
        bool tx;
        lock (_gate) { fault = _state.FaultCode; operate = _state.Operate; tx = TxLocked(); }
        if (fault != prevFault && fault is not null)
        {
            if (fault != "00") Note("tx", $"AMP FAULT {fault}: {KpaProtocol.FaultName(fault)}.");
            else if (prevFault is not null) Note("ok", "Fault cleared.");
        }
        if (operate != prevOperate && operate is not null && prevOperate is not null)
            Note("info", operate == true ? "Amp is in OPERATE." : "Amp is in STANDBY.");

        // Post-open settle: only ever toward STANDBY.
        if (_settlePending)
        {
            _settlePending = false;
            if (cfg.StandbyOnConnect && operate == true && !tx)
            {
                await link.SendAsync(KpaProtocol.SetOperate(false), ct).ConfigureAwait(false);
                Note("info", "Standby on connect.");
            }
        }

        await StepNoAntennaAsync(link, cfg, tx, operate, fault, ct).ConfigureAwait(false);
        if (!_scanning) await TrackRadioAsync(link, cfg, tx, ct).ConfigureAwait(false);
        return cfg.PollMs;
    }

    /// <summary>
    /// The frequency the amp should be set for: TCI's transmit VFO (VFO B when
    /// split is on) when TCI is connected and has reported one, otherwise the
    /// frequency Zeus gives plugins. 0 = nothing known yet.
    /// </summary>
    public (long Hz, string Source) TrackHz
    {
        get
        {
            var tci = _tci;
            var tciHz = tci?.TxHz ?? 0;
            if (tci is not null && tciHz > 0) return (tciHz, tci.Split ? "TCI VFO B (split)" : "TCI VFO A");
            return _radio is null ? (0, "") : (Interlocked.Read(ref _radioHz), "Zeus");
        }
    }

    private async Task TrackRadioAsync(KpaLink link, PluginConfig cfg, bool tx, CancellationToken ct)
    {
        var (hz, _) = TrackHz;
        if (hz <= 0) return;
        var code = BandPlan.FromHz(hz);
        if (code < 0) { _bandPending = -1; return; }
        var khz = (long)Math.Round(hz / 1000.0);

        bool tuning;
        int? ampBand;
        lock (_gate) { tuning = _state.Tuning == true; ampBand = _state.BandCode; }
        var hold = tx || tuning;

        if (cfg.BandFollow && code != _lastBandSent && ampBand != code)
        {
            if (hold)
            {
                if (_bandPending != code) Note("info", $"Band change to {KpaProtocol.BandName(code)} waits until TX drops.");
                _bandPending = code;
            }
            else
            {
                await link.SendAsync(KpaProtocol.SetBand(code), ct).ConfigureAwait(false);
                _lastBandSent = code;
                _bandPending = -1;
                Note("info", $"Band → {KpaProtocol.BandName(code)} (^BN).");
            }
        }
        else if (!cfg.BandFollow || ampBand == code)
        {
            _bandPending = -1;
        }

        if (cfg.SendFrequency && !hold &&
            (_lastFreqSentKhz < 0 || Math.Abs(khz - _lastFreqSentKhz) >= cfg.FrequencyStepKhz))
        {
            await link.SendAsync(KpaProtocol.SetFrequencyKhz(hz), ct).ConfigureAwait(false);
            _lastFreqSentKhz = khz;
            Note("freq", $"Frequency {khz / 1000.0:0.000} MHz sent to amp (^FR).");
        }
    }

    // ── "No antenna?" check ────────────────────────────────────────────────

    private bool _noAntRestore;       // the check put the amp in STANDBY and should restore OPERATE
    private IRadioController? _radioController;

    public bool NoAntHold => _noAntHold;
    public bool Scanning => _scanning;
    public string ScanStatus => _scanStatus;
    public bool CanScan => _radioController is not null && _radio is not null;
    public void SetRadioController(IRadioController? rc) => _radioController = rc;

    public NoAntSnapshot GetNoAnt() => _noAnt.Snapshot(DateTime.UtcNow);

    /// <summary>
    /// Drive power and where it came from. The KPA1500 reports input power as 0
    /// whenever its PA is bypassed (STANDBY or ATU tuning), so the radio's own
    /// measurement (TCI tx_sensors) is used then — as the Thetis bridge did.
    /// </summary>
    public (double? Watts, string Source) Drive
    {
        get
        {
            int? pin; bool? operate; bool tuning;
            lock (_gate) { pin = _state.InputW; operate = _state.Operate; tuning = _state.Tuning == true; }
            if (operate == true && !tuning && pin is > 0) return (pin, "amp");
            if (_tci?.RadioFwdW is { } r && r > 0) return (Math.Round(r, 1), "radio");
            return (pin, pin is null ? "" : "amp");
        }
    }

    private async Task StepNoAntennaAsync(KpaLink link, PluginConfig cfg, bool tx, bool? operate, string? fault, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (!_scanning)
        {
            var (hz, _) = TrackHz;
            var code = hz > 0 ? BandPlan.FromHz(hz) : -1;
            _noAnt.SetBand(code >= 0 ? KpaProtocol.BandName(code) : null, now);
        }
        _noAnt.SetTx(tx, now);
        var snap = _noAnt.Snapshot(now);
        var st = snap.State;
        if (st != _noAntLast)
        {
            switch (st)
            {
                case NoAntState.Low:
                    if (_noAntLast != NoAntState.Tx)
                        Note("warn", $"NO ANTENNA? Receive level {snap.Dbm:0} dBm on {snap.Band} is below the limit {snap.Limit} dBm. Check the antenna, or press DUMMY OK if a dummy load is connected.");
                    break;
                case NoAntState.Tx:
                    Note("tx", $"WARNING: transmitting while NO ANTENNA? is showing (receive level was {snap.Dbm:0} dBm on {snap.Band}). Check the antenna / dummy load!");
                    break;
                case NoAntState.Ok when _noAntLast is NoAntState.Low or NoAntState.Tx or NoAntState.Dummy:
                    Note("ok", $"Receive level back to normal on {snap.Band} ({snap.Dbm:0} dBm).");
                    break;
            }
            _noAntLast = st;
        }

        var warn = cfg.NoAntEnabled && st is NoAntState.Low or NoAntState.Tx;
        var holdWanted = warn && cfg.NoAntAction == "standby";
        bool tuneIdle;
        lock (_gate) tuneIdle = _tune.Phase == TunePhase.Idle;

        if (holdWanted)
        {
            _noAntHold = true;
            // Never switch the amp under RF; Standby-on-Tune has priority.
            if (operate == true && !tx && tuneIdle)
            {
                await link.SendAsync(KpaProtocol.SetOperate(false), ct).ConfigureAwait(false);
                lock (_gate) _state.Operate = false;
                _noAntRestore = true;
                Note("cmd", "NO ANTENNA? — amp put in STANDBY (^OS0;). It returns to OPERATE when a normal level comes back or you press DUMMY OK.");
            }
        }
        else if (_noAntHold)
        {
            _noAntHold = false;
            var faultActive = fault is not null && fault != "00";
            if (_noAntRestore && !tx && tuneIdle && operate == false && !faultActive && cfg.NoAntEnabled)
            {
                await link.SendAsync(KpaProtocol.SetOperate(true), ct).ConfigureAwait(false);
                lock (_gate) _state.Operate = true;
                Note("cmd", "No-antenna hold released — amp back in OPERATE (^OS1;).");
            }
            _noAntRestore = false;
        }
    }

    public (bool Ok, string Message) NoAntLearn(bool antennaConnected)
    {
        var r = _noAnt.LearnCurrent(antennaConnected, DateTime.UtcNow);
        if (r.Ok) Note("info", r.Message);
        return r;
    }

    public (bool Ok, string Message) NoAntDummy(bool on)
    {
        var r = _noAnt.SetDummy(on);
        if (r.Ok) { Note("info", r.Message); Wake(); }
        return r;
    }

    /// <summary>Middle of each band's phone segment (US), where the scan listens.</summary>
    public static readonly IReadOnlyList<(string Band, long Hz)> ScanPlan = new[]
    {
        ("160m", 1_900_000L), ("80m", 3_800_000L), ("60m", 5_357_000L), ("40m", 7_212_000L),
        ("30m", 10_125_000L), ("20m", 14_250_000L), ("17m", 18_140_000L), ("15m", 21_325_000L),
        ("12m", 24_960_000L), ("10m", 28_500_000L), ("6m", 50_200_000L),
    };

    public CommandResult StartScan()
    {
        if (_radioController is not { } rc || _radio is not { } radio)
            return CommandResult.Unavailable("Zeus did not give this feature permission to tune the radio, so the band scan is not available.");
        if (!Config.NoAntEnabled) return CommandResult.Conflict("Turn the No-antenna check on first.");
        if (Transmitting) return CommandResult.Conflict("Not while transmitting.");
        if (_scanning) return CommandResult.Done("A scan is already running.");
        var snap = _noAnt.Snapshot(DateTime.UtcNow);
        if (!snap.LevelSeen)
            return CommandResult.Conflict("No receive level from Zeus yet. Turn on TCI in Zeus and in this panel's settings first.");
        _scanCts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? CancellationToken.None);
        _scanning = true;
        _scanStatus = "Starting…";
        _ = Task.Run(() => ScanAsync(rc, radio.FrequencyHz, _scanCts.Token), CancellationToken.None);
        return CommandResult.Accepted("Band scan started.");
    }

    public CommandResult StopScan()
    {
        _scanCts?.Cancel();
        return CommandResult.Done("Stopping the scan…");
    }

    private async Task ScanAsync(IRadioController rc, long originalHz, CancellationToken ct)
    {
        var learned = new List<string>();
        var stopped = "";
        try
        {
            Note("info", "Band scan started: measuring the noise floor on each band (antenna connected).");
            var bands = _noAnt.Bands;
            foreach (var (band, hz) in ScanPlan)
            {
                ct.ThrowIfCancellationRequested();
                if (!bands.TryGetValue(band, out var b) || b.Limit is null || b.Source == "manual") continue;
                if (Transmitting) { stopped = "transmit started"; break; }
                _scanStatus = $"Measuring {band}…";
                await rc.SetFrequencyAsync(hz, ct).ConfigureAwait(false);
                _noAnt.SetBand(band, DateTime.UtcNow);

                double? level = null;
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(9);
                var earliest = DateTime.UtcNow + NoAntennaMonitor.Settle + TimeSpan.FromSeconds(3);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                    if (Transmitting) break;
                    if (DateTime.UtcNow >= earliest && _noAnt.CurrentMedian(DateTime.UtcNow) is { } m) { level = m; break; }
                }
                if (Transmitting) { stopped = "transmit started"; break; }
                if (level is null) { stopped = $"no receive level from Zeus on {band}"; break; }
                _noAnt.LearnFloor(band, level.Value, DateTime.UtcNow);
                learned.Add($"{band} {level.Value:0}");
            }
        }
        catch (OperationCanceledException) { stopped = "stopped by operator"; }
        catch (Exception ex) { stopped = ex.Message; }
        finally
        {
            try { await rc.SetFrequencyAsync(originalHz, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Note("warn", "Could not put the frequency back after the scan: " + ex.Message); }
            _noAnt.SetBand(null, DateTime.UtcNow);   // re-read from the radio on the next pass
            _scanning = false;
            _scanStatus = stopped.Length == 0
                ? $"Done: {learned.Count} band(s) learned."
                : $"Stopped ({stopped}); {learned.Count} band(s) learned.";
            Note(stopped.Length == 0 ? "ok" : "warn",
                $"Band scan {(stopped.Length == 0 ? "finished" : "stopped: " + stopped)}. Noise floors (dBm): {(learned.Count == 0 ? "none" : string.Join(", ", learned))}. Frequency restored.");
            Wake();
        }
    }

    // ── standby on tune ─────────────────────────────────────────────────────

    private async Task StepTuneGuardAsync(KpaLink link, PluginConfig cfg, CancellationToken ct)
    {
        var signal = TuneSignal(cfg);
        var rising = signal && !_prevTuneSignal;
        _prevTuneSignal = signal;

        TunePhase phase;
        string trigger;
        lock (_gate) { phase = _tune.Phase; trigger = _tune.Trigger; }

        // TCI dropped while a TCI-triggered guard was running: fail-safe, stay in STANDBY.
        if (phase != TunePhase.Idle && trigger == "tci" && !(_tci?.Connected ?? false))
        {
            lock (_gate) _tune.Cancel();
            Note("warn", "TCI lost during TUNE — amp left in STANDBY (fail-safe). Set OPERATE manually.");
            return;
        }
        if (phase != TunePhase.Idle && !cfg.StandbyOnTune)
        {
            lock (_gate) _tune.Cancel();
            return;
        }

        TuneAction action;
        if (rising && cfg.StandbyOnTune && phase == TunePhase.Idle)
        {
            // Read the mode fresh, as the bridge did, so the restore decision is right.
            var os = await Q(link, "OS", ct).ConfigureAwait(false);
            var prior = os is null ? (bool?)null : KpaProtocol.Bool01(os);
            var src = cfg.TciEnabled && (_tci?.Tune ?? false) ? "tci" : "mode";
            lock (_gate)
            {
                if (prior is not null) _state.Operate = prior;
                action = _tune.Arm(src, prior, DateTime.UtcNow);
            }
            Note("info", $"TUNE started in radio — amp to STANDBY (was {(prior is null ? "unknown" : prior == true ? "OPERATE" : "STANDBY")})."
                + (prior is null ? " Could not read the amp mode; will NOT auto-restore." : ""));
        }
        else
        {
            lock (_gate)
            {
                var operate = _state.Operate;
                var faultActive = _state.FaultCode is { } f && f != "00";
                var before = _tune.Phase;
                action = _tune.Step(TxLocked(), signal, operate, faultActive, cfg.RestoreOperateAfterTune,
                    TimeSpan.FromSeconds(cfg.TuneTimeoutS), DateTime.UtcNow);
                if (before != TunePhase.Idle && _tune.Phase == TunePhase.Idle && action == TuneAction.None)
                    _events.Add("info", "TUNE ended — amp left in STANDBY (it was not in OPERATE before, restore is off, or a fault is showing).");
            }
        }
        await ApplyTuneActionAsync(link, action, ct).ConfigureAwait(false);
    }

    private async Task ApplyTuneActionAsync(KpaLink link, TuneAction action, CancellationToken ct)
    {
        if (action == TuneAction.None) return;
        var operate = action == TuneAction.SendOperate;
        await link.SendAsync(KpaProtocol.SetOperate(operate), ct).ConfigureAwait(false);
        await Task.Delay(80, ct).ConfigureAwait(false);
        var os = await Q(link, "OS", ct).ConfigureAwait(false);
        var now = os is null ? (bool?)null : KpaProtocol.Bool01(os);
        if (!operate && now != false)
        {
            await link.SendAsync(KpaProtocol.SetOperate(false), ct).ConfigureAwait(false); // one retry
            await Task.Delay(80, ct).ConfigureAwait(false);
            os = await Q(link, "OS", ct).ConfigureAwait(false);
            now = os is null ? null : KpaProtocol.Bool01(os);
        }
        lock (_gate) { if (now is not null) _state.Operate = now; }
        Note("cmd", operate
            ? $"TUNE ended — OPERATE restored {(now == true ? "(confirmed)" : "(NOT confirmed!)")}"
            : $"Amp STANDBY for tune {(now == false ? "(confirmed)" : "(NOT confirmed!)")}");
    }

    public async Task<CommandResult> ArmTuneGuardAsync(CancellationToken ct)
    {
        var link = Live(out var fail);
        if (link is null) return fail!;
        if (!IsOn) return CommandResult.Conflict("The amplifier is off.");
        if (!Config.StandbyOnTune) return CommandResult.Conflict("Standby-on-Tune is switched off in settings.");
        lock (_gate) { if (_tune.Phase != TunePhase.Idle) return CommandResult.Done("Already armed."); }
        try
        {
            var os = await Q(link, "OS", ct).ConfigureAwait(false);
            var prior = os is null ? (bool?)null : KpaProtocol.Bool01(os);
            TuneAction action;
            lock (_gate) action = _tune.Arm("manual", prior, DateTime.UtcNow);
            await ApplyTuneActionAsync(link, action, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            lock (_gate) _tune.Cancel();
            return CommandResult.Unavailable("Send failed: " + ex.Message);
        }
        Note("info", $"Standby-on-Tune armed — press TUNE on the radio within {Config.TuneTimeoutS} s.");
        Wake();
        return CommandResult.Done("Armed — press TUNE on the radio.");
    }

    public CommandResult CancelTuneGuard()
    {
        lock (_gate) _tune.Cancel();
        Note("info", "Standby-on-Tune cancelled (amp left in STANDBY).");
        return CommandResult.Done("Cancelled.");
    }

    // ── operator commands ───────────────────────────────────────────────────

    public CommandResult RequestConnect()
    {
        lock (_gate)
        {
            if (!HasTarget(_cfg)) return CommandResult.Bad("Set the amplifier's IP address (or USB port) first.");
            _wantOpen = true;
            _nextOpenAttempt = DateTime.MinValue;
        }
        Wake();
        return CommandResult.Accepted("Connecting…");
    }

    public async Task<CommandResult> DisconnectAsync()
    {
        lock (_gate) _wantOpen = false;
        await DropLinkAsync(null).ConfigureAwait(false);
        Note("info", "Disconnected by operator.");
        lock (_gate) _state.ConnectionError = null;
        return CommandResult.Done("Disconnected.");
    }

    private KpaLink? Live(out CommandResult? fail)
    {
        var link = _link;
        if (link is null || !link.IsAlive) { fail = CommandResult.Unavailable("Not connected to the amplifier."); return null; }
        fail = null;
        return link;
    }

    private bool IsOn { get { lock (_gate) return _state.Power == "on"; } }

    private async Task<CommandResult> SendAsync(KpaLink link, string cmd, string ok, CancellationToken ct)
    {
        try
        {
            await link.SendAsync(cmd, ct).ConfigureAwait(false);
            Note("cmd", $"{ok} ({cmd})");
            Wake();
            return CommandResult.Done(ok);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return CommandResult.Unavailable("Send failed: " + ex.Message);
        }
    }

    public Task<CommandResult> SetPowerAsync(bool on, CancellationToken ct)
    {
        var link = Live(out var fail);
        if (link is null) return Task.FromResult(fail!);
        if (!on)
        {
            if (RelayBusy(out var why)) return Task.FromResult(CommandResult.Conflict(why));
            return SendAsync(link, KpaProtocol.SetPower(false), "Power OFF", ct);
        }
        if (Interlocked.CompareExchange(ref _powerOnBusy, 1, 0) != 0)
            return Task.FromResult(CommandResult.Accepted("Power-on already in progress."));
        lock (_gate) _state.PowerOnInProgress = true;
        var token = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(() => PowerOnSequenceAsync(link, token), CancellationToken.None);
        return Task.FromResult(CommandResult.Accepted(Config.Transport == "serial"
            ? "Power-on started (^ON1;)."
            : "Power-on started: Wake-on-LAN + ^ON1; (takes up to ~15 s)."));
    }

    /// <summary>The bridge's power_on_sequence: WoL, then ";" ";" "^ON1;" once a second for 15 s.</summary>
    private async Task PowerOnSequenceAsync(KpaLink link, CancellationToken ct)
    {
        try
        {
            var cfg = Config;
            string? mac;
            lock (_gate) mac = _state.Mac;
            var useWol = cfg.Transport != "serial";
            if (useWol && mac is not null)
            {
                var n = await NetTools.SendWakeOnLanAsync(mac, cfg.Host, cfg.WolPort, ct).ConfigureAwait(false);
                Note("info", $"Wake-on-LAN magic packet sent for {mac} ({n} sends).");
            }
            else if (useWol)
            {
                Note("warn", "No amp MAC known — skipping Wake-on-LAN. Enter it in Settings (amp menu NET MAC), or turn the amp on once so it is learned.");
            }

            for (var attempt = 0; attempt < 15; attempt++)
            {
                await link.SendAsync(";", ct).ConfigureAwait(false);
                await link.SendAsync(";", ct).ConfigureAwait(false);
                await link.SendAsync(KpaProtocol.SetPower(true), ct).ConfigureAwait(false);
                await Task.Delay(1000, ct).ConfigureAwait(false);
                var r = await link.QueryAsync("ON", QueryTimeout, ct).ConfigureAwait(false);
                if (KpaProtocol.Bool01(r ?? "") == true)
                {
                    Note("ok", "Power-on CONFIRMED (^ON1;).");
                    Wake();
                    return;
                }
                if (useWol && mac is not null && attempt is 4 or 9)
                    await NetTools.SendWakeOnLanAsync(mac, cfg.Host, cfg.WolPort, ct).ConfigureAwait(false);
            }
            Note("warn", "Power-on NOT confirmed after 15 s. Check amp menu NET WAKE on LAN = ON, the MAC address, and that Zeus and the amp are on the same LAN.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Note("warn", "Power-on failed: " + ex.Message); }
        finally
        {
            lock (_gate) _state.PowerOnInProgress = false;
            Interlocked.Exchange(ref _powerOnBusy, 0);
        }
    }

    public async Task<CommandResult> EnableWakeOnLanAsync(CancellationToken ct)
    {
        var link = Live(out var fail);
        if (link is null) return fail!;
        if (!IsOn) return CommandResult.Conflict("Turn the amplifier on first.");
        var r = await SendAsync(link, KpaProtocol.EnableWakeOnLan, "Wake-on-LAN enabled in the amp", ct).ConfigureAwait(false);
        if (r.Ok)
        {
            await Task.Delay(80, ct).ConfigureAwait(false);
            var wl = await link.QueryAsync("WL", QueryTimeout, ct).ConfigureAwait(false);
            lock (_gate) _state.WakeOnLan = KpaProtocol.Bool01(wl ?? "") ?? _state.WakeOnLan;
        }
        return r;
    }

    public async Task<CommandResult> SetOperateAsync(bool operate, CancellationToken ct)
    {
        var link = Live(out var fail);
        if (link is null) return fail!;
        if (!IsOn) return CommandResult.Conflict("The amplifier is off.");
        if (!operate)
            return await SendAsync(link, KpaProtocol.SetOperate(false), "STANDBY", ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (_tune.Phase != TunePhase.Idle)
                return CommandResult.Conflict("Refused: TUNE in progress — the amp returns to its prior mode when TUNE ends (or press Cancel).");
        }
        if (_noAntHold)
            return CommandResult.Conflict("Refused: NO ANTENNA? is showing and the check keeps the amp in STANDBY. Check the antenna, or press DUMMY OK.");
        if (RelayBusy(out var why)) return CommandResult.Conflict(why);
        return await SendAsync(link, KpaProtocol.SetOperate(true), "OPERATE", ct).ConfigureAwait(false);
    }

    public async Task<CommandResult> ClearFaultAsync(CancellationToken ct)
    {
        var link = Live(out var fail);
        if (link is null) return fail!;
        return await SendAsync(link, KpaProtocol.ClearFault, "Fault clear", ct).ConfigureAwait(false);
    }

    public async Task<CommandResult> SetBandAsync(string band, CancellationToken ct)
    {
        var code = KpaProtocol.BandCode(band);
        if (code < 0) return CommandResult.Bad($"'{band}' is not a KPA1500 band.");
        var link = Live(out var fail);
        if (link is null) return fail!;
        if (!IsOn) return CommandResult.Conflict("The amplifier is off.");
        if (RelayBusy(out var why)) return CommandResult.Conflict(why);
        lock (_gate) _state.BandCode = code;
        return await SendAsync(link, KpaProtocol.SetBand(code), $"Band {KpaProtocol.BandName(code)}", ct).ConfigureAwait(false);
    }

    /// <param name="ant">1, 2, or 0 for "next enabled antenna".</param>
    public async Task<CommandResult> SetAntennaAsync(int ant, CancellationToken ct)
    {
        if (ant is not (0 or 1 or 2)) return CommandResult.Bad("Antenna must be 1, 2, or 0 (next).");
        var link = Live(out var fail);
        if (link is null) return fail!;
        if (!IsOn) return CommandResult.Conflict("The amplifier is off.");
        if (RelayBusy(out var why)) return CommandResult.Conflict(why);
        return ant == 0
            ? await SendAsync(link, KpaProtocol.AntennaNext, "Next antenna", ct).ConfigureAwait(false)
            : await SendAsync(link, KpaProtocol.SetAntenna(ant), $"Antenna {ant}", ct).ConfigureAwait(false);
    }

    public async Task<CommandResult> SetAtuInlineAsync(bool inline, CancellationToken ct)
    {
        var link = Live(out var fail);
        if (link is null) return fail!;
        if (!IsOn) return CommandResult.Conflict("The amplifier is off.");
        if (RelayBusy(out var why)) return CommandResult.Conflict(why);
        return await SendAsync(link, KpaProtocol.SetAtuMode(inline), inline ? "ATU in-line" : "ATU bypass", ct).ConfigureAwait(false);
    }

    public async Task<CommandResult> StartTuneAsync(CancellationToken ct)
    {
        var link = Live(out var fail);
        if (link is null) return fail!;
        if (!IsOn) return CommandResult.Conflict("The amplifier is off.");
        if (RelayBusy(out var why)) return CommandResult.Conflict(why);
        var r = await SendAsync(link, KpaProtocol.StartTune,
            "ATU tune started on the amp — press TUNE in the radio at LOW power to supply the carrier", ct).ConfigureAwait(false);
        if (r.Ok) lock (_gate) _state.Tuning = true;
        return r;
    }

    public async Task<CommandResult> CancelTuneAsync(CancellationToken ct)
    {
        var link = Live(out var fail);
        if (link is null) return fail!;
        return await SendAsync(link, KpaProtocol.CancelTune, "ATU tune cancelled", ct).ConfigureAwait(false);
    }

    public async Task<CommandResult> SetFanMinimumAsync(int speed, CancellationToken ct)
    {
        if (speed is < 0 or > 5) return CommandResult.Bad("Fan speed must be from 0 to 5.");
        var link = Live(out var fail);
        if (link is null) return fail!;
        if (!IsOn) return CommandResult.Conflict("The amplifier is off.");
        lock (_gate) _state.FanMinimum = speed;
        return await SendAsync(link, KpaProtocol.SetFanMinimum(speed), $"Fan minimum speed {speed}", ct).ConfigureAwait(false);
    }

    private void Note(string kind, string message)
    {
        _events.Add(kind, message);
        if (kind is "warn" or "tx") _log.LogWarning("{Message}", message);
        else if (kind != "freq") _log.LogInformation("{Message}", message);
    }
}
