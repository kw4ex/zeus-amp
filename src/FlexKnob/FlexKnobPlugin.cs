// SPDX-License-Identifier: GPL-2.0-or-later
//
// FlexRadio FlexControl knob — Zeus community feature entry point.
// Loads the settings, opens the knob, the Zeus TCI connection and the link to
// the KPA1500 feature, and exposes the panel's API under
// /api/plugins/<plugin id>/.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Community.FlexKnob;

public sealed record TryRequest(string Event);
public sealed record HelloRequest(string Origin);

public sealed class FlexKnobPlugin : IZeusPlugin, IBackendPlugin
{
    /// <summary>Keep identical to plugin.json "version".</summary>
    public const string ManifestVersion = "1.0.1";
    private const string SettingsKey = "config";

    private IPluginContext? _ctx;
    private readonly EventLog _events = new(250);
    private readonly object _gate = new();
    private KnobConfig _cfg = new();
    private ZeusWeb? _web;
    private AmpLink? _amp;
    private ZeusTci? _tci;
    private KnobController? _knob;
    private KnobSerial? _serial;
    private AmpStatus? _ampCache;
    private long _ampCacheTicks;

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        KnobConfig? stored = null;
        try { stored = await context.Settings.GetAsync<KnobConfig>(SettingsKey, ct).ConfigureAwait(false); }
        catch (Exception ex) { context.Logger.LogWarning(ex, "FlexKnob settings unreadable; using defaults"); }
        _cfg = KnobConfig.Sanitize(stored);

        _web = new ZeusWeb(() => Config);
        _amp = new AmpLink(() => _web.BaseUrl, () => Config.AmpPluginId);
        _tci = NewTci(_cfg);
        _knob = new KnobController(_cfg, _events, () => _tci, _amp,
            () => _ctx?.Radio, () => _ctx?.RadioController, _web);
        _serial = new KnobSerial(_cfg.Port, code => _knob?.Post(code), (k, m) => _events.Add(k, m));
        _knob.Leds = s => _serial?.Send(s);
        if (context.RadioController is null)
            _events.Add("warn", "Zeus radio control is not available — VFO A tuning will go through TCI instead.");
        _events.Add("info", $"FlexControl knob v{ManifestVersion} ready.");
    }

    private KnobConfig Config { get { lock (_gate) return _cfg; } }

    private ZeusTci NewTci(KnobConfig c) => new(c.TciHost, c.TciPort, c.TciRx, (k, m) => _events.Add(k, m));

    public async Task ShutdownAsync(CancellationToken ct)
    {
        if (_serial is not null) await _serial.DisposeAsync().ConfigureAwait(false);
        if (_knob is not null) await _knob.DisposeAsync().ConfigureAwait(false);
        if (_tci is not null) await _tci.DisposeAsync().ConfigureAwait(false);
        if (_web is not null) await _web.DisposeAsync().ConfigureAwait(false);
        _amp?.Dispose();
        _serial = null;
        _knob = null;
        _tci = null;
        _ctx = null;
    }

    private async Task ApplyAsync(KnobConfig cfg)
    {
        KnobConfig old;
        lock (_gate) { old = _cfg; _cfg = cfg; }
        _knob?.ApplyConfig(cfg);
        if (cfg.Port != old.Port) _serial?.SetPort(cfg.Port);
        if (cfg.TciHost != old.TciHost || cfg.TciPort != old.TciPort || cfg.TciRx != old.TciRx)
        {
            var was = _tci;
            _tci = NewTci(cfg);
            if (was is not null) await was.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<AmpStatus?> AmpForPanelAsync(CancellationToken ct)
    {
        if (_amp is null) return null;
        if (Environment.TickCount64 - Interlocked.Read(ref _ampCacheTicks) < 2000) return _ampCache;
        var s = await _amp.StatusAsync(ct).ConfigureAwait(false);
        _ampCache = s;
        Interlocked.Exchange(ref _ampCacheTicks, Environment.TickCount64);
        return s;
    }

    private static IResult NotReady() =>
        Results.Json(new { ok = false, message = "Feature is starting." }, statusCode: 503);

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        _web?.UseServices(endpoints.ServiceProvider);

        endpoints.MapGet("status", async (CancellationToken ct) =>
        {
            if (_knob is not { } knob || _serial is not { } ser || _web is not { } web) return NotReady();
            var cfg = Config;
            var tci = _tci;
            var amp = await AmpForPanelAsync(ct).ConfigureAwait(false);
            var (code, text, age) = knob.Last;
            var step = knob.Step;
            var radio = _ctx?.Radio;
            return Results.Ok(new
            {
                version = ManifestVersion,
                knob = new
                {
                    port = cfg.Port, connected = ser.Connected, device = ser.Device, error = ser.Error,
                    mode = knob.Mode, modeName = knob.ModeName, last = code, lastText = text, lastAge = age,
                    lines = new { knob = knob.Line("knob"), b1 = knob.Line("b1"), b2 = knob.Line("b2"), b3 = knob.Line("b3") },
                    tuneActive = knob.KnobTuneActive,
                },
                tci = new { connected = tci?.Connected ?? false, target = tci?.Target ?? "", error = tci?.LastError ?? "" },
                zeus = new
                {
                    url = web.BaseUrl, urlSource = web.BaseSource,
                    stepHz = step.Hz, stepText = KnobController.StepText(step.Hz), stepSource = step.Source, stepError = web.StepError,
                    radio = radio is not null, control = _ctx?.RadioController is not null,
                    frequencyHz = radio?.FrequencyHz ?? 0, mode = radio?.Mode, mox = radio?.Mox ?? false,
                },
                amp = new
                {
                    reachable = amp is not null, error = amp is null ? _amp?.LastError : null,
                    connected = amp?.Connected ?? false, power = amp?.Power, operate = amp?.Operate,
                    antenna = amp?.Antenna, noAnt = amp?.NoAntState,
                },
            });
        });

        endpoints.MapGet("catalog", () => Results.Ok(new
        {
            events = KnobCatalog.Events.Select(e => new { id = e.Id, name = e.Name }),
            actions = KnobCatalog.Actions.Select(a => new { id = a.Id, name = a.Name, group = a.Group }),
            modes = KnobCatalog.Modes.Select(m => new { id = m.Id, name = m.Name }),
            steps = KnobCatalog.Steps,
            defaults = KnobCatalog.DefaultTable,
        }));

        endpoints.MapGet("config", () => Results.Ok(Config));

        endpoints.MapPost("config", async (KnobConfig body, CancellationToken ct) =>
        {
            if (_ctx is not { } ctx) return NotReady();
            var errors = body.Validate();
            if (errors.Count > 0)
                return Results.Json(new { ok = false, message = string.Join(" ", errors) }, statusCode: 400);
            var cfg = KnobConfig.Sanitize(body);
            await ctx.Settings.SetAsync(SettingsKey, cfg, ct).ConfigureAwait(false);
            await ApplyAsync(cfg).ConfigureAwait(false);
            _events.Add("info", "FlexControl knob settings saved.");
            return Results.Ok(cfg);
        });

        endpoints.MapPost("defaults", async (CancellationToken ct) =>
        {
            if (_ctx is not { } ctx) return NotReady();
            var cur = Config;
            // Functions back to the defaults; connection settings kept.
            var cfg = KnobConfig.Sanitize(new KnobConfig
            {
                Port = cur.Port, TciHost = cur.TciHost, TciPort = cur.TciPort, TciRx = cur.TciRx,
                ZeusUrl = cur.ZeusUrl, AmpPluginId = cur.AmpPluginId,
            });
            await ctx.Settings.SetAsync(SettingsKey, cfg, ct).ConfigureAwait(false);
            await ApplyAsync(cfg).ConfigureAwait(false);
            _events.Add("info", "FlexControl knob functions back to the defaults.");
            return Results.Ok(cfg);
        });

        endpoints.MapPost("try", async (TryRequest body, CancellationToken ct) =>
        {
            if (_knob is not { } knob) return NotReady();
            if (body.Event is null || !KnobCatalog.EventIds.Contains(body.Event))
                return Results.Json(new { ok = false, message = "Unknown knob event." }, statusCode: 400);
            var (ok, msg) = await knob.TryAsync(body.Event, ct).ConfigureAwait(false);
            msg = $"Try {KnobCatalog.EventName(body.Event)}: {msg}";
            _events.Add(ok ? "info" : "warn", msg);
            return Results.Json(new { ok, message = msg }, statusCode: ok ? 200 : 409);
        });

        endpoints.MapPost("hello", (HelloRequest body) =>
        {
            _web?.SetPanelOrigin(body.Origin);
            return Results.Ok(new { ok = true });
        });

        endpoints.MapGet("ports", () => Results.Ok(new { ports = KnobSerial.PortNames(), found = KnobSerial.FindKnob() }));
        endpoints.MapGet("log", () => Results.Ok(_events.Snapshot()));
    }
}
