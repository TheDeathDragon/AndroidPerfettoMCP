#!/usr/bin/env sh
# Install or hot-update perfetto-mcp (Linux): the latest GitHub release, or a local tarball / publish directory.
#
#   curl -fsSL https://raw.githubusercontent.com/TheDeathDragon/AndroidPerfettoMCP/main/scripts/install.sh | sh -s -- --auto-config
#   ./install.sh [perfetto-mcp-*-linux-x64.tar.gz | publish/linux-x64] [--version X.Y.Z] [--prefix DIR] [--bin-dir DIR] [--auto-config]
#
# Without a source the release asset is downloaded (latest, or --version); set GITHUB_TOKEN for a private repository.
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
VERSION=""
while [ $# -gt 0 ]; do
  case "$1" in
    --prefix) PREFIX="$2"; shift 2 ;;
    --bin-dir) BIN_DIR="$2"; shift 2 ;;
    --version) VERSION="${2#v}"; shift 2 ;;
    --auto-config) AUTO=1; shift ;;
    -h|--help) echo "usage: install.sh [tarball|dir] [--version X.Y.Z] [--prefix DIR] [--bin-dir DIR] [--auto-config]"; exit 0 ;;
    -*) echo "unknown option: $1" >&2; exit 2 ;;
    *) SRC="$1"; shift ;;
  esac
done

TMP=""
DL=""
cleanup() { rm -rf ${TMP:+"$TMP"} ${DL:+"$DL"}; }
trap cleanup EXIT

if [ -z "$SRC" ]; then
  for t in curl tar; do command -v "$t" >/dev/null 2>&1 || { echo "error: $t is required" >&2; exit 1; }; done
  API="https://api.github.com/repos/TheDeathDragon/AndroidPerfettoMCP/releases"
  if [ -n "$VERSION" ]; then API="$API/tags/v$VERSION"; else API="$API/latest"; fi
  AUTH=""
  [ -n "${GITHUB_TOKEN:-}" ] && AUTH="Authorization: Bearer $GITHUB_TOKEN"
  REL="$(curl -fsSL -H 'User-Agent: perfetto-mcp-installer' ${AUTH:+-H "$AUTH"} "$API")"
  # One JSON field per line so sed / awk can pick values without jq. Within an asset, "url" (API) precedes "name".
  FIELDS="$(printf '%s' "$REL" | tr ',' '\n')"
  TAG="$(printf '%s\n' "$FIELDS" | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -n 1)"
  NAME="$(printf '%s\n' "$FIELDS" | sed -n 's/.*"name": *"\([^"]*linux-x64\.tar\.gz\)".*/\1/p' | head -n 1)"
  [ -n "$NAME" ] || { echo "error: no linux-x64 tarball in release ${TAG:-?}" >&2; exit 1; }
  DL="$(mktemp -d)"
  echo "Downloading $NAME from release $TAG"
  if [ -n "$AUTH" ]; then
    # Private repositories serve assets only through the API URL with a token.
    URL="$(printf '%s\n' "$FIELDS" | awk -v n="\"$NAME\"" '
      match($0, /"url": *"https:[^"]*\/releases\/assets\/[0-9]+"/) { u = substr($0, RSTART, RLENGTH); sub(/^"url": *"/, "", u); sub(/"$/, "", u) }
      index($0, n) && $0 ~ /"name"/ { print u; exit }')"
    curl -fsSL -H "$AUTH" -H 'Accept: application/octet-stream' "$URL" -o "$DL/$NAME"
  else
    URL="$(printf '%s\n' "$FIELDS" | sed -n 's/.*"browser_download_url": *"\([^"]*linux-x64\.tar\.gz\)".*/\1/p' | head -n 1)"
    curl -fsSL "$URL" -o "$DL/$NAME"
  fi
  SRC="$DL/$NAME"
fi

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
