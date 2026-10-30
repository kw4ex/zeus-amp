# Elecraft KPA1500 for Zeus

Control and telemetry for the Elecraft KPA1500 amplifier and its built-in ATU,
as a panel inside Zeus. It is the Zeus successor to the
`kpa1500_thetis_bridge.py` bridge and keeps its behaviour.

## What it does

- **Talks UDP to the amp on port 1500.** No session is held, so the Elecraft
  KPA1500 Utility, loggers, or another program can query the amp at the same
  time. (USB serial is available as a fallback; it locks the port.)
- **Follows Zeus.** Sends your frequency to the amp (`^FR`) so it selects the
  band and recalls its ATU setting. Optionally also sends the band (`^BN`).
  Nothing is sent while transmitting; it catches up at unkey.
- **Standby on Tune.** Radio TUNE → amp to STANDBY so the tune carrier goes
  through barefoot; when TUNE ends the amp goes back to OPERATE — only if it was
  in OPERATE before, the mode was read and confirmed, and no fault is showing.
  If the tune signal is lost mid-tune, the amp is left in STANDBY (fail-safe).
- **Telemetry** (each item can be shown or hidden): forward power with peak
  hold, SWR, reflected power, drive, PA dissipation, temperature, PA volts and
  amps, fan speed, ATU in-line/bypass, bypass SWR, fault, band, antenna.
- **Controls** (each can be shown or hidden): Power on/off, Operate/Standby,
  Tune (STBY), Clear fault, Band, Antenna 1/2/Next, ATU in-line/bypass, ATU
  full-search tune and cancel, fan minimum speed.
- **Remote power-on** with Wake-on-LAN plus `^ON1;` retries for 15 seconds.
  The amp's MAC address is learned automatically the first time the amp is on.
- **Find amplifier** searches your local network for KPA1500s.

## Safety rules

- It **never keys your transmitter.** ATU tune only starts the amp's own
  full-search tune; press TUNE on the radio (low power) to give it a carrier.
- Band, antenna, ATU in-line/bypass, ATU tune, OPERATE and power-off are
  refused while the radio or amp is transmitting or the ATU is tuning.
  STANDBY is always allowed.
- Automatic actions only move the amp toward STANDBY, except Standby-on-Tune
  restoring OPERATE after a tune as described above.

## Setup

1. In Zeus: **Features → Community → Install local feature**, choose the ZIP.
2. Add the **KPA1500** panel (Add Panel → Amplifiers).
3. Open ⚙ Settings. Enter the amp's IP address, or press **Find amplifier**
   and **Use**. Leave the UDP port at 1500. Save.
4. For remote power-on: set the amp menu **NET WAKE on LAN = ON** (or press
   *Enable Wake-on-LAN* in the panel when it offers it).

### Standby on Tune: where the TUNE signal comes from

The Zeus plugin SDK tells features the frequency, mode, band and MOX, but has no
TUNE flag. Three sources are offered; use any combination:

| Source | Setting | Notes |
|---|---|---|
| Zeus TCI `tune:` events | *Use Zeus TCI tune events* + host/port | Same signal the Thetis bridge used. Enable TCI in Zeus and match the port. Most reliable. |
| Zeus mode | *Also trigger when Zeus mode reports TUNE* | Works if Zeus reports a mode containing "TUN" while tuning. |
| Panel button | **Tune (STBY)** | Press it, then press TUNE on the radio. If no carrier appears within the wait time, OPERATE is restored. |

## Troubleshooting

- *NO REPLY*: the amp is off/asleep, or the IP is wrong. Try **Find amplifier**.
- *Power-on not confirmed*: check NET WAKE on LAN = ON, the MAC address, and
  that Zeus and the amp are on the same network.
- *TCI WAIT*: Zeus's TCI server is off or on a different port.
- Turn on **Event log** under Telemetry shown to see every command sent.

## No warranty — use at your own risk

This add-on is shared by one radio operator, **as is**. There is **no
warranty of any kind**: no promise that it works, that it is safe, or that
it suits your equipment or purpose. Amplifiers, radios and antennas can be
damaged, and RF can be dangerous. You are responsible for checking that it
behaves correctly with your own station, and for operating within your license.
The author is not liable for any damage or loss. It is not made, approved or
supported by Elecraft, FlexRadio or the Zeus project.

License: GPL-2.0-or-later (see the LICENSE file in the repository).
