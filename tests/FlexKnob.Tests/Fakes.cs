// SPDX-License-Identifier: GPL-2.0-or-later
//
// Stand-ins for the hardware and for Zeus, so the knob can be tested with no
// radio, no amplifier and no knob:
//   FakeKnobPort  what the FlexControl would send, and what it is sent (LEDs)
//   FakeRadio     Zeus plugin radio state + controller (frequency, MOX)
//   FakeZeus      one local web server playing Zeus: the TCI WebSocket
//                 (answers gets, records sets), the toolbar step, and the
//                 KPA1500 feature's HTTP API.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zeus.Community.FlexKnob;
using Zeus.Plugins.Contracts;

namespace FlexKnob.Tests;

public sealed class FakeKnobPort : IKnobPort
{
    private readonly ConcurrentQueue<byte> _in = new();
    public readonly ConcurrentQueue<string> Written = new();
    public bool Disposed;

    public void Feed(string s) { foreach (var b in Encoding.ASCII.GetBytes(s)) _in.Enqueue(b); }
    public int BytesToRead => _in.Count;
    public int Read(byte[] buffer, int offset, int count)
    {
        var n = 0;
        while (n < count && _in.TryDequeue(out var b)) buffer[offset + n++] = b;
        return n;
    }
    public void Write(byte[] data) => Written.Enqueue(Encoding.ASCII.GetString(data));
    public void Dispose() => Disposed = true;
    public bool Saw(string s) => Written.Contains(s);
    public string? LastLeds => Written.LastOrDefault(w => w.StartsWith('I'));
}

public sealed class FakeRadio : IRadioStateReader, IRadioController
{
    private long _hz = 14_200_000;
    private volatile bool _mox;
    public readonly ConcurrentQueue<long> Sets = new();
    public readonly ConcurrentQueue<bool> MoxSets = new();

    public long FrequencyHz => Interlocked.Read(ref _hz);
    public string Mode => "USB";
    public string Band => "20m";
    public bool Mox => _mox;
    public event Action<long> FrequencyChanged { add { } remove { } }
    public event Action<string> ModeChanged { add { } remove { } }
    public event Action<bool> MoxChanged { add { } remove { } }

    public Task SetFrequencyAsync(long hz, CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _hz, hz);
        Sets.Enqueue(hz);
        return Task.CompletedTask;
    }
    public Task SetModeAsync(string mode, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetMoxAsync(bool keyed, CancellationToken ct = default)
    {
        _mox = keyed;
        MoxSets.Enqueue(keyed);
        return Task.CompletedTask;
    }
    public void Set(long hz) => Interlocked.Exchange(ref _hz, hz);
}

public sealed class FakeZeus : IAsyncDisposable
{
    public const string AmpId = "com.kw4ex.zeus.kpa1500";
    private WebApplication? _app;
    private readonly ConcurrentDictionary<string, string> _tci = new();
    public readonly ConcurrentQueue<string> TciReceived = new();
    public readonly ConcurrentQueue<string> AmpCalls = new();
    private readonly ConcurrentBag<WebSocket> _clients = new();

    // amplifier state
    public volatile bool AmpOperate = true;
    public volatile bool AmpPowerOn = true;
    public volatile int AmpAntenna = 1;
    public volatile string NoAnt = "ok";
    public volatile bool StandbyOnTune = true;
    public volatile bool AmpAnswers = true;   // false: KPA1500 feature "not installed" (404)
    public volatile bool AmpIgnoresStandby;   // true: never confirms STANDBY
    public int StepHz = 500;

    public string Url { get; private set; } = "";
    public int Port { get; private set; }
    public IServiceProvider Services => _app!.Services;

    public FakeZeus()
    {
        _tci["volume"] = "-20";
        _tci["mute"] = "false";
        _tci["lock:0"] = "false";
        _tci["split_enable:0"] = "false";
        _tci["rit_enable:0"] = "false";
        _tci["xit_enable:0"] = "false";
        _tci["rit_offset:0"] = "0";
        _tci["xit_offset:0"] = "0";
        _tci["rx_nb_enable:0"] = "false";
        _tci["agc_gain:0"] = "60";
        _tci["drive:0"] = "50";
        _tci["tune:0"] = "false";
        _tci["trx:0"] = "false";
        _tci["vfo:0,0"] = "14200000";
        _tci["vfo:0,1"] = "14250000";
    }

    public string? Tci(string key) => _tci.TryGetValue(key, out var v) ? v : null;
    public void SetTci(string key, string value) => _tci[key] = value;
    public bool Got(string cmd) => TciReceived.Contains(cmd);
    public int Count(string cmd) => TciReceived.Count(x => x == cmd);
    public void ClearTci() { while (TciReceived.TryDequeue(out _)) { } }

    public async Task StartAsync()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        _app = b.Build();
        _app.UseWebSockets();

        _app.Map("/", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            _clients.Add(ws);
            await SendAsync(ws, "protocol:ExpertSDR3,2.0;device:Zeus;ready;");
            var buf = new byte[4096];
            while (ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult r;
                try { r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ctx.RequestAborted); }
                catch (Exception) { break; }
                if (r.MessageType == WebSocketMessageType.Close) break;
                var text = Encoding.ASCII.GetString(buf, 0, r.Count);
                foreach (var cmd in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    TciReceived.Enqueue(cmd);
                    var reply = Handle(cmd);
                    if (reply is not null) await SendAsync(ws, reply);
                }
            }
        });

        _app.MapGet("/api/toolbar-settings", () => Results.Ok(new { mode = (string[]?)null, band = (string[]?)null, step = (string[]?)null, stepHz = StepHz }));

        var p = "/api/plugins/" + AmpId;
        _app.MapGet(p + "/status", () =>
        {
            AmpCalls.Enqueue("GET status");
            if (!AmpAnswers) return Results.NotFound();
            return Results.Ok(new
            {
                version = "1.1.6",
                connection = new { state = "open" },
                amp = new { power = AmpPowerOn ? "on" : "off", operate = AmpOperate, antenna = AmpAntenna },
                noAnt = new { state = NoAnt },
                tx = false,
                tuneGuard = new { phase = "idle" },
            });
        });
        _app.MapPost(p + "/tune-guard", async (HttpContext ctx) =>
        {
            var body = await JsonDocument.ParseAsync(ctx.Request.Body);
            var arm = body.RootElement.GetProperty("arm").GetBoolean();
            AmpCalls.Enqueue("tune-guard " + arm);
            if (!AmpAnswers) return Results.NotFound();
            if (arm && !StandbyOnTune) return Results.Json(new { ok = false, message = "Standby-on-Tune is switched off in settings." }, statusCode: 409);
            if (arm && !AmpIgnoresStandby) _ = Task.Delay(200).ContinueWith(_ => AmpOperate = false, TaskScheduler.Default);
            return Results.Ok(new { ok = true, message = arm ? "Armed — press TUNE on the radio." : "Cancelled." });
        });
        _app.MapPost(p + "/operate", async (HttpContext ctx) =>
        {
            var body = await JsonDocument.ParseAsync(ctx.Request.Body);
            AmpOperate = body.RootElement.GetProperty("operate").GetBoolean();
            AmpCalls.Enqueue("operate " + AmpOperate);
            return Results.Ok(new { ok = true, message = AmpOperate ? "OPERATE." : "STANDBY." });
        });
        _app.MapPost(p + "/antenna", async (HttpContext ctx) =>
        {
            var body = await JsonDocument.ParseAsync(ctx.Request.Body);
            AmpAntenna = body.RootElement.GetProperty("antenna").GetInt32();
            AmpCalls.Enqueue("antenna " + AmpAntenna);
            return Results.Ok(new { ok = true, message = "Antenna " + AmpAntenna + "." });
        });
        _app.MapPost(p + "/noant/dummy", async (HttpContext ctx) =>
        {
            var body = await JsonDocument.ParseAsync(ctx.Request.Body);
            var on = body.RootElement.GetProperty("on").GetBoolean();
            NoAnt = on ? "dummy" : "ok";
            AmpCalls.Enqueue("dummy " + on);
            return Results.Ok(new { ok = true, message = on ? "Dummy load: OK." : "Dummy off." });
        });
        _app.MapPost(p + "/atu/tune", () => { AmpCalls.Enqueue("atu tune"); return Results.Ok(new { ok = true, message = "ATU tune started." }); });
        _app.MapGet(p + "/config", () => Results.Ok(new { host = "192.168.1.50", standbyOnTune = StandbyOnTune, tuneTimeoutS = 30 }));
        _app.MapPost(p + "/config", async (HttpContext ctx) =>
        {
            var body = await JsonDocument.ParseAsync(ctx.Request.Body);
            StandbyOnTune = body.RootElement.GetProperty("standbyOnTune").GetBoolean();
            AmpCalls.Enqueue("config sot=" + StandbyOnTune + " host=" + body.RootElement.GetProperty("host").GetString());
            return Results.Ok(new { ok = true });
        });

        await _app.StartAsync();
        var addr = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var u = new Uri(addr);
        Port = u.Port;
        Url = $"http://127.0.0.1:{u.Port}";
    }

    private static readonly HashSet<string> Bare = new() { "volume", "mute" };

    /// <summary>Like Zeus's TCI server: a get (no value) is answered, a set is stored and echoed.</summary>
    private string? Handle(string cmd)
    {
        var colon = cmd.IndexOf(':');
        var name = colon < 0 ? cmd : cmd[..colon];
        var args = colon < 0 ? Array.Empty<string>() : cmd[(colon + 1)..].Split(',');
        int idx = Bare.Contains(name) ? 0 : name == "vfo" ? 2 : 1;
        if (name is "rx_nr_enable" or "rx_anf_enable" && args.Length == 1) return null;   // Zeus: no get for these
        var key = idx == 0 ? name : name + ":" + string.Join(",", args.Take(idx));
        if (args.Length <= idx)
            return _tci.TryGetValue(key, out var v) ? $"{key}{(idx == 0 ? ":" : ",")}{v};" : null;
        var val = string.Join(",", args.Skip(idx));
        _tci[key] = val;
        return $"{key}{(idx == 0 ? ":" : ",")}{val};";
    }

    private static Task SendAsync(WebSocket ws, string s) =>
        ws.SendAsync(new ArraySegment<byte>(Encoding.ASCII.GetBytes(s)), WebSocketMessageType.Text, true, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}

public sealed class FixedWeb : IZeusWeb
{
    public int? StepHz { get; set; } = 500;
    public string? BaseUrl { get; set; }
}

public static class Num
{
    public static string S(long v) => v.ToString(CultureInfo.InvariantCulture);
}
