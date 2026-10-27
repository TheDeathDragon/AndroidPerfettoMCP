#!/usr/bin/env sh
# Usage: ./publish.sh [linux-x64|win-x64]
#   -> publish/<rid>/ (perfetto-mcp + tools/trace_processor_shell) and publish/perfetto-mcp-<ver>-<rid>.tar.gz
set -eu
cd "$(dirname "$0")"
RID="${1:-linux-x64}"
OUT="publish/$RID"
rm -rf "$OUT"
dotnet publish src/PerfettoMcp/PerfettoMcp.csproj -c Release -r "$RID" -o "$OUT" --nologo
[ "$RID" = "linux-x64" ] && chmod +x "$OUT/perfetto-mcp" "$OUT/tools/trace_processor_shell"
VER="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/PerfettoMcp/PerfettoMcp.csproj | head -n 1)"
tar -czf "publish/perfetto-mcp-$VER-$RID.tar.gz" -C publish "$RID"
ls -la "$OUT" "$OUT/tools" "publish/perfetto-mcp-$VER-$RID.tar.gz"
