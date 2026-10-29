// SPDX-License-Identifier: GPL-2.0-or-later
//
// No-hardware tests. Run:  dotnet run --project tests/FlexKnob.Tests -c Release
// Exit code 0 = all passed. A pretend knob sends clicks and presses; a
// pretend Zeus checks that the right TCI commands and radio calls arrive and
// answers like Zeus would; a pretend KPA1500 feature answers the amp calls.
// Never keys a transmitter (there is none).

using FlexKnob.Tests;
using Zeus.Community.FlexKnob;

var failures = 0;
var passed = 0;
void Check(string name, bool ok)
{
    if (ok) { passed++; Console.WriteLine("  ok   " + name); }
    else
    {
        failures++;
        Console.WriteLine("  FAIL " + name);
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            Console.WriteLine($"::error title=Test failed on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}::{name}");
    }
}
async Task<bool> WaitFor(Func<bool> cond, double seconds = 4)
{
    var end = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < end) { if (cond()) return true; await Task.Delay(20); }
    return cond();
}

Console.WriteLine("Knob messages");
Check("U = one click up", KnobCatalog.Parse("U") == ("turn", 1, ""));
Check("D05 = five clicks down", KnobCatalog.Parse("D05") == ("turn", -5, ""));
Check("U00 counts as one", KnobCatalog.Parse("U00") == ("turn", 1, ""));
Check("S / C / L = knob press tap / double / long",
    KnobCatalog.Parse("S").Event == "knob_tap" && KnobCatalog.Parse("C").Event == "knob_double" && KnobCatalog.Parse("L").Event == "knob_long");
Check("X2C = button 2 double, X3L = button 3 long",
    KnobCatalog.Parse("X2C").Event == "b2_double" && KnobCatalog.Parse("x3l").Event == "b3_long");
Check("unknown messages ignored", KnobCatalog.Parse("F0304").Kind == "none" && KnobCatalog.Parse("X4S").Kind == "none");
Check("every default function exists", KnobCatalog.DefaultTable.Values.All(KnobCatalog.ActionMap.ContainsKey) &&
    KnobCatalog.DefaultTable.Count == 12 && KnobCatalog.EventIds.All(KnobCatalog.DefaultTable.ContainsKey));

Console.WriteLine("Settings");
{
    var bad = new KnobConfig
    {
        Port = "LPT1", Speed = 99, AfStep = 0, ReturnS = -5, TciPort = 70000, StepSource = "x", OwnStepHz = 7,
        Table = new() { ["knob_tap"] = "self_destruct", ["b9_tap"] = "mute", ["b1_tap"] = "vfoa_nope", ["b2_tap"] = "lock" },
        Custom = new() { ["b3_tap"] = "rm -rf /" },
    };
    Check("validate reports bad port / TCI port / custom", bad.Validate().Count == 3);
    var s = KnobConfig.Sanitize(bad);
    Check("sanitize: numbers clamped, bad port → auto", s.Port == "auto" && s.Speed == 8 && s.AfStep == 1 && s.ReturnS == 0 && s.TciPort == 40001);
    Check("sanitize: unknown functions and events dropped, good one kept",
        s.Table["knob_tap"] == "mode_af_toggle" && !s.Table.ContainsKey("b9_tap") && s.Table["b2_tap"] == "lock" && s.Custom.Count == 0);
    Check("sanitize: step source / step", s.StepSource == "zeus" && s.OwnStepHz == 100);
    Check("default settings are valid", new KnobConfig().Validate().Count == 0);
    Check("port names accepted", KnobConfig.PortOk("COM7") && KnobConfig.PortOk("/dev/ttyACM0") && KnobConfig.PortOk("/dev/cu.usbmodem1421") && KnobConfig.PortOk(""));
    Check("custom TCI command accepted", KnobConfig.Sanitize(new KnobConfig { Custom = new() { ["b1_tap"] = "rx_bin_enable:0,true;" } }).Custom["b1_tap"] == "rx_bin_enable:0,true;");
}

Console.WriteLine("TCI state");
{
    var t = new TciState();
    t.Ingest("volume:-12;mute:true;agc_gain:0,60;vfo:0,1,14250000;lock:0,false;protocol:ExpertSDR3,2.0;");
    Check("bare values (volume, mute)", t.Get("volume") == "-12" && t.GetBool("mute") == true);
    Check("indexed values (agc_gain:0, lock:0)", t.Get("agc_gain:0") == "60" && t.GetBool("lock:0") == false);
    Check("two-index values (vfo:0,1)", TciState.AsLong(t.Get("vfo:0,1")) == 14_250_000);
    t.Ingest("nb_enable:0,true;");
    Check("NB reply filed under both names", t.GetBool("rx_nb_enable:0") == true);
}

Console.WriteLine("Step and band");
Check("round to step", KnobController.RoundToStep(14_200_260, 500) == 14_200_500 && KnobController.RoundToStep(14_200_240, 500) == 14_200_000);
Check("band up from 20 m is 17 m", KnobCatalog.Bands[KnobCatalog.NextBand(14_200_000, 1)].Name == "17m");
Check("band down from 160 m wraps to 6 m", KnobCatalog.Bands[KnobCatalog.NextBand(1_840_000, -1)].Name == "6m");
Check("between bands: up goes to the next band above", KnobCatalog.Bands[KnobCatalog.NextBand(12_000_000, 1)].Name == "20m");
Check("server address → loopback", ZeusWeb.PickLoopback(new[] { "http://[::]:6060" }) == "http://127.0.0.1:6060" &&
    ZeusWeb.PickLoopback(new[] { "https://0.0.0.0:6061", "http://0.0.0.0:6060" }) == "http://127.0.0.1:6060");

Console.WriteLine("Knob with a pretend Zeus, radio and KPA1500");
await using (var zeus = new FakeZeus())
{
    await zeus.StartAsync();
    var log = new EventLog(500);
    var radio = new FakeRadio();
    var web = new ZeusWeb(() => new KnobConfig());
    web.UseServices(zeus.Services);
    Check("Zeus web address found from the server", await WaitFor(() => web.BaseUrl == zeus.Url) && web.BaseSource == "Zeus server");
    Check("Zeus toolbar step read (500 Hz)", await WaitFor(() => web.StepHz == 500, 6));

    var cfg = new KnobConfig { TciPort = zeus.Port, TuneTimeoutS = 0 };
    await using var tci = new ZeusTci("127.0.0.1", zeus.Port, 0, (k, m) => log.Add(k, m));
    Check("TCI connects", await WaitFor(() => tci.Connected));
    Check("on connect the knob's values are asked for", await WaitFor(() => zeus.Got("volume") && zeus.Got("agc_gain:0") && zeus.Got("mute")));
    Check("answers are filed", await WaitFor(() => tci.State.Get("volume") == "-20" && tci.State.Get("agc_gain:0") == "60"));

    var amp = new AmpLink(() => web.BaseUrl, () => FakeZeus.AmpId);
    var knob = new KnobController(cfg, log, () => tci, amp, () => radio, () => radio, web);
    var port = new FakeKnobPort();
    await using var ser = new KnobSerial("FAKE", knob.Post, (k, m) => log.Add(k, m), open: _ => port, find: () => null);
    knob.Leds = ser.Send;

    Check("knob connects; LEDs set for VFO A (I000;)", await WaitFor(() => ser.Connected) && await WaitFor(() => port.Saw("I000;")));

    port.Feed("U;");
    Check("one click up → VFO A +500 Hz (Zeus's step)", await WaitFor(() => radio.FrequencyHz == 14_200_500));
    Check("first messages from the knob are shown in the event log", await WaitFor(() => log.Snapshot().Any(e => e.Message == "Knob sends: U;")));
    port.Feed("D03;");
    Check("fast spin down (D03 × speed 2 = 6 steps) → -3 kHz", await WaitFor(() => radio.FrequencyHz == 14_197_500));
    port.Feed("U;U;U;U;");
    Check("four quick clicks all arrive", await WaitFor(() => radio.FrequencyHz == 14_199_500));

    port.Feed("X1S;");
    Check("button 1 tap → knob now AF volume, LED 1 on (I100;)", await WaitFor(() => knob.Mode == "af") && await WaitFor(() => port.LastLeds == "I100;"));
    port.Feed("U;");
    Check("AF mode: click up → volume -19 dB", await WaitFor(() => zeus.Got("volume:-19")));
    port.Feed("D02;");
    Check("AF mode: two clicks down → volume -21 dB", await WaitFor(() => zeus.Got("volume:-21")));
    Check("panel line shows the toggle back to VFO A", knob.Short("b1_tap") == ">VFO A");
    port.Feed("X1S;");
    Check("button 1 tap again → back to VFO A (I000;)", await WaitFor(() => knob.Mode == "vfoa") && await WaitFor(() => port.LastLeds == "I000;"));

    port.Feed("X1C;");
    Check("button 1 double → AGC gain, LED 2 on (I010;)", await WaitFor(() => knob.Mode == "agc") && await WaitFor(() => port.LastLeds == "I010;"));
    port.Feed("U03;");
    Check("AGC mode: 3 clicks up → agc_gain 63", await WaitFor(() => zeus.Got("agc_gain:0,63")));
    port.Feed("L;");
    Check("knob long press → VFO A", await WaitFor(() => knob.Mode == "vfoa"));

    port.Feed("C;");
    Check("knob double press → mute ON (mute:true)", await WaitFor(() => zeus.Got("mute:true")));
    port.Feed("C;");
    Check("again → mute OFF (asks Zeus first)", await WaitFor(() => zeus.Got("mute:false")));

    port.Feed("X2S;");
    Check("button 2 tap → NR on (rx_nr_enable:0,true)", await WaitFor(() => zeus.Got("rx_nr_enable:0,true")));

    port.Feed("X2C;");
    Check("button 2 double → step larger (1 kHz)", await WaitFor(() => knob.Step.Hz == 1000 && knob.Step.Source == "knob"));
    var before = radio.FrequencyHz;
    port.Feed("U;");
    Check("VFO A now moves 1 kHz per click", await WaitFor(() => radio.FrequencyHz == KnobController.RoundToStep(before + 1000, 1000)));
    port.Feed("X2L;");
    port.Feed("X2L;");
    Check("button 2 long twice → 500 Hz then 250 Hz", await WaitFor(() => knob.Step.Hz == 250));

    radio.Set(14_200_000);
    port.Feed("X3C;");
    Check("button 3 double → band up: 17 m (18.120 MHz)", await WaitFor(() => radio.FrequencyHz == 18_120_000));
    port.Feed("X3L;");
    Check("button 3 long → band down: back to 14.200 MHz (remembered)", await WaitFor(() => radio.FrequencyHz == 14_200_000));

    // lock
    zeus.SetTci("lock:0", "true");
    tci.State.Put("lock:0", "true");
    var f0 = radio.FrequencyHz;
    port.Feed("U;");
    await Task.Delay(300);
    Check("VFO locked in Zeus → turning ignored", radio.FrequencyHz == f0);
    tci.State.Put("lock:0", "false");
    zeus.SetTci("lock:0", "false");

    // safe TUNE
    zeus.ClearTci();
    zeus.AmpOperate = true;
    port.Feed("X3S;");
    Check("TUNE: amp put in STANDBY first (Standby-on-Tune armed)", await WaitFor(() => zeus.AmpCalls.Contains("tune-guard True")));
    Check("TUNE: radio TUNE only after the amp confirmed STANDBY", await WaitFor(() => zeus.Got("tune:0,true")) && !zeus.AmpOperate);
    Check("TUNE running is shown", knob.KnobTuneActive);
    port.Feed("X3S;");
    Check("TUNE pressed again → stopped (tune:0,false)", await WaitFor(() => zeus.Got("tune:0,false")) && await WaitFor(() => !knob.KnobTuneActive));

    zeus.ClearTci();
    zeus.AmpOperate = true;
    zeus.AmpIgnoresStandby = true;
    var r1 = await knob.TryAsync("b3_tap", default);
    Check("TUNE refused when the amp never confirms STANDBY - nothing keyed",
        !r1.Ok && r1.Message.Contains("did not confirm", StringComparison.Ordinal) && !zeus.Got("tune:0,true") && zeus.AmpCalls.Contains("tune-guard False"));
    zeus.AmpIgnoresStandby = false;

    zeus.NoAnt = "low";
    var r2 = await knob.TryAsync("b3_tap", default);
    Check("TUNE refused on a no-antenna warning", !r2.Ok && r2.Message.Contains("no antenna", StringComparison.Ordinal) && !zeus.Got("tune:0,true"));
    zeus.NoAnt = "ok";

    zeus.AmpAnswers = false;
    var r3 = await knob.TryAsync("b3_tap", default);
    Check("TUNE refused when the KPA1500 feature can't be reached", !r3.Ok && !zeus.Got("tune:0,true"));
    zeus.AmpAnswers = true;

    zeus.AmpOperate = false;
    var r4 = await knob.TryAsync("b3_tap", default);
    Check("TUNE with the amp already in STANDBY starts at once", r4.Ok && r4.Message.Contains("already in STANDBY", StringComparison.Ordinal) && await WaitFor(() => zeus.Got("tune:0,true")));
    await knob.TryAsync("b3_tap", default);
    Check("…and stops", await WaitFor(() => zeus.Got("tune:0,false")));

    // amplifier functions
    var cfg2 = KnobConfig.Sanitize(cfg);
    cfg2.Table["b1_tap"] = "amp_operate";
    cfg2.Table["b1_double"] = "ant_toggle";
    cfg2.Table["b1_long"] = "dummy_ok";
    cfg2.Table["b2_tap"] = "sot_toggle";
    cfg2.Table["b2_double"] = "atu_tune";
    cfg2.Table["b2_long"] = "custom";
    cfg2.Custom["b2_long"] = "rx_bin_enable:0,true;";
    cfg2.Table["knob_tap"] = "mox";
    cfg2.ReturnS = 1;
    cfg2.TuneTimeoutS = 1;
    knob.ApplyConfig(cfg2);
    zeus.AmpOperate = false;
    port.Feed("X1S;");
    Check("amp OPERATE / STANDBY → OPERATE", await WaitFor(() => zeus.AmpCalls.Contains("operate True")));
    port.Feed("X1C;");
    Check("amp antenna 1 → 2", await WaitFor(() => zeus.AmpCalls.Contains("antenna 2")));
    port.Feed("X1L;");
    Check("DUMMY OK", await WaitFor(() => zeus.AmpCalls.Contains("dummy True")));
    zeus.NoAnt = "ok";
    port.Feed("X2S;");
    Check("Standby-on-Tune off (other KPA1500 settings kept)", await WaitFor(() => zeus.AmpCalls.Contains("config sot=False host=192.168.1.50")));
    zeus.StandbyOnTune = true;
    port.Feed("X2C;");
    Check("amp ATU tune", await WaitFor(() => zeus.AmpCalls.Contains("atu tune")));
    port.Feed("X2L;");
    Check("custom TCI command sent", await WaitFor(() => zeus.Got("rx_bin_enable:0,true")));

    zeus.NoAnt = "low";
    port.Feed("S;");
    await Task.Delay(400);
    Check("MOX refused on a no-antenna warning", radio.MoxSets.IsEmpty);
    zeus.NoAnt = "ok";
    port.Feed("S;");
    Check("MOX on", await WaitFor(() => radio.Mox));
    port.Feed("S;");
    Check("MOX off", await WaitFor(() => !radio.Mox));

    // watchdogs
    zeus.ClearTci();
    zeus.AmpOperate = false;
    var r5 = await knob.TryAsync("b3_tap", default);
    Check("TUNE with a 1 s limit starts", r5.Ok);
    Check("…and stops by itself", await WaitFor(() => zeus.Got("tune:0,false"), 4) && !knob.KnobTuneActive);

    cfg2.Table["b3_double"] = "mode_drive_toggle";
    knob.ApplyConfig(cfg2);
    port.Feed("X3C;");
    Check("TX drive mode (all LEDs on)", await WaitFor(() => knob.Mode == "drive") && await WaitFor(() => port.LastLeds == "I111;"));
    port.Feed("D;");
    Check("drive 50 → 49", await WaitFor(() => zeus.Got("drive:0,49")));
    Check("back to VFO A after 1 s without use", await WaitFor(() => knob.Mode == "vfoa", 4) && await WaitFor(() => port.LastLeds == "I000;"));

    var ev = log.Snapshot().Select(e => e.Message).ToList();
    Check("presses are logged", ev.Any(m => m.Contains("Knob Button 2: double tap", StringComparison.Ordinal)));

    // reconnect
    ser.SetPort("FAKE2");
    Check("knob port change reconnects", await WaitFor(() => port.Disposed) && await WaitFor(() => ser.Connected && ser.Device == "FAKE2"));

    await knob.DisposeAsync();
    amp.Dispose();
    await web.DisposeAsync();
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? $"ALL TESTS PASSED ({passed}/{passed})" : $"SOME TESTS FAILED ({failures} of {passed + failures})");
return failures == 0 ? 0 : 1;
