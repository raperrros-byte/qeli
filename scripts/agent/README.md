# Agent release scripts (Windows + Docker)

Maintained by: Daniil Nekrasov <raperrros@yandex.ru>  
Updated: 2026-08-22

Runbook для сборки и деплоя из `c:\projects\home\qeli` без bash/WSL.  
Полный контекст деплоя сервera: [`../AGENT_DEB_BUILD_DEPLOY.md`](../AGENT_DEB_BUILD_DEPLOY.md).

## Быстрый старт

```powershell
# Полный цикл: upstream check → .deb → docker smoke → host2 → Win + Android
powershell -ExecutionPolicy Bypass -File scripts/agent/release_flow.ps1

# Только артефакты, без деплоя
powershell -ExecutionPolicy Bypass -File scripts/agent/release_flow.ps1 -SkipDeploy
```

## Скрипты

| Скрипт | Назначение |
|--------|------------|
| [`release_flow.ps1`](release_flow.ps1) | Оркестратор всего pipeline |
| [`build_deb_docker.sh`](build_deb_docker.sh) | `.deb` в Docker + cargo cache (Git Bash/WSL или через `docker run … bash`) |
| [`docker_smoke_local.ps1`](docker_smoke_local.ps1) | 2-container smoke на Docker Desktop |
| [`build_android_apk.ps1`](build_android_apk.ps1) | **Подписанный** release APK для sideload / OTA поверх |
| [`../deploy/remote-upgrade-host2.sh`](../deploy/remote-upgrade-host2.sh) | In-place upgrade на SSH_HOST2 |

## Android APK (sideload + обновление поверх)

Подробно: [`../../qeli-android/README.md`](../../qeli-android/README.md) § «Release APK».

```powershell
powershell -ExecutionPolicy Bypass -File scripts/agent/build_android_apk.ps1
```

**Артефакт:** `qeli-android/dist/qeli-android-<version>.apk`

При первом запуске создаются (git-ignored):

- `qeli-android/qeli-dev-release.jks` — постоянный dev-ключ подписи
- `qeli-android/keystore.properties` — пароли (шаблон: `keystore.properties.example`)

**Обновление без удаления:** каждая новая сборка должна:

1. Использовать **тот же** `qeli-dev-release.jks`
2. Иметь **`versionCode` строго больше** установленного (`app/build.gradle.kts`)

Если на телефоне стояла **другая** подпись (debug с другой машины, GitHub release) — один раз удалить Qeli, дальше все dev-сборки обновляются поверх.

**Samsung «Приложение не установлено»:** почти всегда unsigned APK или несовпадение подписи / `versionCode`.

## Windows client

```powershell
cd qeli-win
dotnet publish QeliWin\QeliWin.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -o dist\net-required
Copy-Item dist\net-required\QeliWin.exe dist\QeliWin-net-required.exe -Force
dotnet exec QeliWin\bin\Release\net10.0-windows\win-x64\QeliWin.dll selftest
```

Артефакт: `qeli-win/dist/QeliWin-net-required.exe`

## Host2 deploy (из PowerShell)

```powershell
docker run --rm -v "c:/projects/home/qeli:/w" -w /w `
  -e "DEB=/w/qeli/debian/qeli_0.7.16_amd64.deb" `
  alpine:3.20 sh /w/scripts/deploy/remote-upgrade-host2.sh

docker run --rm -v "c:/projects/home/qeli:/w" -w /w `
  alpine:3.20 sh /w/scripts/deploy/remote-smoke-api-host2.sh
```

Требует `scripts/.lab_secrets` (из `lab_secrets.example`).
