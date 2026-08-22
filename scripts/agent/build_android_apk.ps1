# Build signed Android release APK (local sideload / in-place update).
# Creates qeli-android/qeli-dev-release.jks + keystore.properties on first run.
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
)

$ErrorActionPreference = "Stop"
$Android = Join-Path $RepoRoot "qeli-android"
$Jks = Join-Path $Android "qeli-dev-release.jks"
$Props = Join-Path $Android "keystore.properties"
$Dist = Join-Path $Android "dist"

if (-not (Test-Path $Jks)) {
    Write-Host "== generate dev release keystore (once) =="
    docker run --rm -v "${Android}:/w" -w /w eclipse-temurin:17 keytool -genkeypair -v `
        -keystore qeli-dev-release.jks -alias qeli `
        -keyalg RSA -keysize 4096 -validity 10000 `
        -storepass qeli-dev-local -keypass qeli-dev-local `
        -dname "CN=Qeli Dev, OU=Mobile, O=Qeli, L=Local, ST=Local, C=RU"
}

if (-not (Test-Path $Props)) {
    @"
storeFile=qeli-dev-release.jks
storePassword=qeli-dev-local
keyAlias=qeli
keyPassword=qeli-dev-local
"@ | Set-Content -Encoding ASCII $Props
    Write-Host "created $Props"
}

Write-Host "== assembleRelease (signed) =="
docker run --rm -v "${Android}:/project" -w /project `
    -e ANDROID_HOME=/opt/android-sdk-linux `
    -e GRADLE_USER_HOME=/project/.gradle-docker `
    ghcr.io/cirruslabs/android-sdk:35 bash -lc "chmod +x ./gradlew && ./gradlew --no-daemon assembleRelease"

$releaseDir = Join-Path $Android "app\build\outputs\apk\release"
$apk = Get-ChildItem $releaseDir -Filter "*release*.apk" |
    Where-Object { $_.Name -notmatch 'unsigned' } |
    Select-Object -First 1
if (-not $apk) {
    $apk = Get-ChildItem $releaseDir -Filter "*.apk" | Select-Object -First 1
}
if (-not $apk) { throw "APK not found under $releaseDir" }

New-Item -ItemType Directory -Force -Path $Dist | Out-Null
$ver = (Select-String -Path (Join-Path $Android "app\build.gradle.kts") -Pattern 'versionName = "([^"]+)"').Matches.Groups[1].Value
$out = Join-Path $Dist "qeli-android-${ver}.apk"
Copy-Item $apk.FullName $out -Force
Write-Host "OK: $out ($([math]::Round($apk.Length/1MB, 1)) MB)"
