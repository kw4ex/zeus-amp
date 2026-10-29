#!/bin/bash
# ======================================================================
#  Elecraft KPA1500 for Zeus - one-click build for macOS
#  Double-click this file in Finder (the first time: right-click > Open).
#  It checks the tools, builds, runs the self-tests, and makes the ZIP
#  you install in Zeus. Everything is also saved to build-log.txt.
# ======================================================================
cd "$(dirname "$0")" || exit 1
LOG="$(pwd)/build-log.txt"
echo "KPA1500 macOS build started $(date)" > "$LOG"
export PATH="$PATH:/usr/local/share/dotnet:/usr/local/bin:/opt/homebrew/bin:$HOME/.dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

fail() {
  echo
  echo "  =============================================================="
  echo "    Something went wrong. build-log.txt is opening in TextEdit."
  echo "    Send that file (or paste its contents) back to Claude."
  echo "  =============================================================="
  echo "FAILED" >> "$LOG"
  open -e "$LOG"
  read -r -p "  Press Return to close this window." _
  exit 1
}

case "$(uname -m)" in
  arm64) RID=osx-arm64; KIND="Apple silicon (M1/M2/M3/M4)";;
  *)     RID=osx-x64;   KIND="Intel";;
esac

echo
echo "  =============================================================="
echo "    Elecraft KPA1500 for Zeus  -  building for this $KIND Mac"
echo "  =============================================================="
echo

echo "  [1/5] Checking for the .NET 10 SDK..."
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  echo
  echo "  The .NET 10 SDK is not installed. Install it from:"
  echo "     https://dotnet.microsoft.com/download/dotnet/10.0"
  echo "  (SDK, macOS, choose Arm64 for Apple silicon or x64 for Intel),"
  echo "  then run this file again."
  echo "NO DOTNET" >> "$LOG"
  read -r -p "  Press Return to close this window." _
  exit 1
fi
dotnet --list-sdks >> "$LOG" 2>&1
echo "        found."

echo "  [2/5] Checking for PowerShell 7..."
if ! command -v pwsh >/dev/null 2>&1; then
  echo
  echo "  PowerShell 7 is not installed. Download the macOS .pkg installer"
  echo "  from https://aka.ms/powershell-release?tag=stable"
  echo "  (osx-arm64.pkg for Apple silicon, osx-x64.pkg for Intel),"
  echo "  open it, click through to Install, then run this file again."
  echo "NO PWSH" >> "$LOG"
  read -r -p "  Press Return to close this window." _
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
pwsh -NoProfile -File tools/package-macos.ps1 -Rid "$RID" >> "$LOG" 2>&1 || fail
echo "        done."

echo
echo "  =============================================================="
echo "    SUCCESS.  The file to install in Zeus ends in -$RID.zip and"
echo "    is in the Finder window that is opening now."
echo "  =============================================================="
echo "SUCCESS" >> "$LOG"
open "artifacts/com.kw4ex.zeus.kpa1500"
read -r -p "  Press Return to close this window." _
