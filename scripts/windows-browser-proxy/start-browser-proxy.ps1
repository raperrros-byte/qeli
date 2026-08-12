#Requires -RunAsAdministrator
<#
  Browser -> local SOCKS/HTTP (127.0.0.1:1080) -> sing-box -> Qeli Wintun -> server

  Prerequisites:
    1. Qeli Win connected in SPLIT-TUNNEL mode (gateway = false, dns = off).
    2. sing-box.exe on PATH or in .\bin\sing-box.exe
       https://github.com/SagerNet/sing-box/releases  (windows-amd64)

  Usage:
    .\start-browser-proxy.ps1
    .\start-browser-proxy.ps1 -Port 1080 -Listen 127.0.0.1
#>
param(
    [string]$Listen = "127.0.0.1",
    [int]$Port = 1080,
    [string]$SingBoxExe = "",
    [switch]$HttpOnly,
    [switch]$NoWait
)

$ErrorActionPreference = "Stop"

function Find-SingBox {
    param([string]$Override)
    if ($Override -and (Test-Path $Override)) { return (Resolve-Path $Override).Path }
    $local = Join-Path $PSScriptRoot "bin\sing-box.exe"
    if (Test-Path $local) { return (Resolve-Path $local).Path }
    $cmd = Get-Command sing-box.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "sing-box.exe not found. Put it in $local or add to PATH."
}

function Find-QeliAdapter {
    $adp = Get-NetAdapter -ErrorAction SilentlyContinue |
        Where-Object { $_.Status -eq "Up" -and ($_.Name -like "Qeli-*" -or $_.InterfaceDescription -like "*Wintun*") } |
        Sort-Object { if ($_.Name -like "Qeli-*") { 0 } else { 1 } } |
        Select-Object -First 1
    if (-not $adp) {
        throw "Qeli Wintun adapter not found. Connect Qeli (split-tunnel) first."
    }
    $ip = Get-NetIPAddress -InterfaceIndex $adp.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike "169.254.*" } |
        Select-Object -First 1
    if (-not $ip) {
        throw "No IPv4 on adapter '$($adp.Name)'. Wait for Qeli handshake to finish."
    }
    [pscustomobject]@{
        Name  = $adp.Name
        Index = $adp.ifIndex
        Ip    = $ip.IPAddress
    }
}

function Wait-QeliAdapter {
    param([int]$TimeoutSec = 120)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try { return Find-QeliAdapter } catch { Start-Sleep -Seconds 2 }
    }
    throw "Timed out waiting for Qeli Wintun (${TimeoutSec}s)."
}

if (-not $NoWait) {
    Write-Host "Waiting for Qeli Wintun..."
    $tun = Wait-QeliAdapter
} else {
    $tun = Find-QeliAdapter
}

$singBox = Find-SingBox -Override $SingBoxExe
$runtimeDir = Join-Path $env:TEMP "qeli-browser-proxy"
New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null

$inboundType = if ($HttpOnly) { "http" } else { "mixed" }
$configPath = Join-Path $runtimeDir "sing-box.json"
$pidPath = Join-Path $runtimeDir "sing-box.pid"

$config = @{
    log = @{ level = "warn" }
    inbounds = @(
        @{
            type         = $inboundType
            tag          = "browser-in"
            listen       = $Listen
            listen_port  = $Port
            sniff        = $true
            sniff_override_destination = $false
        }
    )
    outbounds = @(
        @{
            type              = "direct"
            tag               = "via-qeli-tun"
            bind_interface    = $tun.Name
            inet4_bind_address = $tun.Ip
            domain_strategy   = "prefer_ipv4"
        }
    )
    route = @{
        final = "via-qeli-tun"
    }
} | ConvertTo-Json -Depth 8

$config | Set-Content -Path $configPath -Encoding UTF8

$existing = Get-Process -Name "sing-box" -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping previous sing-box (PID $($existing.Id))..."
    Stop-Process -Id $existing.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

Write-Host ""
Write-Host "Qeli adapter : $($tun.Name) ($($tun.Ip))"
Write-Host "Proxy listen : $Listen`:$Port ($inboundType = SOCKS5 + HTTP)"
Write-Host "sing-box     : $singBox"
Write-Host ""
Write-Host "Browser settings:"
Write-Host "  SOCKS5  $Listen`:$Port"
Write-Host "  HTTP    $Listen`:$Port  (if mixed inbound)"
Write-Host "  Enable 'Proxy DNS / remote DNS' (Firefox: network.proxy.socks_remote_dns=true)"
Write-Host ""
Write-Host "Verify:"
Write-Host "  curl.exe -x socks5h://${Listen}:$Port https://api.ipify.org"
Write-Host "  curl.exe https://api.ipify.org   # should stay on your real IP"
Write-Host ""

$proc = Start-Process -FilePath $singBox -ArgumentList @("run", "-c", $configPath) `
    -PassThru -WindowStyle Hidden
$proc.Id | Set-Content -Path $pidPath -Encoding ASCII
Write-Host "sing-box started (PID $($proc.Id)). Stop with .\stop-browser-proxy.ps1"
