@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

set "RID=%~1"
if "%RID%"=="" set "RID=win-x64"

set "OUT=publish\%RID%"
if exist "%OUT%" rmdir /s /q "%OUT%"

dotnet publish src\PerfettoMcp\PerfettoMcp.csproj -c Release -r %RID% -o "%OUT%" --nologo
if errorlevel 1 (
    echo [ERROR] Publish failed.
    exit /b 1
)

rem Release archive: zip for Windows, tar.gz for Linux (install scripts in scripts/).
powershell -NoProfile -Command "$v = ([xml](Get-Content src\PerfettoMcp\PerfettoMcp.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1; $name = \"perfetto-mcp-$v-%RID%\"; if (\"%RID%\" -like \"win-*\") { Compress-Archive -Force -Path \"%OUT%\*\" -DestinationPath \"publish\$name.zip\" } else { tar -czf \"publish/$name.tar.gz\" -C publish %RID% }; Write-Host \"Archive: publish\$name\""
if errorlevel 1 exit /b 1

echo.
echo Published to: %CD%\%OUT%
endlocal
