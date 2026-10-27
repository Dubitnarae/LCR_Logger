@echo off
rem Build LCR_Logger.exe and package it into release\LCR_Logger_v<version>.zip
set ROOT=%~dp0
call "%ROOT%build.bat" || exit /b 1
powershell -NoProfile -Command ^
  "$ErrorActionPreference='Stop'; $root='%ROOT%';" ^
  "$v=(Get-Item (Join-Path $root 'LCR_Logger.exe')).VersionInfo.FileVersion -replace '\.0\.0$','' -replace '^(\d+\.\d+\.\d+)\.0$','$1'; $stage=Join-Path $root ('release\LCR_Logger_v'+$v); $zip=$stage+'.zip';" ^
  "if (Test-Path $stage) { Remove-Item -Recurse -Force $stage };" ^
  "New-Item -ItemType Directory -Force $stage | Out-Null;" ^
  "Copy-Item (Join-Path $root 'LCR_Logger.exe') $stage;" ^
  "Get-ChildItem -LiteralPath $root -Filter *.txt | Copy-Item -Destination $stage;" ^
  "if (Test-Path $zip) { Remove-Item -Force $zip };" ^
  "Compress-Archive -Path $stage -DestinationPath $zip;" ^
  "Write-Host ('RELEASE OK: '+$zip)"
