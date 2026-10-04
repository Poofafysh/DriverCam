<#
.SYNOPSIS
  IL2CPP checks the compiler can't do, on the built plugin DLLs (no game run needed):
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/il2cpp-check.ps1                 # every plugin
    ... -Plugin RacingLine,Police                                                              # only these
    ... -NoBuild                                                                               # check the DLLs already in bin/
  Exit code 0 = OK (warnings may be listed), 1 = something must be fixed, 2 = could not run.

  Checks (tools/IlCheck, reads the DLLs and the game's BepInEx/interop assemblies)
    1. Stripped methods   every game / Unity method the plugin calls must exist in the game build. The interop lists
                          all of them, but a method IL2CPP stripped and BepInEx couldn't rebuild throws
                          "Method unstripping failed" in game (e.g. GUI.DrawTexture). Calls through rebuilt
                          methods are followed down to the one that fails.
    2. Injectable types   no instance method on a MonoBehaviour (or other injected class) takes a `ref` to a
                          compiler-made struct: a local function using both locals and `this` compiles to one, and
                          ClassInjector.RegisterTypeInIl2Cpp then throws, so the plugin never loads.

  Builds use -p:SkipDeploy=true: nothing is copied into the game (DriverCam's car setups stay untouched).
#>
[CmdletBinding()]
param([string[]]$Plugin, [switch]$NoBuild, [string]$GameDir)

$ErrorActionPreference = "Continue"
# -File passes "A,B" as one string: split it
if ($Plugin) { $Plugin = @($Plugin | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
$root = (git rev-parse --show-toplevel 2>$null)
if (-not $root) { Write-Host "Not inside a git repo." -ForegroundColor Red; exit 2 }
Set-Location $root

if (-not $GameDir) {
    $lp = Join-Path $root "source/local.props"
    if (Test-Path $lp) { $GameDir = ([regex]::Match((Get-Content $lp -Raw), '<GameDir>([^<]+)</GameDir>')).Groups[1].Value }
}
if (-not $GameDir) { $GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Driving Rogue" }
$interop = Join-Path $GameDir "BepInEx\interop"
if (-not (Test-Path (Join-Path $interop "Assembly-CSharp.dll"))) {
    Write-Host "BepInEx\interop not found in $GameDir (set source/local.props or -GameDir; start the game once with BepInEx)." -ForegroundColor Red
    exit 2
}

# ---------------------------------------------------------------- targets
$targets = @()
foreach ($d in (Get-ChildItem (Join-Path $root "source") -Directory)) {
    $proj = Get-ChildItem $d.FullName -Filter *.csproj -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proj) { continue }
    if ($Plugin -and -not ($Plugin | Where-Object { $_ -ieq $d.Name })) { continue }
    $asm = ([regex]::Match((Get-Content $proj.FullName -Raw), '<AssemblyName>([^<]+)</AssemblyName>')).Groups[1].Value
    if (-not $asm) { $asm = $d.Name }
    $targets += [pscustomobject]@{ Name = $d.Name; Dir = $d.FullName; Assembly = $asm }
}
if ($Plugin) {
    foreach ($n in $Plugin) { if (-not ($targets | Where-Object { $_.Name -ieq $n })) { Write-Host "no plugin '$n' under source/" -ForegroundColor Red; exit 2 } }
}
if (@($targets).Count -eq 0) { Write-Host "no plugins found under source/" -ForegroundColor Red; exit 2 }

# ---------------------------------------------------------------- build
$tool = Join-Path $root "tools\IlCheck"
$out = & dotnet build $tool -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { $out | Select-Object -Last 15 | ForEach-Object { Write-Host $_ }; Write-Host "RESULT: could not build tools/IlCheck" -ForegroundColor Red; exit 2 }
$toolDll = Get-ChildItem (Join-Path $tool "bin\Release") -Recurse -Filter IlCheck.dll | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $toolDll) { Write-Host "RESULT: tools/IlCheck built but IlCheck.dll not found" -ForegroundColor Red; exit 2 }

$dlls = @()
$buildFails = 0
foreach ($t in $targets) {
    if (-not $NoBuild) {
        $out = & dotnet build $t.Dir -c Release -nologo "-p:GameDir=$GameDir" "-p:SkipDeploy=true"
        if ($LASTEXITCODE -ne 0) {
            $buildFails++
            Write-Host "  FAIL  $($t.Name) does not build:" -ForegroundColor Red
            $errs = @($out | Select-String -Pattern 'error' | Select-Object -First 5 | ForEach-Object { $_.Line.Trim() })
            if ($errs.Count -eq 0) { $errs = @($out | Select-Object -Last 8) }   # e.g. a file locked by another build
            $errs | ForEach-Object { Write-Host "        $_" }
            continue
        }
    }
    $dll = Get-ChildItem (Join-Path $t.Dir "bin\Release") -Recurse -Filter "$($t.Assembly).dll" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $dll) { $buildFails++; Write-Host "  FAIL  $($t.Name): bin\Release\$($t.Assembly).dll not found (build it, or drop -NoBuild)" -ForegroundColor Red; continue }
    $dlls += $dll.FullName
}

# ---------------------------------------------------------------- check
$code = 0
if (@($dlls).Count -gt 0) {
    & dotnet $toolDll.FullName --interop $interop @dlls | ForEach-Object {
        $line = $_
        if ($line -match '^\s+FAIL') { Write-Host $line -ForegroundColor Red }
        elseif ($line -match '^\s+WARN') { Write-Host $line -ForegroundColor Yellow }
        elseif ($line -match '^\s+info') { Write-Host $line -ForegroundColor DarkGray }
        elseif ($line -match '^RESULT') { }
        else { Write-Host $line -ForegroundColor Cyan }
        if ($line -match '^RESULT: (\d+) problem') { $script:toolFails = [int]$Matches[1] }
    }
    $code = $LASTEXITCODE
}
$total = $buildFails + $(if ($script:toolFails) { $script:toolFails } else { 0 })
if ($code -eq 2) { Write-Host "RESULT: IlCheck could not run" -ForegroundColor Red; exit 2 }
if ($total -eq 0) { Write-Host "RESULT: OK ($(@($dlls).Count) plugin(s) checked)" -ForegroundColor Green; exit 0 }
Write-Host "RESULT: fix $total problem(s)" -ForegroundColor Red
exit 1
