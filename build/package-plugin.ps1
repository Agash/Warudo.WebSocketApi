<#
.SYNOPSIS
    Builds the Warudo plugin against an installed Warudo and packs it as a .warudo mod.
.PARAMETER WarudoRoot
    Warudo's install folder; defaults to the Steam library location.
.PARAMETER Install
    Also copies the mod into Warudo's plugin folder, where Warudo loads it while running.
#>
param(
    [string]$WarudoRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Warudo',
    [string]$Version = '0.1.0',
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$managed = Join-Path $WarudoRoot 'Warudo_Data\Managed'
if (-not (Test-Path (Join-Path $managed 'Warudo.Core.dll'))) {
    throw "Warudo is not installed at $WarudoRoot; pass -WarudoRoot."
}

$plugin = Join-Path $root 'src\Warudo.WebSocketApi.Plugin\Warudo.WebSocketApi.Plugin.csproj'
dotnet build $plugin -c Release -nologo "-p:WarudoManaged=$managed"
if ($LASTEXITCODE -ne 0) { throw 'The plugin build failed.' }

$dll = Join-Path $root 'src\Warudo.WebSocketApi.Plugin\bin\Release\netstandard2.0\Agash.Warudo.WebSocketApi.dll'
$out = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force $out | Out-Null
$mod = Join-Path $out 'WebSocketApi.warudo'

dotnet run --project (Join-Path $root 'tools\Warudo.ModPacker') -c Release -- `
    --dll $dll --out $mod --name 'WebSocket API' --version $Version `
    --description 'JSON-RPC 2.0 API over WebSocket for automation and streaming tools.' --author 'Agash'
if ($LASTEXITCODE -ne 0) { throw 'Packing the mod failed.' }

if ($Install) {
    $plugins = Join-Path $WarudoRoot 'Warudo_Data\StreamingAssets\Plugins'
    Copy-Item $mod $plugins -Force
    Write-Host "Installed into $plugins"
}
