@echo off
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /out:ScanPublisher.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ScanPublisher.cs
if errorlevel 1 (
  echo Build FAILED
  pause
  exit /b 1
)
echo Build OK: ScanPublisher.exe
