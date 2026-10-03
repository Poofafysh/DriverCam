<#
.SYNOPSIS
  Pre-push checks for the Driving Rogue mods repo (DriverCam + CurbFeel + any future plugin in source/).
  Run from anywhere inside the repo:  powershell -ExecutionPolicy Bypass -File tools/push-check.ps1
  Exit code 0 = OK to push (warnings may be listed), 1 = something must be fixed first.

  Checks
    1. Remote state      fetch; are you behind origin/main? which files did the other person change that you also changed?
    2. Working tree      uncommitted / untracked files; merge-conflict markers in tracked text files
    3. Source only       no binaries, BepInEx/dotnet folders, build output, game assets or big files
    4. Plugin identity   every source/*/Plugin.cs: unique BepInPlugin GUID, name and AssemblyName
    5. Versions          a plugin whose files changed must have a NEW version, higher than origin/main and not
                         already used by a tag; READMEs must state the same version as the code
    6. Key bindings      the plugins' default hotkeys must not overlap
    7. Harmony targets   two plugins patching the same game method are flagged
#>
[CmdletBinding()]
param([string]$Remote = "origin", [string]$Branch = "main", [switch]$NoFetch)

$ErrorActionPreference = "Continue"
$root = (git rev-parse --show-toplevel 2>$null)
if (-not $root) { Write-Host "Not inside a git repo." -ForegroundColor Red; exit 1 }
Set-Location $root

$fails = New-Object System.Collections.Generic.List[string]
$warns = New-Object System.Collections.Generic.List[string]
$infos = New-Object System.Collections.Generic.List[string]
function Fail($m) { $fails.Add($m) }
function Warn($m) { $warns.Add($m) }
function Info($m) { $infos.Add($m) }
function Short($list, [int]$n = 8) { $a = @($list); if ($a.Count -le $n) { return ($a -join ", ") }; return (($a[0..($n - 1)] -join ", ") + " ... (+" + ($a.Count - $n) + " more)") }
function GitOut { $out = & git.exe @args 2>$null; if ($LASTEXITCODE -ne 0) { return ,@() }; return ,@($out) }

# ---------------------------------------------------------------- 1. remote state
if (-not $NoFetch) { & git fetch $Remote --tags --quiet 2>$null }
$up = "$Remote/$Branch"
$hasUp = (GitOut rev-parse --verify --quiet $up).Count -gt 0
$changed = @()
if ($hasUp) {
    $behind = [int](GitOut rev-list --count "HEAD..$up")[0]
    $ahead  = [int](GitOut rev-list --count "$up..HEAD")[0]
    Info "branch: $ahead commit(s) ahead, $behind behind $up"
    $base = (GitOut merge-base HEAD $up)[0]
    $mine = @(GitOut diff --name-only "$base" HEAD) + @(GitOut diff --name-only) + @(GitOut diff --name-only --cached) | Sort-Object -Unique
    $theirs = @(GitOut diff --name-only "$base" $up) | Sort-Object -Unique
    $changed = $mine
    if ($behind -gt 0) {
        $authors = (GitOut log --format="%an" "HEAD..$up" | Sort-Object -Unique) -join ", "
        Fail "you are $behind commit(s) behind $up (by $authors). Run: git pull --rebase   then re-run this check."
        $overlap = $mine | Where-Object { $theirs -contains $_ }
        if ($overlap) { Fail "both sides changed: $($overlap -join ', ') - expect a merge conflict; coordinate before pushing." }
    }
} else {
    Warn "no $up found (first push?)"
    $changed = @(GitOut diff --name-only) + @(GitOut diff --name-only --cached) | Sort-Object -Unique
}

# ---------------------------------------------------------------- 2. working tree
$dirty = GitOut status --porcelain
if ($dirty) {
    $untracked = $dirty | Where-Object { $_ -like '`?`?*' } | ForEach-Object { $_.Substring(3) }
    $modified  = $dirty | Where-Object { $_ -notlike '`?`?*' } | ForEach-Object { $_.Substring(3) }
    if ($modified)  { Warn "uncommitted changes, not part of the push until committed ($(@($modified).Count)): $(Short $modified)" }
    if ($untracked) { Warn "untracked files, not part of the push ($(@($untracked).Count)): $(Short $untracked)" }
}
$textExt = '\.(cs|csproj|props|md|txt|cfg|json|xml|py|ps1|gitignore)$'
foreach ($f in (GitOut ls-files)) {
    if ($f -notmatch $textExt -or -not (Test-Path $f)) { continue }
    if (Select-String -Path $f -Pattern '^(<{7}|>{7})( |$)' -Quiet) { Fail "merge-conflict markers in $f" }
}

# ---------------------------------------------------------------- 3. source only
$blocked = '(^|/)(BepInEx|dotnet|bin|obj|VehicleAssets|dump|release|backup)/|\.(dll|exe|pdb|zip|7z|rar|unitypackage|blend1)$|(^|/)winhttp\.dll$|(^|/)doorstop_config\.ini$|(^|/)local\.props$'
foreach ($f in (GitOut ls-files)) {
    if ($f -match $blocked) { Fail "not allowed in this source-only repo: $f" }
    elseif ((Test-Path $f) -and (Get-Item $f).Length -gt 5MB) { Warn ("large file ({0:N1} MB): {1}" -f ((Get-Item $f).Length / 1MB), $f) }
}

# ---------------------------------------------------------------- 4. plugin identity
function Read-Plugin([string]$dir, [string]$rev) {
    $p = "$dir/Plugin.cs"
    $text = if ($rev) { (GitOut show "${rev}:$p") -join "`n" } elseif (Test-Path $p) { Get-Content $p -Raw } else { "" }
    if (-not $text) { return $null }
    $consts = @{}
    foreach ($m in [regex]::Matches($text, 'const\s+string\s+(\w+)\s*=\s*"([^"]*)"')) { $consts[$m.Groups[1].Value] = $m.Groups[2].Value }
    $attr = [regex]::Match($text, '\[BepInPlugin\(\s*([^,]+?)\s*,\s*([^,]+?)\s*,\s*([^\)]+?)\s*\)\]')
    if (-not $attr.Success) { return $null }
    $val = { param($tok) $t = $tok.Trim(); if ($t.StartsWith('"')) { $t.Trim('"') } elseif ($consts.ContainsKey($t)) { $consts[$t] } else { $t } }
    $csproj = Get-ChildItem $dir -Filter *.csproj -ErrorAction SilentlyContinue | Select-Object -First 1
    $asm = if ($csproj) { ([regex]::Match((Get-Content $csproj.FullName -Raw), '<AssemblyName>([^<]+)</AssemblyName>')).Groups[1].Value } else { "" }
    [pscustomobject]@{ Dir = $dir; Guid = (& $val $attr.Groups[1].Value); Name = (& $val $attr.Groups[2].Value); Version = (& $val $attr.Groups[3].Value); Assembly = $asm }
}
$plugins = @()
foreach ($d in (Get-ChildItem source -Directory -ErrorAction SilentlyContinue)) {
    $p = Read-Plugin ("source/" + $d.Name) $null
    if ($p) { $plugins += $p }
}
foreach ($p in $plugins) { Info "plugin $($p.Name) $($p.Version)  guid=$($p.Guid)  dll=$($p.Assembly).dll  ($($p.Dir))" }
foreach ($field in 'Guid', 'Name', 'Assembly') {
    $plugins | Group-Object $field | Where-Object { $_.Count -gt 1 -and $_.Name } | ForEach-Object {
        Fail "duplicate plugin $($field): '$($_.Name)' used by $(($_.Group | ForEach-Object Dir) -join ' and ')"
    }
}

# ---------------------------------------------------------------- 5. versions
function Parse-Ver([string]$v) { $n = [regex]::Matches($v, '\d+') | ForEach-Object { [int]$_.Value }; return @($n + @(0, 0, 0, 0))[0..3] }
function Compare-Ver([string]$a, [string]$b) { $x = Parse-Ver $a; $y = Parse-Ver $b; for ($i = 0; $i -lt 4; $i++) { if ($x[$i] -ne $y[$i]) { return [Math]::Sign($x[$i] - $y[$i]) } }; return 0 }
$tags = GitOut tag --list
foreach ($p in $plugins) {
    $codeChanged = $changed | Where-Object { $_ -like "$($p.Dir)/*" -and $_ -notmatch '\.md$' }
    $old = if ($hasUp) { Read-Plugin $p.Dir $up } else { $null }
    if ($old) {
        $cmp = Compare-Ver $p.Version $old.Version
        if ($cmp -lt 0) { Fail "$($p.Name): version $($p.Version) is LOWER than $($old.Version) on $up - someone else bumped it; pull and bump past it." }
        elseif ($codeChanged -and $cmp -eq 0) { Fail "$($p.Name): code changed ($(@($codeChanged).Count) file(s)) but version is still $($p.Version) - bump the version in $($p.Dir)/Plugin.cs." }
        elseif ($cmp -gt 0) { Info "$($p.Name): $($old.Version) -> $($p.Version)" }
    }
    $tagHits = $tags | Where-Object { $_ -match ("(?i)^(" + [regex]::Escape($p.Name) + "[-_]?)?v?" + [regex]::Escape($p.Version) + "$") }
    if ($tagHits -and $codeChanged) { Fail "$($p.Name) $($p.Version): a tag with this version already exists ($($tagHits -join ', ')) - pick a new version." }
    foreach ($readme in @("$($p.Dir)/README.md", "README.md")) {
        if (-not (Test-Path $readme)) { continue }
        $t = Get-Content $readme -Raw
        $pattern = if ($readme -eq "README.md") { [regex]::Escape($p.Name) + '[^\n]{0,80}?v?(\d+\.\d+(\.\d+)?)' } else { 'Current version:\s*\**v?(\d+\.\d+(\.\d+)?)' }
        $m = [regex]::Match($t, $pattern)
        if ($m.Success -and $m.Groups[1].Value -ne $p.Version) { Warn "$readme says $($p.Name) $($m.Groups[1].Value) but the code is $($p.Version)." }
    }
}

# ---------------------------------------------------------------- 6. key bindings
$keys = @{}
foreach ($p in $plugins) {
    $src = (Get-ChildItem $p.Dir -Filter *.cs -Recurse | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
    $found = @()
    $found += [regex]::Matches($src, '\b(f\d{1,2})Key\b') | ForEach-Object { $_.Groups[1].Value.ToUpper() }
    $found += [regex]::Matches($src, 'Key\.(F\d{1,2})\b') | ForEach-Object { $_.Groups[1].Value.ToUpper() }
    $found += [regex]::Matches($src, 'Bind\([^;]*?"(F\d{1,2})"') | ForEach-Object { $_.Groups[1].Value.ToUpper() }
    $keys[$p.Name] = $found | Sort-Object -Unique
    if ($keys[$p.Name]) { Info "$($p.Name) hotkeys: $($keys[$p.Name] -join ', ')" }
}
$names = @($keys.Keys)
for ($i = 0; $i -lt $names.Count; $i++) { for ($j = $i + 1; $j -lt $names.Count; $j++) {
    $both = $keys[$names[$i]] | Where-Object { $keys[$names[$j]] -contains $_ }
    if ($both) { Fail "hotkey clash between $($names[$i]) and $($names[$j]): $($both -join ', ')" }
} }

# ---------------------------------------------------------------- 7. Harmony targets
$targets = @{}
foreach ($p in $plugins) {
    $src = (Get-ChildItem $p.Dir -Filter *.cs -Recurse | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
    $t = [regex]::Matches($src, 'HarmonyPatch\(\s*typeof\((\w+)\)\s*,\s*(?:nameof\(\w+\.(\w+)\)|"(\w+)")') |
         ForEach-Object { "$($_.Groups[1].Value).$($_.Groups[2].Value)$($_.Groups[3].Value)" } | Sort-Object -Unique
    $targets[$p.Name] = $t
}
$names = @($targets.Keys)
for ($i = 0; $i -lt $names.Count; $i++) { for ($j = $i + 1; $j -lt $names.Count; $j++) {
    $both = $targets[$names[$i]] | Where-Object { $targets[$names[$j]] -contains $_ }
    if ($both) { Warn "$($names[$i]) and $($names[$j]) both patch: $($both -join ', ') - make sure the patches cooperate." }
} }

# ---------------------------------------------------------------- report
Write-Host ""
Write-Host "push-check  ($root)" -ForegroundColor Cyan
foreach ($m in $infos) { Write-Host "  info  $m" -ForegroundColor DarkGray }
foreach ($m in $warns) { Write-Host "  WARN  $m" -ForegroundColor Yellow }
foreach ($m in $fails) { Write-Host "  FAIL  $m" -ForegroundColor Red }
if ($fails.Count -eq 0) { Write-Host "RESULT: OK to push ($($warns.Count) warning(s))" -ForegroundColor Green; exit 0 }
Write-Host "RESULT: fix $($fails.Count) problem(s) before pushing" -ForegroundColor Red
exit 1
