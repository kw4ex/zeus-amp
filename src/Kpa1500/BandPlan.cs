// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Community.Kpa1500;

/// <summary>Frequency → KPA1500 band code (^BN numbering). -1 = not an amp band.</summary>
public static class BandPlan
{
    private static readonly (long LoHz, long HiHz, int Code)[] Edges =
    {
        (1_800_000, 2_000_000, 0),    // 160m
        (3_500_000, 4_000_000, 1),    // 80m
        (5_250_000, 5_450_000, 2),    // 60m (covers channelised and WRC-15 allocations)
        (7_000_000, 7_300_000, 3),    // 40m
        (10_100_000, 10_150_000, 4),  // 30m
        (14_000_000, 14_350_000, 5),  // 20m
        (18_068_000, 18_168_000, 6),  // 17m
        (21_000_000, 21_450_000, 7),  // 15m
        (24_890_000, 24_990_000, 8),  // 12m
        (28_000_000, 29_700_000, 9),  // 10m
        (50_000_000, 54_000_000, 10), // 6m
    };

    public static int FromHz(long hz)
    {
        foreach (var (lo, hi, code) in Edges)
            if (hz >= lo && hz <= hi) return code;
        return -1;
    }
}
