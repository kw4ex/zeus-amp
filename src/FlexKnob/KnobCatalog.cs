// SPDX-License-Identifier: GPL-2.0-or-later
//
// FlexRadio FlexControl tuning knob — what the knob sends, what turning it
// can do, and what each press can do. Ported from the FlexControl section of
// the KPA1500 Thetis bridge (kpa1500_thetis_bridge.py v2.31); the Thetis CAT
// commands are replaced by their Zeus equivalents:
//
//   VFO A            Zeus plugin SDK (SetFrequencyAsync), Zeus's tuning step
//   everything else  Zeus TCI server (volume, mute, agc_gain, rit_offset ...)
//   amplifier        the Elecraft KPA1500 Zeus feature's own HTTP API
//
// The knob's serial protocol (USB, 9600 bit/s):
//   knob -> PC:  U; D;          one click up / down
//                U03; D05;      several clicks at once when spun fast
//                S; C; L;       knob press: tap / double tap / long
//                X1S; X2C; X3L; buttons 1-3: tap / double tap / long
//   PC -> knob:  Ixyz;          LEDs 1-3 on (1) / off (0)

using System.Text.RegularExpressions;

namespace Zeus.Community.FlexKnob;

public enum ModeKind { Vfo, TciVfo, Value, StepList }

/// <summary>What turning the knob does.</summary>
/// <param name="Leds">LED pattern for buttons 1-3 ("100" = LED 1 on).</param>
/// <param name="Get">TCI query that makes Zeus report the value (Value modes).</param>
/// <param name="Key">State key the reply is filed under (see <see cref="TciState"/>).</param>
/// <param name="Set">TCI set command; {0} is the new value.</param>
/// <param name="PerClick">How much one click changes the value.</param>
public sealed record KnobMode(string Id, string Name, string Short, string Leds, ModeKind Kind,
    string Get = "", string Key = "", string Set = "", int Min = 0, int Max = 0, int PerClick = 1);

/// <summary>What a press does. <paramref name="How"/>:
///   mode:X / toggle_mode:X / cycle   change what turning does
///   tci_toggle:get|key|set           read a true/false value, set the other
///   tci:cmd                          one TCI command
///   step:up|down|zeus  band:up|down  mox  tune  amp:...  custom
/// </summary>
public sealed record KnobAction(string Id, string Name, string Group, string How, string Short);

public static class KnobCatalog
{
    public const int Vid = 0x2192;
    public const int Pid = 0x0010;
    public const int Baud = 9600;

    public static readonly IReadOnlyList<(string Id, string Name)> Events = new[]
    {
        ("knob_tap", "Knob press: tap"), ("knob_double", "Knob press: double tap"), ("knob_long", "Knob press: long"),
        ("b1_tap", "Button 1: tap"), ("b1_double", "Button 1: double tap"), ("b1_long", "Button 1: long"),
        ("b2_tap", "Button 2: tap"), ("b2_double", "Button 2: double tap"), ("b2_long", "Button 2: long"),
        ("b3_tap", "Button 3: tap"), ("b3_double", "Button 3: double tap"), ("b3_long", "Button 3: long"),
    };

    public static readonly IReadOnlySet<string> EventIds = Events.Select(e => e.Id).ToHashSet();

    public static string EventName(string id) => Events.FirstOrDefault(e => e.Id == id).Name ?? id;

    public static readonly IReadOnlyList<KnobMode> Modes = new[]
    {
        new KnobMode("vfoa", "VFO A tuning", "VFO A", "000", ModeKind.Vfo),
        new KnobMode("vfob", "VFO B tuning", "VFO B", "101", ModeKind.TciVfo, Get: "vfo:{rx},1", Key: "vfo:{rx},1", Set: "vfo:{rx},1,{0}"),
        new KnobMode("af", "AF volume (master)", "AF", "100", ModeKind.Value, "volume", "volume", "volume:{0}", -50, 20),
        new KnobMode("agc", "AGC gain (RX1)", "AGC", "010", ModeKind.Value, "agc_gain:{rx}", "agc_gain:{rx}", "agc_gain:{rx},{0}", 30, 90),
        new KnobMode("rit", "RIT (10 Hz per click)", "RIT", "001", ModeKind.Value, "rit_offset:{rx}", "rit_offset:{rx}", "rit_offset:{rx},{0}", -9990, 9990, 10),
        new KnobMode("xit", "XIT (10 Hz per click)", "XIT", "011", ModeKind.Value, "xit_offset:{rx}", "xit_offset:{rx}", "xit_offset:{rx},{0}", -9990, 9990, 10),
        new KnobMode("step", "Tuning step size", "Step", "110", ModeKind.StepList),
        new KnobMode("drive", "TX drive", "Drive", "111", ModeKind.Value, "drive:{rx}", "drive:{rx}", "drive:{rx},{0}", 0, 100),
    };

    public static readonly IReadOnlyDictionary<string, KnobMode> ModeMap = Modes.ToDictionary(m => m.Id);

    /// <summary>Order the "next mode" press steps through.</summary>
    public static readonly IReadOnlyList<string> Cycle = new[] { "vfoa", "af", "agc" };

    public static readonly IReadOnlyList<KnobAction> Actions = new[]
    {
        new KnobAction("none", "(nothing)", "", "", "-"),
        new KnobAction("mode_af_toggle", "Knob: AF volume <-> VFO A (toggle)", "Knob mode", "toggle_mode:af", ""),
        new KnobAction("mode_agc_toggle", "Knob: AGC gain <-> VFO A (toggle)", "Knob mode", "toggle_mode:agc", ""),
        new KnobAction("mode_vfob_toggle", "Knob: VFO B <-> VFO A (toggle)", "Knob mode", "toggle_mode:vfob", ""),
        new KnobAction("mode_rit_toggle", "Knob: RIT <-> VFO A (toggle)", "Knob mode", "toggle_mode:rit", ""),
        new KnobAction("mode_xit_toggle", "Knob: XIT <-> VFO A (toggle)", "Knob mode", "toggle_mode:xit", ""),
        new KnobAction("mode_step_toggle", "Knob: tuning step size <-> VFO A (toggle)", "Knob mode", "toggle_mode:step", ""),
        new KnobAction("mode_drive_toggle", "Knob: TX drive <-> VFO A (toggle)", "Knob mode", "toggle_mode:drive", ""),
        new KnobAction("mode_cycle", "Knob: next mode (VFO A > AF volume > AGC gain)", "Knob mode", "cycle", ""),
        new KnobAction("mode_vfoa", "Knob: back to VFO A tuning", "Knob mode", "mode:vfoa", ""),
        new KnobAction("mute", "Mute on / off", "Zeus", "tci_toggle:mute|mute|mute:{0}", "Mute"),
        new KnobAction("lock", "VFO lock on / off", "Zeus", "tci_toggle:lock:{rx}|lock:{rx}|lock:{rx},{0}", "Lock"),
        new KnobAction("split", "Split on / off", "Zeus", "tci_toggle:split_enable:{rx}|split_enable:{rx}|split_enable:{rx},{0}", "Split"),
        new KnobAction("rit_onoff", "RIT on / off", "Zeus", "tci_toggle:rit_enable:{rx}|rit_enable:{rx}|rit_enable:{rx},{0}", "RIT"),
        new KnobAction("rit_clear", "RIT clear (to 0)", "Zeus", "tci:rit_offset:{rx},0", "RIT clr"),
        new KnobAction("xit_onoff", "XIT on / off", "Zeus", "tci_toggle:xit_enable:{rx}|xit_enable:{rx}|xit_enable:{rx},{0}", "XIT"),
        new KnobAction("xit_clear", "XIT clear (to 0)", "Zeus", "tci:xit_offset:{rx},0", "XIT clr"),
        new KnobAction("nr", "Noise reduction (NR) on / off", "Zeus", "tci_toggle:|rx_nr_enable:{rx}|rx_nr_enable:{rx},{0}", "NR"),
        new KnobAction("nb", "Noise blanker (NB) on / off", "Zeus", "tci_toggle:rx_nb_enable:{rx}|rx_nb_enable:{rx}|rx_nb_enable:{rx},{0}", "NB"),
        new KnobAction("anf", "Auto notch (ANF) on / off", "Zeus", "tci_toggle:|rx_anf_enable:{rx}|rx_anf_enable:{rx},{0}", "ANF"),
        new KnobAction("step_up", "Tuning step: larger", "Zeus", "step:up", "Step +"),
        new KnobAction("step_down", "Tuning step: smaller", "Zeus", "step:down", "Step -"),
        new KnobAction("step_zeus", "Tuning step: follow Zeus again", "Zeus", "step:zeus", "Step Z"),
        new KnobAction("band_up", "Band up", "Zeus", "band:up", "Band +"),
        new KnobAction("band_down", "Band down", "Zeus", "band:down", "Band -"),
        new KnobAction("mox", "MOX on / off (TRANSMITS!)", "Zeus", "mox", "MOX!"),
        new KnobAction("radio_tune", "TUNE the safe way (amp STANDBY first) / stop", "KPA1500 amplifier", "tune", "TUNE"),
        new KnobAction("amp_operate", "Amp OPERATE / STANDBY", "KPA1500 amplifier", "amp:operate", "OPR/STBY"),
        new KnobAction("atu_tune", "Amp ATU tune", "KPA1500 amplifier", "amp:atu_tune", "ATU tune"),
        new KnobAction("ant_toggle", "Amp antenna 1 / 2", "KPA1500 amplifier", "amp:antenna", "ANT"),
        new KnobAction("dummy_ok", "DUMMY OK (no-antenna warning)", "KPA1500 amplifier", "amp:dummy", "DUMMY OK"),
        new KnobAction("sot_toggle", "Standby-on-Tune on / off", "KPA1500 amplifier", "amp:sot", "SoT"),
        new KnobAction("custom", "Custom TCI command (type it below)", "Custom", "custom", "C:"),
    };

    public static readonly IReadOnlyDictionary<string, KnobAction> ActionMap = Actions.ToDictionary(a => a.Id);

    /// <summary>
    /// The bridge's default table. Button 2 tap was VOX on / off in the bridge;
    /// Zeus has no VOX control an outside program can reach, so it is noise
    /// reduction here.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultTable = new Dictionary<string, string>
    {
        ["knob_tap"] = "mode_af_toggle", ["knob_double"] = "mute", ["knob_long"] = "mode_vfoa",
        ["b1_tap"] = "mode_af_toggle", ["b1_double"] = "mode_agc_toggle", ["b1_long"] = "mute",
        ["b2_tap"] = "nr", ["b2_double"] = "step_up", ["b2_long"] = "step_down",
        ["b3_tap"] = "radio_tune", ["b3_double"] = "band_up", ["b3_long"] = "band_down",
    };

    /// <summary>Zeus's own tuning-step choices (toolbar Step menu).</summary>
    public static readonly IReadOnlyList<int> Steps = new[]
    {
        1, 10, 50, 100, 250, 500, 1000, 5000, 9000, 10000, 100000, 250000, 1000000,
    };

    /// <summary>Amateur bands for Band up / down: name, low edge, high edge, default frequency.</summary>
    public static readonly IReadOnlyList<(string Name, long Lo, long Hi, long Home)> Bands = new[]
    {
        ("160m", 1_800_000L, 2_000_000L, 1_840_000L), ("80m", 3_500_000L, 4_000_000L, 3_700_000L),
        ("60m", 5_330_000L, 5_410_000L, 5_357_000L), ("40m", 7_000_000L, 7_300_000L, 7_150_000L),
        ("30m", 10_100_000L, 10_150_000L, 10_120_000L), ("20m", 14_000_000L, 14_350_000L, 14_200_000L),
        ("17m", 18_068_000L, 18_168_000L, 18_120_000L), ("15m", 21_000_000L, 21_450_000L, 21_250_000L),
        ("12m", 24_890_000L, 24_990_000L, 24_940_000L), ("10m", 28_000_000L, 29_700_000L, 28_400_000L),
        ("6m", 50_000_000L, 54_000_000L, 50_150_000L),
    };

    /// <summary>Index into <see cref="Bands"/> for a frequency, or -1 when outside every band.</summary>
    public static int BandIndex(long hz)
    {
        for (var i = 0; i < Bands.Count; i++)
            if (hz >= Bands[i].Lo && hz <= Bands[i].Hi) return i;
        return -1;
    }

    /// <summary>
    /// The band after (dir = +1) or before (dir = -1) the frequency. A frequency
    /// between bands moves to the nearest band in that direction.
    /// </summary>
    public static int NextBand(long hz, int dir)
    {
        var i = BandIndex(hz);
        if (i >= 0) return ((i + dir) % Bands.Count + Bands.Count) % Bands.Count;
        if (dir > 0)
        {
            for (var j = 0; j < Bands.Count; j++) if (Bands[j].Lo > hz) return j;
            return 0;
        }
        for (var j = Bands.Count - 1; j >= 0; j--) if (Bands[j].Hi < hz) return j;
        return Bands.Count - 1;
    }

    private static readonly Regex TurnRe = new(@"^([UD])(\d{0,3})$", RegexOptions.CultureInvariant);
    private static readonly Regex ButtonRe = new(@"^X([1-3])([SCL])$", RegexOptions.CultureInvariant);

    private static string Press(char c) => c switch { 'S' => "tap", 'C' => "double", _ => "long" };

    /// <summary>"U03" → (turn, +3)   "D" → (turn, -1)   "X2C" → (event, b2_double)   other → (none).</summary>
    public static (string Kind, int Turn, string Event) Parse(string code)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        var m = TurnRe.Match(code);
        if (m.Success)
        {
            var n = m.Groups[2].Value.Length == 0 ? 1 : int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (n == 0) n = 1;
            return ("turn", m.Groups[1].Value == "U" ? n : -n, "");
        }
        if (code is "S" or "C" or "L") return ("event", 0, "knob_" + Press(code[0]));
        m = ButtonRe.Match(code);
        if (m.Success) return ("event", 0, $"b{m.Groups[1].Value}_{Press(m.Groups[2].Value[0])}");
        return ("none", 0, "");
    }

    /// <summary>A custom TCI command: name, optional ":args", optional ";".</summary>
    public static readonly Regex CustomRe = new(@"^[a-z_][a-z0-9_]{1,30}(:[^;]{0,60})?;?$", RegexOptions.CultureInvariant);

    /// <summary>Fill in the receiver number ("{rx}") of a catalog command.</summary>
    public static string Rx(string template, int rx) =>
        template.Replace("{rx}", rx.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
}
