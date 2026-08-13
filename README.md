# Qeli

> **Fork notice — отличия от upstream**
>
> Upstream (оригинальный автор): [litvinovtd/qeli](https://github.com/litvinovtd/qeli)
>
> Этот форк: [raperrros-byte/qeli](https://github.com/raperrros-byte/qeli)
>
> Maintained by: Daniil Nekrasov \<raperrros@yandex.ru\>
>
> Updated: 2026-08-13
>
> База сравнения — upstream `v0.7.14`. В форке добавлено (новые сверху):
>
> - **Клиент Win/mac: REALITY seal для профиля `reality` (:8443).** Профиль
>   `mode=fake-tls` + `reality_sid` вставляет AEAD-токен в ClientHello `session_id`
>   (как Rust-клиент). Без этого сервер считает пробу и отдаёт ответ decoy →
>   `Failed to parse hybrid ServerHello`.
> - **Метрики панели по HTTPS-домену.** Если `ServerPanelUrl` пуст и хост — домен
>   (не IP), клиент берёт `https://{host}` вместо устаревшего `http://{host}:8080`.
> - **Чистая переустановка lab:** [`scripts/deploy/clean-reinstall-lab.sh`](scripts/deploy/clean-reinstall-lab.sh)
>   (wipe conf/users/identity → `.deb` → nginx SNI → пользователь + share-links).
> - **Режим трафика на главном экране (Windows):** туннель XOR локальный SOCKS/HTTP
>   прокси (порт/режим) применяется сразу ко **всем** профилям; при активном
>   соединении выполняется reconnect.
> - **Массовое удаление профилей (Windows):** чекбоксы в списке + удаление выбранных.
> - **Локальный SOCKS5 и HTTP CONNECT прокси** в Rust CLI и клиентах Windows/macOS.
>   Ключи `proxy`, `proxy_listen`, `proxy_mode` дают per-app туннелирование при
>   split-tunnel на уровне ОС (`gateway = false`).
> - **Рабочий Windows split-proxy routing.** Для адресов через прокси временно ставятся
>   reference-counted маршруты `/32` через Wintun на время соединения; при закрытии или
>   disconnect они снимаются. Иначе физический default route перехватывает сокеты,
>   привязанные к адресу туннеля.
> - **Пресеты маршрутизации прокси в стиле v2rayN:** proxy all, bypass Russia,
>   только заблокированные в РФ, bypass mainland China, GFW blacklist. Окно Settings
>   скачивает `geosite.dat` / `geoip.dat`; каждое соединение классифицируется как
>   proxy / direct / blocked.
> - **Журнал соединений** для proxy и TUN: принятые SOCKS/HTTP цели, решения
>   proxy/direct, TCP/UDP потоки через TUN и DNS-цели туннеля видны в логе десктопа.
> - **Мультипрофильный импорт/экспорт.** Windows/macOS принимают несколько секций
>   `[qeli]` или несколько `qeli://` из одного `.conf` / `.ini` / текста / буфера.
>   Веб-панель умеет выбрать все профили пользователя, скопировать пачкой или скачать
>   один `.conf` с учётками.
> - **Живая телеметрия Windows:** корректные userspace счётчики байт/скорости туннеля
>   (вместо ненадёжных счётчиков Wintun), итоги сессии, публичный IP сервера и графики
>   CPU/RAM с panel API. CPU — процент, занятые/всего ядра и load; RAM — used/total и %.
> - **Управление прокси в GUI:** вкл/выкл, порт, режим SOCKS5/HTTP/mixed, учётные данные
>   панели, geo-пресеты маршрутизации.
> - **Мгновенное применение настроек.** Сохранение Settings переподключает активный
>   туннель, чтобы новые параметры proxy/routing вступили в силу без ручного disconnect.
> - **Изменяемые размеры окон Windows:** Settings, редактор профиля, QR/share, About и
>   текстовый ввод; длинные формы скроллятся, кнопки действий остаются видимыми.
> - **Надёжность маршрутизации Windows:** full-tunnel по умолчанию с interface metric `1`,
>   host-маршруты прокси детерминированно чистятся при teardown туннеля.
> - **Сервер: все профили по умолчанию.** `install-qeli-server.sh` без `QELI_PROFILE`
>   включает весь multiprofile (reality-tls, fake-tls, udp-quic, obfs, …). Один профиль —
>   legacy через `QELI_PROFILE=…` или `QELI_SINGLE_PROFILE=1`.
> - **Сервер: nginx stream SNI на :443.** Панель на домене и reality-tls на одном порту:
>   nginx `ssl_preread` → панель `127.0.0.1:8080`, остальной SNI → `127.0.0.1:4430`.
>   Скрипт деплоя: [`scripts/deploy/nginx-panel-sni.sh`](scripts/deploy/nginx-panel-sni.sh).
> - **Сервер: allowlist панели в nginx `geo`.** После stream qeli видит peer `127.0.0.1` —
>   фильтр IP только в nginx, не в `web.allowed_ips`.
> - **Сервер: `bind.public_port`.** Порт в `qeli://` ссылках, когда listen за nginx
>   (например listen `4430`, public `443`).
> - **Импорт: имя профиля в label.** Share/export всегда пишет имя профиля в fragment,
>   Windows/macOS показывают `WireMode · host (user)`, если label пустой.
>
> Прод-данные с живыми паролями/ключами/IP в Git **не** попадают (см. `.gitignore` и
> [`scripts/lab_secrets.example`](scripts/lab_secrets.example)).
>
> ---
>
> ## Быстрый старт — сервер (форк)
>
> Нужно: **VPS Debian 12/13** (root), открытые порты в firewall, клиент Windows/macOS/Android.
>
> | Что | Зачем |
> |-----|--------|
> | Docker на ПК | собрать `.deb` локально |
> | Домен + TLS-сертификат | только для варианта **с nginx** (панель на `:443`) |
> | `scripts/.lab_secrets` | локально: IP, пароли SSH/панели (шаблон — `lab_secrets.example`) |
>
> ### Шаг 1. Собрать `.deb` (на своём ПК)
>
> ```bash
> # Linux / macOS — из корня репозитория
> docker run --rm -v "$PWD:/w" -w /w rust:1.88-bookworm bash -c '
>   set -euo pipefail
>   export PATH=/usr/local/cargo/bin:$PATH DEBIAN_FRONTEND=noninteractive
>   apt-get update -qq && apt-get install -y --no-install-recommends make dpkg-dev binutils xz-utils >/dev/null
>   make -C qeli/debian deb
>   make -C qeli/debian stage BINARY=../target/release/qeli \
>     DEB_DIR=/tmp/qeli_pkg/qeli_0.7.14_amd64 BUILD_DIR=/tmp/qeli_pkg
>   find /tmp/qeli_pkg -type d -exec chmod 755 {} \;
>   find /tmp/qeli_pkg -type f -exec chmod 644 {} \;
>   dpkg-deb --root-owner-group -Zxz --build /tmp/qeli_pkg/qeli_0.7.14_amd64 /w/qeli/debian/qeli_0.7.14_amd64.deb
> '
> ```
>
> Windows: те же команды, путь `c:/projects/home/qeli:/w` — см.
> [`scripts/AGENT_DEB_BUILD_DEPLOY.md`](scripts/AGENT_DEB_BUILD_DEPLOY.md).
>
> ### Шаг 2a. Установка **без nginx** (простой вариант)
>
> Подходит для lab / когда панель не нужна на `:443`. **reality-tls** слушает `:443` напрямую.
>
> ```bash
> scp qeli/debian/qeli_*_amd64.deb install-qeli-server.sh root@SERVER:/tmp/
> ssh root@SERVER
> echo "qeli qeli/run-as select root" | debconf-set-selections
>
> QELI_DEB=/tmp/qeli_*_amd64.deb \
> QELI_RUN_AS=root \
> bash /tmp/install-qeli-server.sh SERVER_PUBLIC_IP
> ```
>
> По умолчанию поднимаются **все профили**. Панель — loopback `:8080`:
>
> ```bash
> ssh -L 8080:127.0.0.1:8080 root@SERVER
> # браузер: https://127.0.0.1:8080  (логин/пароль из вывода инсталлятора)
> ```
>
> Панель снаружи (осторожно): `QELI_PANEL_PUBLIC=1 QELI_PANEL_ALLOWED_IPS=<YOUR_CIDR>`.
>
> Один профиль: `QELI_PROFILE=reality-tls bash /tmp/install-qeli-server.sh …`
>
> Если `systemctl` падает с `Permission denied` — замените unit:
> [`scripts/deploy/qeli-systemd-lab.service`](scripts/deploy/qeli-systemd-lab.service).
>
> ### Шаг 2b. Установка **с nginx** (панель на домене + reality-tls на :443)
>
> Когда на одном `:443` нужны и **HTTPS-панель** (по домену), и **reality-tls**.
>
> 1. DNS: `panel.example.com` → IP сервера.
> 2. Установите qeli (шаг 2a, можно с `QELI_FORCE_RECONFIG=1` при повторе).
> 3. Загрузите PEM (fullchain + key) и запустите деплой nginx:
>
> ```bash
> scp /path/to/wildcard.pem root@SERVER:/tmp/qeli-panel.pem
> scp scripts/deploy/nginx-panel-sni.sh root@SERVER:/tmp/
> ssh root@SERVER
> PANEL_DOMAIN=panel.example.com \
> PANEL_ALLOW_CIDRS=203.0.113.0/24,10.9.0.0/16 \
> TLS_CERT_PEM=/tmp/qeli-panel.pem \
> bash /tmp/nginx-panel-sni.sh
> ```
>
> Что делает скрипт:
> - nginx stream на `:443` (SNI → панель или reality-tls);
> - `[profile:reality-tls]` → `127.0.0.1:4430` + `bind.public_port = 443`;
> - `[web]` → `127.0.0.1:8080`, TLS, `allowed_ips` **пустой** (фильтр в nginx `geo`);
> - все профили `enabled = true`.
>
> Чистая переустановка (wipe + nginx + один VPN-пользователь):
>
> ```bash
> PANEL_DOMAIN=panel.example.com \
> PANEL_ALLOW_CIDRS=203.0.113.0/24,10.9.0.0/16 \
> TLS_CERT_PEM=/tmp/qeli-panel.pem \
> PANEL_PASSWORD='…' \
> bash /tmp/clean-reinstall-lab.sh
> ```
>
> При `QELI_RUN_AS=root` после install: `chown -R root:root /etc/qeli /var/log/qeli`
> (иначе `users.conf` mode `600` у `qeli:` → Permission denied).
>
> Пример конфига nginx: [`scripts/deploy/nginx-stream-qeli-sni.conf.example`](scripts/deploy/nginx-stream-qeli-sni.conf.example).
>
> Подробно: [`scripts/AGENT_DEB_BUILD_DEPLOY.md`](scripts/AGENT_DEB_BUILD_DEPLOY.md) §3.
>
> ### Шаг 3. Клиент
>
> 1. Скачайте/соберите клиент: Windows — `qeli-win/dist/QeliWin.exe` (или `dist-build/`).
> 2. Импортируйте `qeli://` из `/etc/qeli/client-links/` на сервере или из панели
>    (для профиля `reality` на `:8443` в ссылке **обязателен** `rsid=`).
> 3. Подключитесь (для reality-tls хост = домен/`SERVER_PUBLIC_IP`, порт `443`).
> 4. Метрики CPU/RAM: Settings → Panel URL `https://panel.example.com`, admin + пароль.
>
> Документация: [docs/ru/GETTING-STARTED.md](docs/ru/GETTING-STARTED.md) ·
> [docs/ru/PANEL.md](docs/ru/PANEL.md).
>
> Дополнительно: portable ABI — `make -C qeli/debian deb-portable` (zig + cargo-zigbuild).
> Runbook для агента/Windows: [`scripts/AGENT_DEB_BUILD_DEPLOY.md`](scripts/AGENT_DEB_BUILD_DEPLOY.md).

**Qeli** (Quick Easy Link IP) — a self-hosted VPN with its own L4 protocol and built-in
obfuscation over TCP or UDP. It aims at resilience against passive / signature-based DPI
while keeping the convenience of a classic full-tunnel TUN VPN, and ships with a web admin
panel.

**Документация на русском → [docs/ru/index.md](docs/ru/index.md)** ·
**Documentation in English → [docs/eng/index.md](docs/eng/index.md)**

---

## What it is

- **A TUN VPN, not a per-application proxy**: routing and DNS are handled at the OS level,
  so every application is covered without being configured. Full-tunnel and split-tunnel are
  both first-class — phones default to full-tunnel, the CLI and desktop clients to split.
- **Wire modes**: `plain` · `fake-tls` (TLS 1.3 mimicry) · `obfs` (ChaCha20 stream +
  WebSocket fronting) · `reality` / `reality-tls` (real TLS 1.3 carries the tunnel) ·
  QUIC-masking for UDP.
- **Post-quantum handshake**: hybrid X25519 + ML-KEM-768, ChaCha20-Poly1305 data plane.
- **Web admin panel** with `qeli://` link / QR issuance, Argon2id login, native HTTPS.
- **Server**: Linux (TUN/TAP). **Clients**: Linux CLI · Windows · macOS · Android ·
  Keenetic / OpenWrt routers — plus iOS, which is feature-complete but has never been run
  on a device and ships nothing yet ([details](qeli-ios/README.md)).

## Works under active DPI

Qeli is built for networks where ordinary VPN protocols (WireGuard, OpenVPN, IKEv2) are
fingerprinted and blocked — Iran, China (the Great Firewall) and Russia (TSPU). The
`reality-tls` mode performs a genuine TLS 1.3 handshake against a real third-party site, so
the connection looks like ordinary HTTPS to that site and resists both active probing and
SNI-based blocking; traffic shaping adds idle cover traffic so the flow does not read as a
bulk download to statistical DPI.

> In spirit a self-hosted alternative to Xray / V2Ray / sing-box (REALITY/VLESS) setups, but
> with its own protocol, native GUI clients and a post-quantum handshake.

## Quick start

**One command on a clean Linux server (Debian/Ubuntu), as root:**

```bash
curl -fsSLO https://raw.githubusercontent.com/litvinovtd/qeli/main/install-qeli-server.sh
```

Review it, then run `bash install-qeli-server.sh`. Download-then-run (rather than
`curl … | bash`) exists so the script can be read before it executes as root; the installer
itself verifies the `.deb` against its SHA256.

The script installs the `.deb` from [Releases](https://github.com/litvinovtd/qeli/releases),
asks for the profile and the listen port (default `443`), writes a config with full-tunnel
NAT, creates users and prints ready-to-use `qeli://` links. Three profiles are offered:

| Profile | When to pick it |
|---------|-----------------|
| `reality-tls` | The default the installer provisions. Real TLS 1.3 over TCP:443 — survives active probing. |
| `fake-tls` | Cheaper on CPU; enough against passive/signature DPI. |
| `udp-quic` | A UDP path with QUIC-shaped datagrams — useful where TCP:443 is throttled, reset or otherwise degraded. |

For a non-interactive run set the answers up front:
`QELI_PROFILE=reality-tls|fake-tls|udp-quic` and/or `QELI_PORT=<1-65535>`.

Then install a client from Releases and paste or scan the link.

**Prefer to do it step by step?**

1. Install the server and create the first user — **[Getting started (EN)](docs/eng/GETTING-STARTED.md)** ·
   **[Установка с нуля (RU)](docs/ru/GETTING-STARTED.md)**.
2. Configure it — **[CONFIG (EN)](docs/eng/CONFIG.md)** · **[CONFIG (RU)](docs/ru/CONFIG.md)**.
3. Issue a `qeli://` link or QR from the web panel and import it into a client —
   **[PANEL (EN)](docs/eng/PANEL.md)** · **[PANEL (RU)](docs/ru/PANEL.md)**.

Something went wrong? → **[Troubleshooting (EN)](docs/eng/TROUBLESHOOTING.md)** ·
**[Диагностика (RU)](docs/ru/TROUBLESHOOTING.md)**.

## Repository layout

| Path | What it is |
|------|------------|
| `qeli/` | Rust daemon: server, client CLI, protocol core, web panel |
| `qeli-win/`, `qeli-mac/` | Desktop GUI clients (C#/.NET, shared core in `qeli-shared/`) — [Windows](qeli-win/README.md) · [macOS](qeli-mac/README.md) |
| `qeli-android/` | Android client (Kotlin) — [README](qeli-android/README.md) |
| `qeli-ios/` | iOS client (Swift), feature-complete but untested on a device — [README](qeli-ios/README.md) · [MDM](qeli-ios/MDM/README.md) |
| `qeli-openwrt/` | Router build (Keenetic / OpenWrt) — [README](qeli-openwrt/README.md) |
| `docs/` | Documentation — start at [docs/ru/index.md](docs/ru/index.md) / [docs/eng/index.md](docs/eng/index.md) |
| `release/` | Packaging: [Docker](release/docker/README.md), deb, release artefacts |
| `site/` | Project website |

## Status

Pre-1.0 / beta — the data plane is stable and covered by unit + end-to-end tests, but the
protocol may still change between minor versions. Release builds are published on the
**GitHub Releases** page and are not committed to git. The client **native cores** are the
exception: `libqeli.so` / `qeli.dll` / `libqeli.dylib` (plus third-party `wintun.dll`) are
committed under `native-libs/` and mirrored into each client tree, so the platform CI jobs
need only their own toolchain. Their hashes are pinned in `native-libs/SHA256SUMS` and
checked by the `native-libs` CI gate. This is an explicit trade-off against reproducibility
— see [THREAT-MODEL §4](docs/eng/THREAT-MODEL.md#4-assurance-status) ·
[Модель угроз §4](docs/ru/THREAT-MODEL.md#4-уровень-проверенности).

- Changes: **[CHANGELOG.md](CHANGELOG.md)**
- Security policy: **[SECURITY.md](SECURITY.md)**
- Contributing: **[CONTRIBUTING.md](CONTRIBUTING.md)**
- Licensing: **[LICENSE](LICENSE)** · **[LICENSING.md](LICENSING.md)**

This is a monorepo with **per-directory licences**: the core and server (`qeli/`) are
**AGPL-3.0-only**, the clients (`qeli-android/`, `qeli-win/`, `qeli-mac/`, `qeli-ios/`) are
**MPL-2.0**.
The full map, including the `libqeli`/AGPL note, is in [LICENSING.md](LICENSING.md).
Contributions use a DCO sign-off, no CLA — see [CONTRIBUTING.md](CONTRIBUTING.md).

---

<sub>**Keywords:** self-hosted VPN, anti-censorship VPN, censorship circumvention, anti-DPI,
DPI bypass, deep packet inspection, REALITY, Reality TLS, TLS camouflage, SNI,
active-probing resistant, traffic obfuscation, fake-TLS, obfs, QUIC VPN, post-quantum VPN,
ML-KEM-768, X25519, ChaCha20-Poly1305, Rust VPN, Android VPN, iOS VPN, Windows VPN, macOS
VPN, Keenetic, OpenWrt, WireGuard alternative, Xray / V2Ray / sing-box alternative, VPN for
Iran, VPN for China / Great Firewall, VPN for Russia / TSPU.</sub>
