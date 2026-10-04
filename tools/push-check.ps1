<#
.SYNOPSIS
  Pre-push checks for the Driving Rogue mods repo (DriverCam + CurbFeel + any future plugin in source/).
  Run from anywhere inside the repo:  powershell -ExecutionPolicy Bypass -File tools/push-check.ps1
  Exit code 0 = OK to push (warnings may be listed), 1 = something must be fixed first.
  Tests: tools/push-check.tests.ps1 (builds throwaway repos and plays out every version scenario).

  Checks
    1. Remote state      fetch; behind / diverged from origin/main? files both people changed?
    2. Working tree      uncommitted / untracked files; merge-conflict markers in tracked text files
    3. Source only       no binaries, BepInEx/dotnet folders, build output, game assets or big files
    4. Plugin identity   unique GUID / name / DLL per plugin; GUID, name or DLL changed since origin/main
    5. Versions          three-way (merge-base / origin / local) with full SemVer ordering:
                         same-version collisions, lower than origin, code changed without a bump, diverged bumps
                         (with a predicted outcome and the next free version), version already tagged or released
                         with different code, malformed versions, and one version everywhere it is written
                         ([BepInPlugin], Version const, .csproj, plugin README, root README, log strings).
                         A change to a linked shared file (source/Shared/*.cs) counts for every plugin that links it.
    6. Key bindings      the plugins' default hotkeys must not overlap
    7. Harmony targets   two plugins patching the same game method are flagged (attributes, AccessTools.Method(typeof
                         (T), ...) and '// harmony-target: T.M (cooperates with X)' comments; hand patches without
                         either are flagged so they get a comment)
    8. Ownership         a local commit that changes another developer's plugin (CLAUDE.md table) must say it was approved
#>
[CmdletBinding()]
param([string]$Remote = "origin", [string]$Branch = "main", [switch]$NoFetch, [switch]$NoGitHub, [switch]$AllowIdentityChange)

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
function GitOut { $out = & git.exe @args 2>$null; if ($LASTEXITCODE -ne 0) { return }; $out }

# ================================================================ SemVer
# Accepts 1, 1.2, 1.2.3, 1.2.3.4, optional leading v, optional -prerelease and +build. Ordering per semver.org.
function Parse-SemVer([string]$v) {
    $s = "$v".Trim()
    $m = [regex]::Match($s, '^[vV]?(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z][0-9A-Za-z.-]*))?(?:\+[0-9A-Za-z.-]+)?$')
    if (-not $m.Success) { return [pscustomobject]@{ Raw = $s; Valid = $false; Nums = @(0, 0, 0, 0); Pre = ""; Parts = 0 } }
    $parts = 1; foreach ($g in 2..4) { if ($m.Groups[$g].Success) { $parts++ } }
    $nums = @(1..4 | ForEach-Object { if ($m.Groups[$_].Success) { [int]$m.Groups[$_].Value } else { 0 } })
    [pscustomobject]@{ Raw = $s; Valid = $true; Nums = $nums; Pre = $m.Groups[5].Value; Parts = $parts }
}
function Compare-SemVer([string]$a, [string]$b) {
    $x = Parse-SemVer $a; $y = Parse-SemVer $b
    for ($i = 0; $i -lt 4; $i++) { if ($x.Nums[$i] -ne $y.Nums[$i]) { return [Math]::Sign($x.Nums[$i] - $y.Nums[$i]) } }
    if ($x.Pre -eq $y.Pre) { return 0 }
    if (-not $x.Pre) { return 1 }          # release > prerelease
    if (-not $y.Pre) { return -1 }
    $xa = $x.Pre.Split('.'); $ya = $y.Pre.Split('.')
    for ($i = 0; $i -lt [Math]::Min($xa.Count, $ya.Count); $i++) {
        $xn = $xa[$i] -match '^\d+$'; $yn = $ya[$i] -match '^\d+$'
        if ($xn -and $yn) { $d = [Math]::Sign([long]$xa[$i] - [long]$ya[$i]); if ($d) { return $d } }
        elseif ($xn) { return -1 } elseif ($yn) { return 1 }
        else { $d = [Math]::Sign([string]::CompareOrdinal($xa[$i], $ya[$i])); if ($d) { return $d } }
    }
    return [Math]::Sign($xa.Count - $ya.Count)
}
function Same-Version([string]$a, [string]$b) { return (Compare-SemVer $a $b) -eq 0 }
function Next-Patch([string[]]$vs) {
    $max = $null
    foreach ($v in $vs) { if ($v -and (Parse-SemVer $v).Valid -and (-not $max -or (Compare-SemVer $v $max) -gt 0)) { $max = $v } }
    if (-not $max) { return "0.0.1" }
    $p = Parse-SemVer $max
    if ($p.Pre) { return "{0}.{1}.{2}" -f $p.Nums[0], $p.Nums[1], $p.Nums[2] }     # 0.4.0-beta -> 0.4.0
    return "{0}.{1}.{2}" -f $p.Nums[0], $p.Nums[1], ($p.Nums[2] + 1)
}

# ================================================================ plugin model
# rev = $null reads the working tree; otherwise reads files from that commit.
function Read-Text([string]$path, [string]$rev) {
    if ($rev) { return (@(GitOut show "${rev}:$path") -join "`n") }
    if (Test-Path $path) { return (Get-Content $path -Raw) }
    return ""
}
function List-Files([string]$dir, [string]$rev) {
    if ($rev) { return @(GitOut ls-tree -r --name-only $rev "$dir/") }
    if (-not (Test-Path $dir)) { return @() }
    return @(Get-ChildItem $dir -Recurse -File | ForEach-Object { $_.FullName.Substring($root.Length + 1).Replace('\', '/') } | Where-Object { $_ -notmatch '/(bin|obj)/' })
}
function Read-Plugin([string]$dir, [string]$rev) {
    $text = Read-Text "$dir/Plugin.cs" $rev
    if (-not $text) { return $null }
    $consts = @{}
    foreach ($m in [regex]::Matches($text, 'const\s+string\s+(\w+)\s*=\s*"([^"]*)"')) { $consts[$m.Groups[1].Value] = $m.Groups[2].Value }
    $attr = [regex]::Match($text, '\[BepInPlugin\(\s*([^,]+?)\s*,\s*([^,]+?)\s*,\s*([^\)]+?)\s*\)\]')
    if (-not $attr.Success) { return $null }
    $val = { param($tok) $t = $tok.Trim(); if ($t.StartsWith('"')) { $t.Trim('"') } elseif ($consts.ContainsKey($t)) { $consts[$t] } else { $t } }
    $versionTok = $attr.Groups[3].Value.Trim()
    $csprojPath = (List-Files $dir $rev | Where-Object { $_ -like '*.csproj' } | Select-Object -First 1)
    $csproj = if ($csprojPath) { Read-Text $csprojPath $rev } else { "" }
    $asm = ([regex]::Match($csproj, '<AssemblyName>([^<]+)</AssemblyName>')).Groups[1].Value
    if (-not $asm) { $asm = Split-Path $dir -Leaf }
    $projVersions = @{}
    foreach ($tag in 'Version', 'AssemblyVersion', 'FileVersion', 'InformationalVersion') {
        $m = [regex]::Match($csproj, "<$tag>([^<\$]+)</$tag>"); if ($m.Success) { $projVersions[$tag] = $m.Groups[1].Value.Trim() }
    }
    [pscustomobject]@{
        Dir = $dir; Guid = (& $val $attr.Groups[1].Value); Name = (& $val $attr.Groups[2].Value); Version = (& $val $versionTok)
        VersionConst = $consts['Version']; VersionIsLiteral = $versionTok.StartsWith('"'); Assembly = $asm; ProjVersions = $projVersions
    }
}
function Get-Plugins([string]$rev) {
    $dirs = if ($rev) { @(GitOut ls-tree -d --name-only $rev "source/") } else { @(Get-ChildItem source -Directory -ErrorAction SilentlyContinue | ForEach-Object { "source/" + $_.Name }) }
    $list = @()
    foreach ($d in $dirs) { $p = Read-Plugin $d $rev; if ($p) { $list += $p } }
    return ,$list
}
# files that count as a code/content change needing a version bump: everything in a plugin folder except docs.
# (SavedSettings counts: since DriverCam 0.9.2 the tuned car setups ship with the mod.)
function Is-CodeChange([string]$path) { return ($path -notmatch '\.md$' -and $path -notmatch '/(bin|obj)/') }

# Code files under $dir that really changed between $fromRev and $toRev ($null = working tree).
# Edits that only touch version lines (Version const, [BepInPlugin], .csproj version tags) don't count.
$versionLine = 'const\s+string\s+Version\s*=|\[BepInPlugin\(|<(Assembly|File|Informational)?Version>'
# Files outside the plugin folder that it compiles in (<Compile Include="..\Shared\Perf.cs" .../>), as repo paths.
function Linked-Files([string]$dir, [string]$rev) {
    $csprojPath = (List-Files $dir $rev | Where-Object { $_ -like '*.csproj' } | Select-Object -First 1)
    if (-not $csprojPath) { return @() }
    $xml = Read-Text $csprojPath $rev
    $out = @()
    foreach ($m in [regex]::Matches($xml, '<Compile\s+Include="\.\.[\\/]([^"]+)"')) { $out += "source/" + $m.Groups[1].Value.Replace('\', '/') }
    return $out
}

function Changed-Code([string]$dir, [string]$fromRev, [string]$toRev) {
    # a change to a linked shared file (source/Shared/*.cs) is a code change of every plugin that compiles it in
    $paths = @("$dir/") + @(@(Linked-Files $dir $toRev) + @(Linked-Files $dir $fromRev) | Sort-Object -Unique)
    $files = if ($toRev) { @(GitOut diff --name-only $fromRev $toRev -- @paths) }
             else { @(@(GitOut diff --name-only $fromRev -- @paths) + @(GitOut ls-files --others --exclude-standard -- @paths)) }
    $result = @()
    foreach ($f in ($files | Where-Object { $_ } | Sort-Object -Unique)) {
        if (-not (Is-CodeChange $f)) { continue }
        if ($f -match '(Plugin\.cs|\.csproj)$') {
            $old = @((Read-Text $f $fromRev) -split "`r?`n" | Where-Object { $_ -notmatch $versionLine })
            $new = @((Read-Text $f $toRev) -split "`r?`n" | Where-Object { $_ -notmatch $versionLine })
            if (($old -join "`n").Trim() -eq ($new -join "`n").Trim()) { continue }   # only the version moved
        }
        $result += $f
    }
    return $result
}

# ================================================================ 1. remote state
if (-not $NoFetch) { & git.exe fetch $Remote --tags --quiet 2>$null }
$up = "$Remote/$Branch"
$hasUp = @(GitOut rev-parse --verify --quiet $up).Count -gt 0
$behind = 0; $ahead = 0; $base = $null
$mineFiles = @(); $theirFiles = @()
$upAuthors = ""
if ($hasUp) {
    $behind = [int]@(GitOut rev-list --count "HEAD..$up")[0]
    $ahead = [int]@(GitOut rev-list --count "$up..HEAD")[0]
    $base = @(GitOut merge-base HEAD $up)[0]
    Info "branch: $ahead commit(s) ahead, $behind behind $up"
    $mineFiles = @(@(GitOut diff --name-only "$base" HEAD) + @(GitOut diff --name-only) + @(GitOut diff --name-only --cached) | Where-Object { $_ } | Sort-Object -Unique)
    $theirFiles = @(GitOut diff --name-only "$base" $up | Sort-Object -Unique)
    if ($behind -gt 0) {
        $upAuthors = @(GitOut log --format="%an" "HEAD..$up" | Sort-Object -Unique) -join ", "
        if ($ahead -gt 0) { Fail "your branch and $up have DIVERGED ($ahead local / $behind remote commits by $upAuthors). Run: git pull --rebase, resolve, re-run this check." }
        else { Fail "you are $behind commit(s) behind $up (by $upAuthors). Run: git pull --rebase, then re-run this check." }
        $overlap = $mineFiles | Where-Object { $theirFiles -contains $_ }
        if ($overlap) { Fail "both of you changed: $(Short $overlap) - expect merge conflicts there; talk before overwriting each other." }
    }
} else {
    Warn "no $up found (first push?) - version history checks are limited to tags"
    $mineFiles = @(@(GitOut diff --name-only) + @(GitOut diff --name-only --cached) | Where-Object { $_ } | Sort-Object -Unique)
}

# ================================================================ 2. working tree
$dirty = GitOut status --porcelain
if ($dirty) {
    $untracked = $dirty | Where-Object { $_ -like '`?`?*' } | ForEach-Object { $_.Substring(3) }
    $modified = $dirty | Where-Object { $_ -notlike '`?`?*' } | ForEach-Object { $_.Substring(3) }
    if ($modified) { Warn "uncommitted changes, not part of the push until committed ($(@($modified).Count)): $(Short $modified)" }
    if ($untracked) { Warn "untracked files, not part of the push ($(@($untracked).Count)): $(Short $untracked)" }
}
$textExt = '\.(cs|csproj|props|md|txt|cfg|json|xml|py|ps1|gitignore)$'
foreach ($f in @(GitOut ls-files)) {
    if ($f -notmatch $textExt -or -not (Test-Path $f)) { continue }
    if (Select-String -Path $f -Pattern '^(<{7}|>{7})( |$)' -Quiet) { Fail "merge-conflict markers in $f" }
}

# ================================================================ 3. source only
$blocked = '(^|/)(BepInEx|dotnet|bin|obj|VehicleAssets|dump|release|backup)/|\.(dll|exe|pdb|zip|7z|rar|unitypackage|blend1)$|(^|/)winhttp\.dll$|(^|/)doorstop_config\.ini$|(^|/)local\.props$'
foreach ($f in @(GitOut ls-files)) {
    if ($f -match $blocked) { Fail "not allowed in this source-only repo: $f" }
    elseif ((Test-Path $f) -and (Get-Item $f).Length -gt 5MB) { Warn ("large file ({0:N1} MB): {1}" -f ((Get-Item $f).Length / 1MB), $f) }
}

# ================================================================ 4. plugin identity
$plugins = Get-Plugins $null
$basePlugins = if ($base) { Get-Plugins $base } else { @() }
$upPlugins = if ($hasUp) { Get-Plugins $up } else { @() }
foreach ($p in $plugins) { Info "plugin $($p.Name) $($p.Version)  guid=$($p.Guid)  dll=$($p.Assembly).dll  ($($p.Dir))" }
foreach ($field in 'Guid', 'Name', 'Assembly') {
    $plugins | Group-Object $field | Where-Object { $_.Count -gt 1 -and $_.Name } | ForEach-Object {
        Fail "duplicate plugin $($field): '$($_.Name)' used by $(($_.Group | ForEach-Object Dir) -join ' and ')"
    }
}
foreach ($p in $plugins) {
    $o = $upPlugins | Where-Object Dir -eq $p.Dir | Select-Object -First 1
    if (-not $o) { continue }
    if ($o.Guid -ne $p.Guid) {
        $m = "$($p.Dir): plugin GUID changed '$($o.Guid)' -> '$($p.Guid)'. Every user's config file is named after the GUID, so their settings reset, and an old DLL left in BepInEx/plugins loads as a second plugin."
        if ($AllowIdentityChange) { Warn $m } else { Fail "$m (re-run with -AllowIdentityChange if this is intended)" }
    }
    if ($o.Name -ne $p.Name) { Warn "$($p.Dir): plugin name changed '$($o.Name)' -> '$($p.Name)' (log lines, README tables and tags use the name)." }
    if ($o.Assembly -ne $p.Assembly) { Warn "$($p.Dir): DLL name changed '$($o.Assembly).dll' -> '$($p.Assembly).dll'. The old DLL stays in everyone's BepInEx/plugins and loads twice unless they delete it; say so in the commit message." }
}

# ================================================================ 5. versions
# 5a. every version must parse; two-part versions are allowed by the check but flagged (keep MAJOR.MINOR.PATCH)
foreach ($p in $plugins) {
    $sv = Parse-SemVer $p.Version
    if (-not $sv.Valid) { Fail "$($p.Name): version '$($p.Version)' is not a valid version (use MAJOR.MINOR.PATCH, e.g. 1.2.3 or 1.2.3-beta.1)" }
    elseif ($sv.Parts -lt 3) { Warn "$($p.Name): version '$($p.Version)' has only $($sv.Parts) part(s); use MAJOR.MINOR.PATCH so ordering is unambiguous" }
    elseif ($sv.Parts -gt 3) { Warn "$($p.Name): version '$($p.Version)' has 4 parts; BepInEx/SemVer expect MAJOR.MINOR.PATCH" }
}

# 5b. one version everywhere it is written
foreach ($p in $plugins) {
    $places = [ordered]@{ "[BepInPlugin]" = $p.Version }
    if ($p.VersionConst -and $p.VersionIsLiteral) { $places["const Version"] = $p.VersionConst }
    foreach ($k in $p.ProjVersions.Keys) { $places[".csproj <$k>"] = $p.ProjVersions[$k] }
    $readme = "$($p.Dir)/README.md"
    if (Test-Path $readme) {
        $m = [regex]::Match((Get-Content $readme -Raw), '(?i)current version:\s*\**\s*(v?\d+(?:\.\d+){0,3}(?:-[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z.]+)?)')
        if ($m.Success) { $places["$readme"] = $m.Groups[1].Value }
    }
    if (Test-Path "README.md") {
        $t = Get-Content "README.md" -Raw
        $m = [regex]::Match($t, '(?m)^\|\s*\**' + [regex]::Escape($p.Name) + '\**\s*\|\s*v?([0-9][0-9A-Za-z.+-]*)\s*\|')
        if (-not $m.Success) { $m = [regex]::Match($t, '(?m)\b' + [regex]::Escape($p.Name) + '\b[^\n|]{0,40}?\(v?(\d+\.\d+(?:\.\d+)?)\)') }
        if ($m.Success) { $places["README.md"] = $m.Groups[1].Value }
    }
    $bad = $places.Keys | Where-Object { -not (Same-Version $places[$_] $p.Version) }
    if ($bad) { Fail "$($p.Name): version is written differently in different places: $(($places.Keys | ForEach-Object { "$_=$($places[$_])" }) -join ', ')" }

    # hard-coded "<Name> <version>" strings in code that don't match (e.g. a log line left at an old version)
    foreach ($f in (List-Files $p.Dir $null | Where-Object { $_ -like '*.cs' })) {
        $n = 0
        foreach ($line in (Get-Content $f)) {
            $n++
            foreach ($mm in [regex]::Matches($line, '"[^"]*\b' + [regex]::Escape($p.Name) + '\s+v?(\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.-]+)?)\b[^"]*"')) {
                if (-not (Same-Version $mm.Groups[1].Value $p.Version)) { Warn "$($p.Name): $($f):$n has a hard-coded '$($p.Name) $($mm.Groups[1].Value)' but the plugin is $($p.Version) - use the Version constant" }
            }
        }
    }
}

# 5c. three-way comparison against what the other person pushed
foreach ($p in $plugins) {
    $L = $p.Version
    $bp = $basePlugins | Where-Object Dir -eq $p.Dir | Select-Object -First 1
    $rp = $upPlugins | Where-Object Dir -eq $p.Dir | Select-Object -First 1
    $B = if ($bp) { $bp.Version } else { $null }
    $R = if ($rp) { $rp.Version } else { $null }
    $localCode = if ($base) { @(Changed-Code $p.Dir $base $null) } else { @() }
    $remoteCode = if ($base -and $hasUp) { @(Changed-Code $p.Dir $base $up) } else { @() }
    $headP = Read-Plugin $p.Dir "HEAD"
    if ($headP -and -not (Same-Version $headP.Version $L)) { Warn "$($p.Name): version changed to $L in the working tree but not committed (HEAD has $($headP.Version))" }

    if (-not $R) {
        if ($hasUp) { Info "$($p.Name): new plugin (not on $up yet)" }
        continue
    }
    $suggest = Next-Patch @($L, $R, $B)
    $who = if ($upAuthors) { $upAuthors } else { "the other developer" }

    if ($behind -gt 0) {
        # predict what happens after `git pull --rebase`
        $localBumped = $B -and -not (Same-Version $L $B)
        $remoteBumped = $B -and -not (Same-Version $R $B)
        if ($localBumped -and $remoteBumped -and (Same-Version $L $R)) {
            Fail "$($p.Name): VERSION COLLISION - you and $who both chose $L for different changes. After pulling, set $($p.Name) to $suggest."
        } elseif ($localBumped -and $remoteBumped -and (Compare-SemVer $L $R) -lt 0) {
            Fail "$($p.Name): $who already pushed $R, higher than your $L. After pulling, set $($p.Name) to $suggest."
        } elseif ($localBumped -and $remoteBumped) {
            Info "$($p.Name): both bumped ($B -> you $L, $who $R). Yours is higher; the version line in $($p.Dir)/Plugin.cs will conflict on rebase - keep $L."
        } elseif ($localCode -and $remoteBumped -and -not $localBumped) {
            Fail "$($p.Name): $who released $R while you changed $($p.Name) code without a bump. After pulling, set $($p.Name) to $suggest."
        } elseif ($localCode -and $remoteCode -and -not $localBumped -and -not $remoteBumped) {
            Fail "$($p.Name): you both changed $($p.Name) code and neither bumped $B. After pulling, set $($p.Name) to $suggest."
        }
        continue
    }

    # up-to-date with origin (fast-forward push)
    $cmp = Compare-SemVer $L $R
    if ($cmp -lt 0) {
        Fail "$($p.Name): your version $L is LOWER than $R on $up. Set it to $suggest."
    } elseif ($cmp -eq 0 -and $localCode.Count -gt 0) {
        Fail "$($p.Name): code changed ($(Short $localCode 4)) but the version is still $L, which is already on $up. Set it to $suggest."
    } elseif ($cmp -gt 0 -and $localCode.Count -eq 0) {
        Info "$($p.Name): version bumped $R -> $L without code changes (fine for a release-only bump)"
    } elseif ($cmp -gt 0) {
        Info "$($p.Name): $R -> $L"
    }
}

# 5d. version already tagged / released with different code
$tagList = @(GitOut tag --list)
$releaseTags = @()
$remoteUrl = @(GitOut remote get-url $Remote)[0]
if (-not $NoGitHub -and $remoteUrl -match 'github\.com[:/](.+?)(\.git)?$' -and (Get-Command gh -ErrorAction SilentlyContinue)) {
    $repoSlug = $Matches[1]
    $json = & gh release list --repo $repoSlug --limit 100 --json tagName 2>$null
    if ($LASTEXITCODE -eq 0 -and $json) { $releaseTags = @(($json | ConvertFrom-Json) | ForEach-Object tagName) }
}
foreach ($p in $plugins) {
    $nameRx = [regex]::Escape($p.Name)
    $hits = @(($tagList + $releaseTags) | Sort-Object -Unique | Where-Object {
        $m = [regex]::Match($_, "(?i)^(?:$nameRx[-_ ]?)?v?(\d[0-9A-Za-z.+-]*)$")
        $m.Success -and (Parse-SemVer $m.Groups[1].Value).Valid -and (Same-Version $m.Groups[1].Value $p.Version) -and
        ($_ -match "(?i)^$nameRx" -or $_ -notmatch '[A-Za-z]{2,}')       # a bare v1.2.3 tag counts for every plugin
    })
    foreach ($t in $hits) {
        $tagTree = @(GitOut rev-parse "${t}^{commit}:$($p.Dir)")[0]
        $headTree = @(GitOut rev-parse "HEAD:$($p.Dir)")[0]
        $workDirty = @(GitOut status --porcelain -- $p.Dir | Where-Object { $_ -and (Is-CodeChange $_.Substring(3)) }).Count -gt 0
        $isRelease = $releaseTags -contains $t
        if (-not $tagTree) {
            # the tag predates this plugin folder; a generic v-tag only collides if it's this plugin's own release
            if ($t -match "(?i)^$nameRx") { Fail "$($p.Name) $($p.Version): '$t' is already used by an older layout of this repo - pick $(Next-Patch @($p.Version))." }
            continue
        }
        if ($tagTree -ne $headTree -or $workDirty) {
            Fail "$($p.Name) $($p.Version): $(if ($isRelease) { 'a GitHub release' } else { 'a tag' }) '$t' already exists with different $($p.Name) code - pick $(Next-Patch @($p.Version))."
        } else { Info "$($p.Name) $($p.Version): matches $(if ($isRelease) { 'release' } else { 'tag' }) '$t'" }
    }
}

# ================================================================ 6. key bindings
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

# ================================================================ 7. Harmony targets
# Three ways a target is found:
#   [HarmonyPatch(typeof(T), nameof(T.M))] / [HarmonyPatch(typeof(T), "M")]                attribute patches
#   AccessTools.Method(typeof(T), nameof(T.M)) / AccessTools.Method(typeof(T), "M")          patches installed by hand
#   // harmony-target: T.M, T.M2 (cooperates with OtherPlugin)                               anything else (a loop over names, ...)
# A plugin that calls harmony.Patch(...) with none of these can't be compared: WARN until it has a harmony-target comment.
# "(cooperates with X)" on a harmony-target line records that the overlap with plugin X was checked: info instead of WARN.
function Last-Seg([string]$s) { return ($s -split '\.')[-1] }
$targets = @{}
$acks = @{}
foreach ($p in $plugins) {
    $src = (Get-ChildItem $p.Dir -Filter *.cs -Recurse | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
    $found = @()
    foreach ($m in [regex]::Matches($src, 'HarmonyPatch\(\s*typeof\(([\w\.]+)\)\s*,\s*(?:nameof\(([\w\.]+)\)|"(\w+)")')) {
        $found += "$(Last-Seg $m.Groups[1].Value).$(Last-Seg ($m.Groups[2].Value + $m.Groups[3].Value))"
    }
    $manual = @()
    foreach ($m in [regex]::Matches($src, 'AccessTools\.(?:Method|DeclaredMethod)\(\s*typeof\(([\w\.]+)\)\s*,\s*(?:nameof\(([\w\.]+)\)|"(\w+)")')) {
        $manual += "$(Last-Seg $m.Groups[1].Value).$(Last-Seg ($m.Groups[2].Value + $m.Groups[3].Value))"
    }
    $acks[$p.Name] = @()
    foreach ($line in [regex]::Matches($src, '(?m)//\s*harmony-target:\s*(.+)$')) {
        $text = $line.Groups[1].Value
        $coop = [regex]::Match($text, '\(cooperates with ([^)]+)\)')   # applies to every target on the line
        $others = if ($coop.Success) { @($coop.Groups[1].Value -split '[,/]| and ' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) } else { @() }
        $list = if ($coop.Success) { $text.Substring(0, $coop.Index) } else { $text }
        foreach ($m in [regex]::Matches($list, '(\w+)\.(\w+)')) {
            $t = "$($m.Groups[1].Value).$($m.Groups[2].Value)"
            $manual += $t
            foreach ($o in $others) { $acks[$p.Name] += "$t|$o" }
        }
    }
    if ($src -match '\.Patch\(' -and $manual.Count -eq 0) {
        Warn "$($p.Name) installs Harmony patches by hand (harmony.Patch) in a way this check can't read; add a '// harmony-target: Type.Method' comment next to it so clashes with other plugins are caught."
    }
    $targets[$p.Name] = @($found + $manual | Sort-Object -Unique)
    if ($targets[$p.Name]) { Info "$($p.Name) patches: $(Short $targets[$p.Name] 6)" }
}
$names = @($targets.Keys)
for ($i = 0; $i -lt $names.Count; $i++) { for ($j = $i + 1; $j -lt $names.Count; $j++) {
    $a = $names[$i]; $b = $names[$j]
    foreach ($t in @($targets[$a] | Where-Object { $targets[$b] -contains $_ })) {
        if (($acks[$a] -contains "$t|$b") -or ($acks[$b] -contains "$t|$a")) { Info "$a and $b both patch $t (marked as cooperating)" }
        else { Warn "$a and $b both patch: $t - make sure the patches cooperate, then mark it: // harmony-target: $t (cooperates with $b)" }
    }
} }

# ================================================================ 8. ownership
# CLAUDE.md's developer table ("| **Name** | `source/X/` (...), ... | git author |") says who owns which plugin. A local
# commit that changes a plugin someone else owns must say in its message that it was approved ("approved by ...").
$me = @(GitOut config user.name)[0]
$owners = @{}
if (Test-Path "CLAUDE.md") {
    foreach ($row in [regex]::Matches((Get-Content "CLAUDE.md" -Raw), '(?m)^\|\s*\*\*([^*|]+)\*\*\s*\|([^|\r\n]*)\|\s*([^|\r\n]+?)\s*\|\s*$')) {
        $owner = [pscustomobject]@{ Who = $row.Groups[1].Value.Trim(); Author = $row.Groups[3].Value.Trim() }
        foreach ($d in [regex]::Matches($row.Groups[2].Value, '`(source/[^/`]+)/?`')) { $owners[$d.Groups[1].Value] = $owner }
    }
}
foreach ($dir in $owners.Keys) {
    $o = $owners[$dir]
    if ($me -and $o.Author -ieq $me) { continue }
    if ($base) {
        foreach ($c in @(GitOut log --format="%H|%an" "$base..HEAD" -- "$dir/")) {
            $hash, $author = $c -split '\|', 2
            if ($author -ieq $o.Author) { continue }   # the owner's own commit
            $subject = @(GitOut log -1 --format="%h %s" $hash)[0]
            $msg = @(GitOut log -1 --format=%B $hash) -join "`n"
            if ($msg -match '(?i)approv') { Info "$dir (owned by $($o.Who)) changed in $subject - approval is in the message" }
            else { Fail "commit $subject changes $dir, which $($o.Who) owns, and its message doesn't say the change was approved. Add e.g. 'Approved by <name>: <what>' to the message (git commit --amend if it is your last commit) so $($o.Who) sees it when pulling." }
        }
    }
    $uncommitted = @(@(GitOut diff --name-only -- "$dir/") + @(GitOut diff --name-only --cached -- "$dir/") | Where-Object { $_ })
    if ($uncommitted.Count -gt 0) { Warn "uncommitted changes in $dir, which $($o.Who) owns ($(Short $uncommitted 3)): commit them only with approval, and say so in the commit message ('approved by ...')." }
}

# ================================================================ report
Write-Host ""
Write-Host "push-check  ($root)" -ForegroundColor Cyan
foreach ($m in $infos) { Write-Host "  info  $m" -ForegroundColor DarkGray }
foreach ($m in $warns) { Write-Host "  WARN  $m" -ForegroundColor Yellow }
foreach ($m in $fails) { Write-Host "  FAIL  $m" -ForegroundColor Red }
if ($fails.Count -eq 0) { Write-Host "RESULT: OK to push ($($warns.Count) warning(s))" -ForegroundColor Green; exit 0 }
Write-Host "RESULT: fix $($fails.Count) problem(s) before pushing" -ForegroundColor Red
exit 1
