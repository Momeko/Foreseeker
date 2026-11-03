# Build the Foreseeker BepInEx plugin and deploy it into the ORIGINAL game install.
# Default target: D:\Software\steamapps\common\Darkest Dungeon(R) II  (discovered by wildcard
# because the folder name contains a non-ASCII character and this script must stay ASCII-only).
#
# The user launches the game; this script never starts it.
$ErrorActionPreference = 'Stop'

$common  = 'D:\Software\steamapps\common'
$gameDir = Get-ChildItem -LiteralPath $common -Directory -ErrorAction Stop |
           Where-Object { $_.Name -like 'Darkest Dungeon*II' } |
           Select-Object -First 1
if (-not $gameDir) { throw "game install not found under $common" }
$game = $gameDir.FullName

$managed = Join-Path $game 'Darkest Dungeon II_Data\Managed'
$core    = Join-Path $game 'BepInEx\core'
$csc     = 'C:\Program Files\dotnet\sdk\10.0.301\Roslyn\bincore\csc.dll'
$src     = @((Join-Path $PSScriptRoot 'Plugin.cs'), (Join-Path $PSScriptRoot 'SaveReader.cs'), (Join-Path $PSScriptRoot 'RandomTrace.cs'), (Join-Path $PSScriptRoot 'Probe.cs'), (Join-Path $PSScriptRoot 'SolverProbe.cs'), (Join-Path $PSScriptRoot 'ForeseekerHost.cs'), (Join-Path $PSScriptRoot 'Solver.cs'), (Join-Path $PSScriptRoot 'SimState.cs'))
$outDir  = Join-Path $PSScriptRoot 'bin'

if (-not (Test-Path -LiteralPath $managed)) { throw "managed folder not found: $managed" }
if (-not (Test-Path -LiteralPath $csc))     { throw "csc not found: $csc" }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$refs = @(
    'mscorlib.dll', 'System.dll', 'System.Core.dll', 'netstandard.dll', 'System.Runtime.dll',
    'UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.IMGUIModule.dll', 'UnityEngine.InputLegacyModule.dll',
    'Newtonsoft.Json.dll', 'IronCrown.dll'
) | ForEach-Object { Join-Path $managed $_ }
$refs += (Join-Path $core 'BepInEx.dll')
$refs += (Join-Path $core '0Harmony.dll')

$cscArgs = @(
    '-nologo', '-nostdlib+', '-noconfig', '-target:library', '-langversion:latest',
    '-optimize+', '-deterministic'
) + ($refs | ForEach-Object { "-r:$_" }) + @("-out:$outDir\Foreseeker.dll") + $src

& dotnet $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "compile failed with exit code $LASTEXITCODE" }

$deployed = Join-Path $game 'BepInEx\plugins\Foreseeker.dll'

# If the game is running, the DLL is mapped and cannot be overwritten in place.
# Renaming the mapped file is allowed on NTFS, so fall back to: move old -> .prev, copy new.
try {
    Copy-Item -LiteralPath (Join-Path $outDir 'Foreseeker.dll') -Destination $deployed -Force -ErrorAction Stop
    Write-Host "deployed in place (game not running)."
} catch {
    $prev = Join-Path $game 'BepInEx\plugins\Foreseeker.dll.prev'
    if (Test-Path -LiteralPath $prev) { Remove-Item -LiteralPath $prev -Force }
    Move-Item -LiteralPath $deployed -Destination $prev -Force
    Copy-Item -LiteralPath (Join-Path $outDir 'Foreseeker.dll') -Destination $deployed -Force
    Write-Host "game was running: old DLL renamed to Foreseeker.dll.prev (restart the game to load the new build)."
}

# Remove stale backups from earlier versions.
Get-ChildItem (Join-Path $game 'BepInEx\plugins') -Filter 'Foreseeker.dll.old-*' -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

# Install the default INI next to the DLL, but never overwrite an edited one.
$iniSrc = Join-Path $PSScriptRoot 'Foreseeker.ini'
$iniDst = Join-Path $game 'BepInEx\plugins\Foreseeker.ini'
if ((Test-Path -LiteralPath $iniSrc) -and -not (Test-Path -LiteralPath $iniDst)) {
    Copy-Item -LiteralPath $iniSrc -Destination $iniDst -Force
    Write-Host "installed default INI: $iniDst"
}

# The old BepInEx .cfg config is no longer used.
Get-ChildItem (Join-Path $game 'BepInEx\config') -Filter 'com.momeko.dd2.foreseeker.cfg' -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue


Write-Host "game   : $game"
Write-Host "built  : $outDir\Foreseeker.dll"
Write-Host "deploy : $deployed"
Get-Item -LiteralPath $deployed | Select-Object Length, LastWriteTime, FullName | Format-List
