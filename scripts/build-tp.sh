#!/usr/bin/env bash
# Build a trace_processor_shell that runs on glibc 2.27 (Ubuntu 18.04):
# Perfetto hermetic clang + static custom libc++, linked against an Ubuntu 18.04 sysroot.
# Usage: build-tp.sh [perfetto_tag] [work_dir] [jobs]
set -euo pipefail

TAG="${1:-v58.2}"
WORK="${2:-$HOME/tpbuild}"
JOBS="${3:-16}"
SRC="$WORK/perfetto-$TAG"
SYSROOT="$WORK/sysroot-bionic"
OUT="out/linux-bionic"

mkdir -p "$WORK"

for i in 1 2 3 4 5; do
  [ -d "$SRC/.git" ] && break
  rm -rf "$SRC"
  git -c http.version=HTTP/1.1 clone --depth 1 --branch "$TAG" \
    https://github.com/google/perfetto.git "$SRC" || sleep 5
done
[ -d "$SRC/.git" ]

# --no-dev-tools skips the pip venv (grpcio/pandas/...), unused by the C++ build.
# install-build-deps ends with a ~1.5 GB test/data sync that flaky proxies often
# break; it is not needed here, so only the toolchain must be present.
for i in 1 2 3; do
  [ -x "$SRC/buildtools/linux64/clang/bin/clang" ] && [ -d "$SRC/buildtools/open_csd" ] && break
  (cd "$SRC" && python3 tools/install-build-deps --no-dev-tools) || sleep 5
done
[ -x "$SRC/buildtools/linux64/clang/bin/clang" ]

if [ ! -f "$SYSROOT/usr/include/stdio.h" ]; then
  rm -rf "$SYSROOT" && mkdir -p "$SYSROOT"
  cid=$(docker create --network host \
    -e http_proxy -e https_proxy -e no_proxy \
    ubuntu:18.04 bash -c \
    "apt-get update && apt-get install -y --no-install-recommends libc6-dev linux-libc-dev libgcc-7-dev")
  docker start -a "$cid"
  # --anchored: an unanchored "sys" would also drop usr/include/*/sys/.
  docker export "$cid" | tar -x -C "$SYSROOT" --anchored \
    --exclude=dev --exclude=proc --exclude=sys --exclude=var --exclude=tmp \
    --exclude=usr/share --exclude=etc --exclude=root --exclude=home
  docker rm "$cid" >/dev/null
  # Absolute symlinks (e.g. libm.so -> /lib/x86_64-linux-gnu/libm.so.6) would
  # resolve against the host root; rewrite them relative to the sysroot.
  python3 - "$SYSROOT" <<'EOF'
import os, sys
root = os.path.realpath(sys.argv[1])
for d, dirs, files in os.walk(root):
    for n in dirs + files:
        p = os.path.join(d, n)
        if os.path.islink(p):
            t = os.readlink(p)
            if t.startswith('/'):
                os.unlink(p)
                os.symlink(os.path.relpath(root + t, d), p)
EOF
fi

cd "$SRC"
tools/gn gen "$OUT" --args="is_clang=true is_debug=false monolithic_binaries=true \
  enable_perfetto_etm_importer=false skip_buildtools_check=true \
  target_os=\"linux\" target_cpu=\"x64\" \
  sysroot=\"$SYSROOT\""
nice -n 10 tools/ninja -C "$OUT" -j "$JOBS" trace_processor_shell

BIN="$SRC/$OUT/stripped/trace_processor_shell"
[ -f "$BIN" ] || BIN="$SRC/$OUT/trace_processor_shell"
echo "BINARY: $BIN"
ls -la "$BIN"
echo "MAX GLIBC: $(objdump -T "$BIN" | grep -o 'GLIBC_2\.[0-9.]*' | sort -uV | tail -1)"
