@echo off
rem Build LCR_Logger.exe and package it into release\LCR_Logger_v<version>.zip
set ROOT=%~dp0
call "%ROOT%build.bat" || exit /b 1
rem self test of the built exe - stop if it fails
"%ROOT%LCR_Logger.exe" --selftest > "%ROOT%selftest_out.txt"
findstr /x /c:"SELFTEST OK" "%ROOT%selftest_out.txt" >nul || (echo SELFTEST FAILED - see selftest_out.txt & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; $root='%ROOT%';" ^
  "$v=(Get-Item (Join-Path $root 'LCR_Logger.exe')).VersionInfo.FileVersion -replace '\.0\.0$','' -replace '^(\d+\.\d+\.\d+)\.0$','$1'; $stage=Join-Path $root ('release\LCR_Logger_v'+$v); $zip=$stage+'.zip';" ^
  "if (Test-Path $stage) { Remove-Item -Recurse -Force $stage };" ^
  "New-Item -ItemType Directory -Force $stage | Out-Null;" ^
  "Copy-Item (Join-Path $root 'LCR_Logger.exe') $stage;" ^
  "Get-ChildItem -LiteralPath $root -Filter *.txt | Where-Object { $_.Name -ne 'selftest_out.txt' } | Copy-Item -Destination $stage;" ^
  "if (Test-Path $zip) { Remove-Item -Force $zip };" ^
  "Compress-Archive -Path $stage -DestinationPath $zip;" ^
  "& (Join-Path $root 'verify_release.ps1') -Zip $zip -Base (Join-Path $root 'release');" ^
  "Write-Host ('RELEASE OK: '+$zip)"
