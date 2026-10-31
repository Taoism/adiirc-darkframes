@echo off
rem Builds build\DarkFrames.dll with the .NET Framework 4 compiler that ships with Windows.
rem Usage: build.cmd [path\to\AdiIRCAPIv2.dll]
rem AdiIRC keeps AdiIRCAPIv2.dll in its config folder: next to AdiIRC.exe for the portable
rem version, %LOCALAPPDATA%\AdiIRC for an installed one.
setlocal
cd /d "%~dp0"
set "API=%~1"
if "%API%"=="" set "API=%LOCALAPPDATA%\AdiIRC\AdiIRCAPIv2.dll"
if not exist "%API%" (
  echo AdiIRCAPIv2.dll not found: %API%
  echo Usage: build.cmd path\to\AdiIRCAPIv2.dll
  exit /b 1
)
if not exist build mkdir build
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:library /optimize+ /warn:4 ^
  /out:build\DarkFrames.dll /reference:"%API%" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
  src\DarkFrames.cs
