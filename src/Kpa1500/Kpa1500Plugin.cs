// SPDX-License-Identifier: GPL-2.0-or-later
//
// Elecraft KPA1500 — Zeus community feature entry point.
// Loads settings, starts the AmpController, follows Zeus radio state, and
// exposes the panel's HTTP API under /api/plugins/<plugin id>/.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Community.Kpa1500;

public sealed record PowerRequest(bool On);
public sealed record OperateRequest(bool Operate);
public sealed record BandRequest(string Band);
public sealed record AntennaRequest(int Antenna);
public sealed record AtuRequest(bool Inline);
public sealed record TuneGuardRequest(bool Arm);
public sealed record FanRequest(int Speed);
public sealed record NoAntBandRequest(string Band, int? Limit, bool Reset);
public sealed record NoAntLearnRequest(bool AntennaConnected);
public sealed record OnRequest(bool On);

public sealed class Kpa1500Plugin : IZeusPlugin, IBackendPlugin
{
    /// <summary>Keep identical to plugin.json "version".</summary>
    public const string ManifestVersion = "1.1.4";
    private const string SettingsKey = "config";

    private IPluginContext? _ctx;
    private AmpController? _amp;
    private readonly EventLog _events = new(250);
    private Timer? _radioProbe;
    private Timer? _noAntSave;
    private const string NoAntKey = "noant";

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        PluginConfig? stored = null;
        try { stored = await context.Settings.GetAsync<PluginConfig>(SettingsKey, ct).ConfigureAwait(false); }
        catch (Exception ex) { context.Logger.LogWarning(ex, "KPA1500 settings unreadable; using defaults"); }
        var cfg = PluginConfig.Sanitize(stored);

        _amp = new AmpController(context.Logger, _events);
        _amp.MacLearned += mac => _ = SaveLearnedMacAsync(mac);
        _amp.SetRadioController(context.RadioController);

        // Per-band "No antenna?" limits live under their own key so a panel
        // settings save can never overwrite freshly learned values.
        try
        {
            var storedLimits = await context.Settings.GetAsync<Dictionary<string, BandNoise>>(NoAntKey, ct).ConfigureAwait(false);
            _amp.NoAntenna.Load(storedLimits);
        }
        catch (Exception ex) { context.Logger.LogWarning(ex, "KPA1500 no-antenna limits unreadable; using defaults"); }
        _noAntSave = new Timer(_ => _ = SaveNoAntAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _amp.NoAntenna.LimitsChanged += () => _noAntSave?.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        if (context.Radio is { } radio) _amp.AttachRadio(radio);
        else
        {
            _events.Add("warn", "Zeus radio state is not available yet — band tracking and Standby-on-Tune start when it is.");
            // Some hosts register radio state after plugins load; keep checking.
            _radioProbe = new Timer(_ =>
            {
                if (_ctx?.Radio is { } late && _amp is { RadioAttached: false })
                {
                    _amp.AttachRadio(late);
                    _radioProbe?.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }
        _amp.Start(cfg);
        _events.Add("info", $"Elecraft KPA1500 v{ManifestVersion} ready.");
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        if (_radioProbe is not null) await _radioProbe.DisposeAsync().ConfigureAwait(false);
        if (_noAntSave is not null)
        {
            await _noAntSave.DisposeAsync().ConfigureAwait(false);
            await SaveNoAntAsync().ConfigureAwait(false);
        }
        if (_amp is not null)
        {
            _amp.DetachRadio();
            await _amp.DisposeAsync().ConfigureAwait(false);
        }
        _amp = null;
        _ctx = null;
    }

    private int _discovering;

    private async Task SaveLearnedMacAsync(string mac)
    {
        if (_amp is not { } amp || _ctx is not { } ctx) return;
        var cfg = amp.Config;
        if (cfg.AmpMac == mac) return;
        cfg.AmpMac = mac;
        try
        {
            await ctx.Settings.SetAsync(SettingsKey, cfg).ConfigureAwait(false);
            amp.ApplyConfig(cfg);
        }
        catch (Exception ex) { ctx.Logger.LogWarning(ex, "Could not save the learned KPA1500 MAC"); }
    }

    private async Task SaveNoAntAsync()
    {
        if (_amp is not { } amp || _ctx is not { } ctx) return;
        try
        {
            var bands = amp.NoAntenna.Bands.ToDictionary(kv => kv.Key, kv => kv.Value);
            await ctx.Settings.SetAsync(NoAntKey, bands).ConfigureAwait(false);
        }
        catch (Exception ex) { ctx.Logger.LogWarning(ex, "Could not save KPA1500 no-antenna limits"); }
    }

    private static object NoAntJson(AmpController amp)
    {
        var n = amp.GetNoAnt();
        return new
        {
            state = n.State.ToString().ToLowerInvariant(),
            band = n.Band,
            dbm = n.Dbm is { } d ? Math.Round(d, 1) : (double?)null,
            limit = n.Limit,
            source = n.Source,
            floor = n.Floor,
            levelSeen = n.LevelSeen,
            hold = amp.NoAntHold,
            scanning = amp.Scanning,
            scanStatus = amp.ScanStatus,
            canScan = amp.CanScan,
        };
    }

    private static IResult ToResult(CommandResult r) =>
        Results.Json(new { ok = r.Ok, message = r.Message }, statusCode: r.Status);

    private static IResult NotReady() =>
        Results.Json(new { ok = false, message = "Feature is starting." }, statusCode: 503);

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("status", () =>
        {
            if (_amp is not { } amp) return NotReady();
            var s = amp.State;
            var cfg = amp.Config;
            var tune = amp.TuneGuardState;
            var tci = amp.Tci;
            var (hz, hzSource) = amp.TrackHz;
            var (driveW, driveSource) = amp.Drive;
            if (hz <= 0) hz = amp.RadioHz;
            return Results.Ok(new
            {
                version = ManifestVersion,
                connection = new { state = s.Connection, error = s.ConnectionError, target = s.Target, transport = cfg.Transport },
                tci = new
                {
                    enabled = tci.Enabled, connected = tci.Connected, tune = tci.Tune, target = tci.Target, error = tci.Error,
                    levels = tci.Levels, lastLevel = tci.LastLevel is { } ll ? Math.Round(ll, 1) : (double?)null, levelChannel = tci.LevelChannel,
                },
                amp = new
                {
                    power = s.Power,
                    operate = s.Operate,
                    fault = s.FaultCode is null ? null : new
                    {
                        code = s.FaultCode,
                        active = s.FaultCode != "00",
                        name = KpaProtocol.FaultName(s.FaultCode),
                    },
                    band = s.BandCode is { } b ? KpaProtocol.BandName(b) : null,
                    antenna = s.Antenna,
                    atuInline = s.AtuInline,
                    tuning = s.Tuning,
                    forwardW = s.ForwardW,
                    reflectedW = s.ReflectedW,
                    inputW = s.InputW,
                    dissipationW = s.DissipationW,
                    swr = s.Swr,
                    bypassSwr = s.BypassSwr,
                    tempC = s.TempC,
                    volts = s.Volts,
                    amps = s.Amps,
                    fan = s.Fan,
                    fanMinimum = s.FanMinimum,
                    wakeOnLan = s.WakeOnLan,
                    mac = s.Mac,
                    powerOnInProgress = s.PowerOnInProgress,
                    freqKhz = s.AmpFreqKhz,
                    driveW,
                    driveSource,
                    firmware = s.Firmware,
                    serial = s.SerialNumber,
                },
                radio = new
                {
                    available = amp.RadioAttached || hz > 0,
                    frequencyHz = hz,
                    source = hzSource,
                    band = BandPlan.FromHz(hz) is var code and >= 0 ? KpaProtocol.BandName(code) : null,
                    mode = amp.RadioMode,
                    mox = amp.RadioMox,
                },
                tx = amp.Transmitting,
                pendingBand = amp.PendingBand >= 0 ? KpaProtocol.BandName(amp.PendingBand) : null,
                noAnt = NoAntJson(amp),
                tuneGuard = new
                {
                    phase = tune.Phase.ToString().ToLowerInvariant(),
                    trigger = tune.Trigger,
                    restore = tune.Restore,
                },
            });
        });

        endpoints.MapGet("config", () => _amp is { } amp ? Results.Ok(amp.Config) : NotReady());

        endpoints.MapPost("config", async (PluginConfig body, CancellationToken ct) =>
        {
            if (_amp is not { } amp || _ctx is not { } ctx) return NotReady();
            var errors = body.Validate();
            if (errors.Count > 0)
                return Results.Json(new { ok = false, message = string.Join(" ", errors) }, statusCode: 400);
            // Keep a MAC the feature learned if the panel's copy predates it.
            if (string.IsNullOrWhiteSpace(body.AmpMac)) body.AmpMac = amp.Config.AmpMac;
            var cfg = PluginConfig.Sanitize(body);
            await ctx.Settings.SetAsync(SettingsKey, cfg, ct).ConfigureAwait(false);
            amp.ApplyConfig(cfg);
            return Results.Ok(cfg);
        });

        endpoints.MapGet("ports", () => Results.Ok(Transports.SerialPortNames()));
        endpoints.MapGet("log", () => Results.Ok(_events.Snapshot()));

        endpoints.MapPost("connect", () => _amp is { } amp ? ToResult(amp.RequestConnect()) : NotReady());
        endpoints.MapPost("disconnect", async () =>
            _amp is { } amp ? ToResult(await amp.DisconnectAsync().ConfigureAwait(false)) : NotReady());

        endpoints.MapPost("power", async (PowerRequest body, CancellationToken ct) =>
            _amp is { } amp ? ToResult(await amp.SetPowerAsync(body.On, ct).ConfigureAwait(false)) : NotReady());
        endpoints.MapPost("operate", async (OperateRequest body, CancellationToken ct) =>
            _amp is { } amp ? ToResult(await amp.SetOperateAsync(body.Operate, ct).ConfigureAwait(false)) : NotReady());
        endpoints.MapPost("fault/clear", async (CancellationToken ct) =>
            _amp is { } amp ? ToResult(await amp.ClearFaultAsync(ct).ConfigureAwait(false)) : NotReady());
        endpoints.MapPost("band", async (BandRequest body, CancellationToken ct) =>
            _amp is { } amp ? ToResult(await amp.SetBandAsync(body.Band ?? "", ct).ConfigureAwait(false)) : NotReady());
        endpoints.MapPost("antenna", async (AntennaRequest body, CancellationToken ct) =>
            _amp is { } amp ? ToResult(await amp.SetAntennaAsync(body.Antenna, ct).ConfigureAwait(false)) : NotReady());
        endpoints.MapPost("atu", async (AtuRequest body, CancellationToken ct) =>
            _amp is { } amp ? ToResult(await amp.SetAtuInlineAsync(body.Inline, ct).ConfigureAwait(false)) : NotReady());
        endpoints.MapPost("atu/tune", async (CancellationToken ct) =>
            _amp is { } amp ? ToResult(await amp.StartTuneAsync(ct).ConfigureAwait(false)) : NotReady());
        endpoints.MapPost("atu/cancel", async (CancellationToken ct) =>
            _amp is { } amp ? ToResult(await amp.CancelTuneAsync(ct).ConfigureAwait(false)) : NotReady());
        endpoints.MapPost("fan", async (FanRequest body, CancellationToken ct) =>
            _amp is { } amp ? ToResult(await amp.SetFanMinimumAsync(body.Speed, ct).ConfigureAwait(false)) : NotReady());
        endpoints.MapPost("wol/enable", async (CancellationToken ct) =>
            _amp is { } amp ? ToResult(await amp.EnableWakeOnLanAsync(ct).ConfigureAwait(false)) : NotReady());

        endpoints.MapPost("discover", async (CancellationToken ct) =>
        {
            if (_amp is not { } amp) return NotReady();
            if (amp.Transmitting) return ToResult(CommandResult.Conflict("Refused: transmitting."));
            if (Interlocked.CompareExchange(ref _discovering, 1, 0) != 0)
                return ToResult(CommandResult.Conflict("A search is already running."));
            try
            {
                var cfg = amp.Config;
                var (found, searched) = await NetTools.DiscoverAsync(cfg.NetPort, cfg.Host, ct).ConfigureAwait(false);
                _events.Add(found.Count > 0 ? "ok" : "warn", found.Count > 0
                    ? $"Found {found.Count} KPA1500: " + string.Join("; ", found.Select(f => $"{f.Ip} (serial {f.Serial}, fw {f.Firmware})"))
                    : $"No KPA1500 answered (searched {searched}).");
                return Results.Ok(new { ok = true, searched, found });
            }
            finally { Interlocked.Exchange(ref _discovering, 0); }
        });

        // ── "No antenna?" check ──
        endpoints.MapGet("noant", () =>
        {
            if (_amp is not { } amp) return NotReady();
            var bands = amp.NoAntenna.Bands;
            return Results.Ok(new
            {
                current = NoAntJson(amp),
                bands = KpaProtocol.BandNames.Select(b => new
                {
                    band = b,
                    limit = bands[b].Limit,
                    source = bands[b].Source,
                    floor = bands[b].Floor,
                    defaultLimit = NoAntennaMonitor.Defaults[b],
                }),
            });
        });
        endpoints.MapPost("noant/band", (NoAntBandRequest body) =>
        {
            if (_amp is not { } amp) return NotReady();
            if (KpaProtocol.BandCode(body.Band) < 0) return ToResult(CommandResult.Bad("Unknown band."));
            if (body.Reset) { amp.NoAntenna.ResetBand(body.Band); return ToResult(CommandResult.Done($"{body.Band}: default limit restored.")); }
            return amp.NoAntenna.SetManual(body.Band, body.Limit)
                ? ToResult(CommandResult.Done(body.Limit is null ? $"{body.Band}: check off." : $"{body.Band}: limit {body.Limit} dBm."))
                : ToResult(CommandResult.Bad($"The limit must be between {NoAntennaMonitor.MinLimit} and {NoAntennaMonitor.MaxLimit} dBm, or empty for no check."));
        });
        endpoints.MapPost("noant/learn", (NoAntLearnRequest body) =>
        {
            if (_amp is not { } amp) return NotReady();
            var (ok, msg) = amp.NoAntLearn(body.AntennaConnected);
            return ToResult(ok ? CommandResult.Done(msg) : CommandResult.Conflict(msg));
        });
        endpoints.MapPost("noant/defaults", () =>
        {
            if (_amp is not { } amp) return NotReady();
            amp.NoAntenna.RestoreDefaults();
            return ToResult(CommandResult.Done("All bands back to the default limits."));
        });
        endpoints.MapPost("noant/dummy", (OnRequest body) =>
        {
            if (_amp is not { } amp) return NotReady();
            var (ok, msg) = amp.NoAntDummy(body.On);
            return ToResult(ok ? CommandResult.Done(msg) : CommandResult.Conflict(msg));
        });
        endpoints.MapPost("noant/scan", (OnRequest body) =>
        {
            if (_amp is not { } amp) return NotReady();
            return ToResult(body.On ? amp.StartScan() : amp.StopScan());
        });

        endpoints.MapPost("tune-guard", async (TuneGuardRequest body, CancellationToken ct) =>
        {
            if (_amp is not { } amp) return NotReady();
            return ToResult(body.Arm ? await amp.ArmTuneGuardAsync(ct).ConfigureAwait(false) : amp.CancelTuneGuard());
        });
    }
}
