// SPDX-License-Identifier: GPL-2.0-or-later
//
// No-hardware tests. Run:  dotnet run --project tests/Kpa1500.Tests -c Release
// Exit code 0 = all passed. Never keys a transmitter (there is none).

using Microsoft.Extensions.Logging.Abstractions;
using Kpa1500.Tests;
using Zeus.Community.Kpa1500;

var failures = 0;
var passed = 0;
void Check(string name, bool ok)
{
    if (ok) { passed++; Console.WriteLine("  ok   " + name); }
    else
    {
        failures++;
        Console.WriteLine("  FAIL " + name);
        // On GitHub, also raise an annotation so the failure shows on the run page.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            Console.WriteLine($"::error title=Test failed on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}::{name}");
    }
}
async Task<bool> WaitFor(Func<bool> cond, double seconds = 4)
{
    var end = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < end) { if (cond()) return true; await Task.Delay(25); }
    return cond();
}

Console.WriteLine("Protocol");
var ws = KpaProtocol.ParseWs("1204 014");
Check("WS parse", ws.HasValue && ws.Value.Watts == 1204 && Math.Abs(ws.Value.Swr - 1.4) < 1e-9);
var vi = KpaProtocol.ParseVi("513 061");
Check("VI parse", vi.HasValue && Math.Abs(vi.Value.Volts - 51.3) < 1e-9 && Math.Abs(vi.Value.Amps - 61) < 1e-9);
Check("fault parse hex", KpaProtocol.ParseFault("c1") == "C1" && KpaProtocol.FaultName("91").Contains("SWR"));
Check("AM parse", KpaProtocol.ParseAtuMode("I") == true && KpaProtocol.ParseAtuMode("B") == false);
Check("FR format", KpaProtocol.SetFrequencyKhz(14_205_400) == "^FR14205;");
Check("BN format", KpaProtocol.SetBand(10) == "^BN10;");
Check("PW answers are exact", KpaProtocol.Answers("^PWF0100", "PWF") && !KpaProtocol.Answers("^PWF0100", "PW"));
Check("letter fault codes match", KpaProtocol.Answers("^FLC1", "FL") && KpaProtocol.ParseFault(KpaProtocol.Payload("^FLC1", "FL")) == "C1");
Check("ATU letter replies match", KpaProtocol.Answers("^AMI", "AM") && KpaProtocol.Answers("^AMB", "AM") && !KpaProtocol.Answers("^AMI", "AN"));
var sb = new System.Text.StringBuilder("junk;^WS0000 010;^OS1");
var frames = KpaProtocol.ExtractFrames(sb);
Check("framing keeps partial tail", frames.Count == 1 && frames[0] == "^WS0000 010" && sb.ToString() == "^OS1");
Check("band plan", BandPlan.FromHz(14_205_000) == 5 && BandPlan.FromHz(50_100_000) == 10 && BandPlan.FromHz(26_000_000) == -1);
Check("MAC normalise", PluginConfig.NormalizeMac("54-10-ec-14-75-2") == "54:10:EC:14:75:02");
var magic = NetTools.BuildMagic("54:10:EC:14:75:22");
Check("magic packet", magic.Length == 102 && magic[0] == 0xFF && magic[6] == 0x54 && magic[101] == 0x22);
var badCfg = new PluginConfig { PollMs = 10, Transport = "tcp" }.Validate();
Check("config validate", badCfg.Count == 2);
Check("control order: unknown dropped, repeats removed, missing appended",
    PluginConfig.NormalizeControlOrder("band, ANT,band,atu") == "band,atu,power,operate,tunestby,antenna,atutune,fan");
Check("control order default is complete", PluginConfig.NormalizeControlOrder(null) == PluginConfig.DefaultControlOrder);
if (badCfg.Count != 2) Console.WriteLine("::warning::config errors: " + string.Join(" | ", badCfg));
Check("default config is valid", new PluginConfig().Validate().Count == 0);

Console.WriteLine("TuneGuard");
{
    var t0 = DateTime.UtcNow;
    var g = new TuneGuard();
    Check("arm from OPERATE asks STANDBY", g.Arm("tci", true, t0) == TuneAction.SendStandby);
    Check("carrier → active", g.Step(true, true, false, false, true, TimeSpan.FromSeconds(30), t0) == TuneAction.None && g.Phase == TunePhase.Active);
    Check("amp forced back to OPERATE is re-held", g.Step(true, true, true, false, true, TimeSpan.FromSeconds(30), t0) == TuneAction.SendStandby);
    Check("tune ends → restoring", g.Step(false, false, false, false, true, TimeSpan.FromSeconds(30), t0) == TuneAction.None && g.Phase == TunePhase.Restoring);
    Check("after settle → OPERATE", g.Step(false, false, false, false, true, TimeSpan.FromSeconds(30), t0.AddMilliseconds(400)) == TuneAction.SendOperate && g.Phase == TunePhase.Idle);

    var g2 = new TuneGuard();
    g2.Arm("tci", false, t0);
    g2.Step(true, true, false, false, true, TimeSpan.FromSeconds(30), t0);
    g2.Step(false, false, false, false, true, TimeSpan.FromSeconds(30), t0);
    Check("was STANDBY → stays STANDBY", g2.Step(false, false, false, false, true, TimeSpan.FromSeconds(30), t0.AddSeconds(1)) == TuneAction.None);

    var g3 = new TuneGuard();
    g3.Arm("tci", null, t0);
    Check("unknown prior → no restore", g3.Step(false, false, null, false, true, TimeSpan.FromSeconds(30), t0) == TuneAction.None);

    var g4 = new TuneGuard();
    g4.Arm("manual", true, t0);
    Check("manual waits for carrier", g4.Step(false, false, false, false, true, TimeSpan.FromSeconds(30), t0.AddSeconds(5)) == TuneAction.None);
    Check("manual timeout restores", g4.Step(false, false, false, false, true, TimeSpan.FromSeconds(30), t0.AddSeconds(31)) == TuneAction.SendOperate);

    var g5 = new TuneGuard();
    g5.Arm("tci", true, t0);
    Check("fault blocks restore", g5.Step(false, false, false, true, true, TimeSpan.FromSeconds(30), t0) == TuneAction.None);
}

Console.WriteLine("No antenna? check");
{
    var t0 = new DateTime(2026, 10, 29, 4, 0, 0, DateTimeKind.Utc);
    var m = new NoAntennaMonitor { AutoLearn = false };
    DateTime Feed(NoAntennaMonitor mon, DateTime from, double dbm, double seconds)
    {
        var t = from;
        for (var i = 0; i < seconds * 4; i++) { t = t.AddMilliseconds(250); mon.AddLevel(dbm, t); }
        return t;
    }
    m.SetBand("40m", t0);
    Check("no level yet → no data", m.Snapshot(t0.AddSeconds(5)).State == NoAntState.NoData);
    var t = Feed(m, t0, -78, 5);
    Check("normal level on 40m → ok", m.Snapshot(t).State == NoAntState.Ok);
    t = Feed(m, t, -119, 4);
    Check("-119 dBm on 40m (limit -105) → NO ANTENNA?", m.Snapshot(t).State == NoAntState.Low && m.Snapshot(t).Limit == -105);
    m.SetTx(true, t);
    Feed(m, t, -20, 1);
    Check("key up while warned → TX warning, TX readings ignored", m.Snapshot(t).State == NoAntState.Tx && m.Snapshot(t).Dbm == -119);
    m.SetTx(false, t);
    Check("DUMMY OK silences", m.SetDummy(true).Ok);
    t = Feed(m, t, -119, 3);
    Check("dummy state shown", m.Snapshot(t).State == NoAntState.Dummy);
    t = Feed(m, t, -103, 4);
    Check("hysteresis: -103 (limit -105) still low", m.Snapshot(t).State == NoAntState.Dummy);
    t = Feed(m, t, -80, 4);
    Check("normal level → ok and DUMMY OK forgotten", m.Snapshot(t).State == NoAntState.Ok && !m.SetDummy(true).Ok);
    m.SetBand("6m", t);
    Check("6m has no check by default", m.Snapshot(t).State == NoAntState.NoCheck);

    m.SetBand("80m", t);
    t = Feed(m, t, -78, 4);
    Check("learn with antenna: limit = level − 8", m.LearnCurrent(true, t).Ok && m.Bands["80m"].Limit == -86 && m.Bands["80m"].Source == "learned-floor");
    t = Feed(m, t, -119, 4);
    Check("learn without antenna: limit = level + 8", m.LearnCurrent(false, t).Ok && m.Bands["80m"].Limit == -111 && m.Bands["80m"].Source == "learned-equipment");
    Check("typed limit kept", m.SetManual("20m", -100) && m.Bands["20m"].Source == "manual" && !m.SetManual("20m", -10));

    // Auto-learn: moves at most 1 dB a minute, never learns a lost antenna.
    var a = new NoAntennaMonitor { AutoLearn = true };
    var ta = t0;
    a.SetBand("40m", ta);
    ta = Feed(a, ta, -80, 60);                                   // 1 minute of normal noise
    Check("auto-learn starts slowly (≤ 1 dB/min from -105)", a.Bands["40m"].Source == "auto" && a.Bands["40m"].Limit >= -105 && a.Bands["40m"].Limit <= -103);
    ta = Feed(a, ta, -80, 30 * 60);                              // 30 more minutes
    Check("auto-learn reaches floor − 8 (-88)", a.Bands["40m"].Limit == -88);
    ta = Feed(a, ta, -119, 5);
    Check("antenna lost after learning → warning (not learned)", a.Snapshot(ta).State == NoAntState.Low);
    ta = Feed(a, ta, -119, 10 * 60);
    Check("10 min with no antenna: limit unchanged, still warning", a.Bands["40m"].Limit == -88 && a.Snapshot(ta).State == NoAntState.Low);
}

Console.WriteLine("Controller against a fake KPA1500 over UDP");
using (var amp = new FakeAmp())
{
    var events = new EventLog(500);
    var radio = new FakeRadio();
    await using var ctl = new AmpController(NullLogger.Instance, events);
    ctl.AttachRadio(radio);
    var cfg = PluginConfig.Sanitize(new PluginConfig
    {
        Host = "127.0.0.1", NetPort = amp.Port, PollMs = 50, AmpMac = "54:10:EC:14:75:22",
        TciEnabled = false, AutoTuneDetect = true,
    });
    ctl.Start(cfg);

    Check("connects and sees OPERATE", await WaitFor(() => ctl.State.Power == "on" && ctl.State.Operate == true));
    Check("telemetry read", await WaitFor(() => ctl.State.Volts is > 51.2 and < 51.4 && ctl.State.AtuInline == true && ctl.State.FanMinimum == 1));
{ var st = ctl.State; Console.WriteLine($"::notice::state power={st.Power} conn={st.Connection} err={st.ConnectionError} volts={st.Volts} atu={st.AtuInline} fanMin={st.FanMinimum} ant={st.Antenna}"); }
    Check("^FR sent for Zeus frequency", await WaitFor(() => amp.Freq == 14205));
    Check("amp frequency read back while receiving (^FR)", await WaitFor(() => ctl.State.AmpFreqKhz == 14205));
    Check("no ^BN by default", !amp.Received.Any(c => c.StartsWith("^BN") && c.Length == 6));

    radio.SetFrequency(14_210_000);
    Check("^FR follows a 5 kHz move", await WaitFor(() => amp.Freq == 14210));

    // Standby-on-Tune via Zeus mode.
    radio.SetMode("TUNE");
    Check("TUNE → amp STANDBY", await WaitFor(() => amp.Mode == 0));
    radio.SetMox(true);
    var r1 = await ctl.SetOperateAsync(true, CancellationToken.None);
    Check("OPERATE refused during TUNE", r1.Status == 409);
    var r2 = await ctl.SetAtuInlineAsync(false, CancellationToken.None);
    Check("ATU bypass refused under RF", r2.Status == 409);
    radio.SetFrequency(14_250_000);
    await Task.Delay(300);
    Check("no ^FR while transmitting", amp.Freq == 14210);
    radio.SetMox(false);
    radio.SetMode("USB");
    Check("TUNE end → OPERATE restored", await WaitFor(() => amp.Mode == 1));
    Check("deferred ^FR sent after unkey", await WaitFor(() => amp.Freq == 14250));
    amp.Freq = 7100;   // another program moves the amp
    Check("another controller detected", await WaitFor(() => ctl.OtherController, 6));
    Check("our frequency put back", await WaitFor(() => amp.Freq == 14250, 6));

    // Started in STANDBY → stays in STANDBY.
    amp.Mode = 0;
    await WaitFor(() => ctl.State.Operate == false);
    radio.SetMode("TUNE");
    await Task.Delay(300);
    radio.SetMode("USB");
    await Task.Delay(600);
    Check("tune from STANDBY leaves STANDBY", amp.Mode == 0);
    amp.Fwd = 9;
    Check("drive in STANDBY = power passing through (bypass)", await WaitFor(() => ctl.Drive is { Watts: 9, Source: "bypass" }));
    amp.Fwd = 0;
    await WaitFor(() => ctl.State.ForwardW == 0);

    // Controls.
    amp.Mode = 1;
    await WaitFor(() => ctl.State.Operate == true);
    Check("ATU bypass", (await ctl.SetAtuInlineAsync(false, CancellationToken.None)).Ok && await WaitFor(() => amp.Atu == 'B'));
    Check("antenna next", (await ctl.SetAntennaAsync(0, CancellationToken.None)).Ok && await WaitFor(() => amp.Ant == 2));
    Check("fan minimum", (await ctl.SetFanMinimumAsync(3, CancellationToken.None)).Ok && await WaitFor(() => amp.FanMin == 3));
    amp.Fault = 0xC1;
    Check("letter fault shown (C1)", await WaitFor(() => ctl.State.FaultCode == "C1"));
    amp.Fault = 0x91;
    Check("fault shown", await WaitFor(() => ctl.State.FaultCode == "91"));
    Check("fault clear", (await ctl.ClearFaultAsync(CancellationToken.None)).Ok && await WaitFor(() => ctl.State.FaultCode == "00"));
    Check("ATU tune command", (await ctl.StartTuneAsync(CancellationToken.None)).Ok && await WaitFor(() => amp.Tuning));
    Check("band refused while ATU tunes", (await ctl.SetBandAsync("40m", CancellationToken.None)).Status == 409);
    await ctl.CancelTuneAsync(CancellationToken.None);
    await WaitFor(() => ctl.State.Tuning == false);

    // Power off / on (WoL to loopback is harmless).
    Check("power off", (await ctl.SetPowerAsync(false, CancellationToken.None)).Ok && await WaitFor(() => amp.Asleep));
    Check("asleep → no reply", await WaitFor(() => ctl.State.Power == "unknown", 10));
    Check("power on accepted", (await ctl.SetPowerAsync(true, CancellationToken.None)).Status == 202);
    Check("power on confirmed", await WaitFor(() => ctl.State.Power == "on", 8));
    Check("never keyed: no MOX command exists", !amp.Received.Any(c => c.StartsWith("TX") || c.StartsWith("^TX")));
}

Console.WriteLine("TCI parsing");
{
    var notes = new List<string>();
    await using var tci = new TciClient("127.0.0.1", 1, 0, (k, m) => notes.Add(m));
    var changes = 0;
    tci.Changed += () => changes++;
    tci.Ingest("tune:0,true;trx:1,true;");
    Check("TCI tune for RX0 seen, other RX ignored", changes == 1);
    tci.Ingest("vfo:0,0,14205000;vfo:0,1,14225000;vfo:1,0,7100000;");
    Check("TCI VFO A/B parsed, RX1 ignored", changes == 2);
    tci.Ingest("tx_sensors:0,0,9.0,0.4,1.1;");
    Check("TCI tx_sensors not reported while not connected/transmitting", tci.RadioFwdW is null);
    tci.TestSetConnected(true);
    tci.Ingest("trx:0,false;tune:0,true;tx_sensors:0,0,9.0,0.4,1.1;");
    Check("TCI drive during TUNE (trx stays false in Zeus)", tci.RadioFwdW == 9.0);
    tci.Ingest("tx_power:12.5;");
    Check("TCI tx_power accepted as drive backup", tci.RadioFwdW == 12.5);
    tci.Ingest("tune:0,false;tx_sensors:0,0,0.0,0.0,1.0;");
    Check("TCI drive 0 when Zeus unkeys", tci.RadioFwdW == 0);
    tci.TestSetConnected(false);
    changes = 2;
    tci.Ingest("split_enable:0,true;");
    Check("TCI split flag parsed", changes == 3);
    var levels = new List<double>();
    tci.RxLevel += levels.Add;
    tci.Ingest("rx_channel_sensors:0,0,-119;rx_smeter:0,0,-118;rx_channel_sensors:0,1,-50;rx_channel_sensors:1,0,-40;");
    Check("TCI receive level (Zeus rx_channel_sensors / rx_smeter, RX1 ch 0 only)", levels.SequenceEqual(new[] { -119.0, -118.0 }));
    levels.Clear();
    tci.Ingest("rx_channel_sensors_ex:0,0,-100.0,-110.5,-60.0;rx_channel_sensors:0,0,-90;");
    Check("Thetis rx_channel_sensors_ex average preferred", levels.SequenceEqual(new[] { -110.5 }));
}

Console.WriteLine($"\n{passed} passed, {failures} failed");
return failures == 0 ? 0 : 1;
