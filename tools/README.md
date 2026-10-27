# Bundled trace_processor_shell

`trace_processor_shell` from [Perfetto](https://github.com/google/perfetto) v58.2 (`add693d8b`), shipped next to the
server executable. Perfetto is licensed under the Apache License 2.0.

| File | Source | sha256 |
|---|---|---|
| `win-x64/trace_processor_shell.exe` | Official prebuilt, unmodified: `https://commondatastorage.googleapis.com/perfetto-luci-artifacts/v58.2/windows-amd64/trace_processor_shell.exe` | `adfa6bad3d72be3ba9b83fa2b17b69fa13b3ab1cad0f42e52b86188bd5f0f997` |
| `linux-x64/trace_processor_shell` | Built from the v58.2 source with `scripts/build-tp.sh` against an Ubuntu 18.04 sysroot (requires glibc >= 2.23 and an AVX2 CPU) | `aa75f660f039e9e7b242e51dff066dc98a03e713e7bfb92e47cea68dac4d3849` |

Official Linux prebuilts since v48 require glibc 2.34; see `docs/01-trace-processor-ubuntu1804.md` for the build.
Set `PERFETTO_TP_PATH` to use a different binary.
