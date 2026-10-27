@echo off
rem Build, package, and publish a GitHub release (v<version>) that installed copies pick up as an auto-update.
rem Before running: raise AssemblyVersion/AssemblyFileVersion in src\LcrLogger.cs and commit/push.
rem Usage: publish_release.bat "release notes"
set ROOT=%~dp0
call "%ROOT%make_release.bat" || exit /b 1
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Get-Item '%ROOT%LCR_Logger.exe').VersionInfo.FileVersion -replace '\.0\.0$','' -replace '^(\d+\.\d+\.\d+)\.0$','$1'"`) do set VER=%%v
set NOTES=%~1
if "%NOTES%"=="" set NOTES=LCR Logger %VER%
gh release create "v%VER%" "%ROOT%release\LCR_Logger_v%VER%.zip" --repo Dubitnarae/LCR_Logger --title "LCR Logger %VER%" --notes "%NOTES%" || exit /b 1
echo PUBLISHED: v%VER%
