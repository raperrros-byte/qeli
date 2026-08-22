#!/usr/bin/env bash
# Build qeli .deb locally in Docker with persistent cargo caches.
# Usage (from repo root on Windows/Linux):
#   bash scripts/agent/build_deb_docker.sh
# Env: REPO_ROOT (default: git root), RUST_IMAGE (default: rust:1.88-bookworm)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
REPO_ROOT="${REPO_ROOT:-$ROOT}"
VERSION="$(sed -n 's/^Version: //p' "$REPO_ROOT/qeli/debian/control")"
ARCH="$(sed -n 's/^Architecture: //p' "$REPO_ROOT/qeli/debian/control")"
IMAGE="${RUST_IMAGE:-rust:1.88-bookworm}"
PKG="qeli_${VERSION}_${ARCH}"
OUT="$REPO_ROOT/qeli/debian/${PKG}.deb"

echo "== build .deb ${PKG} via ${IMAGE} =="

docker run --rm \
  -v "${REPO_ROOT}:/w" -w /w \
  -v qeli-cargo-registry:/usr/local/cargo/registry \
  -v qeli-cargo-git:/usr/local/cargo/git \
  -v "qeli-target-${VERSION}:/w/qeli/target" \
  "$IMAGE" bash -c "
    set -euo pipefail
    export PATH=/usr/local/cargo/bin:\$PATH
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq
    apt-get install -y --no-install-recommends make dpkg-dev binutils xz-utils >/dev/null
    make -C qeli/debian deb || true
    test -f /w/qeli/target/release/qeli
    rm -rf /tmp/qeli-deb
    make -C qeli/debian stage \
      BINARY=../target/release/qeli \
      DEB_DIR=/tmp/qeli-deb/${PKG} \
      BUILD_DIR=/tmp/qeli-deb
    find /tmp/qeli-deb -type d -exec chmod 755 {} \;
    find /tmp/qeli-deb -type f -exec chmod 644 {} \;
    chmod 755 /tmp/qeli-deb/${PKG}/usr/bin/qeli \
      /tmp/qeli-deb/${PKG}/DEBIAN/postinst \
      /tmp/qeli-deb/${PKG}/DEBIAN/prerm \
      /tmp/qeli-deb/${PKG}/DEBIAN/config
    dpkg-deb --root-owner-group -Zxz --build /tmp/qeli-deb/${PKG} /w/qeli/debian/${PKG}.deb
    /w/qeli/target/release/qeli --version
    ls -lah /w/qeli/debian/${PKG}.deb
  "

test -f "$OUT"
echo "OK: $OUT ($(wc -c < "$OUT") bytes)"
