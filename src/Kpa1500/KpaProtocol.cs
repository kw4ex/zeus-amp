// SPDX-License-Identifier: GPL-2.0-or-later
//
// Pure KPA1500 command formatting and response parsing. No I/O, no Zeus types,
// so every rule here is unit-testable without hardware.
//
// Source: Elecraft KPA1500 Programming Reference (firmware 01.47).
//   Frame:     every command and response is "^" + letters + data + ";".
//   GET:       "^WS;"            SET: "^OS1;"   (SET normally has no reply)
//   Responses: upper case, same letters as the GET that caused them.
//   Pacing:    no flow control, tiny input buffer — one GET in flight at a time.

using System.Globalization;

namespace Zeus.Community.Kpa1500;

public static class KpaProtocol
{
    /// <summary>Band numbers used by ^BN (identical to K3 numbering).</summary>
    public static readonly IReadOnlyList<string> BandNames = new[]
    {
        "160m", "80m", "60m", "40m", "30m", "20m", "17m", "15m", "12m", "10m", "6m",
    };

    public static string? BandName(int code) =>
        code >= 0 && code < BandNames.Count ? BandNames[code] : null;

    public static int BandCode(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        for (var i = 0; i < BandNames.Count; i++)
            if (string.Equals(BandNames[i], name.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // ── formatting ──────────────────────────────────────────────────────────

    public static string Get(string letters) => "^" + letters + ";";

    public static string SetBand(int code)
    {
        if (code < 0 || code >= BandNames.Count) throw new ArgumentOutOfRangeException(nameof(code));
        return "^BN" + code.ToString("00", CultureInfo.InvariantCulture) + ";";
    }

    public static string SetOperate(bool operate) => operate ? "^OS1;" : "^OS0;";
    public static string SetPower(bool on) => on ? "^ON1;" : "^ON0;";
    public static string SetAntenna(int ant)
    {
        if (ant is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(ant));
        return "^AN" + ant.ToString(CultureInfo.InvariantCulture) + ";";
    }
    /// <summary>ATU in-line / bypass for the current band and antenna (^AMI; / ^AMB;), as the proven bridge used.</summary>
    public static string SetAtuMode(bool inline) => inline ? "^AMI;" : "^AMB;";
    public const string AntennaNext = "^AN0;";
    public const string EnableWakeOnLan = "^WL1;";
    public static string SetFanMinimum(int speed)
    {
        if (speed is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(speed));
        return "^FC" + speed.ToString(CultureInfo.InvariantCulture) + ";";
    }

    /// <summary>^AMI; / ^AMB; → true = in-line, false = bypassed.</summary>
    public static bool? ParseAtuMode(string payload) => payload.Trim().ToUpperInvariant() switch
    {
        "I" => true,
        "B" => false,
        _ => null,
    };
    public const string ClearFault = "^FLC;";
    public const string StartTune = "^FT;";
    public const string CancelTune = "^FE;";

    /// <summary>
    /// ^FRfffff; — operating frequency in kHz from an external program. Lets the
    /// ATU memory-recall the stored match for the bin without transmitting.
    /// </summary>
    public static string SetFrequencyKhz(long hz)
    {
        var khz = hz / 1000;
        if (khz < 1 || khz > 99999) throw new ArgumentOutOfRangeException(nameof(hz));
        return "^FR" + khz.ToString("00000", CultureInfo.InvariantCulture) + ";";
    }

    // ── framing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Pull complete ";"-terminated frames out of an accumulating buffer. Text
    /// before the first "^" in a frame (line noise, a bare ";" null reply) is
    /// dropped; partial trailing data stays in <paramref name="buffer"/>.
    /// </summary>
    public static List<string> ExtractFrames(System.Text.StringBuilder buffer)
    {
        var frames = new List<string>();
        while (true)
        {
            var text = buffer.ToString();
            var end = text.IndexOf(';');
            if (end < 0)
            {
                // Guard against a runaway buffer if the line carries garbage.
                if (buffer.Length > 4096) buffer.Clear();
                return frames;
            }
            var raw = text[..end];
            buffer.Remove(0, end + 1);
            var caret = raw.LastIndexOf('^');
            if (caret < 0) continue; // null command echo or noise
            frames.Add(raw[caret..].ToUpperInvariant());
        }
    }

    /// <summary>
    /// True when <paramref name="frame"/> (no trailing ";") answers a GET for
    /// <paramref name="letters"/>. The character after the letters must not be
    /// another letter, so "^PWF" never matches a request for "^PW".
    /// </summary>
    public static bool Answers(string frame, string letters)
    {
        var prefix = "^" + letters;
        if (!frame.StartsWith(prefix, StringComparison.Ordinal)) return false;
        if (frame.Length == prefix.Length) return true;
        // Commands whose reply value can start with a letter: ^AMI; / ^AMB; (ATU in-line /
        // bypass), ^SIT; / ^SIA; (ATU side relay) and ^FLC1; / ^FLB0; (fault codes B0, C0, C1, F0).
        if (LetterValueCommands.Contains(letters)) return true;
        return !char.IsLetter(frame[prefix.Length]);
    }

    private static readonly HashSet<string> LetterValueCommands = new(StringComparer.Ordinal) { "AM", "SI", "FL" };

    /// <summary>The data portion after "^LETTERS", trimmed.</summary>
    public static string Payload(string frame, string letters) =>
        frame.Length > letters.Length + 1 ? frame[(letters.Length + 1)..].Trim() : "";

    // ── parsing ─────────────────────────────────────────────────────────────

    public static int? Int(string payload)
    {
        var s = payload.Trim();
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static bool? Bool01(string payload) => payload.Trim() switch
    {
        "0" => false,
        "1" => true,
        _ => null,
    };

    /// <summary>^WSwwww sss; → forward watts and SWR (sss in tenths: 014 = 1.4).</summary>
    public static (int Watts, double Swr)? ParseWs(string payload)
    {
        var parts = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;
        var w = Int(parts[0]);
        var s = Int(parts[1]);
        if (w is null || s is null) return null;
        return (w.Value, s.Value / 10.0);
    }

    /// <summary>^VIvvv iii; → PA volts (tenths: 513 = 51.3 V) and amps (whole).</summary>
    public static (double Volts, double Amps)? ParseVi(string payload)
    {
        var parts = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;
        var v = Int(parts[0]);
        var a = Int(parts[1]);
        if (v is null || a is null) return null;
        return (v.Value / 10.0, a.Value);
    }

    /// <summary>SWR fields reported in tenths (^SB, ^SW): 123 → 12.3.</summary>
    public static double? Tenths(string payload) => Int(payload) is { } v ? v / 10.0 : null;

    /// <summary>^FLhh; → two-character fault code (hex-style, e.g. "00", "C1").</summary>
    public static string? ParseFault(string payload)
    {
        var s = payload.Trim().ToUpperInvariant();
        return s.Length == 2 && s.All(Uri.IsHexDigit) ? s : null;
    }

    public static readonly IReadOnlyDictionary<string, string> FaultNames = new Dictionary<string, string>
    {
        ["00"] = "No fault",
        ["10"] = "Watchdog reset",
        ["20"] = "PA current too high",
        ["40"] = "Temperature too high",
        ["60"] = "Input power too high",
        ["61"] = "Gain too low",
        ["70"] = "Invalid frequency",
        ["80"] = "50 V supply out of range",
        ["81"] = "5 V supply out of range",
        ["82"] = "10 V supply out of range",
        ["83"] = "12 V supply out of range",
        ["84"] = "-12 V supply out of range",
        ["85"] = "LPF board supply not detected",
        ["90"] = "Reflected power too high",
        ["91"] = "SWR very high (antenna?)",
        ["92"] = "ATU found no match",
        ["B0"] = "PA dissipation too high",
        ["C0"] = "Forward power too high",
        ["C1"] = "Forward power too high for ATU setting",
        ["F0"] = "Gain too high",
    };

    public static string FaultName(string? code) =>
        code is null ? "Unknown" : FaultNames.TryGetValue(code, out var n) ? n : "Fault " + code;
}
