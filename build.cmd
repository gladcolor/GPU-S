@echo off
rem Builds GPU-S.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No Visual Studio, SDK or admin rights needed.
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc.exe not found at %CSC%
  exit /b 1
)
if not exist bin mkdir bin
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /out:bin\GPU-S.exe ^
  /win32manifest:src\app.manifest ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  src\*.cs
if errorlevel 1 exit /b 1
echo Built bin\GPU-S.exe
