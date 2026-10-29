// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus's own web address and its current tuning step.
//
// The address is needed to reach the KPA1500 feature's API and to read the
// toolbar tuning step. It is taken, in this order, from the knob settings
// (if typed), from the web server Zeus is running (ASP.NET's server-address
// list — this also covers the desktop app's random port), or from the
// address the panel was opened at.

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Zeus.Community.FlexKnob;

public sealed class ZeusWeb : IZeusWeb, IAsyncDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly Func<KnobConfig> _cfg;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private IServiceProvider? _services;
    private volatile string? _panelOrigin;
    private volatile string? _serverUrl;
    private int _step;

    public ZeusWeb(Func<KnobConfig> cfg)
    {
        _cfg = cfg;
        _loop = Task.Run(() => PollAsync(_cts.Token));
    }

    public void UseServices(IServiceProvider sp) => _services = sp;

    /// <summary>The page the panel runs in (sent by the panel); used only as a last resort.</summary>
    public void SetPanelOrigin(string? origin)
    {
        if (KnobConfig.UrlOk(origin) && !string.IsNullOrEmpty(origin)) _panelOrigin = origin.TrimEnd('/');
    }

    public int? StepHz { get { var s = Volatile.Read(ref _step); return s > 0 ? s : null; } }

    public string StepError { get; private set; } = "";

    public string? BaseUrl
    {
        get
        {
            var typed = _cfg().ZeusUrl;
            if (!string.IsNullOrEmpty(typed)) return typed;
            var s = _serverUrl ?? (_serverUrl = FromServer());
            return s ?? _panelOrigin;
        }
    }

    /// <summary>Where the address came from, for the panel.</summary>
    public string BaseSource =>
        !string.IsNullOrEmpty(_cfg().ZeusUrl) ? "setting" : _serverUrl is not null ? "Zeus server" : _panelOrigin is not null ? "panel" : "not known";

    /// <summary>
    /// A loopback address from the server's listening addresses
    /// ("http://0.0.0.0:6060" → "http://127.0.0.1:6060"). Prefers plain http.
    /// </summary>
    private string? FromServer()
    {
        try
        {
            var server = _services?.GetService(typeof(IServer)) as IServer;
            var addrs = server?.Features.Get<IServerAddressesFeature>()?.Addresses;
            if (addrs is null || addrs.Count == 0) return null;
            return PickLoopback(addrs);
        }
        catch (Exception) { return null; }
    }

    public static string? PickLoopback(IEnumerable<string> addresses)
    {
        string? best = null;
        foreach (var a in addresses)
        {
            var s = a.Replace("://+", "://127.0.0.1", StringComparison.Ordinal)
                     .Replace("://*", "://127.0.0.1", StringComparison.Ordinal)
                     .Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal)
                     .Replace("://[::]", "://127.0.0.1", StringComparison.Ordinal)
                     .Replace("://localhost", "://127.0.0.1", StringComparison.OrdinalIgnoreCase);
            if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) continue;
            var url = $"{u.Scheme}://{u.Host}:{u.Port}";
            if (u.Scheme == "http" && (u.IsLoopback || u.Host == "127.0.0.1")) return url;
            best ??= url;
        }
        return best;
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_cfg().StepSource == "zeus" && BaseUrl is { } b)
                {
                    var r = await _http.GetFromJsonAsync<JsonElement>(b + "/api/toolbar-settings", ct).ConfigureAwait(false);
                    if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("stepHz", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var hz) && hz > 0)
                        Volatile.Write(ref _step, hz);
                    StepError = "";
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { StepError = ex.Message; }
            try { await Task.Delay(2000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        _cts.Dispose();
        _http.Dispose();
    }
}
