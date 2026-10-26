# zeus-kpa1500

Zeus SDR community feature for the **Elecraft KPA1500** amplifier and its
built-in ATU. Operator guide: [`src/Kpa1500/README.md`](src/Kpa1500/README.md).

Behaviour is ported from the hardware-proven `kpa1500_thetis_bridge.py` v2.9:
UDP on port 1500, `^FR` frequency tracking, Standby-on-Tune with a fresh mode
read and confirmed restore, `^AMI`/`^AMB` ATU switching, Wake-on-LAN power-on,
fan minimum control, and network discovery.

## Layout

```
src/Kpa1500/
  Kpa1500Plugin.cs   Zeus entry point + HTTP API for the panel
  AmpController.cs   link, poll loop, tracking, Standby-on-Tune, commands, safety
  TuneGuard.cs       Standby-on-Tune state machine (pure, tested)
  KpaProtocol.cs     ^…; formatting/parsing, fault table (pure, tested)
  KpaLink.cs         one-command-in-flight request/response over a stream
  Transports.cs      UDP (default) and USB serial
  TciClient.cs       optional read-only TCI listener for tune/trx
  NetTools.cs        Wake-on-LAN + discovery
  PluginConfig.cs    settings, validation, sanitising
  ui/kpa1500.es.js   panel (plain ESM, React.createElement, Zeus tokens only)
  build-package.ps1  catalog packager, unmodified copy
tests/Kpa1500.Tests  fake KPA1500 over loopback UDP + unit tests
sdk/                 vendored Zeus plugin SDK snapshot (ABI 1 / 1.5.0) — do not edit
docs/preview/        panel renders against a mock backend (not hardware screenshots)
```

## Build, test, package

Easiest: double-click **`BUILD.bat`** (Windows). It checks for the tools,
builds, runs the tests, makes the ZIP, and writes everything to `build-log.txt`.

By hand — needs the .NET 10 SDK and PowerShell 7:

```powershell
dotnet build Zeus.Kpa1500.slnx -c Release
dotnet run --project tests/Kpa1500.Tests -c Release --no-build
pwsh src/Kpa1500/build-package.ps1 -ManagedDependency System.IO.Ports.dll
```

The ZIP lands in `artifacts/<id>/`. Install it in Zeus with
**Features → Community → Install local feature**. CI (`.github/workflows/ci.yml`)
does all three on every push and attaches the ZIP to `v*` tag releases.

## Before publishing

- `global.json` currently allows any .NET 10 SDK so local builds are easy. The
  catalog's rebuild check requires an exact pin with `"rollForward": "disable"`;
  set that to the SDK you build the release with before submitting.

- Feature ID is `com.kw4ex.zeus.kpa1500` (author KW4EX). It is permanent once
  listed; the `ROOT` class prefix at the top of the UI module must keep matching it.
- Community catalog notes: the optional TCI listener connects to Zeus's own TCI
  server, and the bundled `System.IO.Ports.dll` uses P/Invoke; both will draw
  reviewer attention under the catalog's rules. USB serial is kept on purpose:
  many stations reach the amp through a serial port or a virtual serial-port
  emulator. Raise both with the maintainers. See the catalog's `CONTRIBUTING.md`.

License: GPL-2.0-or-later.
