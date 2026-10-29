// SPDX-License-Identifier: GPL-2.0-or-later
//
// Reads the FlexControl over its USB serial port and hands each ";"-ended
// message to the controller. Finds the knob by its USB id when the port is
// "auto", reconnects by itself, and sends LED patterns back. Ported from the
// bridge's FlexKnob thread.

using System.IO.Ports;
using System.Runtime.Versioning;
using System.Text;

namespace Zeus.Community.FlexKnob;

/// <summary>An open knob port (a real serial port, or a fake one in the tests).</summary>
public interface IKnobPort : IDisposable
{
    int BytesToRead { get; }
    int Read(byte[] buffer, int offset, int count);
    void Write(byte[] data);
}

public sealed class KnobSerial : IAsyncDisposable
{
    private readonly Func<string, IKnobPort> _open;
    private readonly Func<string?> _find;
    private readonly Action<string> _onCode;
    private readonly Action<string, string> _note;
    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _out = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private volatile string _port;
    private volatile bool _reconnect;
    private volatile bool _connected;
    private int _shown;   // first few messages are written to the event log (shows the knob is talking)

    public KnobSerial(string port, Action<string> onCode, Action<string, string> note,
        Func<string, IKnobPort>? open = null, Func<string?>? find = null)
    {
        _port = port;
        _onCode = onCode;
        _note = note;
        _open = open ?? OpenSerial;
        _find = find ?? FindKnob;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public bool Connected => _connected;
    public string Device { get; private set; } = "";
    public string Error { get; private set; } = "";

    public void SetPort(string port)
    {
        _port = port;
        _reconnect = true;
    }

    public void Send(string text) => _out.Enqueue(Encoding.ASCII.GetBytes(text));

    private async Task RunAsync(CancellationToken ct)
    {
        IKnobPort? ser = null;
        var buf = new StringBuilder();
        var chunk = new byte[256];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_reconnect)
                {
                    _reconnect = false;
                    Close(ref ser);
                    Error = "";
                }
                var want = _port;
                if (string.IsNullOrEmpty(want))
                {
                    Close(ref ser);
                    Device = "";
                    Error = "";
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    continue;
                }
                if (ser is null)
                {
                    var dev = want == "auto" ? _find() : want;
                    if (string.IsNullOrEmpty(dev))
                    {
                        Device = "";
                        Error = "not found (is it plugged in to the computer running Zeus?)";
                        await Task.Delay(2000, ct).ConfigureAwait(false);
                        continue;
                    }
                    try
                    {
                        ser = _open(dev);
                        while (_out.TryDequeue(out _)) { }
                        buf.Clear();
                        Device = dev;
                        Error = "";
                        _shown = 0;
                        _connected = true;
                        _note("ok", $"FlexControl knob connected on {dev}.");
                        _onCode("__connected__");
                    }
                    catch (Exception ex)
                    {
                        var msg = ex.Message;
                        if (ex is UnauthorizedAccessException || msg.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                            msg.Contains("busy", StringComparison.OrdinalIgnoreCase))
                            msg = $"{dev} is in use by another program (DDUtil, the Thetis bridge?) - close it there first";
                        if (msg != Error) _note("warn", "FlexControl knob: " + msg);
                        Device = dev;
                        Error = msg;
                        ser = null;
                        await Task.Delay(3000, ct).ConfigureAwait(false);
                        continue;
                    }
                }

                while (_out.TryDequeue(out var o)) ser.Write(o);
                var n = ser.BytesToRead;
                if (n > 0)
                {
                    var got = ser.Read(chunk, 0, Math.Min(n, chunk.Length));
                    for (var i = 0; i < got; i++)
                    {
                        var ch = (char)chunk[i];
                        if (ch == ';')
                        {
                            var code = buf.ToString().Trim();
                            buf.Clear();
                            if (code.Length > 0)
                            {
                                if (_shown < 5) { _shown++; _note("info", $"Knob sends: {code};"); }
                                _onCode(code);
                            }
                        }
                        else if (buf.Length < 64) buf.Append(ch);
                        else buf.Clear();               // junk without ";" — start over
                    }
                }
                else await Task.Delay(10, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (ser is not null)
                {
                    _note("warn", "FlexControl knob disconnected: " + ex.Message);
                    Close(ref ser);
                    Error = "disconnected";
                }
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        Close(ref ser);
    }

    private void Close(ref IKnobPort? ser)
    {
        try { ser?.Dispose(); } catch { }
        ser = null;
        _connected = false;
    }

    // ── the real serial port ───────────────────────────────────────────────

    private sealed class SerialKnobPort : IKnobPort
    {
        private readonly SerialPort _p;
        public SerialKnobPort(SerialPort p) => _p = p;
        public int BytesToRead => _p.BytesToRead;
        public int Read(byte[] buffer, int offset, int count) => _p.Read(buffer, offset, count);
        public void Write(byte[] data) => _p.Write(data, 0, data.Length);
        public void Dispose() { try { _p.Close(); } catch { } _p.Dispose(); }
    }

    public static IKnobPort OpenSerial(string name)
    {
        var p = new SerialPort(name, KnobCatalog.Baud, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            // The bridge's pyserial switches DTR and RTS on when it opens a
            // port; the FlexControl only sends once they are on. .NET leaves
            // them off unless asked.
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 200,
            WriteTimeout = 500,
        };
        try { p.Open(); }
        catch { p.Dispose(); throw; }
        return new SerialKnobPort(p);
    }

    public static string[] PortNames()
    {
        try { return SerialPort.GetPortNames().Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    // ── finding the FlexControl by its USB id (VID 2192, PID 0010) ─────────

    public static string? FindKnob()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return FindWindows();
            if (OperatingSystem.IsLinux()) return FindLinux();
            if (OperatingSystem.IsMacOS()) return FindMac();
        }
        catch (Exception) { }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? FindWindows()
    {
        // HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_2192&PID_0010[&MI_xx]\<instance>\Device Parameters\PortName
        var present = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
        using var usb = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb is null) return null;
        var prefix = $"VID_{KnobCatalog.Vid:X4}&PID_{KnobCatalog.Pid:X4}";
        foreach (var dev in usb.GetSubKeyNames().Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            using var dk = usb.OpenSubKey(dev);
            if (dk is null) continue;
            foreach (var inst in dk.GetSubKeyNames())
            {
                using var pk = dk.OpenSubKey(inst + @"\Device Parameters");
                if (pk?.GetValue("PortName") is string port && present.Contains(port)) return port;
            }
        }
        return null;
    }

    private static string? FindLinux()
    {
        const string root = "/sys/class/tty";
        if (!Directory.Exists(root)) return null;
        foreach (var tty in Directory.GetDirectories(root).Select(d => Path.GetFileName(d))
                     .Where(n => n.StartsWith("ttyACM", StringComparison.Ordinal) || n.StartsWith("ttyUSB", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            // device → the USB interface; its parent (or grandparent for ttyUSB) holds idVendor/idProduct.
            foreach (var up in new[] { "..", "../.." })
            {
                var dir = Path.Combine(root, tty, "device", up);
                var v = TryRead(Path.Combine(dir, "idVendor"));
                var p = TryRead(Path.Combine(dir, "idProduct"));
                if (v is null || p is null) continue;
                if (string.Equals(v, KnobCatalog.Vid.ToString("x4"), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p, KnobCatalog.Pid.ToString("x4"), StringComparison.OrdinalIgnoreCase))
                    return "/dev/" + tty;
                break;
            }
        }
        return null;
    }

    private static string? FindMac()
    {
        // macOS names USB modems /dev/cu.usbmodem<location>; without IOKit the
        // id is not readable here, so take it only when exactly one is present.
        var modems = PortNames().Where(n => n.StartsWith("/dev/cu.usbmodem", StringComparison.Ordinal)).ToArray();
        return modems.Length == 1 ? modems[0] : null;
    }

    private static string? TryRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
