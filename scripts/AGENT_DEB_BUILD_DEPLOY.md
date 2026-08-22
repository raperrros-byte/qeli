# Runbook агента: сборка, деплой и клиенты (Windows + Docker)

Maintained by: Daniil Nekrasov <raperrros@yandex.ru>  
Updated: 2026-08-22

Для AI-агентов в `c:\projects\home\qeli` (Windows + Docker Desktop).  
**Не собирать на VPS.** `.deb` — локально в Docker, на сервер только копирование + установка.

Скрипты pipeline: [`agent/README.md`](agent/README.md). Android подробно: [`../qeli-android/README.md`](../qeli-android/README.md).

Секреты: [`lab_secrets.example`](lab_secrets.example) → `scripts/.lab_secrets` (в git не коммитить).

---

## Цель

1. Собрать `qeli_<ver>_amd64.deb` из **этого форка**.
2. Поставить на Debian lab VPS.
3. Поднять VPN (все профили) + панель на домене через **nginx stream SNI**, без поломки **reality-tls**.

База: Debian 12/13, версия из `qeli/debian/control`, образ `rust:1.88-bookworm`.

### Быстрый pipeline (рекомендуется)

Один скрипт: deb → docker smoke → host2 deploy → Win + Android:

```powershell
pwsh -File scripts/agent/release_flow.ps1
```

Только сборка + локальный smoke (без деплоя):

```powershell
pwsh -File scripts/agent/release_flow.ps1 -SkipDeploy -SkipClients
```

Отдельные шаги:

```powershell
# .deb (PowerShell — см. build_deb_docker.sh или release_flow.ps1)
powershell -ExecutionPolicy Bypass -File scripts/agent/release_flow.ps1 -SkipDeploy -SkipClients

powershell -ExecutionPolicy Bypass -File scripts/agent/docker_smoke_local.ps1

docker run --rm -v "c:/projects/home/qeli:/w" -w /w alpine:3.20 sh /w/scripts/deploy/remote-upgrade-host2.sh
docker run --rm -v "c:/projects/home/qeli:/w" -w /w alpine:3.20 sh /w/scripts/deploy/remote-smoke-api-host2.sh

# Android (подписанный APK → qeli-android/dist/)
powershell -ExecutionPolicy Bypass -File scripts/agent/build_android_apk.ps1
```

---

## 0. Предусловия

- Docker Desktop на Windows.
- Репозиторий: `c:\projects\home\qeli`.
- SSH: `ssh-keyscan` + Docker `alpine` + `sshpass` (plink в batch ненадёжен).
- DNS: `PANEL_DOMAIN` → IP сервера.
- TLS: PEM fullchain + private key (локальный файл, в git не класть).

Секреты:

```powershell
Copy-Item scripts\lab_secrets.example scripts\.lab_secrets
# заполнить SSH_HOST, PANEL_DOMAIN, PANEL_ALLOW_CIDRS, пароли
```

SSH/SCP из PowerShell — скрипты класть в файлы и `scp`, не городить вложенные кавычки.

**Ловушки PowerShell:** `` `$PATH `` в контейнере; не `bash -lc` у образа `rust`; `|` в `grep -E` ломает PowerShell — remote `.sh`.

---

## 1. Сборка `.deb` в Docker

### 1.1 Компиляция

```powershell
docker run --rm -v "c:/projects/home/qeli:/w" -w /w rust:1.88-bookworm bash -c "set -euo pipefail; export PATH=/usr/local/cargo/bin:`$PATH; export DEBIAN_FRONTEND=noninteractive; apt-get update -qq; apt-get install -y --no-install-recommends make dpkg-dev binutils xz-utils >/dev/null; make -C qeli/debian clean || true; make -C qeli/debian deb"
```

### 1.2 Упаковка из `/tmp` (fix Windows mount 777)

```powershell
docker run --rm -v "c:/projects/home/qeli:/w" -w /w rust:1.88-bookworm bash -c "set -euo pipefail; export PATH=/usr/local/cargo/bin:`$PATH; export DEBIAN_FRONTEND=noninteractive; apt-get update -qq; apt-get install -y --no-install-recommends make dpkg-dev binutils xz-utils >/dev/null; cd /w/qeli/debian; test -f ../target/release/qeli; rm -rf /tmp/qeli-deb; make stage BINARY=../target/release/qeli DEB_DIR=/tmp/qeli-deb/qeli_0.7.14_amd64 BUILD_DIR=/tmp/qeli-deb; find /tmp/qeli-deb -type d -exec chmod 755 {} \; ; find /tmp/qeli-deb -type f -exec chmod 644 {} \; ; chmod 755 /tmp/qeli-deb/qeli_0.7.14_amd64/usr/bin/qeli /tmp/qeli-deb/qeli_0.7.14_amd64/DEBIAN/postinst /tmp/qeli-deb/qeli_0.7.14_amd64/DEBIAN/prerm /tmp/qeli-deb/qeli_0.7.14_amd64/DEBIAN/config; dpkg-deb --root-owner-group -Zxz --build /tmp/qeli-deb/qeli_0.7.14_amd64 /w/qeli/debian/qeli_0.7.14_amd64.deb; ls -lah /w/qeli/debian/qeli_0.7.14_amd64.deb"
```

Артефакт: `qeli/debian/qeli_0.7.14_amd64.deb`.

---

## 2. Деплой пакета

### 2.1 Копирование

- `.deb` → `/tmp/`
- `install-qeli-server.sh` → `/tmp/`
- (с nginx) `scripts/deploy/nginx-panel-sni.sh` + PEM → `/tmp/`

### 2.2 Установка (все профили по умолчанию)

```bash
QELI_DEB=/tmp/qeli_0.7.14_amd64.deb \
QELI_FORCE_RECONFIG=1 \
QELI_RUN_AS=root \
bash /tmp/install-qeli-server.sh <SERVER_PUBLIC_IP>
```

- `QELI_FORCE_RECONFIG=1` — переписывает `/etc/qeli/server.conf` (бэкап с timestamp).
- Один профиль (legacy): `QELI_PROFILE=reality-tls` или `QELI_SINGLE_PROFILE=1`.
- Не ставить **fake-tls на :443** — порт 443 для reality-tls / nginx.

### 2.3 Nginx + панель на домене

```bash
PANEL_DOMAIN=panel.example.com \
PANEL_ALLOW_CIDRS=203.0.113.0/24,10.9.0.0/16 \
TLS_CERT_PEM=/tmp/qeli-panel.pem \
bash /tmp/nginx-panel-sni.sh
```

Скрипт: [`deploy/nginx-panel-sni.sh`](deploy/nginx-panel-sni.sh).

### 2.4 Systemd `Permission denied`

Минимальный root unit: [`deploy/qeli-systemd-lab.service`](deploy/qeli-systemd-lab.service).

```bash
cp qeli-systemd-lab.service /etc/systemd/system/qeli.service
systemctl daemon-reload && systemctl restart qeli
```

После `QELI_RUN_AS=root`:

```bash
chown -R root:root /etc/qeli /var/log/qeli
chmod 600 /etc/qeli/users.conf
systemctl restart qeli
```

### 2.5 Чистая переустановка (wipe + nginx)

Скрипт: [`deploy/clean-reinstall-lab.sh`](deploy/clean-reinstall-lab.sh).

```bash
# на сервере уже лежат: .deb, install-qeli-server.sh, nginx-panel-sni.sh, PEM
PANEL_DOMAIN=panel.example.com \
PANEL_ALLOW_CIDRS=203.0.113.0/24,10.9.0.0/16 \
TLS_CERT_PEM=/tmp/qeli-panel.pem \
PANEL_PASSWORD='…' \
VPN_USER=daniil \
bash /tmp/clean-reinstall-lab.sh
```

Ссылки: `/etc/qeli/client-links/<user>-all.txt`. Перегенерация: `PANEL_DOMAIN=… bash regen-links.sh`.

Проверка API/метрик на сервере:

```bash
PANEL_DOMAIN=panel.example.com PANEL_PASSWORD='…' bash /tmp/verify-local-api.sh
```

---

## 3. Nginx + порты (обязательно менять И nginx, И server.conf)

**Проблема:** и VPN (reality-tls), и HTTPS-панель хотят публичный **TCP :443**.  
Один процесс на `:443` — **nginx `stream` + `ssl_preread` (SNI)**.

Пакеты: `nginx`, `libnginx-mod-stream`, `openssl`.

### 3.1 Карта портов

| Кто | Адрес:порт | Назначение |
|-----|------------|------------|
| **nginx stream** | `0.0.0.0:443` | SNI-роутер |
| **qeli reality-tls** | `127.0.0.1:4430` listen + `bind.public_port = 443` | VPN; в ссылках порт **443** |
| **qeli [web] панель** | `127.0.0.1:8080` | TLS terminate в qeli |
| **qeli reality** | `0.0.0.0:8443` | профиль reality |
| **qeli fake-tls … obfs-awg** | `8444`–`8451`, UDP `8448`–`8450` | остальные профили |

SNI:

| SNI | Куда |
|-----|------|
| `PANEL_DOMAIN` (разрешённый IP) | `127.0.0.1:8080` |
| `PANEL_DOMAIN` (запрещённый IP) | `127.0.0.1:9` (drop) |
| всё остальное | `127.0.0.1:4430` (reality-tls) |

**Не** вешать внутренний SSL nginx на `:8443` — порт профиля `reality`.

### 3.2 `/etc/qeli/server.conf`

```ini
[profile:reality-tls]
bind.address = 127.0.0.1
bind.port = 4430
bind.public_port = 443            # порт в qeli:// (nginx снаружи на :443)

[web]
bind = 127.0.0.1
port = 8080
tls = true
tls_cert = /etc/qeli/web-tls-cert.pem
tls_key  = /etc/qeli/web-tls-key.pem
secure_cookie = true
public_host = panel.example.com
allowed_origins = panel.example.com
allowed_ips =                     # ПУСТО — peer после stream = 127.0.0.1
trusted_proxies =
```

Фильтр клиентов — **только** в nginx `geo`, не в `web.allowed_ips`.

### 3.3 Nginx stream

Шаблон: [`deploy/nginx-stream-qeli-sni.conf.example`](deploy/nginx-stream-qeli-sni.conf.example).

```nginx
geo $remote_addr $panel_allowed {
    203.0.113.0/24 1;
    10.9.0.0/16    1;
    default         0;
}

map "$ssl_preread_server_name:$panel_allowed" $qeli_upstream {
    "panel.example.com:1"  127.0.0.1:8080;
    "panel.example.com:0"  127.0.0.1:9;
    default                127.0.0.1:4430;
}
```

В `/etc/nginx/nginx.conf`:

```nginx
stream { include /etc/nginx/stream.d/*.conf; }
```

Проверка:

```bash
nginx -t && systemctl reload nginx && systemctl restart qeli
curl -skI https://panel.example.com/login   # 200 с allow CIDR; иначе connection refused
```

### 3.4 Пароль панели

```bash
qeli set-web-password --password '<PASSWORD>' --config /etc/qeli/server.conf
systemctl restart qeli
```

---

## 4. Чеклист после деплоя

- [ ] `systemctl is-active qeli nginx` → `active`
- [ ] Публично `:443` — nginx; qeli **не** на `0.0.0.0:443`
- [ ] qeli: `127.0.0.1:4430`, `127.0.0.1:8080`, профили 8443–8451
- [ ] Панель с allow CIDR — OK; снаружи — refused
- [ ] reality-tls short_id не ротировали без нужды
- [ ] Секреты не в git

---

## 5. Типичные сбои

| Сбой | Причина | Фикс |
|------|---------|------|
| `dpkg-deb bad permissions 777` | Windows mount | stage в `/tmp` |
| systemd `Permission denied` | Hardened unit | `qeli-systemd-lab.service` |
| `users.conf` Permission denied | root unit + файл `qeli:qeli` 600 | `chown -R root:root /etc/qeli` |
| `unknown directive "stream"` | Нет модуля | `libnginx-mod-stream` |
| Панель 403 всегда | `web.allowed_ips` при stream | allowlist в nginx `geo`, `allowed_ips` пустой |
| nginx `:8443` | Конфликт с `reality` | другой порт или passthrough на 8080 |
| fake-tls на `:443` | Занят | fake-tls только `:8444` |
| `Failed to parse hybrid ServerHello` на `:8443` | нет `rsid` / старый Win-клиент | импорт с `rsid=`; клиент с RealitySession seal |
| CPU/RAM «—» в Win | Panel URL `:8080` / нет пароля | Settings → `https://PANEL_DOMAIN` + admin |
| reality-tls link на `:4430` | нет `bind.public_port` | `upgrade-public-port.sh` / nginx-panel-sni.sh |
| Android «Приложение не установлено» | unsigned APK или другая подпись | `build_android_apk.ps1`; см. [`../qeli-android/README.md`](../qeli-android/README.md) |
| Android не обновляется поверх | другой keystore / тот же `versionCode` | тот же `qeli-dev-release.jks`; поднять `versionCode` |

---

## 6. Android release APK

```powershell
powershell -ExecutionPolicy Bypass -File scripts/agent/build_android_apk.ps1
```

- **Выход:** `qeli-android/dist/qeli-android-<version>.apk` (подписан).
- **Первый запуск:** создаёт `qeli-dev-release.jks` + `keystore.properties` (git-ignored).
- **Обновление на телефоне:** тот же `.jks` + `versionCode` выше установленного.
- **Один раз удалить** старый Qeli, если раньше стояла другая подпись (debug/unsigned/GitHub).

Подробности, Samsung S24, backup профилей: [`../qeli-android/README.md`](../qeli-android/README.md) § Release APK.

---

## 7. Справка

- Упаковка: `qeli/debian/Makefile` (`make deb`).
- Инсталлятор: `install-qeli-server.sh` — все профили по умолчанию; `QELI_PANEL_DOMAIN` для loopback panel.
- Деплой nginx: `scripts/deploy/nginx-panel-sni.sh`.
- Clean reinstall: `scripts/deploy/clean-reinstall-lab.sh`.
- Public port: `scripts/deploy/upgrade-public-port.sh`.
