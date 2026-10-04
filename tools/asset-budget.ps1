<#
.SYNOPSIS
  Static triangle / material / draw-call budget for the shipped 3D assets: DriverCam cockpits (*.dcm) and Police car
  models (*.pcm). Reads the text files directly (no Blender, no game). Read-only.
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/asset-budget.ps1 [-Path <file or folder>[,...]]

  Counting rules (the same ones the plugins use when they build meshes):
    .dcm (source/DriverCam/CockpitModel.cs + Cockpit.cs)
      tris   = 'f' + 'u' lines (unwelded: 3 vertices each)
      verts  = tris * 3
      mats   = distinct material tags that own at least one triangle ('m <tag>'; the tag resets to interior_dark at
               every 'o' line)
      draws  = per group ('g <group>', default Misc): one renderer per distinct tag over the group's parts without a
               pivot, plus one per tag of every part with a pivot ('p' line: steering wheel, needles)
      mirrors= distinct mirror tags with triangles (mirror_glass, mirror_left, mirror_right: each can drive a
               render-to-texture camera)
    .pcm (source/Police/PoliceModels.cs)
      tris   = 't' lines + 'f' + 'u' lines
      verts  = 'v' lines + 3 per 'f'/'u' line
      mats   = distinct tags with triangles
      draws  = per part with triangles: 1 (all untextured tags share one palette mesh) + 1 per textured tag
               ('tex <tag>') on a part without a pivot (a decal mesh); a pivot part (wheel) is 1
  Budgets (fixed here so every run is judged the same; change them only when the person sets new numbers):
    cockpit: tris <= 8000, draws <= 60, mirrors <= 3
    police : tris <= 6000, draws <= 8
  Output: one 'ASSET' line per file, then 'TOTAL' and 'RESULT: OK | FAIL'. Exit 0 = all within budget, 1 = any OVER,
  2 = could not run (no files / unreadable file).
#>
[CmdletBinding()]
param([string[]]$Path)

$ErrorActionPreference = "Stop"
$root = (git rev-parse --show-toplevel 2>$null)
if (-not $root) { $root = Split-Path -Parent $PSScriptRoot }
Set-Location $root

$Budget = @{
    dcm = @{ Tris = 8000; Draws = 60; Mirrors = 3 }
    pcm = @{ Tris = 6000; Draws = 8; Mirrors = 0 }
}
$MirrorTags = @("mirror_glass", "mirror_left", "mirror_right")

if (-not $Path -or $Path.Count -eq 0) {
    $Path = @("source\DriverCam\Assets\cockpits", "source\Police\Assets\models")
}
$files = New-Object System.Collections.Generic.List[string]
foreach ($p in $Path) {
    foreach ($one in ($p -split ',')) {
        $q = $one.Trim(); if (-not $q) { continue }
        if (-not (Test-Path -LiteralPath $q)) { Write-Output "ERROR not found: $q"; Write-Output "RESULT: FAIL (could not run)"; exit 2 }
        $item = Get-Item -LiteralPath $q
        if ($item.PSIsContainer) {
            foreach ($f in @(Get-ChildItem -LiteralPath $item.FullName -File | Where-Object { $_.Extension -eq ".dcm" -or $_.Extension -eq ".pcm" } | Sort-Object Name)) { $files.Add($f.FullName) }
        } elseif ($item.Extension -eq ".dcm" -or $item.Extension -eq ".pcm") { $files.Add($item.FullName) }
    }
}
if ($files.Count -eq 0) { Write-Output "ERROR no .dcm / .pcm files under: $($Path -join ', ')"; Write-Output "RESULT: FAIL (could not run)"; exit 2 }

$rootFwd = ($root -replace '\\', '/').TrimEnd('/')
function Rel([string]$full) { $r = ($full -replace '\\', '/'); if ($r.StartsWith($rootFwd + '/', [StringComparison]::OrdinalIgnoreCase)) { $r = $r.Substring($rootFwd.Length + 1) }; return $r }

$over = 0; $sumTris = 0; $sumDraws = 0
foreach ($file in $files) {
    $kind = [IO.Path]::GetExtension($file).TrimStart('.').ToLowerInvariant()
    $tris = 0; $verts = 0
    $matsUsed = New-Object 'System.Collections.Generic.HashSet[string]'
    $texTags = New-Object 'System.Collections.Generic.HashSet[string]'
    # parts: name, group, pivot, tags with tris
    $parts = New-Object System.Collections.Generic.List[object]
    $part = $null
    $tag = $(if ($kind -eq "dcm") { "interior_dark" } else { "paint_a" })
    try {
        foreach ($raw in [IO.File]::ReadLines($file)) {
            if ($raw.Length -lt 2 -or $raw[0] -eq '#') { continue }
            $sp = $raw.IndexOf(' '); $k = $(if ($sp -lt 0) { $raw } else { $raw.Substring(0, $sp) })
            switch -CaseSensitive ($k) {
                "tex" { $t = $raw.Split(' ', [StringSplitOptions]::RemoveEmptyEntries); if ($t.Count -ge 2) { [void]$texTags.Add($t[1]) } }
                "o" {
                    $t = $raw.Split(' ', [StringSplitOptions]::RemoveEmptyEntries)
                    $part = [pscustomobject]@{ Name = $t[1]; Group = "Misc"; Pivot = $false; Tags = (New-Object 'System.Collections.Generic.HashSet[string]') }
                    $parts.Add($part)
                    if ($kind -eq "dcm") { $tag = "interior_dark" }
                }
                "g" { if ($part) { $part.Group = $raw.Substring(2).Trim() } }
                "p" { if ($part) { $part.Pivot = $true } }
                "m" { $tag = $raw.Substring(2).Trim() }
                "v" { if ($kind -eq "pcm") { $verts++ } }
                "t" { if ($kind -eq "pcm" -and $part) { $tris++; [void]$matsUsed.Add($tag); [void]$part.Tags.Add($tag) } }
                { $_ -eq "f" -or $_ -eq "u" } { if ($part) { $tris++; $verts += 3; [void]$matsUsed.Add($tag); [void]$part.Tags.Add($tag) } }
            }
        }
    } catch { Write-Output "ERROR reading $(Rel $file): $($_.Exception.Message)"; Write-Output "RESULT: FAIL (could not run)"; exit 2 }

    $draws = 0
    if ($kind -eq "dcm") {
        $groups = @{}
        foreach ($pt in $parts) {
            if ($pt.Tags.Count -eq 0) { continue }
            if ($pt.Pivot) { $draws += $pt.Tags.Count; continue }
            if (-not $groups.ContainsKey($pt.Group)) { $groups[$pt.Group] = New-Object 'System.Collections.Generic.HashSet[string]' }
            foreach ($tg in $pt.Tags) { [void]$groups[$pt.Group].Add($tg) }
        }
        foreach ($g in $groups.Values) { $draws += $g.Count }
    } else {
        foreach ($pt in $parts) {
            if ($pt.Tags.Count -eq 0) { continue }
            if ($pt.Pivot) { $draws += 1; continue }
            $opaque = 0; $decals = 0
            foreach ($tg in $pt.Tags) { if ($texTags.Contains($tg)) { $decals++ } else { $opaque++ } }
            $draws += $decals + $(if ($opaque -gt 0) { 1 } else { 0 })
        }
    }
    $mirrors = 0; foreach ($m in $MirrorTags) { if ($matsUsed.Contains($m)) { $mirrors++ } }

    $b = $Budget[$kind]
    $bad = @()
    if ($tris -gt $b.Tris) { $bad += "tris" }
    if ($draws -gt $b.Draws) { $bad += "draws" }
    if ($mirrors -gt $b.Mirrors) { $bad += "mirrors" }
    $state = $(if ($bad.Count -eq 0) { "OK" } else { "OVER(" + ($bad -join ',') + ")" })
    if ($bad.Count -gt 0) { $over++ }
    $sumTris += $tris; $sumDraws += $draws
    $mirrorText = $(if ($kind -eq "dcm") { " mirrors=$mirrors/$($b.Mirrors)" } else { "" })
    Write-Output ("ASSET {0} tris={1}/{2} verts={3} mats={4} draws={5}/{6}{7} parts={8} {9}" -f (Rel $file), $tris, $b.Tris, $verts, $matsUsed.Count, $draws, $b.Draws, $mirrorText, $parts.Count, $state)
}
Write-Output ("TOTAL files={0} tris={1} draws={2} over={3}" -f $files.Count, $sumTris, $sumDraws, $over)
if ($over -gt 0) { Write-Output "RESULT: FAIL ($over file(s) over budget)"; exit 1 }
Write-Output "RESULT: OK"
exit 0
