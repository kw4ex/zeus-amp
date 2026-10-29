// SPDX-License-Identifier: GPL-2.0-or-later
//
// A TCI server that behaves like Zeus's: it never announces TUNE by itself and
// only answers "tune:0;" / "trx:0;" questions with the current state.

using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Kpa1500.Tests;

public sealed class FakeTci : IDisposable
{
    private readonly HttpListener _http = new();
    private readonly CancellationTokenSource _cts = new();
    public int Port { get; }
    public volatile bool TunOn;
    public volatile int TuneQueries;

    public FakeTci()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _http.Prefixes.Add($"http://localhost:{Port}/");
        _http.Start();
        _ = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync().ConfigureAwait(false); } catch { return; }
            if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }
            _ = Task.Run(() => ServeAsync(ctx));
        }
    }

    private async Task ServeAsync(HttpListenerContext ctx)
    {
        try
        {
            var wsc = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false);
            var ws = wsc.WebSocket;
            var buf = new byte[4096];
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
                var reply = new StringBuilder();
                foreach (var cmd in Encoding.ASCII.GetString(buf, 0, r.Count).Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var c = cmd.Trim();
                    if (c == "tune:0") { TuneQueries++; reply.Append($"tune:0,{(TunOn ? "true" : "false")};"); }
                    else if (c == "trx:0") reply.Append("trx:0,false;");
                }
                if (reply.Length > 0)
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.ASCII.GetBytes(reply.ToString())),
                        WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
            }
        }
        catch { /* test teardown */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _http.Stop(); _http.Close(); } catch { }
    }
}
