#!/usr/bin/env sh
# Install or hot-update perfetto-mcp from a release tarball or a publish directory (Linux).
#
#   ./install.sh <perfetto-mcp-*-linux-x64.tar.gz | publish/linux-x64> [--prefix DIR] [--bin-dir DIR] [--auto-config]
#
# Layout under $PREFIX (default ~/.local/share/perfetto-mcp):
#   perfetto-mcp        supervisor (what the MCP client runs, with --supervise)
#   current.txt         active version id
#   versions/<id>/      worker: perfetto-mcp + tools/trace_processor_shell
# Re-running while the server is in use is safe: the running supervisor switches to the new
# version within seconds. --auto-config registers it with Claude Code (user scope).
set -eu

SRC=""
PREFIX="${HOME}/.local/share/perfetto-mcp"
BIN_DIR="${HOME}/.local/bin"
AUTO=0
while [ $# -gt 0 ]; do
  case "$1" in
    --prefix) PREFIX="$2"; shift 2 ;;
    --bin-dir) BIN_DIR="$2"; shift 2 ;;
    --auto-config) AUTO=1; shift ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) SRC="$1"; shift ;;
  esac
done
[ -n "$SRC" ] || { echo "usage: $0 <tarball|dir> [--prefix DIR] [--bin-dir DIR] [--auto-config]" >&2; exit 2; }

TMP=""
cleanup() { if [ -n "$TMP" ]; then rm -rf "$TMP"; fi; }
trap cleanup EXIT

if [ -f "$SRC" ]; then
  TMP="$(mktemp -d)"
  tar -xzf "$SRC" -C "$TMP"
  SRC="$(dirname "$(find "$TMP" -name perfetto-mcp -type f | head -n 1)")"
fi
[ -f "$SRC/perfetto-mcp" ] && [ -f "$SRC/tools/trace_processor_shell" ] || {
  echo "error: $SRC does not contain perfetto-mcp and tools/trace_processor_shell" >&2; exit 1; }

if [ "$(uname -m)" != "x86_64" ]; then
  echo "warning: built for x86_64, this machine is $(uname -m)" >&2
fi
if ! grep -qw avx2 /proc/cpuinfo 2>/dev/null; then
  echo "warning: CPU lacks AVX2; the bundled trace_processor_shell will not run (set PERFETTO_TP_PATH)" >&2
fi

chmod +x "$SRC/perfetto-mcp" "$SRC/tools/trace_processor_shell"
SEMVER="$("$SRC/perfetto-mcp" --version 2>&1 | sed -n 's/.*perfetto-mcp \([^ ]*\).*/\1/p' | head -n 1)"
ID="${SEMVER:-dev}-$(date +%Y%m%d%H%M%S)"
DEST="$PREFIX/versions/$ID"
mkdir -p "$DEST" "$BIN_DIR"
cp -f "$SRC/perfetto-mcp" "$DEST/"
cp -R "$SRC/tools" "$DEST/"
chmod +x "$DEST/perfetto-mcp" "$DEST/tools/trace_processor_shell"

# Replace the supervisor via rename so a running instance keeps its inode.
cp -f "$DEST/perfetto-mcp" "$PREFIX/perfetto-mcp.new"
mv -f "$PREFIX/perfetto-mcp.new" "$PREFIX/perfetto-mcp"
rm -rf "$PREFIX/tools"   # pre-0.3 flat layout

PREVIOUS=""
[ -f "$PREFIX/current.txt" ] && PREVIOUS="$(cat "$PREFIX/current.txt")"
printf '%s' "$ID" > "$PREFIX/current.txt.tmp"
mv -f "$PREFIX/current.txt.tmp" "$PREFIX/current.txt"

# Keep the new and the previous version (it may still be draining).
for d in "$PREFIX"/versions/*; do
  n="$(basename "$d")"
  [ "$n" = "$ID" ] || [ "$n" = "$PREVIOUS" ] || rm -rf "$d"
done
ln -sf "$PREFIX/perfetto-mcp" "$BIN_DIR/perfetto-mcp"

"$DEST/perfetto-mcp" --version
"$DEST/tools/trace_processor_shell" --version | head -n 1

BIN="$PREFIX/perfetto-mcp"
echo
echo "Installed version $ID (previous: ${PREVIOUS:-none}); a running supervisor switches within seconds."
if [ "$AUTO" = 1 ]; then
  if command -v claude >/dev/null 2>&1; then
    claude mcp remove perfetto --scope user >/dev/null 2>&1 || true
    claude mcp add perfetto --scope user -- "$BIN" --supervise
    echo "Registered with Claude Code (user scope) as 'perfetto'."
  else
    echo "claude CLI not found; register manually (see below)." >&2
  fi
fi
cat <<EOF

Claude Code:  claude mcp add perfetto --scope user -- $BIN --supervise
JSON config:  { "mcpServers": { "perfetto": { "command": "$BIN", "args": ["--supervise"] } } }
EOF
