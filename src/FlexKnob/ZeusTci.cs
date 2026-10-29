// SPDX-License-Identifier: GPL-2.0-or-later
//
// Connection to Zeus's TCI server (a WebSocket, port 40001 by default). This
// is how the knob reaches Zeus functions the plugin SDK does not offer:
// volume, mute, AGC gain, RIT/XIT, lock, split, NR/NB/ANF, drive and TUNE —
// the same way the bridge reached Thetis. It reconnects by itself, and on
// every connect asks Zeus for the current values the knob works with.
//
// Note for catalog submission: the Zeus community rules discourage features
// from talking to Zeus's own interfaces. Here it is the only route to these
// functions; see the README.

using System.Net.WebSockets;
using System.Text;

namespace Zeus.Community.FlexKnob;

public interface ITciLink
{
    bool Connected { get; }
    TciState State { get; }
    Task<bool> SendAsync(string commands, CancellationToken ct = default);
}

public sealed class ZeusTci : ITciLink, IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly int _rx;
    private readonly Action<string, string> _note;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Task _loop;
    private ClientWebSocket? _ws;
    private volatile bool _connected;

    public ZeusTci(string host, int port, int rx, Action<string, string> note)
    {
        _host = host;
        _port = port;
        _rx = rx;
        _note = note;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public TciState State { get; } = new();
    public bool Connected => _connected;
    public string Target => $"{_host}:{_port}";
    /// <summary>Why the last connection attempt failed, in plain words ("" when connected).</summary>
    public string LastError { get; private set; } = "";
    /// <summary>Raised (on the TCI thread) when the connection comes up or goes down.</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>Values the knob needs; asked for on every connect.</summary>
    private string StartupQueries() => KnobCatalog.Rx(
        "mute;lock:{rx};split_enable:{rx};rit_enable:{rx};xit_enable:{rx};rit_offset:{rx};xit_offset:{rx};" +
        "rx_nb_enable:{rx};volume;agc_gain:{rx};drive:{rx};tune:{rx};trx:{rx};vfo:{rx},0;vfo:{rx},1;", _rx);

    public async Task<bool> SendAsync(string commands, CancellationToken ct = default)
    {
        var ws = _ws;
        if (ws is null || !_connected || ws.State != WebSocketState.Open) return false;
        if (!commands.EndsWith(';')) commands += ";";
        var bytes = Encoding.ASCII.GetBytes(commands);
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
        finally { _sendGate.Release(); }
    }

    private string Explain(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException se)
            {
                return se.SocketErrorCode switch
                {
                    System.Net.Sockets.SocketError.ConnectionRefused =>
                        $"nothing is listening at {Target}: TCI is off in Zeus, or Zeus uses a different port",
                    System.Net.Sockets.SocketError.TimedOut or System.Net.Sockets.SocketError.HostUnreachable =>
                        $"no answer from {Target}",
                    _ => $"{se.SocketErrorCode} at {Target}",
                };
            }
        }
        if (ex is OperationCanceledException) return $"no answer from {Target} within 5 s";
        return ex.Message;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var warned = false;
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(5));
                    await ws.ConnectAsync(new Uri($"ws://{_host}:{_port}/"), cts.Token).ConfigureAwait(false);
                }
                _ws = ws;
                _connected = true;
                warned = false;
                LastError = "";
                State.Clear();
                _note("ok", $"Zeus TCI connected ({Target}).");
                ConnectionChanged?.Invoke(true);
                await SendAsync(StartupQueries(), ct).ConfigureAwait(false);

                var buf = new byte[8192];
                var msg = new StringBuilder();
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    if (r.MessageType != WebSocketMessageType.Text) continue;   // audio / IQ streams: not used
                    msg.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                    if (!r.EndOfMessage) continue;
                    State.Ingest(msg.ToString());
                    msg.Clear();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                LastError = Explain(ex);
                if (!_connected && !warned) { _note("warn", $"Waiting for Zeus TCI at {Target} ({LastError})."); warned = true; }
            }

            _ws = null;
            if (_connected)
            {
                _connected = false;
                LastError = "connection lost";
                _note("warn", $"Zeus TCI connection to {Target} lost.");
                ConnectionChanged?.Invoke(false);
            }
            try { await Task.Delay(2000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        _cts.Dispose();
        _sendGate.Dispose();
    }
}
