#!/usr/bin/env python3
"""Smoke-test new transport auto / SNI / speedtest panel APIs.

Reads credentials from scripts/.lab_secrets (PANEL_URL, PANEL_USER, PANEL_PASSWORD).
"""
from __future__ import annotations

import json
import os
import sys
import time
import urllib.error
import urllib.request
from http.cookiejar import CookieJar
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SECRETS = ROOT / "scripts" / ".lab_secrets"


def load_secrets() -> dict[str, str]:
    out: dict[str, str] = {}
    if not SECRETS.is_file():
        raise SystemExit(f"missing {SECRETS}")
    for line in SECRETS.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        k, v = line.split("=", 1)
        out[k.strip()] = v.strip().strip('"').strip("'")
    return out


def main() -> int:
    s = load_secrets()
    base = s.get("PANEL_URL", "").rstrip("/")
    user = s.get("PANEL_USER", "admin")
    password = s.get("PANEL_PASSWORD", "")
    if not base or not password:
        raise SystemExit("PANEL_URL / PANEL_PASSWORD required in .lab_secrets")

    jar = CookieJar()
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))

    def req(method: str, path: str, body: bytes | None = None, headers: dict | None = None):
        h = {"Accept": "application/json"}
        if headers:
            h.update(headers)
        r = urllib.request.Request(base + path, data=body, headers=h, method=method)
        try:
            with opener.open(r, timeout=60) as resp:
                data = resp.read()
                return resp.status, dict(resp.headers), data
        except urllib.error.HTTPError as e:
            return e.code, dict(e.headers), e.read()

    print(f"panel={base}")
    st, _, raw = req(
        "POST",
        "/api/login",
        body=json.dumps({"username": user, "password": password}).encode(),
        headers={"Content-Type": "application/json"},
    )
    assert st == 200, f"login failed: {st} {raw[:200]!r}"
    print("OK login")

    checks = []

    st, _, raw = req("GET", "/api/transport/modes")
    checks.append(("transport/modes", st, raw))
    st, _, raw = req("GET", "/api/transport/sni-presets")
    checks.append(("transport/sni-presets", st, raw))
    st, _, raw = req("GET", "/api/transport/health")
    checks.append(("transport/health", st, raw))

    t0 = time.perf_counter()
    st, hdr, raw = req("GET", "/api/speedtest?bytes=262144")
    elapsed = max(time.perf_counter() - t0, 0.001)
    mbps = (len(raw) * 8) / elapsed / 1_000_000
    checks.append(("speedtest", st, f"bytes={len(raw)} mbps={mbps:.2f}".encode()))

    failed = 0
    for name, status, payload in checks:
        ok = status == 200
        if name.startswith("transport/") and ok:
            try:
                doc = json.loads(payload)
                ok = bool(doc.get("ok", True))
                if name.endswith("modes"):
                    ok = ok and isinstance(doc.get("modes"), list) and len(doc["modes"]) > 0
                    print(f"  modes={len(doc.get('modes', []))} order={doc.get('order')}")
                if name.endswith("sni-presets"):
                    ok = ok and isinstance(doc.get("presets"), list) and len(doc["presets"]) > 0
                    print(f"  sni_presets={doc.get('presets')}")
                if name.endswith("health"):
                    auto = doc.get("auto_transport") or {}
                    ok = ok and "order" in auto
                    print(f"  health profiles={doc.get('summary', {}).get('profiles')} auto={auto.get('order')}")
            except Exception as e:
                ok = False
                print(f"  parse error: {e}")
        mark = "OK" if ok else "FAIL"
        print(f"{mark} {name} HTTP {status} {payload[:120]!r}")
        if not ok:
            failed += 1

    if failed:
        print(f"FAILED {failed} check(s)")
        return 1
    print("ALL SMOKE CHECKS PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
