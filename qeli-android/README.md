# qeli-android

Android-клиент qeli: системный VPN через `VpnService` (весь трафик и DNS на уровне ОС, не
пер-приложенческий прокси). Connect, handshake, обфускация, криптография и packet pumps
выполняются общим Rust-ядром; Kotlin остаётся адаптером Android API и UI.

- Общая карта документации — [docs/ru/index.md](../docs/ru/index.md)
- Подключение «с нуля» (выдача `qeli://` на сервере) — [GETTING-STARTED §8.1](../docs/ru/GETTING-STARTED.md)
- Все ключи конфигурации — [CONFIG.md](../docs/ru/CONFIG.md)
- Если не подключается — [TROUBLESHOOTING.md](../docs/ru/TROUBLESHOOTING.md)

## Технологии

- **Kotlin**, `minSdk 28` (Android 9), `targetSdk 37`, Material Components.
- **`VpnService`** — TUN-интерфейс, маршруты, DNS, per-app split tunnel.
- **JNI к Rust-ядру** (`libqeli.so`, `app/src/main/jniLibs/{arm64-v8a,x86_64}/`) —
  единый TCP/UDP/Reality transport, ML-KEM-768, QUIC/MTU, shaping и bonding. JNI также
  предоставляет credential-free UDP first-flight probe для проверки доступности профиля.
- Foreground-сервис со `specialUse`-типом: туннель живёт, пока приложение свёрнуто.

## Структура

```
app/src/main/kotlin/com/qeli/
├── MainActivity.kt        — UI: профили, импорт (QR/ссылка/файл), лог, настройки, бэкап,
│                            авто-транспорт, SNI, вкладка SNI speed
├── TransportAuto.kt       — ранжирование режимов (зеркало Rust auto_transport)
├── SniCatalog.kt          — каталог front-хостов из assets/sni_hosts.txt
├── SniSpeedProbe.kt       — TLS + HTTPS download probe
├── QeliService.kt         — platform adapter: protect/trust, NetworkPlan/TUN, reconnect
├── TransportCore.kt       — JNI owner общего Rust transport и native UDP diagnostic
├── ProfileStore.kt        — хранилище профилей (EncryptedSharedPreferences)
├── QeliTileService.kt     — плитка в «Быстрых настройках»
├── QeliWidgetProvider.kt  — виджет на рабочий стол
├── BootReceiver.kt        — автоподключение после перезагрузки
├── UpdateChecker.kt       — проверка обновлений (opt-in)
├── crypto/BackupCrypto.kt — шифрование импорта/экспорта профилей (не transport)
└── model/Config.kt        — разбор/сборка flat-INI и `qeli://`
```

## Возможности

| Возможность | Как работает |
|---|---|
| **Импорт профиля** | QR-код (камера), вставка `qeli://`-ссылки, тап по `qeli://`-ссылке (deep link), файл в формате flat-INI или legacy-JSON |
| **Авто-транспорт** | Проба профилей / режимов и выбор лучшего; failover при обрыве |
| **SNI на главном** | Пресет / свой host, Apply SNI, panel `/api/speedtest` |
| **Вкладка SNI** | Каталог ~285 хостов, Top-20, TLS+HTTPS probe, Apply best (без OOM на полном списке) |
| **Per-app split tunnel** | Выбор приложений в режиме «только эти» (`addAllowedApplication`) или «кроме этих» (`addDisallowedApplication`) |
| **Плитка Quick Settings** | Подключение/отключение из шторки |
| **Виджет** | Статус и переключение с рабочего стола |
| **Автоподключение** | После перезагрузки (`BOOT_COMPLETED`) и/или при запуске приложения |
| **Доверенный Wi-Fi** | Локальный список точных SSID: Qeli снимает VPN в доверенной сети и восстанавливает его после выхода. При Android lockdown/`kill_switch` пауза запрещена, потому что TUN обязан оставаться установленным |
| **Автоопрос профилей** | Настраиваемая проверка доступности только пока приложение видно и VPN отключён; её можно полностью выключить, при этом ручные проверки остаются доступны |
| **Доступ к локальной сети** | Тумблер «разрешить LAN» при full-tunnel (принтеры, NAS, роутер) |
| **Бэкап профилей** | Экспорт/импорт JSON; **с парольной фразой** — шифрованный контейнер (PBKDF2-HMAC-SHA256 + AES-256-GCM), совместимый с десктопом. Пустая фраза = **открытый JSON с паролями** |
| **Формат времени в логе** | Пять вариантов, совпадают с серверным `[logging] time_format` — удобно сверять логи |
| **Проверка обновлений** | Opt-in, выключена по умолчанию |

## Разрешения и зачем они

| Разрешение | Зачем |
|---|---|
| `INTERNET`, `ACCESS_NETWORK_STATE`, `ACCESS_WIFI_STATE` | сеть, выбранный физический carrier и реакция на его смену (Wi-Fi ⇄ LTE) |
| `NEARBY_WIFI_DEVICES`, `ACCESS_FINE_LOCATION` | чтение текущего SSID для доверенного Wi-Fi; без runtime-разрешения SSID считается неизвестным и VPN остаётся включённым |
| `FOREGROUND_SERVICE` + `FOREGROUND_SERVICE_SPECIAL_USE` | туннель как foreground-сервис |
| `POST_NOTIFICATIONS` | уведомление активного туннеля (Android 13+) |
| `WAKE_LOCK` | не терять соединение в глубоком сне |
| `RECEIVE_BOOT_COMPLETED` | автоподключение после перезагрузки (если включено) |
| `REQUEST_IGNORE_BATTERY_OPTIMIZATIONS` | чтобы система не убивала туннель |
| `CAMERA` | сканирование QR с профилем |
| `QUERY_ALL_PACKAGES` | список приложений для per-app split tunnel |

## Запуск

1. Установите APK со страницы **GitHub Releases** (или соберите, см. ниже).
2. На сервере выдайте ссылку: `qeli add-client <user> --link --host <хост:порт>`.
3. В приложении: **Add profile → Scan QR** или вставьте `qeli://`-ссылку — профиль
   появится со всеми параметрами и **запиненным ключом сервера**.
4. Нажмите кольцо подключения и подтвердите системный запрос VPN.

Full-tunnel, «маршрутизировать локальные сети», LAN-доступ и per-app split tunnel
переключаются в приложении и **не передаются** в `qeli://`-ссылке — это локальные настройки.
Список доверенных SSID также хранится только на устройстве. Совпадение выполняется точно и
регистрозависимо; точка доступа с тем же именем может подделать доверенную сеть, поэтому эту
функцию нельзя считать криптографической проверкой Wi-Fi.

## Сборка из исходников

### Debug (быстро, для разработки)

Нужен Android SDK и JDK 17+.

```bash
cd qeli-android
./gradlew assembleDebug        # app/build/outputs/apk/debug/app-debug.apk
./gradlew testDebugUnitTest
```

### Release APK (sideload, обновление поверх)

**Windows (рекомендуется, без локального JDK):**

```powershell
powershell -ExecutionPolicy Bypass -File scripts/agent/build_android_apk.ps1
```

**Артефакт для телефона:**

```
qeli-android/dist/qeli-android-<version>.apk
```

Пример: `qeli-android/dist/qeli-android-0.7.16.apk`

#### Подпись и in-place update

Samsung / Android 14 **не ставят unsigned** release (`app-release-unsigned.apk` → «Приложение не установлено»).

Скрипт `build_android_apk.ps1` при **первом** запуске создаёт локальные файлы (в git не попадают):

| Файл | Зачем |
|------|--------|
| `qeli-dev-release.jks` | Постоянный dev-ключ подписи |
| `keystore.properties` | Пароли к keystore (шаблон: `keystore.properties.example`) |

**Чтобы обновлять поверх без удаления:**

1. Всегда собирать через **тот же** `qeli-dev-release.jks` (не удалять).
2. Перед каждым релизом поднимать `versionCode` в `app/build.gradle.kts` (720 → 721 → …).
3. `applicationId` не менять (`com.qeli`).

Если на телефоне уже стоит Qeli с **другой** подписью (debug с другого ПК, старый unsigned, GitHub release с production keystore) — **один раз** удалите приложение и поставьте dev APK. Дальше все сборки с `qeli-dev-release.jks` обновляются поверх; профили сохраняются только если делали backup внутри Qeli.

Без `keystore.properties` Gradle подписывает release **debug-ключом** (fallback в `build.gradle.kts`) — подходит только если на телефоне стоял debug с **этого же** ПК.

#### Production keystore

Для публикации в store / GitHub Releases — свой `qeli-release.jks` по [`keystore.properties.example`](keystore.properties.example). **Не коммитить** keystore и пароли.

#### Docker вручную

```powershell
docker run --rm -v "c:/projects/home/qeli/qeli-android:/project" -w /project `
  -e ANDROID_HOME=/opt/android-sdk-linux `
  ghcr.io/cirruslabs/android-sdk:35 bash -lc "./gradlew --no-daemon assembleRelease"
```

Нужны `keystore.properties` + `.jks` в каталоге проекта (volume mount).

---

Нативное ядро (`libqeli.so`) в репозитории уже собрано — пересобирать только при изменении Rust-клиента (см. `scripts/build_android_so_11.py`, `native-libs/README.md`).

Runbook агента: [`../scripts/AGENT_DEB_BUILD_DEPLOY.md`](../scripts/AGENT_DEB_BUILD_DEPLOY.md) · [`../scripts/agent/README.md`](../scripts/agent/README.md)

> Инкрементальная сборка иногда раздувает APK — `./gradlew clean` и пересобрать.
