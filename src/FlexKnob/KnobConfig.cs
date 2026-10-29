// SPDX-License-Identifier: GPL-2.0-or-later
//
// FlexKnob settings, stored with the Zeus plugin settings store. Every value
// is cleaned on load and on save, the way clean_knob() did in the bridge, so
// a damaged or old settings record can never break the feature.

using System.Text.RegularExpressions;

namespace Zeus.Community.FlexKnob;

public sealed class KnobConfig
{
    /// <summary>"auto" = find the FlexControl by its USB id; "" = knob off; else a port name.</summary>
    public string Port { get; set; } = "auto";
    public Dictionary<string, string> Table { get; set; } = new(KnobCatalog.DefaultTable);
    public Dictionary<string, string> Custom { get; set; } = new();

    /// <summary>Spin acceleration: 3 or more clicks at once are multiplied by this (1 = off).</summary>
    public int Speed { get; set; } = 2;
    /// <summary>AF volume change per click, dB.</summary>
    public int AfStep { get; set; } = 1;
    /// <summary>Go back to VFO A tuning after this many seconds without knob use (0 = never).</summary>
    public int ReturnS { get; set; }

    /// <summary>"zeus" = use the step chosen in Zeus's toolbar; "own" = <see cref="OwnStepHz"/>.</summary>
    public string StepSource { get; set; } = "zeus";
    public int OwnStepHz { get; set; } = 100;

    public string TciHost { get; set; } = "127.0.0.1";
    public int TciPort { get; set; } = 40001;
    public int TciRx { get; set; }

    /// <summary>Zeus's own web address, e.g. http://127.0.0.1:6060. Empty = found automatically.</summary>
    public string ZeusUrl { get; set; } = "";
    /// <summary>Feature id of the Elecraft KPA1500 Zeus feature.</summary>
    public string AmpPluginId { get; set; } = "com.kw4ex.zeus.kpa1500";

    /// <summary>Knob TUNE stops by itself after this many seconds (0 = never).</summary>
    public int TuneTimeoutS { get; set; } = 20;
    /// <summary>Refuse knob TUNE when the KPA1500 feature cannot be reached.</summary>
    public bool TuneNeedsAmp { get; set; } = true;

    private static readonly Regex PortRe = new(@"^(COM\d{1,3}|/dev/[A-Za-z0-9._\-/]{1,60})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex HostRe = new(@"^[A-Za-z0-9.\-:\[\]]{1,100}$", RegexOptions.CultureInvariant);
    private static readonly Regex IdRe = new(@"^[a-z][a-z0-9]*(\.[a-z0-9]+)+$", RegexOptions.CultureInvariant);

    public static bool PortOk(string? p) => p is "" or "auto" || (p is not null && PortRe.IsMatch(p));

    public static bool UrlOk(string? u) =>
        string.IsNullOrEmpty(u) ||
        (Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https") && uri.Query.Length == 0);

    /// <summary>Problems a panel save would have, in plain words (empty = fine).</summary>
    public List<string> Validate()
    {
        var e = new List<string>();
        if (!PortOk(Port)) e.Add("The knob port must be Auto, Off, or a port name like COM7 or /dev/ttyACM0.");
        if (!HostRe.IsMatch(TciHost ?? "")) e.Add("The TCI host must be an address like 127.0.0.1.");
        if (TciPort is < 1 or > 65535) e.Add("The TCI port must be 1-65535.");
        if (!UrlOk(ZeusUrl)) e.Add("The Zeus address must look like http://127.0.0.1:6060 (or be empty).");
        if (!IdRe.IsMatch(AmpPluginId ?? "")) e.Add("The KPA1500 feature id must look like com.kw4ex.zeus.kpa1500.");
        foreach (var (ev, c) in Custom ?? new())
            if (!string.IsNullOrWhiteSpace(c) && !KnobCatalog.CustomRe.IsMatch(c.Trim()))
                e.Add($"{KnobCatalog.EventName(ev)}: custom command must look like  name:args;  (for example rx_bin_enable:0,true;).");
        return e;
    }

    /// <summary>A complete, safe copy: unknown events / functions dropped, numbers clamped.</summary>
    public static KnobConfig Sanitize(KnobConfig? k)
    {
        k ??= new KnobConfig();
        var c = new KnobConfig();
        c.Port = PortOk(k.Port) ? k.Port : "auto";
        foreach (var (ev, act) in k.Table ?? new())
            if (KnobCatalog.EventIds.Contains(ev) && act is not null && KnobCatalog.ActionMap.ContainsKey(act))
                c.Table[ev] = act;
        foreach (var (ev, cmd) in k.Custom ?? new())
            if (KnobCatalog.EventIds.Contains(ev) && cmd is not null && KnobCatalog.CustomRe.IsMatch(cmd.Trim()))
                c.Custom[ev] = cmd.Trim();
        c.Speed = Math.Clamp(k.Speed, 1, 8);
        c.AfStep = Math.Clamp(k.AfStep, 1, 10);
        c.ReturnS = Math.Clamp(k.ReturnS, 0, 600);
        c.StepSource = k.StepSource == "own" ? "own" : "zeus";
        c.OwnStepHz = KnobCatalog.Steps.Contains(k.OwnStepHz) ? k.OwnStepHz : 100;
        c.TciHost = HostRe.IsMatch(k.TciHost ?? "") ? k.TciHost! : "127.0.0.1";
        c.TciPort = k.TciPort is >= 1 and <= 65535 ? k.TciPort : 40001;
        c.TciRx = Math.Clamp(k.TciRx, 0, 1);
        c.ZeusUrl = UrlOk(k.ZeusUrl) ? (k.ZeusUrl ?? "").TrimEnd('/') : "";
        c.AmpPluginId = IdRe.IsMatch(k.AmpPluginId ?? "") ? k.AmpPluginId! : "com.kw4ex.zeus.kpa1500";
        c.TuneTimeoutS = Math.Clamp(k.TuneTimeoutS, 0, 600);
        c.TuneNeedsAmp = k.TuneNeedsAmp;
        return c;
    }
}
