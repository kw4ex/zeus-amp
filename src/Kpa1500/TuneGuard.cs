// SPDX-License-Identifier: GPL-2.0-or-later
//
// Standby-on-Tune state machine. Pure logic (time is passed in) so it can be
// tested without an amplifier.
//
//   Idle ──Arm──▶ Armed ──RF up──▶ Active ──RF down──▶ Restoring ──settle──▶ Idle
//                   │                 ▲                    │
//                   │                 └────── re-keyed ────┘
//                   └── timeout / tune mode ends with no carrier ──▶ Idle
//
// Arm puts the amp in STANDBY (if it was in OPERATE) so the radio's tune
// carrier goes through barefoot. When the carrier has been gone for a short
// settle time, OPERATE is restored — only if the amp was in OPERATE when the
// guard armed, restore is enabled, and no fault is showing. While the guard is
// armed or active, an amp that is switched back to OPERATE is put back in
// STANDBY. The guard never keys the radio.

namespace Zeus.Community.Kpa1500;

public enum TunePhase { Idle, Armed, Active, Restoring }

public enum TuneAction { None, SendStandby, SendOperate }

public sealed class TuneGuard
{
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    public TunePhase Phase { get; private set; } = TunePhase.Idle;
    public string Trigger { get; private set; } = "";
    public bool RestoreOperate { get; private set; }
    public bool PriorKnown { get; private set; }

    private DateTime _armedAt;
    private DateTime _droppedAt;

    public static bool IsTuneMode(string? mode) =>
        mode is not null && mode.Contains("TUN", StringComparison.OrdinalIgnoreCase);

    /// <param name="trigger">"tci" (TCI tune event), "mode" (Zeus mode reports TUNE) or "manual" (panel button).</param>
    public TuneAction Arm(string trigger, bool? ampOperate, DateTime now)
    {
        if (Phase != TunePhase.Idle) return TuneAction.None;
        Phase = TunePhase.Armed;
        Trigger = trigger;
        RestoreOperate = ampOperate == true;
        PriorKnown = ampOperate is not null;
        _armedAt = now;
        return ampOperate == true ? TuneAction.SendStandby : TuneAction.None;
    }

    public void Cancel()
    {
        Phase = TunePhase.Idle;
        Trigger = "";
        RestoreOperate = false;
        PriorKnown = false;
    }

    public TuneAction Step(bool tx, bool tuneMode, bool? ampOperate, bool faultActive,
        bool restoreEnabled, TimeSpan timeout, DateTime now)
    {
        switch (Phase)
        {
            case TunePhase.Armed:
                if (tx) { Phase = TunePhase.Active; return Hold(ampOperate); }
                var noCarrier = Trigger == "manual" ? now - _armedAt >= timeout : !tuneMode;
                if (noCarrier) return Finish(restoreEnabled, faultActive);
                return Hold(ampOperate);

            case TunePhase.Active:
                if (!tx && (Trigger == "manual" || !tuneMode))
                {
                    Phase = TunePhase.Restoring;
                    _droppedAt = now;
                    return TuneAction.None;
                }
                return Hold(ampOperate);

            case TunePhase.Restoring:
                if (tx) { Phase = TunePhase.Active; return Hold(ampOperate); }
                if (now - _droppedAt >= Settle) return Finish(restoreEnabled, faultActive);
                return TuneAction.None;

            default:
                return TuneAction.None;
        }
    }

    private static TuneAction Hold(bool? ampOperate) =>
        ampOperate == true ? TuneAction.SendStandby : TuneAction.None;

    private TuneAction Finish(bool restoreEnabled, bool faultActive)
    {
        var restore = RestoreOperate && restoreEnabled && !faultActive;
        Cancel();
        return restore ? TuneAction.SendOperate : TuneAction.None;
    }
}
