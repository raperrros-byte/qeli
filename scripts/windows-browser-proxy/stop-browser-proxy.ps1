#Requires -RunAsAdministrator

$runtimeDir = Join-Path $env:TEMP "qeli-browser-proxy"
$pidPath = Join-Path $runtimeDir "sing-box.pid"

if (Test-Path $pidPath) {
    $pid = [int](Get-Content $pidPath -Raw).Trim()
    $proc = Get-Process -Id $pid -ErrorAction SilentlyContinue
    if ($proc) {
        Stop-Process -Id $pid -Force
        Write-Host "Stopped sing-box (PID $pid)."
    }
    Remove-Item $pidPath -Force -ErrorAction SilentlyContinue
}

Get-Process -Name "sing-box" -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.Id -Force
    Write-Host "Stopped sing-box (PID $($_.Id))."
}

Write-Host "Done."
