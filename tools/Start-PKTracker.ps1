# Запуск PKTracker: при необходимости собирает проект и поднимает exe.
# Обычно вызывается из PKTracker.bat, отдельно нужен разве что для -Rebuild / -Shortcut.
[CmdletBinding()]
param(
    [switch]$Rebuild,     # пересобрать, даже если exe свежий
    [switch]$Shortcut,    # создать ярлык на рабочем столе и выйти
    [switch]$NoLaunch     # только собрать
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$bat = Join-Path $root 'PKTracker.bat'
$ico = Join-Path $root 'src\PKTracker.App\PKTracker.ico'
$release = Join-Path $root 'src\PKTracker.App\bin\Release\net10.0-windows\PKTracker.exe'
$debug = Join-Path $root 'src\PKTracker.App\bin\Debug\net10.0-windows\PKTracker.exe'

function Write-Step([string]$text) { Write-Host $text -ForegroundColor DarkYellow }

if ($Shortcut) {
    $lnkPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'PKTracker.lnk'
    $ws = New-Object -ComObject WScript.Shell
    $lnk = $ws.CreateShortcut($lnkPath)
    $lnk.TargetPath = $bat
    $lnk.WorkingDirectory = $root
    $lnk.Description = 'PKTracker — трекер и HUD для PokerKing'
    if (Test-Path $ico) { $lnk.IconLocation = "$ico,0" }
    $lnk.WindowStyle = 7    # консоль запуска — свёрнутой, чтобы не мигала поверх столов
    $lnk.Save()
    Write-Host "Ярлык на рабочем столе: $lnkPath"
    exit 0
}

# Собранный exe: сначала Release, иначе Debug
$exe = if (Test-Path $release) { $release } elseif (Test-Path $debug) { $debug } else { $null }

# Исходники новее exe — значит exe устарел
$stale = $false
if ($exe) {
    $exeTime = (Get-Item $exe).LastWriteTimeUtc
    $newest = Get-ChildItem (Join-Path $root 'src') -Recurse -Include *.cs, *.xaml, *.csproj, *.sql -File |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    $stale = $newest -and $newest.LastWriteTimeUtc -gt $exeTime
}

# SDK может стоять не в Program Files (например, на другом диске): DOTNET_ROOT указывает на него.
if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
    $env:PATH = $env:DOTNET_ROOT + ';' + $env:PATH
}

$sdk = $false
try { $sdk = [bool](& dotnet --list-sdks 2>$null) } catch { $sdk = $false }

if (-not $exe -or $stale -or $Rebuild) {
    if (-not $sdk) {
        if (-not $exe) {
            Write-Host "PKTracker не собран, а .NET SDK 10 не установлен." -ForegroundColor Red
            Write-Host "Установите SDK и запустите этот файл снова:"
            Write-Host "    winget install Microsoft.DotNet.SDK.10"
            Write-Host "    (или https://dotnet.microsoft.com/download/dotnet/10.0)"
            exit 1
        }
        Write-Host "Исходники новее собранного exe, но .NET SDK 10 не установлен — запускаю старую сборку." -ForegroundColor Yellow
        Write-Host "Чтобы получить свежую: winget install Microsoft.DotNet.SDK.10, затем запустите этот файл снова."
    }
    else {
        Write-Step 'Сборка PKTracker (первый раз — пара минут)…'
        & dotnet build (Join-Path $root 'PKTracker.sln') -c Debug -v minimal
        if ($LASTEXITCODE -ne 0) {
            Write-Host 'Сборка не удалась — см. ошибки выше.' -ForegroundColor Red
            exit 1
        }
        $exe = $debug
    }
}

if ($NoLaunch) { exit 0 }

if (Get-Process -Name 'PKTracker' -ErrorAction SilentlyContinue) {
    Write-Host 'PKTracker уже запущен — смотрите значок в трее.'
    exit 0
}

Write-Step "Запуск: $exe"
Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe)
