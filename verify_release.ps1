# 배포 파일 깨짐 검사 (이 PC들에서 빌드·압축 결과가 조용히 깨진 적이 있음). 깨졌으면 throw.
#   zip 검사:   .\verify_release.ps1 -Zip <zip> -Base <zip 안 경로의 기준 폴더>
#               zip 안 파일 하나하나를 기준 폴더의 원본과 SHA256 대조, 파일 수도 대조
#   게시 검사:  .\verify_release.ps1 -Repo <소유자/저장소> -Tag <태그> -Files <올린 파일들>
#               GitHub 릴리스에서 다시 내려받아 올린 파일과 SHA256 대조
# Du_App 의 다른 저장소에도 같은 파일이 있다 — 고치면 같이 고칠 것.
param([string]$Zip, [string]$Base, [string]$Repo, [string]$Tag, [string[]]$Files)
$ErrorActionPreference = "Stop"
function Get-Sha($stream) { $h = [Security.Cryptography.SHA256]::Create(); try { [BitConverter]::ToString($h.ComputeHash($stream)) } finally { $h.Dispose() } }

if ($Zip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $baseFull = (Resolve-Path $Base).Path
    $z = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Zip).Path)
    try {
        $entries = @($z.Entries | Where-Object { $_.Name })   # 이름 없는 항목 = 빈 폴더
        $prefixes = @($entries | ForEach-Object { ($_.FullName -replace '/', '\').Split('\')[0] } | Sort-Object -Unique)
        foreach ($e in $entries) {
            $src = Join-Path $baseFull ($e.FullName -replace '/', '\')
            if (-not (Test-Path -LiteralPath $src)) { throw "zip 에만 있는 파일: $($e.FullName)" }
            $s = $e.Open(); try { $a = Get-Sha $s } finally { $s.Dispose() }
            $f = [IO.File]::OpenRead($src); try { $b = Get-Sha $f } finally { $f.Dispose() }
            if ($a -ne $b) { throw "zip 안 파일이 원본과 다름(깨짐): $($e.FullName)" }
        }
        # 원본 쪽 파일 수: zip 맨 위 항목들(폴더 또는 파일) 아래 전부
        $srcCount = 0
        foreach ($p in $prefixes) { $srcCount += @(Get-ChildItem -LiteralPath (Join-Path $baseFull $p) -Recurse -File -Force).Count }
        if ($srcCount -ne $entries.Count) { throw "파일 수가 다름: zip $($entries.Count)개, 원본 ${srcCount}개" }
    } finally { $z.Dispose() }
    Write-Host "ZIP OK: $Zip ($($entries.Count)개 파일 원본과 같음)"
}

if ($Repo) {
    $gh = "C:\Program Files\GitHub CLI\gh.exe"; if (-not (Test-Path $gh)) { $gh = "gh" }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("verify_release_" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory $tmp | Out-Null
    try {
        foreach ($f in $Files) {
            $name = Split-Path $f -Leaf
            & $gh release download $Tag -R $Repo -p $name -D $tmp --clobber
            if ($LASTEXITCODE -ne 0) { throw "내려받기 실패: $name" }
            if ((Get-FileHash -LiteralPath $f).Hash -ne (Get-FileHash -LiteralPath (Join-Path $tmp $name)).Hash) { throw "GitHub 에 올라간 $name 이 올린 파일과 다름 — 다시 올릴 것" }
        }
    } finally { Remove-Item $tmp -Recurse -Force }
    Write-Host "RELEASE OK: $Repo $Tag ($($Files.Count)개 파일 내려받아 대조함)"
}
