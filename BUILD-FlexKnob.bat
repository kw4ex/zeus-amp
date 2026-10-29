@echo off
setlocal
rem ======================================================================
rem  FlexControl Knob for Zeus - one-click build
rem  Double-click this file. It checks the tools, builds the feature,
rem  runs the self-tests, and makes the ZIP you install in Zeus.
rem  Everything it prints is also saved to build-log-flexknob.txt in this folder.
rem ======================================================================
cd /d "%~dp0"
set LOG=%~dp0build-log-flexknob.txt
echo FlexKnob build started %date% %time% > "%LOG%"

echo.
echo  ==============================================================
echo    FlexControl Knob for Zeus  -  building, please wait...
echo  ==============================================================
echo.

echo  [1/5] Checking for the .NET SDK...
where dotnet >nul 2>&1
if errorlevel 1 goto nodotnet
dotnet --list-sdks >> "%LOG%" 2>&1
dotnet --list-sdks | findstr /b "10." >nul
if errorlevel 1 goto nodotnet
echo        found.

echo  [2/5] Checking for PowerShell 7...
where pwsh >nul 2>&1
if errorlevel 1 goto nopwsh
echo        found.

echo  [3/5] Building (the first time this can take a few minutes)...
dotnet build Zeus.FlexKnob.slnx -c Release --nologo >> "%LOG%" 2>&1
if errorlevel 1 goto failed
echo        done.

echo  [4/5] Running the self-tests with a pretend knob, Zeus and KPA1500...
dotnet run --project tests\FlexKnob.Tests -c Release --no-build >> "%LOG%" 2>&1
if errorlevel 1 goto failed
echo        all tests passed.

echo  [5/5] Making the install ZIP...
pwsh -NoProfile -ExecutionPolicy Bypass -File src\FlexKnob\build-package.ps1 -ManagedDependency System.IO.Ports.dll >> "%LOG%" 2>&1
if errorlevel 1 goto failed
echo        done.

echo.
echo  ==============================================================
echo    SUCCESS.  The file to install in Zeus is in the folder that
echo    is opening now (it ends in .zip).
echo  ==============================================================
echo SUCCESS >> "%LOG%"
start "" explorer "%~dp0artifacts\com.kw4ex.zeus.flexknob"
echo.
pause
exit /b 0

:nodotnet
echo.
echo  The .NET 10 SDK is not installed (or the computer needs a restart).
echo  Install it from:  https://dotnet.microsoft.com/download/dotnet/10.0
echo  (choose "SDK", Windows, x64), restart the PC, then run this again.
echo NO DOTNET >> "%LOG%"
echo.
pause
exit /b 1

:nopwsh
echo.
echo  PowerShell 7 is not installed. To install it, open the Start menu,
echo  type  cmd  , open "Command Prompt", and paste this line:
echo.
echo      winget install --id Microsoft.PowerShell --source winget
echo.
echo  When it finishes, close that window and run this file again.
echo NO PWSH >> "%LOG%"
echo.
pause
exit /b 1

:failed
echo.
echo  ==============================================================
echo    Something went wrong. build-log-flexknob.txt is opening in Notepad.
echo    Send that file (or paste its contents) back to Claude.
echo  ==============================================================
echo FAILED >> "%LOG%"
start "" notepad "%LOG%"
echo.
pause
exit /b 1
