# KW4EX — Zeus SDR features

Two add-ons ("community features") for **Zeus**, the SDR program for ANAN and
other openHPSDR radios. They are written for operators, not programmers: you
download one file and Zeus installs it.

| Feature | Latest | What it does |
| --- | --- | --- |
| **Elecraft KPA1500** | 1.1.9 | Meters and buttons for the KPA1500 amplifier and its ATU; the amp follows the VFO; Standby on Tune; "No antenna?" warning; power-on over the network. |
| **FlexControl Knob** | 1.0.1 | Use the FlexRadio FlexControl USB tuning knob with Zeus: VFO tuning, AF volume, AGC gain and more, every press programmable. |

## Download

Open the folder for the feature you want, click the file for your computer,
then click the **download** button (↓, "Download raw file"). **Don't unzip it** —
Zeus opens the `.zip` itself.

- **KPA1500:** [builds/latest](https://github.com/kw4ex/zeus-amp/tree/builds/latest)
- **FlexControl Knob:** [builds/flexknob/latest](https://github.com/kw4ex/zeus-amp/tree/builds/flexknob/latest)

| Your computer | File name ends in |
| --- | --- |
| Windows PC | `-<version>.zip` (no extra word) |
| Mac with Apple silicon (M1–M4) | `-osx-arm64.zip` |
| Intel Mac | `-osx-x64.zip` |
| Linux PC (64-bit) | `-linux-x64.zip` |
| Raspberry Pi 4/5 (64-bit OS) | `-linux-arm64.zip` |

Older versions are kept in numbered folders on the
[builds branch](https://github.com/kw4ex/zeus-amp/tree/builds).

## Install

1. In Zeus open **Features › Community › Install local feature**.
2. Pick the `.zip` file you downloaded and click **Open**. A newer version
   simply replaces the older one.
3. Close and restart Zeus, then add the panel: **Add Panel › Amplifiers ›
   KPA1500** or **Add Panel › Controls › FlexControl Knob**.

## Instructions

- **KPA1500:** [step-by-step guide (PDF)](docs/guides/KPA1500-for-Zeus-guide.pdf) —
  what you need, install on Windows, Mac, Linux and Raspberry Pi, first-time
  settings, what each button means, a safe first test and troubleshooting.
  Short reference: [src/Kpa1500/README.md](src/Kpa1500/README.md).
- **FlexControl Knob:** [src/FlexKnob/README.md](src/FlexKnob/README.md).

**Important:** only one program should control the amplifier. Close the
KPA1500 Thetis bridge (and Thetis) while Zeus is running. Standby on Tune and
the receive-level features need **TCI switched on in Zeus** (port 40001).

## For programmers

Building, testing and packaging: [docs/DEVELOPERS.md](docs/DEVELOPERS.md).

License: GPL-2.0-or-later. "Elecraft", "KPA1500", "FlexRadio" and "FlexControl"
are names of their owners; these are independent, community-made add-ons.
