<#
.SYNOPSIS
  Set a plugin's version everywhere it is written, choosing a version that can't collide with the other developer's.
    powershell -ExecutionPolicy Bypass -File tools/bump-version.ps1 -Plugin CurbFeel -To patch     # minor | major | 1.4.0 | 1.4.0-beta.1
  patch/minor/major count up from the HIGHER of your version and origin/main's, so it never lands on or below what was
  already pushed. Updates: Plugin.cs (Version const or [BepInPlugin] literal), .csproj <Version>/<AssemblyVersion>/
  <FileVersion>/<InformationalVersion> if present, source/<Plugin>/README.md "Current version", root README.md table row.
  Refuses a version that is not higher than origin/main or that already has a tag. Does not commit.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Plugin, [string]$To = "patch", [string]$Remote = "origin", [string]$Branch = "main", [switch]$NoFetch)

$ErrorActionPreference = "Continue"
$root = (git rev-parse --show-toplevel 2>$null)
if (-not $root) { Write-Host "Not inside a git repo." -ForegroundColor Red; exit 1 }
Set-Location $root
function GitOut { $out = & git.exe @args 2>$null; if ($LASTEXITCODE -ne 0) { return }; $out }
# UTF-8 in, UTF-8 out, keeping each file's BOM (PowerShell 5.1's Get-Content reads BOM-less UTF-8 as ANSI and garbles non-ASCII)
function Read-Utf8([string]$path) { return [IO.File]::ReadAllText((Resolve-Path $path), [Text.Encoding]::UTF8) }
function Has-Bom([string]$path) { $b = [IO.File]::ReadAllBytes((Resolve-Path $path)); return $b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF }
function Die($m) { Write-Host "bump-version: $m" -ForegroundColor Red; exit 1 }

function Parse-SemVer([string]$v) {
    $m = [regex]::Match("$v".Trim(), '^[vV]?(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z][0-9A-Za-z.-]*))?(?:\+[0-9A-Za-z.-]+)?$')
    if (-not $m.Success) { return $null }
    [pscustomobject]@{ Nums = @(1..4 | ForEach-Object { if ($m.Groups[$_].Success) { [int]$m.Groups[$_].Value } else { 0 } }); Pre = $m.Groups[5].Value }
}
function Compare-SemVer([string]$a, [string]$b) {
    $x = Parse-SemVer $a; $y = Parse-SemVer $b
    for ($i = 0; $i -lt 4; $i++) { if ($x.Nums[$i] -ne $y.Nums[$i]) { return [Math]::Sign($x.Nums[$i] - $y.Nums[$i]) } }
    if ($x.Pre -eq $y.Pre) { return 0 }; if (-not $x.Pre) { return 1 }; if (-not $y.Pre) { return -1 }
    $xa = $x.Pre.Split('.'); $ya = $y.Pre.Split('.')
    for ($i = 0; $i -lt [Math]::Min($xa.Count, $ya.Count); $i++) {
        $xn = $xa[$i] -match '^\d+$'; $yn = $ya[$i] -match '^\d+$'
        if ($xn -and $yn) { $d = [Math]::Sign([long]$xa[$i] - [long]$ya[$i]); if ($d) { return $d } }
        elseif ($xn) { return -1 } elseif ($yn) { return 1 }
        else { $d = [Math]::Sign([string]::CompareOrdinal($xa[$i], $ya[$i])); if ($d) { return $d } }
    }
    return [Math]::Sign($xa.Count - $ya.Count)
}

# ---- find the plugin
$dir = $null; $cur = $null; $name = $null
foreach ($d in (Get-ChildItem source -Directory -ErrorAction SilentlyContinue)) {
    $pc = Join-Path $d.FullName "Plugin.cs"
    if (-not (Test-Path $pc)) { continue }
    $text = Read-Utf8 $pc
    $attr = [regex]::Match($text, '\[BepInPlugin\(\s*([^,]+?)\s*,\s*([^,]+?)\s*,\s*([^\)]+?)\s*\)\]')
    if (-not $attr.Success) { continue }
    $consts = @{}; foreach ($m in [regex]::Matches($text, 'const\s+string\s+(\w+)\s*=\s*"([^"]*)"')) { $consts[$m.Groups[1].Value] = $m.Groups[2].Value }
    $res = { param($t) $t = $t.Trim(); if ($t.StartsWith('"')) { $t.Trim('"') } elseif ($consts.ContainsKey($t)) { $consts[$t] } else { $t } }
    $n = & $res $attr.Groups[2].Value
    if ($n -ieq $Plugin -or $d.Name -ieq $Plugin) { $dir = "source/$($d.Name)"; $name = $n; $cur = & $res $attr.Groups[3].Value; $verTok = $attr.Groups[3].Value.Trim(); break }
}
if (-not $dir) { Die "no plugin named '$Plugin' in source/*/Plugin.cs" }

# ---- origin's version
if (-not $NoFetch) { & git.exe fetch $Remote --tags --quiet 2>$null }
$originVer = $null
$ot = (@(GitOut show "$Remote/${Branch}:$dir/Plugin.cs") -join "`n")
if ($ot) {
    $oc = @{}; foreach ($m in [regex]::Matches($ot, 'const\s+string\s+(\w+)\s*=\s*"([^"]*)"')) { $oc[$m.Groups[1].Value] = $m.Groups[2].Value }
    $oa = [regex]::Match($ot, '\[BepInPlugin\(\s*([^,]+?)\s*,\s*([^,]+?)\s*,\s*([^\)]+?)\s*\)\]')
    if ($oa.Success) { $tok = $oa.Groups[3].Value.Trim(); $originVer = if ($tok.StartsWith('"')) { $tok.Trim('"') } elseif ($oc.ContainsKey($tok)) { $oc[$tok] } else { $null } }
}

# ---- choose the new version
$start = $cur
if ($originVer -and (Parse-SemVer $originVer) -and (Parse-SemVer $cur) -and (Compare-SemVer $originVer $cur) -gt 0) { $start = $originVer }
$sv = Parse-SemVer $start
if (-not $sv -and $To -in 'patch', 'minor', 'major') { Die "current version '$cur' is not a valid version; pass an explicit one with -To 1.2.3" }
switch ($To) {
    'patch' { $new = if ($sv.Pre) { "{0}.{1}.{2}" -f $sv.Nums[0], $sv.Nums[1], $sv.Nums[2] } else { "{0}.{1}.{2}" -f $sv.Nums[0], $sv.Nums[1], ($sv.Nums[2] + 1) } }
    'minor' { $new = "{0}.{1}.0" -f $sv.Nums[0], ($sv.Nums[1] + 1) }
    'major' { $new = "{0}.0.0" -f ($sv.Nums[0] + 1) }
    default { $new = $To.TrimStart('v', 'V') }
}
if (-not (Parse-SemVer $new)) { Die "'$new' is not a valid version (use MAJOR.MINOR.PATCH, optionally -prerelease)" }
if ($originVer -and (Parse-SemVer $originVer) -and (Compare-SemVer $new $originVer) -le 0) { Die "$new is not higher than $originVer on $Remote/$Branch - pick a higher version" }
$tags = @(GitOut tag --list)
$clash = $tags | Where-Object { $_ -match ("(?i)^(" + [regex]::Escape($name) + "[-_ ]?)?v?" + [regex]::Escape($new) + "$") }
if ($clash) { Die "tag(s) already exist for $name $new`: $($clash -join ', ')" }

# ---- write it everywhere
$changed = @()
function Set-File([string]$path, [string]$text) { $full = Join-Path $root $path; [IO.File]::WriteAllText($full, $text, (New-Object Text.UTF8Encoding (Has-Bom $full))); $script:changed += $path }

$pcPath = "$dir/Plugin.cs"; $pc = Read-Utf8 $pcPath; $orig = $pc
$pc = [regex]::Replace($pc, '(const\s+string\s+Version\s*=\s*")[^"]*(")', "`${1}$new`${2}")
if ($verTok.StartsWith('"')) { $pc = [regex]::Replace($pc, '(\[BepInPlugin\(\s*[^,]+?\s*,\s*[^,]+?\s*,\s*")[^"]*("\s*\)\])', "`${1}$new`${2}") }
if ($pc -ne $orig) { Set-File $pcPath $pc }

$cp = Get-ChildItem $dir -Filter *.csproj | Select-Object -First 1
if ($cp) {
    $rel = "$dir/$($cp.Name)"; $t = Read-Utf8 $cp.FullName; $o = $t
    foreach ($tag in 'Version', 'AssemblyVersion', 'FileVersion', 'InformationalVersion') {
        $plain = ($new -split '-')[0]
        $value = if ($tag -in 'AssemblyVersion', 'FileVersion') { $plain } else { $new }
        $t = [regex]::Replace($t, "(<$tag>)[^<\$]+(</$tag>)", "`${1}$value`${2}")
    }
    if ($t -ne $o) { Set-File $rel $t }
}

$rd = "$dir/README.md"
if (Test-Path $rd) {
    $t = Read-Utf8 $rd; $o = $t
    $t = [regex]::Replace($t, '(?i)(current version:\s*\**\s*)v?\d+(?:\.\d+){0,3}(?:-[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z.]+)?', "`${1}$new")
    if ($t -ne $o) { Set-File $rd $t }
}
if (Test-Path "README.md") {
    $t = Read-Utf8 "README.md"; $o = $t
    $t = [regex]::Replace($t, '(?m)(^\|\s*\**' + [regex]::Escape($name) + '\**\s*\|\s*)v?[0-9][0-9A-Za-z.+-]*(\s*\|)', "`${1}$new`${2}")
    if ($t -ne $o) { Set-File "README.md" $t }
}

Write-Host "bump-version: $name $cur -> $new" -ForegroundColor Green
if ($originVer) { Write-Host "  ($Remote/$Branch has $originVer)" -ForegroundColor DarkGray }
$changed | ForEach-Object { Write-Host "  updated $_" -ForegroundColor DarkGray }
Write-Host "Next: commit, then run tools/push-check.ps1" -ForegroundColor DarkGray
exit 0
