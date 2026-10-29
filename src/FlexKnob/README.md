# FlexControl Knob for Zeus

Use the **FlexRadio FlexControl** USB tuning knob with Zeus. It does what the
knob did in the KPA1500 Thetis bridge (v2.31), with Zeus in place of Thetis:

- **Turning** tunes VFO A in Zeus's own tuning step. A press can switch the
  knob to AF volume, AGC gain, VFO B, RIT, XIT, tuning step or TX drive, and
  the three button LEDs show which.
- **12 presses** (knob press and buttons 1–3; each tap, double tap or long
  press) each run a function you choose in ⚙ Settings.

Plug the knob into the **computer that runs Zeus**. The panel shows what
turning does right now and what each press does. Add it from
Add Panel → Controls → **FlexControl Knob**.

## Default functions (as in the bridge)

| Press | Tap | Double tap | Long |
|---|---|---|---|
| Knob | AF volume ⇄ VFO A | Mute | Back to VFO A |
| Button 1 | AF volume ⇄ VFO A | AGC gain ⇄ VFO A | Mute |
| Button 2 | Noise reduction (NR) | Tuning step larger | Tuning step smaller |
| Button 3 | Safe TUNE / stop | Band up | Band down |

The bridge had **VOX** on button 2 tap. Zeus has no VOX control that other
programs can reach, so that press is NR here.

## Knob modes and LEDs (buttons 1-2-3)

VFO A ○○○ · AF volume ●○○ · AGC gain ○●○ · RIT ○○● · XIT ○●● ·
VFO B ●○● · Tuning step ●●○ · TX drive ●●●

## Functions you can assign

- **Knob mode:** any of the modes above as a toggle with VFO A, *next mode*
  (VFO A → AF → AGC), *back to VFO A*.
- **Zeus:** Mute, VFO lock, Split, RIT on/off and clear, XIT on/off and clear,
  NR, NB, ANF, tuning step larger / smaller / follow Zeus again, Band up /
  down (remembers where you were on each band), MOX (transmits!).
- **KPA1500 amplifier** (needs the Elecraft KPA1500 Zeus feature):
  - **Safe TUNE:** amp to STANDBY first, confirmed, then the radio's TUNE.
    Press again to stop. The KPA1500 feature restores OPERATE afterwards.
  - Amp OPERATE / STANDBY, ATU tune, antenna 1 / 2, DUMMY OK, and
    Standby-on-Tune on / off.
- **Custom:** any Zeus TCI command, e.g. `rx_bin_enable:0,true;`.

## Safety rules

- **TUNE** is refused when the KPA1500 feature can't be reached (you can
  change this in Settings), when the amp doesn't confirm STANDBY, when the
  no-antenna warning is showing, or while transmitting. TUNE stops by itself
  after 20 s (adjustable).
- **MOX** is refused while the no-antenna warning is showing.
- **Band changes** are refused while transmitting.
- **Tuning** is ignored while the VFO is locked in Zeus.

## Setup

1. Close any other program that uses the knob, such as DDUtil or the
   KPA1500 Thetis bridge. Only one program can open it at a time.
2. In Zeus, turn on **TCI** (normally port 40001). Without TCI only VFO A
   tuning works.
3. Install the ZIP with **Features → Community → Install local feature**, then
   add the panel.
4. **Port:** Auto finds the knob by its USB id (Windows, Linux). On a Mac,
   Auto works when the knob is the only USB modem; otherwise pick
   `/dev/cu.usbmodem…` in ⚙ Settings.

## How it reaches Zeus

- VFO A uses the Zeus plugin interface.
- The other Zeus functions use Zeus's TCI server, as the bridge did with
  Thetis.
- Amplifier functions use the KPA1500 feature's own API on Zeus's web
  server. Zeus's address is found automatically; you can type it in Settings.

*Catalog note:* the Zeus community rules discourage features from talking to
Zeus's own interfaces (TCI, its web API). Here they are the only route to
these functions.
