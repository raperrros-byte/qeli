#Requires -Version 5.1
<#
  Build qeli image, run server + client (split-tunnel + local proxy), verify SOCKS5.

  Usage (from repo root):
    .\scripts\docker_proxy_test.ps1
    .\scripts\docker_proxy_test.ps1 -SkipBuild   # reuse existing qeli:proxy-test image
#>
param(
    [string]$Image = "qeli:proxy-test",
    [string]$Network = "qeli-proxy-net",
    [int]$ProxyPort = 1080,
    [switch]$SkipBuild,
    [switch]$Keep
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Wait-Docker {
    param([int]$TimeoutSec = 180)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        docker info *> $null
        if ($LASTEXITCODE -eq 0) { return $true }
        Start-Sleep -Seconds 3
    }
    throw "Docker daemon not ready after ${TimeoutSec}s"
}

function Invoke-Check {
    param([string]$Name, [scriptblock]$Test, [string]$Detail = "")
    try {
        $ok = [bool](& $Test)
    } catch {
        $ok = $false
        if (-not $Detail) { $Detail = $_.Exception.Message }
    }
    $mark = if ($ok) { "PASS" } else { "FAIL" }
    $suffix = if ($Detail) { " - $Detail" } else { "" }
    Write-Host "  [$mark] $Name$suffix"
    return $ok
}

function Set-Utf8NoBom([string]$Path, [string]$Content) {
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function Get-DockerLogs([string]$Name) {
    return (cmd /c "docker logs $Name 2>&1")
}

Write-Host "=== wait for Docker ==="
Wait-Docker | Out-Null
Write-Host "Docker ready"

if (-not $SkipBuild) {
    Write-Host "`n=== build image $Image ==="
    docker build -f (Join-Path $RepoRoot "release/docker/Dockerfile") -t $Image $RepoRoot
    if ($LASTEXITCODE -ne 0) { throw "docker build failed" }
}

$Base = Join-Path $env:TEMP "qeli-proxy-test"
$ServerEtc = Join-Path $Base "server/etc"
$ClientEtc = Join-Path $Base "client/etc"
New-Item -ItemType Directory -Force -Path (Join-Path $ServerEtc "identity") | Out-Null
New-Item -ItemType Directory -Force -Path $ClientEtc | Out-Null

$User = "proxytest"
$Pass = "testpass123"
# argon2id("testpass123") - same as scripts/docker_2container_test.py
$Hash = '$argon2id$v=19$m=16384,t=2,p=1$cWVsaVNhbHRWYWw$CCYuTv8pvqQrvhrBQW3KjPpEN0MZaFfTKv3HOcGqB8w'

Set-Utf8NoBom (Join-Path $ServerEtc "server.conf") @'
[auth]
users_file = /etc/qeli/users.conf

[logging]
level = info

[profile:tcp]
identity_key = /etc/qeli/identity/tcp.key
bind.address = 0.0.0.0
bind.port = 443
bind.transport = tcp
tun.name = vpn0
tun.address = 10.8.0.1
tun.netmask = 255.255.255.0
tun.mtu = 1400
pool.cidr = 10.8.0.0/24
pool.exclude = 10.8.0.1
routing.nat.enabled = true
dns.enabled = false
obf.mode = fake-tls
obf.tls.server_name = www.microsoft.com
'@

Set-Utf8NoBom (Join-Path $ServerEtc "users.conf") @"
[user:$User]
password_hash = $Hash
enabled = true
"@

Write-Host "`n=== cleanup old containers ==="
cmd /c "docker rm -f qeli-proxy-server qeli-proxy-client 2>nul"
cmd /c "docker network rm $Network 2>nul"
docker network create $Network | Out-Null

Write-Host "`n=== start server ==="
docker run -d --name qeli-proxy-server --network $Network `
    --cap-add NET_ADMIN --cap-add NET_RAW --device /dev/net/tun `
    --sysctl net.ipv4.ip_forward=1 `
    -v "${ServerEtc}:/etc/qeli" `
    $Image server | Out-Null

$pub = ""
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 1
    $flat = (Get-DockerLogs "qeli-proxy-server") -replace "\s", ""
    $m = [regex]::Match($flat, 'pinonclient\):([0-9a-f]{64})')
    if ($m.Success) {
        $pub = $m.Groups[1].Value
        break
    }
}
if (-not $pub) {
    Write-Host ($logs | Select-Object -Last 30)
    throw "server did not publish identity key"
}
Write-Host "server pubkey: $($pub.Substring(0,16))..."

Set-Utf8NoBom (Join-Path $ClientEtc "client.conf") @"
[qeli]
server = qeli-proxy-server:443
proto = tcp
user = $User
pass = $Pass
key = $pub
mode = fake-tls
sni = www.microsoft.com
dns = off
gateway = false
proxy = true
proxy_listen = 127.0.0.1:$ProxyPort
proxy_mode = mixed

[logging]
level = info
"@

Write-Host "`n=== start client (split-tunnel + proxy) ==="
docker run -d --name qeli-proxy-client --network $Network `
    --cap-add NET_ADMIN --device /dev/net/tun `
    -v "${ClientEtc}:/etc/qeli" `
    $Image client | Out-Null

$authOk = $false
$clientIp = ""
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Seconds 1.5
    $cl = Get-DockerLogs "qeli-proxy-client"
    $m = [regex]::Match($cl, 'Auth OK.*?((10\.8\.0\.\d+))')
    if (-not $m.Success) {
        $m = [regex]::Match($cl, 'assigned IP:\s*(10\.8\.0\.\d+)')
    }
    if ($m.Success) {
        $authOk = $true
        $clientIp = $m.Groups[1].Value
        break
    }
}

$results = @()
$results += Invoke-Check "client Auth OK" { $authOk } $(if ($clientIp) { "IP $clientIp" } else { "" })
$results += Invoke-Check "proxy listener in logs" {
    (Get-DockerLogs "qeli-proxy-client") -match 'Local proxy'
} ""

$listen = docker exec qeli-proxy-client sh -c "ss -lntp 2>/dev/null | grep ':$ProxyPort '" 2>&1
$results += Invoke-Check "proxy port $ProxyPort listening" { $listen -match ":$ProxyPort" } ($listen.Trim())

function Invoke-DockerExec([string[]]$Cmd) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    $out = & docker exec qeli-proxy-client @Cmd 2>&1 | Out-String
    $ErrorActionPreference = $prev
    return $out.Trim()
}

Write-Host "`n=== proxy data-plane ==="
# Real domains via socks5h (remote DNS) — exercises ATYP=domain path end-to-end.
$example = Invoke-DockerExec @(
    "curl", "-sS", "--max-time", "25",
    "-x", "socks5h://127.0.0.1:$ProxyPort",
    "-o", "/dev/null", "-w", "%{http_code}",
    "http://example.com/"
)
$results += Invoke-Check "SOCKS5h -> example.com" {
    $example -match '^[23]\d\d$'
} "HTTP $example"

$cf = Invoke-DockerExec @(
    "curl", "-sS", "--max-time", "25",
    "-x", "socks5h://127.0.0.1:$ProxyPort",
    "-o", "/dev/null", "-w", "%{http_code}",
    "http://www.cloudflare.com/"
)
$results += Invoke-Check "SOCKS5h -> www.cloudflare.com" {
    $cf -match '^[23]\d\d$'
} "HTTP $cf"

# IPv4 ATYP path (after SOCKS5 ATYP fix — must not mis-parse as 1.1.1.0:20480).
$ipHttp = Invoke-DockerExec @(
    "curl", "-sS", "--max-time", "25",
    "-x", "socks5://127.0.0.1:$ProxyPort",
    "-o", "/dev/null", "-w", "%{http_code}",
    "http://1.1.1.1/"
)
$results += Invoke-Check "SOCKS5 -> 1.1.1.1 (IPv4 ATYP)" {
    $ipHttp -match '^[23]\d\d$'
} "HTTP $ipHttp"

$direct = Invoke-DockerExec @("ping", "-c2", "-W2", "1.1.1.1")
$results += Invoke-Check "direct ping 1.1.1.1 (split-tunnel: may bypass VPN)" {
    $true
} ($(if ($direct -match '0% packet loss') { 'reachable outside tunnel (expected in split mode)' } else { 'unreachable or filtered' }))

Write-Host "`n=== logs (tail) ==="
Write-Host "--- client ---"
Get-DockerLogs "qeli-proxy-client" | Select-Object -Last 12
Write-Host "--- server ---"
Get-DockerLogs "qeli-proxy-server" | Select-Object -Last 8

$passed = ($results | Where-Object { $_ }).Count
$total = $results.Count
Write-Host "`n============================================================"
if ($passed -eq $total) {
    Write-Host "RESULT: ALL PASS ($passed/$total) - proxy works in Docker"
} else {
    Write-Host "RESULT: $passed/$total checks passed"
}
Write-Host "============================================================"

if (-not $Keep) {
    Write-Host "`n=== cleanup ==="
    cmd /c "docker rm -f qeli-proxy-server qeli-proxy-client 2>nul"
    cmd /c "docker network rm $Network 2>nul"
    Write-Host "containers removed (configs in $Base)"
}

if ($passed -ne $total) { exit 1 }
