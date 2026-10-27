<#
.SYNOPSIS
  Install or hot-update perfetto-mcp from a release zip or a publish directory (Windows).

.DESCRIPTION
  Layout under -Prefix:
    perfetto-mcp.exe            supervisor (what the MCP client runs, with --supervise)
    current.txt                 active version id
    versions\<id>\              worker: perfetto-mcp.exe + tools\trace_processor_shell.exe
  Re-running the script while the server is in use is safe: the new version lands in a new
  directory, current.txt is switched atomically and the running supervisor swaps workers within
  seconds (notifying the client that the tool list changed).

.EXAMPLE
  .\install.ps1 .\perfetto-mcp-0.2.0-win-x64.zip -AutoConfig
  .\install.ps1 ..\publish\win-x64
#>
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Source,
    [string]$Prefix = (Join-Path $env:LOCALAPPDATA 'Programs\perfetto-mcp'),
    [switch]$AutoConfig
)
$ErrorActionPreference = 'Stop'

$tmp = $null
try {
    $src = (Resolve-Path $Source).Path
    if (Test-Path $src -PathType Leaf) {
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ("perfetto-mcp-" + [Guid]::NewGuid().ToString('N'))
        Expand-Archive -Path $src -DestinationPath $tmp
        $exe = Get-ChildItem -Path $tmp -Recurse -Filter 'perfetto-mcp.exe' | Select-Object -First 1
        if (-not $exe) { throw "perfetto-mcp.exe not found in $Source" }
        $src = $exe.DirectoryName
    }
    if (-not (Test-Path (Join-Path $src 'perfetto-mcp.exe')) -or -not (Test-Path (Join-Path $src 'tools\trace_processor_shell.exe'))) {
        throw "$src does not contain perfetto-mcp.exe and tools\trace_processor_shell.exe"
    }

    $semver = ((& (Join-Path $src 'perfetto-mcp.exe') --version 2>&1) -join ' ') -replace '.*perfetto-mcp\s+', ''
    $id = "$($semver.Trim())-$(Get-Date -Format 'yyyyMMddHHmmss')"
    $versions = Join-Path $Prefix 'versions'
    $dest = Join-Path $versions $id
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -Force (Join-Path $src 'perfetto-mcp.exe') $dest
    Copy-Item -Recurse -Force (Join-Path $src 'tools') $dest

    # Supervisor binary: a running exe cannot be overwritten on Windows but can be renamed.
    $launcher = Join-Path $Prefix 'perfetto-mcp.exe'
    Get-ChildItem $Prefix -Filter 'perfetto-mcp.exe.old-*' -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
    if (Test-Path $launcher) {
        try { Copy-Item -Force (Join-Path $dest 'perfetto-mcp.exe') $launcher }
        catch {
            Rename-Item $launcher ("perfetto-mcp.exe.old-" + (Get-Date -Format 'yyyyMMddHHmmss'))
            Copy-Item -Force (Join-Path $dest 'perfetto-mcp.exe') $launcher
        }
    }
    else {
        Copy-Item -Force (Join-Path $dest 'perfetto-mcp.exe') $launcher
    }
    # Pre-0.3 flat layout leftovers.
    if (Test-Path (Join-Path $Prefix 'tools')) { Remove-Item -Recurse -Force (Join-Path $Prefix 'tools') -ErrorAction SilentlyContinue }

    $previous = if (Test-Path (Join-Path $Prefix 'current.txt')) { (Get-Content (Join-Path $Prefix 'current.txt') -Raw).Trim() } else { '' }
    $currentTmp = Join-Path $Prefix 'current.txt.tmp'
    Set-Content -Path $currentTmp -Value $id -NoNewline -Encoding ascii
    Move-Item -Force $currentTmp (Join-Path $Prefix 'current.txt')

    # Keep the new and the previous version (it may still be draining); others go if unlocked.
    Get-ChildItem $versions -Directory | Where-Object { $_.Name -ne $id -and $_.Name -ne $previous } |
        ForEach-Object { Remove-Item -Recurse -Force $_.FullName -ErrorAction SilentlyContinue }

    & (Join-Path $dest 'perfetto-mcp.exe') --version
    & (Join-Path $dest 'tools\trace_processor_shell.exe') --version | Select-Object -First 1

    Write-Host ""
    Write-Host "Installed version $id (previous: $(if ($previous) { $previous } else { 'none' }))"
    Write-Host "A running supervisor switches to it within a few seconds."
    if ($AutoConfig) {
        if (Get-Command claude -ErrorAction SilentlyContinue) {
            & claude mcp remove perfetto --scope user 2>$null | Out-Null
            & claude mcp add perfetto --scope user -- $launcher --supervise
            Write-Host "Registered with Claude Code (user scope) as 'perfetto'."
        }
        else {
            Write-Warning "claude CLI not found; register manually (see below)."
        }
    }
    $json = ($launcher -replace '\\', '\\')
    Write-Host ""
    Write-Host "Claude Code:  claude mcp add perfetto --scope user -- `"$launcher`" --supervise"
    Write-Host "JSON config:  { `"mcpServers`": { `"perfetto`": { `"command`": `"$json`", `"args`": [`"--supervise`"] } } }"
}
finally {
    if ($tmp -and (Test-Path $tmp)) { Remove-Item -Recurse -Force $tmp }
}
