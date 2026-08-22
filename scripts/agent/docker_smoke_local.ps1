# Local two-container smoke: qeli server + client on Docker Desktop (Windows).
param([string]$ImageTag = "qeli:0.7.16-smoke")

$ErrorActionPreference = "Stop"
$Repo = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$Net = "qeli-smoke-net"
$Srv = "qeli-smoke-server"
$Cli = "qeli-smoke-client"
$Base = Join-Path $Repo ".agent-smoke/run"
$User = "test"
$Pass = "testpass123"
$Hash = '$argon2id$v=19$m=16384,t=2,p=1$cWVsaVNhbHRWYWw$CCYuTv8pvqQrvhrBQW3KjPpEN0MZaFfTKv3HOcGqB8w'
$Utf8NoBom = New-Object System.Text.UTF8Encoding $false
function Write-Utf8NoBom([string]$Path, [string]$Content) {
    [System.IO.File]::WriteAllText($Path, $Content, $Utf8NoBom)
}

function Cleanup {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    cmd /c "docker rm -f $Srv $Cli 2>nul"
    cmd /c "docker network rm $Net 2>nul"
    if (Test-Path $Base) { Remove-Item -Recurse -Force $Base }
    $ErrorActionPreference = $prev
}
Cleanup

Write-Host "== build image $ImageTag =="
docker build -f (Join-Path $Repo "release/docker/Dockerfile") -t $ImageTag $Repo

Write-Host "== prepare configs =="
New-Item -ItemType Directory -Force -Path (Join-Path $Base "server/etc/identity") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $Base "client/etc") | Out-Null

$serverConf = @'
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
tun.mtu = 1400
pool.cidr = 10.8.0.0/24
pool.exclude = 10.8.0.1
routing.nat.enabled = true
dns.enabled = false
obf.mode = fake-tls
obf.tls.server_name = www.microsoft.com
'@
Write-Utf8NoBom (Join-Path $Base "server/etc/server.conf") $serverConf

Write-Utf8NoBom (Join-Path $Base "server/etc/users.conf") "[user:$User]`npassword_hash = $Hash`nenabled = true"

Write-Host "== start server =="
docker network create $Net | Out-Null
$serverEtc = (Join-Path $Base "server/etc") -replace '\\','/'
docker run -d --name $Srv --network $Net `
    --cap-add NET_ADMIN --cap-add NET_RAW --cap-add NET_BIND_SERVICE `
    --device /dev/net/tun --sysctl net.ipv4.ip_forward=1 `
    -v "${serverEtc}:/etc/qeli" -e QELI_CONFIG=/etc/qeli/server.conf `
    $ImageTag server | Out-Null
Start-Sleep -Seconds 4

$identity = docker exec $Srv qeli show-identity --config /etc/qeli/server.conf
$pub = ($identity -split "`n" | Select-Object -Index 1) -split '\s+' | Select-Object -Last 1
if (-not $pub) { throw "failed to read server identity pubkey" }

$clientConf = @(
"[qeli]"
"server = ${Srv}:443"
"proto = tcp"
"user = $User"
"pass = $Pass"
"key = $pub"
"mode = fake-tls"
"sni = www.microsoft.com"
"dns = off"
"gateway = true"
""
"[logging]"
"level = info"
)
Write-Utf8NoBom (Join-Path $Base "client/etc/client.conf") ($clientConf -join "`n")

Write-Host "== check-config =="
$clientEtc = (Join-Path $Base "client/etc") -replace '\\','/'
docker run --rm --network $Net --cap-add NET_ADMIN --device /dev/net/tun `
    -v "${clientEtc}:/etc/qeli:ro" --entrypoint /usr/local/bin/qeli $ImageTag `
    check-config --client --config /etc/qeli/client.conf

Write-Host "== client tunnel =="
docker run -d --name $Cli --network $Net `
    --cap-add NET_ADMIN --device /dev/net/tun --sysctl net.ipv4.ip_forward=1 `
    -v "${clientEtc}:/etc/qeli:ro" $ImageTag client | Out-Null
Start-Sleep -Seconds 8

$prevEa = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$logs = (docker logs $Cli 2>&1 | Out-String)
$ErrorActionPreference = $prevEa
if ($logs -notmatch 'Auth OK') { throw "client auth failed: $logs" }
Write-Host "PASS  client Auth OK"

docker exec $Cli ping -c 2 -W 2 10.8.0.1 | Out-Null
Write-Host "PASS  ping VPN gateway"

docker exec $Cli ping -c 2 -W 3 1.1.1.1 | Out-Null
Write-Host "PASS  ping 1.1.1.1 via NAT"

Cleanup
Write-Host "`nSMOKE_OK: local docker 2-container test passed"
