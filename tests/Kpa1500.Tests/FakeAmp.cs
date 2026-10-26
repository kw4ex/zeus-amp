// SPDX-License-Identifier: GPL-2.0-or-later
//
// A KPA1500 that answers on loopback UDP — ported from the FakeAmp in the
// earlier kpa1500_thetis_bridge.py selftest, so the plugin is tested against
// the same simulated behaviour the bridge was.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Kpa1500.Tests;

public sealed class FakeAmp : IDisposable
{
    private readonly UdpClient _sock = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public int Port => ((IPEndPoint)_sock.Client.LocalEndPoint!).Port;
    public ConcurrentQueue<string> Received { get; } = new();

    public volatile bool Asleep;
    public volatile int Mode = 1;        // 0 standby / 1 operate
    public volatile int Fwd;             // watts reported by ^WS
    public volatile int Fault;
    public volatile int FanMin = 1;
    public volatile int Wl;
    public volatile int Freq;
    public volatile int Band = 5;
    public volatile int Ant = 1;
    public volatile char Atu = 'I';
    public volatile bool Tuning;

    public FakeAmp() => _loop = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _sock.ReceiveAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            var c = Encoding.ASCII.GetString(r.Buffer).Trim().ToUpperInvariant();
            Received.Enqueue(c);
            if (Asleep)
            {
                if (c == "^ON1;") Asleep = false;
                continue;
            }
            string? o = c switch
            {
                "^I;" => "^KPA1500;",
                "^SN;" => "^SN00022;",
                "^RV;" => "^RV03.02;",
                "^ON;" => "^ON1;",
                "^MA;" => "^MA54:10:EC:14:75:22;",
                "^WL;" => $"^WL{Wl};",
                "^OS;" => $"^OS{Mode};",
                "^WS;" => $"^WS{Fwd:0000} 014;",
                "^PWI;" => $"^PWI{(Mode == 1 ? 47 : 0):0000};",
                "^PWR;" => "^PWR0012;",
                "^PWD;" => "^PWD0300;",
                "^TM;" => "^TM031;",
                "^FL;" => $"^FL{Fault:X2};",
                "^VI;" => "^VI513 061;",
                "^AM;" => $"^AM{Atu};",
                "^AN;" => $"^AN{Ant};",
                "^BN;" => $"^BN{Band:00};",
                "^TP;" => Tuning ? "^TP1;" : "^TP0;",
                "^FS;" => $"^FS{Math.Max(FanMin, 1)};",
                "^FC;" => $"^FC{FanMin};",
                "^SB;" => "^SB016;",
                _ => null,
            };
            if (o is null) Apply(c);
            else await _sock.SendAsync(Encoding.ASCII.GetBytes(o), r.RemoteEndPoint);
        }
    }

    private void Apply(string c)
    {
        if (c is "^OS0;" or "^OS1;") Mode = c[3] - '0';
        else if (c == "^ON0;") Asleep = true;
        else if (c == "^FLC;") Fault = 0;
        else if (c == "^WL1;") Wl = 1;
        else if (c is "^AMI;" or "^AMB;") Atu = c[3];
        else if (c is "^AN1;" or "^AN2;") Ant = c[3] - '0';
        else if (c == "^AN0;") Ant = Ant == 1 ? 2 : 1;
        else if (c == "^FT;") Tuning = true;
        else if (c == "^FE;") Tuning = false;
        else if (Regex.IsMatch(c, @"^\^FC[0-5];$")) FanMin = c[3] - '0';
        else if (Regex.IsMatch(c, @"^\^FR\d{5};$")) Freq = int.Parse(c[3..8]);
        else if (Regex.IsMatch(c, @"^\^BN\d{2};$")) Band = int.Parse(c[3..5]);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop.Wait(1000); } catch { }
        _sock.Dispose();
    }
}

/// <summary>Stand-in for Zeus's IRadioStateReader.</summary>
public sealed class FakeRadio : Zeus.Plugins.Contracts.IRadioStateReader
{
    public long FrequencyHz { get; private set; } = 14_205_000;
    public string Mode { get; private set; } = "USB";
    public string Band => "20m";
    public bool Mox { get; private set; }

    public event Action<long> FrequencyChanged = delegate { };
    public event Action<string> ModeChanged = delegate { };
    public event Action<bool> MoxChanged = delegate { };

    public void SetFrequency(long hz) { FrequencyHz = hz; FrequencyChanged(hz); }
    public void SetMode(string m) { Mode = m; ModeChanged(m); }
    public void SetMox(bool on) { Mox = on; MoxChanged(on); }
}
