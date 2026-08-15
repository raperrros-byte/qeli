#!/usr/bin/env python3
"""Fetch community SNI candidate lists and emit a curated hosts file."""
from __future__ import annotations

import re
import urllib.request
from pathlib import Path

OUT = Path(__file__).resolve().parents[1] / "qeli-shared" / "QeliShared" / "Assets" / "sni_hosts.txt"

SOURCES = [
    ("meower1/Reality-SNI-Finder", "https://raw.githubusercontent.com/meower1/Reality-SNI-Finder/main/sni.txt"),
    ("Reza-shojaei/Reality-SNI-finder", "https://raw.githubusercontent.com/Reza-shojaei/Reality-SNI-finder/main/sni.txt"),
    ("evkir/reality-probe", "https://raw.githubusercontent.com/evkir/reality-probe/main/reality_probe.py"),
]

# Well-known fronts commonly cited for fake-tls / REALITY (not Iran-local junk).
SEED = [
    "www.cloudflare.com",
    "www.google.com",
    "www.microsoft.com",
    "www.apple.com",
    "www.amazon.com",
    "www.speedtest.net",
    "speed.cloudflare.com",
    "github.com",
    "www.github.com",
    "cdn.jsdelivr.net",
    "jsdelivr.com",
    "updates.cdn-apple.com",
    "addons.mozilla.org",
    "ftp.debian.org",
    "cwiki.apache.org",
    "stackoverflow.com",
    "www.gsmarena.com",
    "www.netlify.com",
    "cloudinary.com",
    "www.emirates.com",
    "samsung.com",
    "www.samsung.com",
    "discord.com",
    "discordapp.com",
    "yahoo.com",
    "www.yahoo.com",
    "www.theverge.com",
    "dns.google",
    "cloudflare-dns.com",
    "az764295.vo.msecnd.net",
    "gateway.icloud.com",
    "www.bing.com",
    "login.live.com",
    "outlook.office.com",
    "www.office.com",
    "dl.google.com",
    "fonts.gstatic.com",
    "ajax.googleapis.com",
    "cdnjs.cloudflare.com",
    "static.cloudflareinsights.com",
    "www.cloudfront.net",
    "d2c8v52ll5s99u.cloudfront.net",
    "itunes.apple.com",
    "swcdn.apple.com",
    "mensura.cdn-apple.com",
    "www.nvidia.com",
    "developer.nvidia.com",
    "www.intel.com",
    "www.amd.com",
    "www.adobe.com",
    "www.dropbox.com",
    "www.spotify.com",
    "open.spotify.com",
    "www.twitch.tv",
    "www.reddit.com",
    "www.wikipedia.org",
    "en.wikipedia.org",
    "www.bbc.com",
    "www.cnn.com",
    "www.nytimes.com",
    "www.vk.com",
    "eh.vk.com",
    "ads.x5.ru",
    "max.ru",
    "yandex.ru",
    "www.yandex.ru",
    "mail.ru",
    "ok.ru",
]


def normalize(host: str) -> str | None:
    h = host.strip().lower().split("#", 1)[0].strip()
    if not h or h.startswith(";"):
        return None
    if "://" in h:
        h = h.split("://", 1)[1]
    h = h.split("/", 1)[0]
    if ":" in h:
        # host:443
        host_part, _, port = h.rpartition(":")
        if port.isdigit():
            h = host_part
    h = h.strip(".")
    if not re.fullmatch(r"[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+", h):
        return None
    # Drop obvious local/spam TLDs from IR scanner dumps for default catalog size.
    if h.endswith((".ir", ".tk", ".xyz", ".lol", ".fun", ".click", ".shop", ".online")):
        return None
    if len(h) > 80:
        return None
    return h


def fetch(url: str) -> str:
    req = urllib.request.Request(url, headers={"User-Agent": "qeli-sni-catalog/1.0"})
    with urllib.request.urlopen(req, timeout=60) as resp:
        return resp.read().decode("utf-8", "replace")


def from_probe_py(text: str) -> set[str]:
    out: set[str] = set()
    for m in re.finditer(r'["\']([a-zA-Z0-9][a-zA-Z0-9.-]+\.[a-zA-Z]{2,})["\']', text):
        n = normalize(m.group(1))
        if n:
            out.add(n)
    return out


def main() -> None:
    hosts: set[str] = set()
    for h in SEED:
        n = normalize(h)
        if n:
            hosts.add(n)

    notes = []
    for name, url in SOURCES:
        try:
            text = fetch(url)
        except Exception as e:
            notes.append(f"# skip {name}: {e}")
            continue
        if url.endswith(".py"):
            found = from_probe_py(text)
        else:
            found = set()
            for line in text.splitlines():
                n = normalize(line)
                if n:
                    found.add(n)
        hosts |= found
        notes.append(f"# +{len(found)} from {name}")

    ordered = sorted(hosts)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    header = [
        "# Curated SNI / TLS front-host candidates for qeli clients.",
        "# Sources: community Reality SNI finder lists + common CDN fronts.",
        "# Maintained by: Daniil Nekrasov <raperrros@yandex.ru>",
        "# Updated: 2026-08-15",
        *notes,
        f"# total={len(ordered)}",
        "",
    ]
    OUT.write_text("\n".join(header + ordered) + "\n", encoding="utf-8")
    print(f"wrote {OUT} ({len(ordered)} hosts)")


if __name__ == "__main__":
    main()
