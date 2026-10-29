// SPDX-License-Identifier: GPL-2.0-or-later
//
// Optional TCI listener. It learns when the radio's TUNE and TX are active
// (the Zeus plugin SDK exposes MOX but no TUNE flag), the transmit frequency —
// VFO A, or VFO B when split is on — and the receive level in dBm for the
// "No antenna?" check, as the earlier Thetis bridge did from "tune:", "trx:",
// "vfo:", "split_enable:" and "rx_channel_sensors:" events.
// The only thing it sends is "rx_sensors_enable:true,250;", which asks Zeus
// for receive-level reports; it never sends commands that change the radio.
//
// Note for catalog submission: the Zeus community rules discourage plugins
// from talking to Zeus's own interfaces. This listener is OFF by default; see
// the README.

using System.Globalization;
using System.Net.WebSockets;
using System.Text;

namespace Zeus.Community.Kpa1500;

public sealed class TciClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly int _rx;
    private readonly Action<string, string> _note;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private volatile bool _connected;
    private volatile bool _tune;
    private volatile bool _trx;
    private volatile bool _split;
    private long _vfoA;
    private long _vfoB;

    public TciClient(string host, int port, int rx, Action<string, string> note)
    {
        _host = host;
        _port = port;
        _rx = rx;
        _note = note;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public bool Connected => _connected;
    public bool Tune => _connected && _tune;
    public bool Trx => _connected && _trx;
    public bool Split => _connected && _split;

    /// <summary>Transmit frequency per TCI: VFO B when split is on, else VFO A. 0 = not known yet.</summary>
    public long TxHz
    {
        get
        {
            if (!_connected) return 0;
            var b = Interlocked.Read(ref _vfoB);
            return _split && b > 0 ? b : Interlocked.Read(ref _vfoA);
        }
    }

    /// <summary>Raised on the TCI thread whenever tune/trx/split/VFO/connection changes.</summary>
    public event Action? Changed;

    /// <summary>Raised on the TCI thread for each receive-level report (dBm, RX1 / channel 0).</summary>
    public event Action<double>? RxLevel;

    private volatile bool _sensorsEx;   // Thetis-style rx_channel_sensors_ex seen: prefer its average
    private int _levelChannel = int.MaxValue;   // receiver channel the level reports use (lowest seen)
    private long _levelCount;
    private long _lastLevelBits = BitConverter.DoubleToInt64Bits(double.NaN);

    /// <summary>Receive-level reports accepted since connecting.</summary>
    public long LevelCount => Interlocked.Read(ref _levelCount);
    /// <summary>Last receive level reported by Zeus (dBm), or null.</summary>
    public double? LastLevel { get { var v = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _lastLevelBits)); return double.IsNaN(v) || !_connected ? null : v; } }
    /// <summary>The channel number Zeus uses for the receive level (diagnostics).</summary>
    public int? LevelChannel => _levelChannel == int.MaxValue ? null : _levelChannel;

    public string Target => $"{_host}:{_port}";

    /// <summary>Why the last connection attempt failed, in plain words ("" when connected).</summary>
    public string LastError { get; private set; } = "";

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
                _connected = true;
                warned = false;
                LastError = "";
                _levelChannel = int.MaxValue;
                Interlocked.Exchange(ref _levelCount, 0);
                Interlocked.Exchange(ref _lastLevelBits, BitConverter.DoubleToInt64Bits(double.NaN));
                _sensorsEx = false;
                // Ask for receive-level reports every 250 ms (TCI spec §4.4).
                var req = Encoding.ASCII.GetBytes("rx_sensors_enable:true,250;");
                await ws.SendAsync(new ArraySegment<byte>(req), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                _note("ok", $"TCI connected ({Target}) — Standby-on-Tune follows radio TUNE.");
                Changed?.Invoke();

                var buf = new byte[8192];
                var msg = new StringBuilder();
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    if (r.MessageType != WebSocketMessageType.Text) continue;
                    msg.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                    if (!r.EndOfMessage) continue;
                    Ingest(msg.ToString());
                    msg.Clear();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                LastError = Explain(ex);
                if (!_connected && !warned) { _note("warn", $"Waiting for TCI at {Target} ({LastError})."); warned = true; }
                Changed?.Invoke();
            }

            if (_connected)
            {
                _connected = false;
                _tune = false;
                _trx = false;
                _split = false;
                Interlocked.Exchange(ref _vfoA, 0);
                Interlocked.Exchange(ref _vfoB, 0);
                _note("warn", $"TCI connection to {Target} lost.");
                Changed?.Invoke();
            }
            try { await Task.Delay(2000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Parse one TCI text message ("name:arg,arg;name:arg;…").</summary>
    internal void Ingest(string message)
    {
        var changed = false;
        foreach (var raw in message.Split(';'))
        {
            var tok = raw.Trim();
            if (tok.Length == 0) continue;
            var colon = tok.IndexOf(':');
            if (colon < 0) continue;
            var name = tok[..colon].Trim().ToLowerInvariant();
            var args = tok[(colon + 1)..].Split(',');
            if (args.Length < 2) continue;
            if (!int.TryParse(args[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rx) || rx != _rx) continue;
            if (name is "rx_channel_sensors_ex" or "rx_channel_sensors" or "rx_smeter")
            {
                // rx_channel_sensors:rx,channel,dBm   (Zeus, Thetis)
                // rx_channel_sensors_ex:rx,channel,signal,average,peak_bin  (Thetis)
                // rx_smeter:rx,channel,dBm   (Zeus broadcasts this to every client)
                if (args.Length < 3 || !int.TryParse(args[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ch) || ch < 0)
                    continue;
                // Zeus tags the level with its internal DSP channel number, which is
                // not always 0. Follow the lowest channel seen (the main receiver).
                if (ch < _levelChannel) _levelChannel = ch;
                if (ch != _levelChannel) continue;
                string? level = null;
                if (name == "rx_channel_sensors_ex" && args.Length >= 4) { _sensorsEx = true; level = args[3]; }
                else if (name == "rx_channel_sensors" && !_sensorsEx) level = args[2];
                else if (name == "rx_smeter" && !_sensorsEx) level = args[2];
                if (level is not null && double.TryParse(level.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dbm))
                {
                    if (Interlocked.Increment(ref _levelCount) == 1)
                        _note("ok", $"Zeus is sending the receive level ({name}, channel {ch}, {dbm:0} dBm).");
                    Interlocked.Exchange(ref _lastLevelBits, BitConverter.DoubleToInt64Bits(dbm));
                    RxLevel?.Invoke(dbm);
                }
                continue;
            }
            if (name == "vfo")
            {
                // vfo:rx,channel,hz  (channel 0 = VFO A, 1 = VFO B)
                if (args.Length >= 3 &&
                    int.TryParse(args[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var chan) &&
                    long.TryParse(args[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hz) && hz > 0)
                {
                    if (chan == 0 && Interlocked.Exchange(ref _vfoA, hz) != hz) changed = true;
                    else if (chan == 1 && Interlocked.Exchange(ref _vfoB, hz) != hz) changed = true;
                }
                continue;
            }
            var flag = string.Equals(args[1].Trim(), "true", StringComparison.OrdinalIgnoreCase);
            if (name == "tune" && flag != _tune) { _tune = flag; changed = true; }
            else if (name == "trx" && flag != _trx) { _trx = flag; changed = true; }
            else if (name == "split_enable" && flag != _split) { _split = flag; changed = true; }
        }
        if (changed) Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
