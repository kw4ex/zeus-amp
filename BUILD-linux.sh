#!/bin/bash
# ======================================================================
#  Elecraft KPA1500 for Zeus - one-step build for Linux / Raspberry Pi
#  In a terminal, in this folder, run:   ./BUILD-linux.sh
#  It checks the tools, builds, runs the self-tests, and makes the ZIP
#  you install in Zeus. Everything is also saved to build-log.txt.
# ======================================================================
cd "$(dirname "$0")" || exit 1
LOG="$(pwd)/build-log.txt"
echo "KPA1500 Linux build started $(date)" > "$LOG"
export PATH="$PATH:$HOME/.dotnet:/usr/share/dotnet:/usr/lib/dotnet:/opt/microsoft/powershell/7"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

fail() {
  echo
  echo "  ============================================================"
  echo "    Something went wrong. Send build-log.txt (in this folder)"
  echo "    to Claude, or paste its contents."
  echo "  ============================================================"
  echo "FAILED" >> "$LOG"
  exit 1
}

case "$(uname -m)" in
  x86_64)          RID=linux-x64;   KIND="64-bit PC";;
  aarch64|arm64)   RID=linux-arm64; KIND="64-bit ARM (Raspberry Pi 4/5)";;
  *) echo "  This computer ($(uname -m)) is not supported. Use a 64-bit OS."; exit 1;;
esac

echo
echo "  ============================================================"
echo "    Elecraft KPA1500 for Zeus - building for this $KIND"
echo "  ============================================================"
echo

echo "  [1/5] Checking for the .NET 10 SDK..."
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  echo
  echo "  The .NET 10 SDK is not installed. See the Linux setup guide, or:"
  echo "     https://learn.microsoft.com/dotnet/core/install/linux"
  echo "NO DOTNET" >> "$LOG"
  exit 1
fi
dotnet --list-sdks >> "$LOG" 2>&1
echo "        found."

echo "  [2/5] Checking for PowerShell 7..."
if ! command -v pwsh >/dev/null 2>&1; then
  echo
  echo "  PowerShell 7 is not installed. See the Linux setup guide, or:"
  echo "     https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-linux"
  echo "NO PWSH" >> "$LOG"
  exit 1
fi
echo "        found."

echo "  [3/5] Building (the first time this can take a few minutes)..."
dotnet build Zeus.Kpa1500.slnx -c Release --nologo >> "$LOG" 2>&1 || fail
echo "        done."

echo "  [4/5] Running the self-tests against a simulated KPA1500..."
dotnet run --project tests/Kpa1500.Tests -c Release --no-build >> "$LOG" 2>&1 || fail
echo "        all tests passed."

echo "  [5/5] Making the install ZIP for $RID..."
pwsh -NoProfile -File tools/package-unix.ps1 -Rid "$RID" >> "$LOG" 2>&1 || fail
echo "        done."

OUT="$(pwd)/artifacts/com.kw4ex.zeus.kpa1500"
echo
echo "  ============================================================"
echo "    SUCCESS.  The file to install in Zeus is:"
echo "    $(ls "$OUT"/*-"$RID".zip 2>/dev/null)"
echo "  ============================================================"
echo "SUCCESS" >> "$LOG"
command -v xdg-open >/dev/null 2>&1 && xdg-open "$OUT" >/dev/null 2>&1 &
