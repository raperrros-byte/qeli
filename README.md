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
> База сравнения — upstream `v0.7.15`. В форке добавлено (новые сверху):
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
> - **Сервер: Docker.** Multiprofile в контейнере (`release/docker/`), порты 443/8443–8451;
>   с nginx на хосте — `:443` у nginx, не у контейнера. См. шаг 2c.
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
>     DEB_DIR=/tmp/qeli_pkg/qeli_0.7.15_amd64 BUILD_DIR=/tmp/qeli_pkg
>   find /tmp/qeli_pkg -type d -exec chmod 755 {} \;
>   find /tmp/qeli_pkg -type f -exec chmod 644 {} \;
>   dpkg-deb --root-owner-group -Zxz --build /tmp/qeli_pkg/qeli_0.7.15_amd64 /w/qeli/debian/qeli_0.7.15_amd64.deb
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
> ### Шаг 2c. Docker на сервере
>
> Альтернатива `.deb`: qeli в контейнере с `NET_ADMIN` + `/dev/net/tun`. Удобно для
> lab, CI и multiprofile без systemd. Подробности: [`release/docker/README.md`](release/docker/README.md).
>
> **Сборка образа** (из корня репо, на ПК или на VPS):
>
> ```bash
> docker buildx build -f release/docker/Dockerfile -t qeli:latest --load .
> ```
>
> **Multiprofile (все 10 профилей)** — скопируйте пример в volume и опубликуйте порты:
>
> ```bash
> mkdir -p data/server/etc data/server/lib
> docker run --rm -v "$PWD/data/server/etc:/etc/qeli" qeli:latest sh -c \
>   'cp /usr/share/qeli/server-multiprofile.conf.example /etc/qeli/server.conf && : > /etc/qeli/users.conf'
>
> docker run -d --name qeli-server \
>   --cap-add NET_ADMIN --cap-add NET_RAW --cap-add NET_BIND_SERVICE \
>   --device /dev/net/tun \
>   --sysctl net.ipv4.ip_forward=1 \
>   -v "$PWD/data/server/etc:/etc/qeli" \
>   -v "$PWD/data/server/lib:/var/lib/qeli" \
>   -e QELI_CONFIG=/etc/qeli/server.conf \
>   -p 443:443/tcp -p 8443:8443/tcp -p 8444:8444/tcp -p 8445:8445/tcp \
>   -p 8446:8446/tcp -p 8447:8447/tcp -p 8451:8451/tcp \
>   -p 8448:8448/udp -p 8449:8449/udp -p 8450:8450/udp \
>   -p 8080:8080/tcp \
>   qeli:latest server
> ```
>
> Пользователь и ссылки:
>
> ```bash
> docker exec qeli-server qeli add-client daniil --config /etc/qeli/server.conf --link --host SERVER_PUBLIC_IP
> docker exec qeli-server qeli set-web-password --password '…' --config /etc/qeli/server.conf
> docker restart qeli-server
> ```
>
> **Панель:** в `[web]` задайте `bind = 0.0.0.0`, `password_hash` (через `set-web-password`),
> опубликуйте `-p 8080:8080`. Логи: `docker logs -f qeli-server`.
>
> **Docker + nginx на хосте (панель на домене):** nginx stream держит `:443` на **хосте**,
> контейнер **не** публикует `443:443`. Внутри контейнера `reality-tls` слушает `:4430`,
> `bind.public_port = 443`; снаружи nginx проксирует SNI → `127.0.0.1:4430` (проброс
> `-p 4430:4430` или `network_mode: host`). Остальные профили — `-p 8443:8443` и т.д.
> Скрипт nginx: [`scripts/deploy/nginx-panel-sni.sh`](scripts/deploy/nginx-panel-sni.sh)
> (редактирует `/etc/qeli/server.conf` на хосте — для Docker монтируйте тот же volume).
>
> **compose:** `docker compose -f release/docker/docker-compose.yml up -d` (один профиль
> по умолчанию; для multiprofile замените `server.conf` в `./data/server/etc/`).
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

<p align="center">
  <img src="assets/branding/qeli-logo.png" alt="Qeli logo" width="180">
</p>

**Qeli** (Quick Easy Link IP) — a self-hosted VPN with its own L4 protocol and built-in
obfuscation over TCP or UDP. It aims at resilience against passive / signature-based DPI
while keeping the convenience of a classic full-tunnel TUN VPN, and ships with a web admin
panel.

---

**Qeli** (Quick Easy Link IP) — self-hosted VPN со своим L4-протоколом, встроенной
обфускацией (TCP/UDP) и веб-панелью. Цель — устойчивость к пассивному и
сигнатурному DPI при удобстве классического TUN VPN.

**Документация:** [RU](docs/ru/index.md) · [EN](docs/eng/index.md)

## Что это

- **TUN VPN** с optional per-app routing (Windows, macOS, Android): маршруты и DNS на
  уровне ОС; full- и split-tunnel. Локальный SOCKS/HTTP proxy в форке — для split-tunnel
  без замены qeli application-layer прокси.
- **Wire modes:** `plain` · `fake-tls` · `obfs` · `reality` / `reality-tls` · UDP+QUIC.
- **Post-quantum:** hybrid X25519 + ML-KEM-768, ChaCha20-Poly1305.
- **Панель:** `qeli://` / QR, Argon2id, HTTPS.
- **Сервер:** Linux (TUN), деплой `.deb` · **Docker** · nginx SNI. **Клиенты:** CLI,
  Windows, macOS, Android, Keenetic/OpenWrt; iOS — в репо, без релиза
  ([qeli-ios/README.md](qeli-ios/README.md)).

## Под активным DPI

`reality-tls` — настоящий TLS 1.3 к decoy-сайту; traffic shaping маскирует idle.
Профиль **`reality`** (`:8443`, fake-tls + REALITY proxy) требует `rsid` в ссылке —
см. fork-notice выше.

## Деплой сервера (форк)

| Способ | Когда |
|--------|--------|
| `.deb` + `install-qeli-server.sh` (шаги 1–2a в блоке выше) | production VPS, systemd |
| nginx SNI + панель на домене (шаг 2b) | `:443` = панель + reality-tls |
| Docker (шаг 2c) | lab, multiprofile, без systemd |
| [`clean-reinstall-lab.sh`](scripts/deploy/clean-reinstall-lab.sh) | wipe + nginx + пользователь |

Upstream one-liner (`curl …/litvinovtd/qeli/…`) ставит **upstream** `.deb` с GitHub
Releases — для форка собирайте `.deb` локально (шаг 1) или образ из **этого** репо.

Пошагово: [docs/ru/GETTING-STARTED.md](docs/ru/GETTING-STARTED.md) ·
[docs/eng/GETTING-STARTED.md](docs/eng/GETTING-STARTED.md) ·
[CONFIG RU](docs/ru/CONFIG.md) · [PANEL RU](docs/ru/PANEL.md).

Проблемы: [docs/ru/TROUBLESHOOTING.md](docs/ru/TROUBLESHOOTING.md) ·
[docs/eng/TROUBLESHOOTING.md](docs/eng/TROUBLESHOOTING.md).

## Структура репозитория

| Путь | Назначение |
|------|------------|
| `qeli/` | Rust: сервер, CLI, протокол, панель |
| `qeli-win/`, `qeli-mac/` | Desktop GUI — [Win](qeli-win/README.md) · [macOS](qeli-mac/README.md) |
| `qeli-android/` | Android — [README](qeli-android/README.md) |
| `qeli-shared/` | Общее ядро C# (клиенты + REALITY seal) |
| `scripts/deploy/` | nginx SNI, clean-reinstall, lab systemd unit |
| `scripts/AGENT_DEB_BUILD_DEPLOY.md` | Runbook сборки `.deb` и деплоя |
| `release/docker/` | **Docker-образ** сервера/клиента — [README](release/docker/README.md) |
| `docs/` | Документация RU/EN |

## Статус

Pre-1.0 / beta. Релизы форка: [GitHub Releases](https://github.com/raperrros-byte/qeli/releases)
(если опубликованы) или локальная сборка `.deb` / Docker из `main`.

Native cores (`libqeli.so`, `qeli.dll`, …) — в `native-libs/` с pin в `SHA256SUMS`.

- [CHANGELOG.md](CHANGELOG.md) · [SECURITY.md](SECURITY.md) · [CONTRIBUTING.md](CONTRIBUTING.md)
- Лицензии: core/server **AGPL-3.0**, клиенты **MPL-2.0** — [LICENSING.md](LICENSING.md)

---

<sub>**Keywords:** self-hosted VPN, anti-censorship VPN, anti-DPI, REALITY, fake-TLS,
obfs, QUIC VPN, post-quantum VPN, Rust VPN, Docker VPN, nginx SNI, WireGuard alternative,
Xray / V2Ray / sing-box alternative.</sub>
