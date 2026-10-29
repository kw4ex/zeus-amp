// SPDX-License-Identifier: GPL-2.0-or-later
//
// Operator settings, persisted through IPluginSettings. Every field has a safe
// default; Validate() rejects a bad POST outright and Sanitize() repairs a
// stored record (e.g. after an upgrade) rather than refusing to start.
// Defaults follow the earlier, hardware-proven Thetis bridge.

namespace Zeus.Community.Kpa1500;

public sealed class PluginConfig
{
    // ── connection ──
    public string Transport { get; set; } = "udp";       // "udp" | "serial"
    public string Host { get; set; } = "";
    public int NetPort { get; set; } = 1500;
    public string SerialPort { get; set; } = "";
    public int Baud { get; set; } = 38400;
    public bool AutoConnect { get; set; } = true;
    public int PollMs { get; set; } = 100;

    // ── remote power-on ──
    public string AmpMac { get; set; } = "";             // learned from ^MA; when the amp is on
    public int WolPort { get; set; } = 9;

    // ── frequency / band tracking ──
    public bool SendFrequency { get; set; } = true;      // ^FR: amp picks band + recalls ATU setting
    public int FrequencyStepKhz { get; set; } = 1;
    public bool BandFollow { get; set; }                 // also send ^BN on band change

    // ── behaviour ──
    public bool StandbyOnConnect { get; set; }
    public bool ConfirmPowerOff { get; set; } = true;
    public bool ConfirmPowerOn { get; set; }

    // ── standby on tune ──
    public bool StandbyOnTune { get; set; } = true;       // master switch
    public bool TciEnabled { get; set; }                  // tune/TX events from Zeus's TCI server
    public string TciHost { get; set; } = "127.0.0.1";
    public int TciPort { get; set; } = 40001;            // Zeus's default TCI port
    public int TciReceiver { get; set; }
    public bool AutoTuneDetect { get; set; } = true;      // also trigger when Zeus's mode reports TUNE
    public bool RestoreOperateAfterTune { get; set; } = true;
    public int TuneTimeoutS { get; set; } = 30;           // manual arm: wait this long for a carrier

    // ── "No antenna?" check (receive level from Zeus over TCI) ──
    public bool NoAntEnabled { get; set; } = true;
    public string NoAntAction { get; set; } = "warn";     // "warn" | "standby"
    public bool NoAntAutoLearn { get; set; } = true;      // learn each band's noise floor while listening
    public int NoAntBelowFloorDb { get; set; } = 8;       // limit = band noise floor − this
    public int NoAntAboveEquipDb { get; set; } = 8;       // limit = equipment noise (no antenna) + this

    // ── telemetry shown ──
    public bool ShowForward { get; set; } = true;
    public bool ShowSwr { get; set; } = true;
    public bool ShowReflected { get; set; } = true;
    public bool ShowInput { get; set; } = true;
    public bool ShowDissipation { get; set; }
    public bool ShowTemperature { get; set; } = true;
    public bool ShowVoltage { get; set; } = true;
    public bool ShowCurrent { get; set; } = true;
    public bool ShowFan { get; set; } = true;
    public bool ShowAmpFreq { get; set; } = true;
    public bool ShowRxLevel { get; set; } = true;
    public bool ShowAtuStatus { get; set; } = true;
    public bool ShowLog { get; set; }

    // ── controls shown ──
    public bool CtlPower { get; set; } = true;
    public bool CtlOperate { get; set; } = true;
    public bool CtlTuneGuard { get; set; } = true;
    public bool CtlClearFault { get; set; } = true;
    public bool CtlBand { get; set; } = true;
    public bool CtlAntenna { get; set; } = true;
    public bool CtlAtu { get; set; } = true;
    public bool CtlTune { get; set; } = true;
    public bool CtlFan { get; set; }

    /// <summary>Order of the controls at the top of the panel, comma separated.</summary>
    public string ControlOrder { get; set; } = DefaultControlOrder;
    public static readonly string[] ControlIds = { "power", "operate", "tunestby", "antenna", "atu", "atutune", "band", "fan" };
    public const string DefaultControlOrder = "power,operate,tunestby,antenna,atu,atutune,band,fan";

    /// <summary>Known ids only, no repeats, any missing ones appended in default order.</summary>
    public static string NormalizeControlOrder(string? order)
    {
        var list = new List<string>();
        foreach (var raw in (order ?? "").Split(','))
        {
            var id = raw.Trim().ToLowerInvariant();
            if (Array.IndexOf(ControlIds, id) >= 0 && !list.Contains(id)) list.Add(id);
        }
        foreach (var id in ControlIds) if (!list.Contains(id)) list.Add(id);
        return string.Join(',', list);
    }

    // ── display ──
    public bool Compact { get; set; }
    public string TempUnit { get; set; } = "C";          // "C" | "F"
    public int MeterMaxWatts { get; set; } = 1500;
    public double SwrWarn { get; set; } = 2.0;
    public int TempWarnC { get; set; } = 65;

    public PluginConfig Clone() => (PluginConfig)MemberwiseClone();

    /// <summary>"54-10-ec-14-75-2" / "54:10:EC:14:75:22" → "54:10:EC:14:75:22"; "" if not a MAC.</summary>
    public static string NormalizeMac(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var parts = text.Trim().Split(':', '-');
        if (parts.Length != 6) return "";
        var bytes = new List<string>(6);
        foreach (var p in parts)
        {
            if (p.Length is < 1 or > 2 || !p.All(Uri.IsHexDigit)) return "";
            bytes.Add(p.ToUpperInvariant().PadLeft(2, '0'));
        }
        return string.Join(':', bytes);
    }

    public List<string> Validate()
    {
        var e = new List<string>();
        if (Transport is not ("udp" or "serial")) e.Add("Connection must be udp or serial.");
        if ((Host ?? "").Length > 253) e.Add("Host name is too long.");
        if (NetPort is < 1 or > 65535) e.Add("Amp network port must be 1–65535.");
        if ((SerialPort ?? "").Length > 128) e.Add("Serial port name is too long.");
        if (Array.IndexOf(Transports.SupportedBauds, Baud) < 0) e.Add("Baud must be one the KPA1500 supports (4800–230400).");
        if (PollMs is < 50 or > 2000) e.Add("Poll interval must be 50–2000 ms.");
        if (!string.IsNullOrWhiteSpace(AmpMac) && NormalizeMac(AmpMac) == "") e.Add("MAC address must look like 54:10:EC:12:34:56.");
        if (WolPort is < 1 or > 65535) e.Add("Wake-on-LAN port must be 1–65535.");
        if (FrequencyStepKhz is < 1 or > 100) e.Add("Frequency step must be 1–100 kHz.");
        if ((ControlOrder ?? "").Length > 200) e.Add("Control order is too long.");
        if ((TciHost ?? "").Length > 253) e.Add("TCI host is too long.");
        if (TciEnabled && string.IsNullOrWhiteSpace(TciHost)) e.Add("Enter the TCI host (usually 127.0.0.1).");
        if (TciPort is < 1 or > 65535) e.Add("TCI port must be 1–65535.");
        if (TciReceiver is < 0 or > 7) e.Add("TCI receiver must be 0–7.");
        if (TuneTimeoutS is < 5 or > 120) e.Add("Tune timeout must be 5–120 s.");
        if (NoAntAction is not ("warn" or "standby")) e.Add("No-antenna action must be warn or standby.");
        if (NoAntBelowFloorDb is < 1 or > 30) e.Add("Margin below the noise floor must be 1–30 dB.");
        if (NoAntAboveEquipDb is < 1 or > 30) e.Add("Margin above equipment noise must be 1–30 dB.");
        if (TempUnit is not ("C" or "F")) e.Add("Temperature unit must be C or F.");
        if (MeterMaxWatts is < 100 or > 3000) e.Add("Meter full scale must be 100–3000 W.");
        if (double.IsNaN(SwrWarn) || SwrWarn is < 1.1 or > 10) e.Add("SWR warning must be 1.1–10.");
        if (TempWarnC is < 30 or > 100) e.Add("Temperature warning must be 30–100 °C.");
        return e;
    }

    public static PluginConfig Sanitize(PluginConfig? stored)
    {
        var c = stored?.Clone() ?? new PluginConfig();
        var d = new PluginConfig();
        c.Host = (c.Host ?? "").Trim();
        c.SerialPort = (c.SerialPort ?? "").Trim();
        c.TciHost = (c.TciHost ?? d.TciHost).Trim();
        c.AmpMac = NormalizeMac(c.AmpMac);
        if (c.Transport is not ("udp" or "serial")) c.Transport = d.Transport;
        if (c.Host.Length > 253) c.Host = "";
        if (c.NetPort is < 1 or > 65535) c.NetPort = d.NetPort;
        if (c.SerialPort.Length > 128) c.SerialPort = "";
        if (Array.IndexOf(Transports.SupportedBauds, c.Baud) < 0) c.Baud = d.Baud;
        c.PollMs = Math.Clamp(c.PollMs, 50, 2000);
        if (c.WolPort is < 1 or > 65535) c.WolPort = d.WolPort;
        c.FrequencyStepKhz = Math.Clamp(c.FrequencyStepKhz, 1, 100);
        if (c.TciHost.Length > 253 || c.TciHost.Length == 0) c.TciHost = d.TciHost;
        if (c.TciPort is < 1 or > 65535) c.TciPort = d.TciPort;
        c.TciReceiver = Math.Clamp(c.TciReceiver, 0, 7);
        c.TuneTimeoutS = Math.Clamp(c.TuneTimeoutS, 5, 120);
        if (c.TciPort == 6060) c.TciPort = d.TciPort;   // old wrong default (Zeus uses 40001)
        if (c.NoAntAction is not ("warn" or "standby")) c.NoAntAction = d.NoAntAction;
        c.NoAntBelowFloorDb = Math.Clamp(c.NoAntBelowFloorDb, 1, 30);
        c.NoAntAboveEquipDb = Math.Clamp(c.NoAntAboveEquipDb, 1, 30);
        if (c.TempUnit is not ("C" or "F")) c.TempUnit = d.TempUnit;
        c.MeterMaxWatts = Math.Clamp(c.MeterMaxWatts, 100, 3000);
        c.SwrWarn = double.IsNaN(c.SwrWarn) ? d.SwrWarn : Math.Clamp(c.SwrWarn, 1.1, 10);
        c.TempWarnC = Math.Clamp(c.TempWarnC, 30, 100);
        c.ControlOrder = NormalizeControlOrder(c.ControlOrder);
        return c;
    }
}
