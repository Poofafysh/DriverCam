<#
.SYNOPSIS
  Save and put back an install's DriverCam car setups (BepInEx\plugins\DriverCam\cars\*.cfg) around anything that
  deploys DriverCam (sync-install.ps1, a /build with --deploy-drivercam). The person's tuned values only live in the
  install; DriverCam's DeployToGame copies the repo's SavedSettings/DriverCam_cars over them.

    powershell -NoProfile -ExecutionPolicy Bypass -File tools/car-setups.ps1 -GameDir "<dir>" -Save
        -> "CARS SAVED <n> <folder>"  (pass <folder> to -Restore)
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/car-setups.ps1 -GameDir "<dir>" -Restore "<folder>" [-MoveNew]
        -> one "CARS OK|MISMATCH|NEW <file>" line per file, then "RESULT: ..."
        -MoveNew: car files the install didn't have before are moved to <folder>\_new_from_repo\ (use it for every
        install except <GameDir>: repo car setups never go into the frozen copy).

  Read-only on the repo. Exit code 0 = OK, 1 = failed (see the FAIL / RESULT line). The save folder is under
  %TEMP%\rogue-cars\ and records which install it came from (install.txt); -Restore refuses another install's folder.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$GameDir, [switch]$Save, [string]$Restore, [switch]$MoveNew)

$ErrorActionPreference = "Stop"
function Done([string]$msg, [int]$code) { Write-Host $msg; exit $code }

if ($Save -eq [bool]$Restore) { Done "RESULT: FAIL pass exactly one of -Save or -Restore <folder>" 1 }
$full = [IO.Path]::GetFullPath($GameDir).TrimEnd('\')
if (-not (Test-Path (Join-Path $full "BepInEx\plugins"))) { Done "RESULT: FAIL no BepInEx\plugins in $full" 1 }
$cars = Join-Path $full "BepInEx\plugins\DriverCam\cars"

if ($Save) {
    $leaf = Split-Path $full -Leaf
    $keep = Join-Path $env:TEMP ("rogue-cars\" + (Get-Date -Format "yyyyMMdd-HHmmss-fff") + "-" + $leaf)
    New-Item -ItemType Directory -Force $keep | Out-Null
    [IO.File]::WriteAllText((Join-Path $keep "install.txt"), $full)
    $n = 0
    if (Test-Path $cars) {
        foreach ($f in @(Get-ChildItem $cars -Filter *.cfg -File)) { Copy-Item $f.FullName $keep; $n++ }
    }
    Done "CARS SAVED $n $keep" 0
}

# ---------------------------------------------------------------- restore
$keep = $Restore.TrimEnd('\')
$marker = Join-Path $keep "install.txt"
if (-not (Test-Path $marker)) { Done "RESULT: FAIL $keep is not a car-setups save folder (no install.txt)" 1 }
$from = [IO.File]::ReadAllText($marker).Trim()
if (-not [string]::Equals($from, $full, [StringComparison]::OrdinalIgnoreCase)) { Done "RESULT: FAIL $keep was saved from $from, not $full" 1 }

$saved = @(Get-ChildItem $keep -Filter *.cfg -File)
if ($saved.Count -gt 0 -and -not (Test-Path $cars)) { New-Item -ItemType Directory -Force $cars | Out-Null }
foreach ($f in $saved) { Copy-Item $f.FullName (Join-Path $cars $f.Name) -Force }

$bad = 0; $new = 0
foreach ($f in $saved) {
    $dst = Join-Path $cars $f.Name
    if ((Test-Path $dst) -and (Get-FileHash $f.FullName).Hash -eq (Get-FileHash $dst).Hash) { Write-Host "CARS OK $($f.Name)" }
    else { Write-Host "CARS MISMATCH $($f.Name)"; $bad++ }
}
if (Test-Path $cars) {
    foreach ($f in @(Get-ChildItem $cars -Filter *.cfg -File | Where-Object { -not (Test-Path (Join-Path $keep $_.Name)) })) {
        $new++
        if ($MoveNew) {
            $aside = Join-Path $keep "_new_from_repo"
            New-Item -ItemType Directory -Force $aside | Out-Null
            Move-Item $f.FullName (Join-Path $aside $f.Name) -Force
            Write-Host "CARS NEW $($f.Name) (moved to $aside)"
        } else { Write-Host "CARS NEW $($f.Name) (left in place)" }
    }
}
if ($bad -gt 0) { Done "RESULT: FAIL $bad car setup(s) not restored - copies are in $keep" 1 }
Done "RESULT: OK $($saved.Count) restored, $new new" 0
