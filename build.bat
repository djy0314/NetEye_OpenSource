@echo off
rem ============================================================
rem  NetEye build script  (pure ASCII, C#5, .NET Framework 4.x)
rem  Output: build\NetEye.exe   (single file, no dependencies)
rem ============================================================
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo ERROR: csc.exe not found. Install .NET Framework 4.x.
  exit /b 1
)

if not exist build mkdir build

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /unsafe- ^
  /out:build\NetEye.exe ^
  /win32manifest:src\app.manifest ^
  /win32icon:"%~dp0app.ico" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Security.dll /r:System.Net.Http.dll ^
  src\NetTable.cs src\Theme.cs src\ProcBadge.cs src\ProcAge.cs src\NetSpeed.cs ^
  src\Qqwry.cs src\LibUpdater.cs src\AppConfig.cs src\SettingsForm.cs ^
  src\ProcIcons.cs src\SpeedGraph.cs ^
  src\DonateImage.cs src\DonateForm.cs src\MainForm.cs src\NetEyeApp.cs

if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo BUILD OK: build\NetEye.exe
endlocal
