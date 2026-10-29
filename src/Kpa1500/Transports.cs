// SPDX-License-Identifier: GPL-2.0-or-later
//
// Opening a KpaLink over the connections the KPA1500 offers:
//   • Ethernet UDP, port 1500 (default). One "^...;" command per datagram,
//     the answer comes back as a datagram. This is the path proven on the
//     operator's amp by the earlier Thetis bridge.
//     UDP keeps no session, so other programs on the LAN (the Elecraft
//     utility, loggers, another bridge) can query the amp at the same time;
//     this side uses its own ephemeral source port so replies never collide.
//   • Serial: the rear USB (FTDI) port or a virtual serial port, 8N1, no flow
//     control, 4800–230400 bit/s. COM3 on Windows, /dev/cu.usbserial-… on macOS.
// The serial code lives in its own type so System.IO.Ports is only touched
// when the operator actually picks USB; UDP works on every platform.

using System.IO.Ports;
using System.Net.Sockets;

namespace Zeus.Community.Kpa1500;

public static class Transports
{
    public static readonly int[] SupportedBauds = { 4800, 9600, 19200, 38400, 57600, 115200, 230400 };

    public static async Task<KpaLink> OpenUdpAsync(string host, int port, CancellationToken ct)
    {
        var addresses = await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                 ?? throw new IOException($"Cannot resolve '{host}' to an IPv4 address.");
        var client = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            client.Connect(new System.Net.IPEndPoint(ip, port));
            return new KpaLink(new UdpDatagramStream(client), client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static KpaLink OpenSerial(string portName, int baud) => SerialOpener.Open(portName, baud);

    public static string[] SerialPortNames()
    {
        try { return SerialOpener.Names(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    private static class SerialOpener
    {
        public static KpaLink Open(string portName, int baud)
        {
            SerialPort? port = null;
            try
            {
                port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
                {
                    Handshake = Handshake.None,
                    DtrEnable = true,
                    RtsEnable = true,
                    ReadTimeout = SerialPort.InfiniteTimeout,
                    WriteTimeout = 1000,
                };
                port.Open();
                return new KpaLink(port.BaseStream, port);
            }
            catch (PlatformNotSupportedException)
            {
                port?.Dispose();
                throw new IOException("USB serial is not available in this build on this operating system. Use the amplifier's Ethernet (TCP) connection instead.");
            }
            catch
            {
                port?.Dispose();
                throw;
            }
        }

        public static string[] Names()
        {
            var names = SerialPort.GetPortNames();
            if (OperatingSystem.IsMacOS())
            {
                // macOS lists each port twice (/dev/tty.* and /dev/cu.*); the
                // "cu" (call-out) device is the one a program should open.
                var cu = names.Where(n => n.StartsWith("/dev/cu.", StringComparison.Ordinal)).ToArray();
                if (cu.Length > 0) names = cu;
            }
            return names.Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>
    /// Presents a connected UdpClient as a Stream: each write is one datagram,
    /// each read returns the next datagram. ICMP "port unreachable" resets
    /// (Windows reports them on the next receive) are ignored, because the amp
    /// may simply be asleep.
    /// </summary>
    private sealed class UdpDatagramStream : Stream
    {
        private readonly UdpClient _client;
        public UdpDatagramStream(UdpClient client) => _client = client;

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) =>
            _client.Send(buffer.AsSpan(offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                UdpReceiveResult r;
                try { r = await _client.ReceiveAsync(cancellationToken).ConfigureAwait(false); }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
                {
                    continue;
                }
                var n = Math.Min(r.Buffer.Length, buffer.Length);
                r.Buffer.AsSpan(0, n).CopyTo(buffer.Span);
                if (n > 0) return n;
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await _client.SendAsync(buffer, cancellationToken).ConfigureAwait(false); }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused) { }
        }
    }
}
