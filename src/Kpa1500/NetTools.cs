// SPDX-License-Identifier: GPL-2.0-or-later
//
// Wake-on-LAN and network discovery for the KPA1500, ported from the earlier
// Thetis bridge (send_wol / discover_amps / _probe).

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Zeus.Community.Kpa1500;

public sealed record FoundAmp(string Ip, bool Boot, string Serial, string Firmware, string Mac);

public static class NetTools
{
    /// <summary>Magic packet: 6 × 0xFF then the MAC address 16 times (102 bytes).</summary>
    public static byte[] BuildMagic(string mac)
    {
        var norm = PluginConfig.NormalizeMac(mac);
        if (norm == "") throw new ArgumentException("Not a MAC address.", nameof(mac));
        var raw = norm.Split(':').Select(p => Convert.ToByte(p, 16)).ToArray();
        var pkt = new byte[6 + 16 * 6];
        for (var i = 0; i < 6; i++) pkt[i] = 0xFF;
        for (var r = 0; r < 16; r++) Array.Copy(raw, 0, pkt, 6 + r * 6, 6);
        return pkt;
    }

    /// <summary>This machine's usable IPv4 addresses with their prefix lengths.</summary>
    public static List<(IPAddress Address, int Prefix)> LocalIPv4()
    {
        var list = new List<(IPAddress, int)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = ua.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue; // link-local
                    list.Add((ua.Address, ua.PrefixLength));
                }
            }
        }
        catch (NetworkInformationException) { }
        return list;
    }

    private static IPAddress Slash24Broadcast(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        b[3] = 255;
        return new IPAddress(b);
    }

    /// <summary>Send the magic packet to the global, /24 and direct addresses from every local interface.</summary>
    public static async Task<int> SendWakeOnLanAsync(string mac, string ampHost, int port, CancellationToken ct)
    {
        var pkt = BuildMagic(mac);
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        if (IPAddress.TryParse(ampHost, out var ampIp) && ampIp.AddressFamily == AddressFamily.InterNetwork)
        {
            targets.Add(ampIp);
            targets.Add(Slash24Broadcast(ampIp));
        }
        var sources = LocalIPv4().Select(x => x.Address).ToList();
        if (sources.Count == 0) sources.Add(IPAddress.Any);

        var sent = 0;
        foreach (var src in sources)
        {
            foreach (var dst in targets)
            {
                try
                {
                    using var s = new UdpClient(new IPEndPoint(src, 0)) { EnableBroadcast = true };
                    await s.SendAsync(pkt, new IPEndPoint(dst, port), ct).ConfigureAwait(false);
                    sent++;
                }
                catch (SocketException) { }
            }
        }
        return sent;
    }

    /// <summary>Ask one amplifier one question over UDP; returns the value after the prefix, or null.</summary>
    public static async Task<string?> ProbeAsync(IPAddress ip, int port, string cmd, string prefix, TimeSpan wait, CancellationToken ct)
    {
        using var s = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            await s.SendAsync(Encoding.ASCII.GetBytes(cmd), new IPEndPoint(ip, port), ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(wait);
            while (true)
            {
                UdpReceiveResult r;
                try { r = await s.ReceiveAsync(cts.Token).ConfigureAwait(false); }
                catch (SocketException) { continue; }
                var t = Encoding.ASCII.GetString(r.Buffer).Trim();
                if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return t[prefix.Length..].TrimEnd(';').Trim();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (SocketException) { return null; }
    }

    /// <summary>
    /// Send "^I;" to every address on this machine's /24 networks (plus the /24
    /// of <paramref name="hint"/>) and collect the ones that answer "^KPA1500;".
    /// </summary>
    public static async Task<(List<FoundAmp> Found, string Searched)> DiscoverAsync(int port, string? hint, CancellationToken ct)
    {
        var nets = new List<IPAddress>();
        foreach (var (addr, _) in LocalIPv4())
        {
            var bc = Slash24Broadcast(addr);
            if (!nets.Contains(bc)) nets.Add(bc);
        }
        if (IPAddress.TryParse(hint, out var h) && h.AddressFamily == AddressFamily.InterNetwork)
        {
            var bc = Slash24Broadcast(h);
            if (!nets.Contains(bc)) nets.Add(bc);
        }
        if (nets.Count == 0) return (new List<FoundAmp>(), "no local IPv4 network");

        var targets = new List<IPAddress>();
        foreach (var bc in nets)
        {
            var b = bc.GetAddressBytes();
            for (var host = 1; host <= 254; host++)
                targets.Add(new IPAddress(new[] { b[0], b[1], b[2], (byte)host }));
            targets.Add(bc);
        }

        var seen = new Dictionary<string, bool>();
        using (var sock = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true })
        {
            var probe = Encoding.ASCII.GetBytes("^I;");
            for (var pass = 0; pass < 3; pass++)
            {
                for (var i = 0; i < targets.Count; i++)
                {
                    try { await sock.SendAsync(probe, new IPEndPoint(targets[i], port), ct).ConfigureAwait(false); }
                    catch (SocketException) { }
                    if (i % 64 == 63) await Task.Delay(5, ct).ConfigureAwait(false);
                }
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(800));
                try
                {
                    while (true)
                    {
                        UdpReceiveResult r;
                        try { r = await sock.ReceiveAsync(cts.Token).ConfigureAwait(false); }
                        catch (SocketException) { continue; } // ICMP reset on Windows
                        var t = Encoding.ASCII.GetString(r.Buffer).Trim();
                        if (t.StartsWith("^KPA1500;", StringComparison.OrdinalIgnoreCase))
                            seen[r.RemoteEndPoint.Address.ToString()] = t.StartsWith("^kpa", StringComparison.Ordinal);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
        }

        var found = new List<FoundAmp>();
        foreach (var (ip, boot) in seen.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var addr = IPAddress.Parse(ip);
            var wait = TimeSpan.FromMilliseconds(600);
            found.Add(new FoundAmp(ip, boot,
                await ProbeAsync(addr, port, "^SN;", "^SN", wait, ct).ConfigureAwait(false) ?? "",
                await ProbeAsync(addr, port, "^RV;", "^RV", wait, ct).ConfigureAwait(false) ?? "",
                PluginConfig.NormalizeMac(await ProbeAsync(addr, port, "^MA;", "^MA", wait, ct).ConfigureAwait(false))));
        }
        var searched = $"{targets.Count} addresses on " + string.Join(", ", nets.Select(n =>
        {
            var b = n.GetAddressBytes();
            return $"{b[0]}.{b[1]}.{b[2]}.0/24";
        }));
        return (found, searched);
    }
}
