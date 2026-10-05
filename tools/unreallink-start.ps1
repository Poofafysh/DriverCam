<#
.SYNOPSIS
  Starts Unreal Engine as the UnrealLink renderer: a small window running the RogueLink link level, paced by the
  game (see source/UnrealLink/README.md). Also sets the Unreal project up and builds it.

  powershell -NoProfile -ExecutionPolicy Bypass -File tools/unreallink-start.ps1 [-Setup] [-Build] [-NoLaunch]
      [-Project <path to .uproject>] [-Engine <UE folder>] [-ResX 320] [-ResY 180] [-WinX 20] [-WinY 20] [-Offscreen]
      [-Stop]

  -Setup     copies source/UnrealLink/Unreal/RogueLink into <project>\Plugins\RogueLink (source files only), the
             code-project stub (Source\*.Target.cs, Source\RogueDriverAnim\) into the project, merges
             Unreal/Project/Config/DefaultEngine.RogueLink.ini into Config\DefaultEngine.ini (between markers) and adds
             the module and the plugin to the .uproject. Safe to repeat.
  -Build     Build.bat <Name>Editor Win64 Development (needed after -Setup or any change to the plugin source).
  -NoLaunch  only setup / build.
  -Offscreen no window at all (-RenderOffscreen).
  -Stop      ends a running UnrealLink Unreal (started with -game on this project) and exits.
  Defaults: -Project from $env:UNREALLINK_PROJECT, else Documents\RogueDriverAnim\RogueDriverAnim.uproject;
  -Engine from $env:UNREALLINK_ENGINE, else C:\Program Files\Epic Games\UE_5.8.
  Keep the project at a short path (UE builds fail past 260 characters).
  Output ends with 'RESULT: OK' or 'RESULT: FAIL'. Exit 0 = OK.
#>
[CmdletBinding()]
param(
    [string]$Project,
    [string]$Engine,
    [switch]$Setup,
    [switch]$Build,
    [switch]$NoLaunch,
    [switch]$Offscreen,
    [switch]$Stop,
    [int]$ResX = 320,
    [int]$ResY = 180,
    [int]$WinX = 20,
    [int]$WinY = 20
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root "source\UnrealLink\Unreal"
$utf8 = New-Object Text.UTF8Encoding($false)

function Fail([string]$m) { Write-Output "ERROR $m"; Write-Output "RESULT: FAIL"; exit 1 }

if (-not $Project) { $Project = $env:UNREALLINK_PROJECT }
if (-not $Project) { $Project = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "RogueDriverAnim\RogueDriverAnim.uproject" }
if (-not $Engine) { $Engine = $env:UNREALLINK_ENGINE }
if (-not $Engine) { $Engine = "C:\Program Files\Epic Games\UE_5.8" }
if (-not (Test-Path $Project)) { Fail "no Unreal project at $Project (pass -Project)" }
$projDir = Split-Path -Parent (Resolve-Path $Project).Path
$projName = [IO.Path]::GetFileNameWithoutExtension($Project)
$editorExe = Join-Path $Engine "Engine\Binaries\Win64\UnrealEditor.exe"
if (-not (Test-Path $editorExe)) { Fail "no UnrealEditor.exe under $Engine (pass -Engine)" }

function Get-LinkProcesses {
    $all = @(Get-CimInstance Win32_Process -Filter "Name = 'UnrealEditor.exe'" -ErrorAction SilentlyContinue)
    return @($all | Where-Object { $_.CommandLine -and $_.CommandLine -like "*$projName.uproject*" -and $_.CommandLine -like "*-game*" })
}

if ($Stop) {
    $p = Get-LinkProcesses
    foreach ($x in $p) { Stop-Process -Id $x.ProcessId -Force; Write-Output "stopped Unreal pid $($x.ProcessId)" }
    if ($p.Count -eq 0) { Write-Output "no UnrealLink Unreal running" }
    Write-Output "RESULT: OK"; exit 0
}

if ($Setup) {
    if ($projName -ne "RogueDriverAnim") { Write-Output "WARN the code stub is named RogueDriverAnim; project '$projName' needs its own Target.cs names" }
    # 1. plugin source (Binaries / Intermediate in the project are kept; UBT rebuilds on change)
    $dst = Join-Path $projDir "Plugins\RogueLink"
    New-Item -ItemType Directory -Force (Join-Path $dst "Source") | Out-Null
    if (Test-Path (Join-Path $dst "Source\RogueLink")) { Remove-Item -Recurse -Force (Join-Path $dst "Source\RogueLink") }
    Copy-Item -Recurse (Join-Path $src "RogueLink\Source\RogueLink") (Join-Path $dst "Source\RogueLink")
    Copy-Item (Join-Path $src "RogueLink\RogueLink.uplugin") $dst -Force
    if (Test-Path (Join-Path $dst "Shaders")) { Remove-Item -Recurse -Force (Join-Path $dst "Shaders") }
    Copy-Item -Recurse (Join-Path $src "RogueLink\Shaders") (Join-Path $dst "Shaders")
    Write-Output "plugin source -> $dst"
    # 2. code-project stub
    $stubDst = Join-Path $projDir "Source"
    New-Item -ItemType Directory -Force (Join-Path $stubDst "RogueDriverAnim") | Out-Null
    foreach ($f in @(Get-ChildItem (Join-Path $src "Project\Source") -Recurse -File)) {
        $rel = $f.FullName.Substring((Join-Path $src "Project\Source").Length + 1)
        $to = Join-Path $stubDst $rel
        New-Item -ItemType Directory -Force (Split-Path -Parent $to) | Out-Null
        Copy-Item -Force $f.FullName $to
    }
    Write-Output "code stub -> $(Join-Path $projDir 'Source')"
    # 3. DefaultEngine.ini block
    $ini = Join-Path $projDir "Config\DefaultEngine.ini"
    $block = [IO.File]::ReadAllText((Join-Path $src "Project\Config\DefaultEngine.RogueLink.ini"), $utf8)
    $text = ""
    if (Test-Path $ini) { $text = [IO.File]::ReadAllText($ini, $utf8) }
    $text = [regex]::Replace($text, '(?s)\r?\n?; >>> RogueLink.*?; <<< RogueLink[^\n]*\n?', '')
    $text = $text.TrimEnd() + "`r`n`r`n; >>> RogueLink (tools/unreallink-start.ps1 -Setup; edit source/UnrealLink/Unreal/Project/Config/DefaultEngine.RogueLink.ini instead)`r`n" + $block.Trim() + "`r`n; <<< RogueLink`r`n"
    [IO.File]::WriteAllText($ini, $text, $utf8)
    Write-Output "ini block -> $ini"
    # 4. .uproject: the game module and the plugin
    $json = [IO.File]::ReadAllText($Project, $utf8) | ConvertFrom-Json
    $mods = @()
    if ($json.PSObject.Properties.Name -contains "Modules") { $mods = @($json.Modules) }
    if (-not ($mods | Where-Object { $_.Name -eq $projName })) {
        $mods += [pscustomobject]@{ Name = $projName; Type = "Runtime"; LoadingPhase = "Default" }
        $json | Add-Member -Force -NotePropertyName Modules -NotePropertyValue $mods
    }
    $plugs = @()
    if ($json.PSObject.Properties.Name -contains "Plugins") { $plugs = @($json.Plugins) }
    if (-not ($plugs | Where-Object { $_.Name -eq "RogueLink" })) {
        $plugs += [pscustomobject]@{ Name = "RogueLink"; Enabled = $true }
        $json | Add-Member -Force -NotePropertyName Plugins -NotePropertyValue $plugs
    }
    [IO.File]::WriteAllText($Project, ($json | ConvertTo-Json -Depth 10), $utf8)
    Write-Output "uproject -> module $projName, plugin RogueLink"
}

if ($Build) {
    $bat = Join-Path $Engine "Engine\Build\BatchFiles\Build.bat"
    Write-Output "building ${projName}Editor (first build about 2-5 min, then under a minute) ..."
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $bat "${projName}Editor" Win64 Development "-Project=$((Resolve-Path $Project).Path)" -WaitMutex -NoHotReloadFromIDE | ForEach-Object { if ($_ -match 'error|warning C|Result:|Total execution time') { Write-Output $_ } }
    $code = $LASTEXITCODE
    Write-Output ("build exit {0} in {1:0} s" -f $code, $sw.Elapsed.TotalSeconds)
    if ($code -ne 0) { Fail "build failed (see the lines above, or $env:LOCALAPPDATA\UnrealBuildTool\Log.txt)" }
}

if (-not $NoLaunch) {
    $running = Get-LinkProcesses
    if ($running.Count -gt 0) { Write-Output "already running: Unreal pid $($running[0].ProcessId)"; Write-Output "RESULT: OK"; exit 0 }
    $ueArgs = @("`"$((Resolve-Path $Project).Path)`"", "-game", "-dx11", "-nosplash", "-nosound", "-NoLoadingScreen",
              "-ExecCmds=`"t.MaxFPS 0, r.VSync 0, t.IdleWhenNotForeground 0`"")
    if ($Offscreen) { $ueArgs += "-RenderOffscreen" }
    else { $ueArgs += @("-windowed", "-ResX=$ResX", "-ResY=$ResY", "-WinX=$WinX", "-WinY=$WinY") }
    $p = Start-Process -FilePath $editorExe -ArgumentList $ueArgs -PassThru
    Write-Output "started Unreal pid $($p.Id) ($ResX x $ResY window$(if ($Offscreen) { ', offscreen' })); log: $(Join-Path $projDir "Saved\Logs\$projName.log")"
    Write-Output "first start takes 20-40 s (shaders compile once). Stop it with -Stop."
}
Write-Output "RESULT: OK"
exit 0
