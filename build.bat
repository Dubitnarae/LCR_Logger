@echo off
rem LCR_Logger.exe build (uses the C# compiler built into Windows .NET Framework 4.x)
set HERE=%~dp0
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /out:"%HERE%LCR_Logger.exe" /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll "%HERE%src\LcrLogger.cs"
if errorlevel 1 (
  echo BUILD FAILED
  pause
  exit /b 1
)
echo BUILD OK: %~dp0LCR_Logger.exe
