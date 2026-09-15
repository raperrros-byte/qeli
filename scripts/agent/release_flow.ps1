# Agent release pipeline: deb -> docker smoke -> host2 deploy -> Win + Android builds.
# Usage: pwsh -File scripts/agent/release_flow.ps1 [-SkipUpstream] [-SkipDeploy] [-SkipClients]
param(
    [switch]$SkipUpstream,
    [switch]$SkipDeploy,
    [switch]$SkipClients
)

$ErrorActionPreference = "Stop"
$Repo = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $Repo

function Get-QeliVersion {
    $control = Join-Path $Repo "qeli/debian/control"
    foreach ($line in Get-Content $control) {
        if ($line -match '^Version:\s*(.+)$') { return $Matches[1].Trim() }
    }
    throw "Version not found in qeli/debian/control"
}

$Version = Get-QeliVersion
Write-Host "== qeli release flow v$Version ==" -ForegroundColor Cyan

if (-not $SkipUpstream) {
    Write-Host "`n[1/6] upstream sync" -ForegroundColor Yellow
    git fetch upstream --tags 2>$null
    $counts = (git rev-list --left-right --count main...upstream/main).Trim() -split '\s+'
    $ahead = [int]$counts[0]; $behind = [int]$counts[1]
    Write-Host "fork: +$ahead / -$behind vs upstream/main"
    if ($behind -gt 0) {
        throw "main is $behind commits behind upstream/main - merge upstream first"
    }
    Write-Host "upstream: OK (fork includes upstream)"
} else {
    Write-Host "`n[1/6] upstream sync: SKIPPED"
}

Write-Host "`n[2/6] build .deb (Docker + cargo cache)" -ForegroundColor Yellow
$RepoUnix = $Repo -replace '\\','/'
$Image = if ($env:RUST_IMAGE) { $env:RUST_IMAGE } else { "rust:1.88-bookworm" }
$Pkg = "qeli_${Version}_amd64"
docker run --rm `
  -v "${RepoUnix}:/w" -w /w `
  -v "qeli-cargo-registry:/usr/local/cargo/registry" `
  -v "qeli-cargo-git:/usr/local/cargo/git" `
  -v "qeli-target-${Version}:/w/qeli/target" `
  $Image bash -c @"
set -euo pipefail
export PATH=/usr/local/cargo/bin:`$PATH
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y --no-install-recommends make dpkg-dev binutils xz-utils >/dev/null
make -C qeli/debian deb || true
test -f /w/qeli/target/release/qeli
rm -rf /tmp/qeli-deb
make -C qeli/debian stage BINARY=../target/release/qeli DEB_DIR=/tmp/qeli-deb/$Pkg BUILD_DIR=/tmp/qeli-deb
find /tmp/qeli-deb -type d -exec chmod 755 {} \;
find /tmp/qeli-deb -type f -exec chmod 644 {} \;
chmod 755 /tmp/qeli-deb/$Pkg/usr/bin/qeli /tmp/qeli-deb/$Pkg/DEBIAN/postinst /tmp/qeli-deb/$Pkg/DEBIAN/prerm /tmp/qeli-deb/$Pkg/DEBIAN/config
dpkg-deb --root-owner-group -Zxz --build /tmp/qeli-deb/$Pkg /w/qeli/debian/$Pkg.deb
/w/qeli/target/release/qeli --version
ls -lah /w/qeli/debian/$Pkg.deb
"@
$Deb = Join-Path $Repo "qeli/debian/qeli_${Version}_amd64.deb"
if (-not (Test-Path $Deb)) { throw "missing $Deb" }

Write-Host "`n[3/6] local docker smoke" -ForegroundColor Yellow
powershell -ExecutionPolicy Bypass -File (Join-Path $Repo "scripts/agent/docker_smoke_local.ps1") -ImageTag "qeli:${Version}-smoke"

if (-not $SkipDeploy) {
    Write-Host "`n[4/6] deploy to SSH_HOST2" -ForegroundColor Yellow
    $secrets = Join-Path $Repo "scripts/.lab_secrets"
    if (-not (Test-Path $secrets)) { throw "missing $secrets" }
    docker run --rm -v "${Repo}:/w" -w /w `
        -e "DEB=/w/qeli/debian/qeli_${Version}_amd64.deb" `
        alpine:3.20 sh /w/scripts/deploy/remote-upgrade-host2.sh

    Write-Host "`n[5/6] remote API smoke (host2)" -ForegroundColor Yellow
    docker run --rm -v "${Repo}:/w" -w /w alpine:3.20 sh /w/scripts/deploy/remote-smoke-api-host2.sh
} else {
    Write-Host "`n[4/6] deploy: SKIPPED"
    Write-Host "`n[5/6] remote smoke: SKIPPED"
}

if (-not $SkipClients) {
    Write-Host "`n[6/6] client builds" -ForegroundColor Yellow

    Write-Host "  -> Windows (net-required + standalone)"
    Push-Location (Join-Path $Repo "qeli-win")
    dotnet publish QeliWin\QeliWin.csproj -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -o dist\net-required
    Copy-Item dist\net-required\QeliWin.exe dist\QeliWin-net-required.exe -Force
    dotnet publish QeliWin\QeliWin.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -o dist\standalone
    Copy-Item dist\standalone\QeliWin.exe dist\QeliWin-standalone.exe -Force
    dotnet exec QeliWin\bin\Release\net10.0-windows\win-x64\QeliWin.dll selftest
    Pop-Location

    Write-Host "  -> Android release APK (signed, dev keystore)"
    powershell -ExecutionPolicy Bypass -File (Join-Path $Repo "scripts/agent/build_android_apk.ps1") -RepoRoot $Repo
} else {
    Write-Host "`n[6/6] client builds: SKIPPED"
}

Write-Host "`nDONE: qeli $Version" -ForegroundColor Green
