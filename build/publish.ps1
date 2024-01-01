# Сборка релиза: приложение, MCP-сервер и CLI одним архивом (без установленного .NET у пользователя).
param([string]$Runtime = "win-x64", [string]$Output = "publish")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root $Output
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

foreach ($proj in @("src/PKTracker.App", "src/PKTracker.Mcp", "src/PKTracker.Cli")) {
    $name = Split-Path $proj -Leaf
    dotnet publish (Join-Path $root $proj) -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=false -p:DebugType=none -o (Join-Path $out $name) --nologo
}

Compress-Archive -Path (Join-Path $out "*") -DestinationPath (Join-Path $root "PKTracker-$Runtime.zip") -Force
Write-Host "Готово: PKTracker-$Runtime.zip"
