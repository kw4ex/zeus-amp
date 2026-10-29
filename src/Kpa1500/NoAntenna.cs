// SPDX-License-Identifier: GPL-2.0-or-later
//
// "No antenna?" check — ported from the KPA1500/Thetis bridge (v2.19) and
// extended with per-band learning.
//
// Zeus reports the receiver's level in dBm over TCI. While receiving, the
// middle value (median) of the last 2.5 s is compared with a per-band limit.
// Below the limit → "NO ANTENNA?". The warning clears only when the level is
// 3 dB above the limit again. The first 1.5 s after a band change or after
// transmitting is ignored while the receiver settles; readings during
// transmit are ignored and the last receive judgement is kept.
//
// A band's limit comes from one of:
//   default            built-in (bridge values)
//   manual             typed by the operator — never changed automatically
//   learned-floor      "Learn, antenna connected": level − BelowFloorDb
//   learned-equipment  "Learn, antenna disconnected": level + AboveEquipDb
//   auto               auto-learn while listening (antenna connected)
//
// Auto-learn tracks a low percentile of the band's receive level while the
// check is NOT warning, and keeps the limit BelowFloorDb under it. The limit
// moves at most 1 dB per minute either way, so a sudden loss of signal (an
// antenna coming off) still trips the warning rather than being learned, and
// one long strong signal can't drag the limit up. Pure logic:
// time is passed in, so it is fully testable.

namespace Zeus.Community.Kpa1500;

public sealed class BandNoise
{
    public int? Limit { get; set; }
    public string Source { get; set; } = "default";
    public double? Floor { get; set; }
    public DateTime? LearnedAt { get; set; }

    public BandNoise Copy() => (BandNoise)MemberwiseClone();
}

public enum NoAntState { Off, NoData, Settling, Ok, Low, Tx, Dummy, NoCheck }

public sealed record NoAntSnapshot(
    NoAntState State, string? Band, double? Dbm, int? Limit, string Source, double? Floor, bool LevelSeen);

public sealed class NoAntennaMonitor
{
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(2.5);
    public static readonly TimeSpan Stale = TimeSpan.FromSeconds(3);
    public const double Hysteresis = 3.0;
    public const int MinLimit = -150;
    public const int MaxLimit = -30;

    /// <summary>Bridge defaults. null = no check on that band.</summary>
    public static readonly IReadOnlyDictionary<string, int?> Defaults = new Dictionary<string, int?>
    {
        ["160m"] = -105, ["80m"] = -105, ["60m"] = -105, ["40m"] = -105,
        ["30m"] = -112, ["20m"] = -112,
        ["17m"] = -115, ["15m"] = -115, ["12m"] = -115, ["10m"] = -115,
        ["6m"] = null,
    };

    // Auto-learn tuning.
    private const int AutoHistory = 120;                    // window medians kept per band (~5 min)
    private const int AutoMinSamples = 12;                  // ~30 s of listening before a first value
    private static readonly TimeSpan AutoStep = TimeSpan.FromSeconds(2.5);
    private const double AutoRatePerMinute = 1.0;           // dB the limit may move per minute

    private readonly object _gate = new();
    private readonly Dictionary<string, BandNoise> _bands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<double>> _history = new(StringComparer.Ordinal);
    private readonly List<(DateTime T, double Dbm)> _samples = new();
    private readonly HashSet<string> _dummy = new(StringComparer.Ordinal);

    private string? _band;
    private DateTime _quietUntil = DateTime.MinValue;
    private DateTime _lastSample = DateTime.MinValue;
    private DateTime _lastAutoStep = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _autoChanged = new(StringComparer.Ordinal);
    private bool _low;
    private bool _tx;
    private double? _judged;   // median the current judgement is based on (kept through TX)

    public NoAntennaMonitor() => ResetAll();

    /// <summary>Raised (caller's thread) when a band's limit changed, so it can be saved.</summary>
    public event Action? LimitsChanged;

    // ── configuration ───────────────────────────────────────────────────────

    public bool Enabled { get; set; } = true;
    public bool AutoLearn { get; set; } = true;
    public int BelowFloorDb { get; set; } = 8;
    public int AboveEquipDb { get; set; } = 8;

    public IReadOnlyDictionary<string, BandNoise> Bands
    {
        get { lock (_gate) return _bands.ToDictionary(kv => kv.Key, kv => kv.Value.Copy()); }
    }

    public void Load(IReadOnlyDictionary<string, BandNoise>? stored)
    {
        lock (_gate)
        {
            ResetAll();
            if (stored is null) return;
            foreach (var (band, v) in stored)
            {
                if (!_bands.ContainsKey(band) || v is null) continue;
                var b = v.Copy();
                if (b.Limit is { } l && (l < MinLimit || l > MaxLimit)) b.Limit = Defaults[band];
                if (b.Source is not ("default" or "manual" or "learned-floor" or "learned-equipment" or "auto")) b.Source = "default";
                _bands[band] = b;
            }
        }
    }

    public void ResetAll()
    {
        lock (_gate)
        {
            _bands.Clear();
            foreach (var (band, lim) in Defaults) _bands[band] = new BandNoise { Limit = lim, Source = "default" };
            _history.Clear();
            _autoChanged.Clear();
            _dummy.Clear();
            _low = false;
        }
    }

    // ── inputs ──────────────────────────────────────────────────────────────

    /// <summary>The band the receiver is on (KpaProtocol band name), or null when outside a ham band.</summary>
    public void SetBand(string? band, DateTime now)
    {
        lock (_gate)
        {
            if (band == _band) return;
            _band = band;
            _samples.Clear();
            _low = false;
            _judged = null;
            _quietUntil = now + Settle;
        }
    }

    public void SetTx(bool tx, DateTime now)
    {
        lock (_gate)
        {
            if (tx == _tx) return;
            _tx = tx;
            if (!tx)
            {
                _samples.Clear();
                _quietUntil = now + Settle;
            }
        }
    }

    /// <summary>A receive-level reading in dBm.</summary>
    public void AddLevel(double dbm, DateTime now)
    {
        if (double.IsNaN(dbm) || dbm < -200 || dbm > 50) return;
        bool changed = false;
        lock (_gate)
        {
            _lastSample = now;
            if (_tx || now < _quietUntil || _band is null) return;
            _samples.Add((now, dbm));
            _samples.RemoveAll(s => now - s.T > Window);
            var med = MedianLocked();
            if (med is null) return;
            _judged = med;
            var b = _bands[_band];
            if (b.Limit is { } lim)
            {
                if (!_low && med < lim) _low = true;
                else if (_low && med >= lim + Hysteresis) _low = false;
                if (!_low && med >= lim + Hysteresis) _dummy.Remove(_band); // normal level back: forget DUMMY OK
            }
            else _low = false;

            if (AutoLearn && Enabled && !_low && now - _lastAutoStep >= AutoStep)
            {
                _lastAutoStep = now;
                changed = AutoLearnStepLocked(_band, med.Value, now);
            }
        }
        if (changed) LimitsChanged?.Invoke();
    }

    private bool AutoLearnStepLocked(string band, double med, DateTime now)
    {
        var b = _bands[band];
        if (b.Source is "manual" or "learned-equipment") return false;
        if (b.Limit is null) return false;                     // check off on this band
        if (!_history.TryGetValue(band, out var h)) _history[band] = h = new List<double>();
        h.Add(med);
        if (h.Count > AutoHistory) h.RemoveAt(0);
        if (h.Count < AutoMinSamples) return false;

        var sorted = h.OrderBy(x => x).ToList();
        var floor = sorted[(int)Math.Floor((sorted.Count - 1) * 0.1)];   // 10th percentile
        b.Floor = Math.Round(floor, 1);
        var target = Clamp((int)Math.Round(floor - BelowFloorDb));
        var current = b.Limit.Value;

        // Move at most AutoRatePerMinute dB per minute either way: a sudden loss
        // of signal (an antenna coming off) trips the warning instead of being
        // learned, and a long strong signal on frequency can't drag the limit up.
        var last = _autoChanged.TryGetValue(band, out var t) ? t : now - TimeSpan.FromMinutes(1);
        var allowed = (int)Math.Floor((now - last).TotalMinutes * AutoRatePerMinute);
        var next = allowed < 1 ? current
            : target > current ? Math.Min(target, current + allowed)
            : Math.Max(target, current - allowed);

        var firstLearn = b.Source == "default";
        if (next == current && !firstLearn) return false;
        b.Limit = next;
        if (firstLearn) b.Source = "auto";
        b.LearnedAt = now;
        if (next != current) _autoChanged[band] = now;
        return true;
    }

    // ── operator actions ────────────────────────────────────────────────────

    public (bool Ok, string Message) LearnCurrent(bool antennaConnected, DateTime now)
    {
        bool ok;
        string msg;
        lock (_gate)
        {
            if (_band is null) return (false, "The radio is not on a ham band.");
            if (_tx) return (false, "Not while transmitting.");
            var med = MedianLocked();
            if (med is null || _samples.Count < 3 || now - _samples[0].T < TimeSpan.FromSeconds(2))
                return (false, "Not enough receive readings yet — wait 3 seconds on this band and try again.");
            var b = _bands[_band];
            if (antennaConnected)
            {
                b.Limit = Clamp((int)Math.Round(med.Value - BelowFloorDb));
                b.Floor = Math.Round(med.Value, 1);
                b.Source = "learned-floor";
                _history.Remove(_band);
                msg = $"{_band}: noise floor {med:0} dBm, limit set to {b.Limit} dBm ({BelowFloorDb} dB below).";
            }
            else
            {
                b.Limit = Clamp((int)Math.Round(med.Value + AboveEquipDb));
                b.Source = "learned-equipment";
                msg = $"{_band}: equipment noise {med:0} dBm, limit set to {b.Limit} dBm ({AboveEquipDb} dB above).";
            }
            b.LearnedAt = now;
            _low = false;
            ok = true;
        }
        LimitsChanged?.Invoke();
        return (ok, msg);
    }

    /// <summary>Learn the given band from a measured level (used by the band scan).</summary>
    public void LearnFloor(string band, double dbm, DateTime now)
    {
        lock (_gate)
        {
            if (!_bands.TryGetValue(band, out var b) || b.Source == "manual") return;
            b.Limit = Clamp((int)Math.Round(dbm - BelowFloorDb));
            b.Floor = Math.Round(dbm, 1);
            b.Source = "learned-floor";
            b.LearnedAt = now;
            _history.Remove(band);
        }
        LimitsChanged?.Invoke();
    }

    /// <summary>Set a band's limit by hand. null = no check on that band.</summary>
    public bool SetManual(string band, int? limit)
    {
        lock (_gate)
        {
            if (!_bands.TryGetValue(band, out var b)) return false;
            if (limit is { } l && (l < MinLimit || l > MaxLimit)) return false;
            b.Limit = limit;
            b.Source = "manual";
            b.LearnedAt = null;
            _history.Remove(band);
            if (band == _band) _low = false;
        }
        LimitsChanged?.Invoke();
        return true;
    }

    public void ResetBand(string band)
    {
        lock (_gate)
        {
            if (!Defaults.TryGetValue(band, out var lim)) return;
            _bands[band] = new BandNoise { Limit = lim, Source = "default" };
            _history.Remove(band);
            if (band == _band) _low = false;
        }
        LimitsChanged?.Invoke();
    }

    public void RestoreDefaults()
    {
        ResetAll();
        LimitsChanged?.Invoke();
    }

    public (bool Ok, string Message) SetDummy(bool on)
    {
        lock (_gate)
        {
            if (_band is null) return (false, "The radio is not on a ham band.");
            if (on)
            {
                if (!_low) return (false, "DUMMY OK is not needed — there is no warning on this band.");
                _dummy.Add(_band);
                return (true, $"DUMMY OK on {_band}: warning silenced until a normal level returns.");
            }
            _dummy.Remove(_band);
            return (true, $"DUMMY OK cleared on {_band}.");
        }
    }

    /// <summary>Median of the current window — used by the band scan.</summary>
    public double? CurrentMedian(DateTime now)
    {
        lock (_gate)
        {
            if (_tx || now < _quietUntil || _samples.Count == 0) return null;
            if (now - _samples[0].T < TimeSpan.FromSeconds(2)) return null;
            return MedianLocked();
        }
    }

    // ── output ──────────────────────────────────────────────────────────────

    public NoAntSnapshot Snapshot(DateTime now)
    {
        lock (_gate)
        {
            var seen = _lastSample != DateTime.MinValue;
            if (!Enabled) return new(NoAntState.Off, _band, _judged, null, "", null, seen);
            if (_band is null) return new(NoAntState.NoCheck, null, _judged, null, "", null, seen);
            var b = _bands[_band];
            NoAntState st;
            if (b.Limit is null) st = NoAntState.NoCheck;
            else if (!seen || (!_tx && now - _lastSample > Stale)) st = NoAntState.NoData;
            else if (_judged is null) st = NoAntState.Settling;
            else if (_low && _dummy.Contains(_band)) st = NoAntState.Dummy;
            else if (_low) st = _tx ? NoAntState.Tx : NoAntState.Low;
            else st = NoAntState.Ok;
            return new(st, _band, _judged, b.Limit, b.Source, b.Floor, seen);
        }
    }

    private double? MedianLocked()
    {
        if (_samples.Count == 0) return null;
        var v = _samples.Select(s => s.Dbm).OrderBy(x => x).ToList();
        var n = v.Count;
        return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2.0;
    }

    private static int Clamp(int v) => Math.Clamp(v, MinLimit, MaxLimit);
}
