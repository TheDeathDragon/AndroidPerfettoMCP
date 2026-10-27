<#
.SYNOPSIS
  Install or hot-update perfetto-mcp (Windows): the latest GitHub release, or a local release zip / publish directory.

.DESCRIPTION
  Layout under -Prefix:
    perfetto-mcp.exe            supervisor (what the MCP client runs, with --supervise)
    current.txt                 active version id
    versions\<id>\              worker: perfetto-mcp.exe + tools\trace_processor_shell.exe
  Re-running the script while the server is in use is safe: the new version lands in a new
  directory, current.txt is switched atomically and the running supervisor swaps workers within
  seconds (notifying the client that the tool list changed).

  Without a source the release asset is downloaded from GitHub (latest, or -Version); set GITHUB_TOKEN to
  download from a private repository.
  -AutoConfig registers the server with Claude Code and installs the 'perfetto-analysis' skill; -Skill installs
  only the skill.

.EXAMPLE
  & ([scriptblock]::Create((iwr https://raw.githubusercontent.com/TheDeathDragon/AndroidPerfettoMCP/main/scripts/install.ps1))) -AutoConfig
  .\install.ps1 .\perfetto-mcp-0.8.0-win-x64.zip -AutoConfig
  .\install.ps1 ..\publish\win-x64
#>
param(
    [Parameter(Position = 0)][string]$Source,
    [string]$Version,
    [string]$Prefix = (Join-Path $env:LOCALAPPDATA 'Programs\perfetto-mcp'),
    [switch]$AutoConfig,
    [switch]$Skill
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$tmp = $null
$dl = $null
try {
    if (-not $Source) {
        $headers = @{ 'User-Agent' = 'perfetto-mcp-installer' }
        if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }
        $api = 'https://api.github.com/repos/TheDeathDragon/AndroidPerfettoMCP/releases'
        $rel = if ($Version) { Invoke-RestMethod -Headers $headers "$api/tags/v$($Version -replace '^v', '')" } else { Invoke-RestMethod -Headers $headers "$api/latest" }
        $asset = $rel.assets | Where-Object { $_.name -like 'perfetto-mcp-*-win-x64.zip' } | Select-Object -First 1
        if (-not $asset) { throw "no win-x64 zip in release $($rel.tag_name)" }
        Write-Host "Downloading $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB) from release $($rel.tag_name)"
        $dl = Join-Path ([IO.Path]::GetTempPath()) ("perfetto-mcp-dl-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $dl | Out-Null
        $Source = Join-Path $dl $asset.name
        # Private repositories serve assets only through the API URL with a token.
        if ($env:GITHUB_TOKEN) {
            Invoke-WebRequest -Headers ($headers + @{ 'Accept' = 'application/octet-stream' }) -Uri $asset.url -OutFile $Source
        }
        else {
            Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $Source
        }
    }
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
    if ($AutoConfig -or $Skill) {
        $skillSrc = Join-Path $src 'skills\perfetto-analysis'
        if (Test-Path $skillSrc) {
            $claudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }
            $skillDest = Join-Path $claudeDir 'skills\perfetto-analysis'
            if (Test-Path $skillDest) { Remove-Item -Recurse -Force $skillDest }
            New-Item -ItemType Directory -Force -Path (Split-Path $skillDest) | Out-Null
            Copy-Item -Recurse -Force $skillSrc $skillDest
            Write-Host "Installed the Claude Code skill 'perfetto-analysis' to $skillDest."
        }
        else {
            Write-Warning "this package has no skills\perfetto-analysis; skill not installed."
        }
    }
    $json = ($launcher -replace '\\', '\\')
    Write-Host ""
    Write-Host "Claude Code:  claude mcp add perfetto --scope user -- `"$launcher`" --supervise"
    Write-Host "JSON config:  { `"mcpServers`": { `"perfetto`": { `"command`": `"$json`", `"args`": [`"--supervise`"] } } }"
}
finally {
    foreach ($d in @($tmp, $dl)) {
        if ($d -and (Test-Path $d)) { Remove-Item -Recurse -Force $d }
    }
}
