// SPDX-License-Identifier: GPL-2.0-or-later
//
// What Zeus last told us over TCI, filed by a short key:
//
//   "volume:-12;"          → key "volume"          value "-12"
//   "mute:true;"           → key "mute"            value "true"
//   "agc_gain:0,60;"       → key "agc_gain:0"      value "60"
//   "vfo:0,1,14200000;"    → key "vfo:0,1"         value "14200000"
//
// Zeus sends a value when it changes and when asked (a TCI "get" is the same
// command without the value). Waiters let a press ask Zeus for the current
// value and wait briefly for the answer — the bridge's cat_query().

using System.Collections.Concurrent;
using System.Globalization;

namespace Zeus.Community.FlexKnob;

public sealed class TciState
{
    private readonly ConcurrentDictionary<string, (string Value, long Ticks)> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _waiters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Commands whose leading numbers are not an index: they carry the value directly.</summary>
    private static readonly HashSet<string> NoIndex = new(StringComparer.OrdinalIgnoreCase)
    {
        "volume", "mute", "tx_power", "tx_forward_power", "protocol", "device", "ready", "start", "stop",
    };

    /// <summary>Commands with two index numbers (transceiver, channel).</summary>
    private static readonly HashSet<string> TwoIndex = new(StringComparer.OrdinalIgnoreCase)
    {
        "vfo", "vfo_lock", "rx_volume", "rx_channel_enable", "rx_channel_sensors", "rx_smeter",
    };

    /// <summary>Split "name:a,b,c" into its key and value.</summary>
    public static (string Key, string Value)? Split(string token)
    {
        token = token.Trim();
        if (token.Length == 0) return null;
        var colon = token.IndexOf(':');
        if (colon < 0) return (token.ToLowerInvariant(), "");
        var name = token[..colon].Trim().ToLowerInvariant();
        var args = token[(colon + 1)..].Split(',');
        int idx = NoIndex.Contains(name) ? 0 : TwoIndex.Contains(name) ? 2 : 1;
        if (args.Length <= idx) return (token.ToLowerInvariant(), "");   // a bare query echo
        for (var i = 0; i < idx; i++)
            if (!int.TryParse(args[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return (name, string.Join(",", args).Trim());
        var key = idx == 0 ? name : name + ":" + string.Join(",", args.Take(idx).Select(a => a.Trim()));
        return (key, string.Join(",", args.Skip(idx)).Trim());
    }

    /// <summary>One TCI text message (may hold several "...;" commands).</summary>
    public void Ingest(string message)
    {
        foreach (var raw in message.Split(';'))
        {
            var kv = Split(raw);
            if (kv is not { } p || p.Value.Length == 0) continue;
            Put(p.Key, p.Value);
            // Zeus answers the get for NB under its spec name; file the short name too.
            if (p.Key.StartsWith("nb_enable:", StringComparison.OrdinalIgnoreCase)) Put("rx_" + p.Key, p.Value);
            if (p.Key.StartsWith("nr_enable:", StringComparison.OrdinalIgnoreCase)) Put("rx_" + p.Key, p.Value);
            if (p.Key.StartsWith("anf_enable:", StringComparison.OrdinalIgnoreCase)) Put("rx_" + p.Key, p.Value);
        }
    }

    public void Put(string key, string value)
    {
        _values[key] = (value, Environment.TickCount64);
        if (_waiters.TryRemove(key, out var w)) w.TrySetResult(value);
    }

    public void Clear() => _values.Clear();

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v.Value : null;

    public static bool? AsBool(string? v) => v?.Trim().ToLowerInvariant() switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => null,
    };

    public static long? AsLong(string? v) =>
        double.TryParse(v?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)Math.Round(d) : null;

    public bool? GetBool(string key) => AsBool(Get(key));

    /// <summary>Wait (up to the timeout) for the next value Zeus files under the key.</summary>
    public Task<string> Next(string key)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _waiters.GetOrAdd(key, tcs).Task;
    }

    public void CancelWait(string key)
    {
        if (_waiters.TryRemove(key, out var w)) w.TrySetCanceled();
    }
}
