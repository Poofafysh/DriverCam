<#
.SYNOPSIS
  Bring your local Driving Rogue install up to date with this repo, and verify the transition.
    powershell -ExecutionPolicy Bypass -File tools/sync-install.ps1              # pull, back up, build+install, verify
    ... -DryRun                                                                  # only report what would change
    ... -CloseGame                                                               # close a running game first (asks nothing)
    ... -Launch                                                                  # afterwards start the game and check the BepInEx log
    ... -Rollback latest | <backup folder name>                                  # restore a backup
    ... -FromRev <commit>                                                        # compare config defaults from this commit instead of the pre-pull HEAD
  Exit code 0 = transition OK (warnings may be listed), 1 = something failed (see FAIL lines; a backup is kept).

  Steps
    1. Preflight     uncommitted local changes, incoming commits, game folder + BepInEx + interop, game running?
    2. Backup        plugin DLLs, plugin asset folders, plugin configs (incl. DriverCam_cars) -> backup/<timestamp>/
    3. Update        git pull --ff-only (never merges or rewrites), then dotnet build -c Release per plugin (deploys)
    4. Verify        installed DLLs + assets == build output; stale / duplicate plugin DLLs; config transition
                     (new keys, removed/renamed keys still in your .cfg, changed defaults you are still on)
    5. Launch check  (-Launch) BepInEx loads every plugin at the new version, no plugin errors
#>
[CmdletBinding()]
param(
    [switch]$DryRun, [switch]$CloseGame, [switch]$Launch, [switch]$Force,
    [string]$GameDir, [string]$FromRev, [string]$Rollback,
    [string]$Remote = "origin", [string]$Branch = "main"
)

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
function GitOut { $out = & git.exe @args 2>$null; if ($LASTEXITCODE -ne 0) { return ,@() }; return ,@($out) }
function Short($list, [int]$n = 8) { $a = @($list); if ($a.Count -le $n) { return ($a -join ", ") }; return (($a[0..($n - 1)] -join ", ") + " ... (+" + ($a.Count - $n) + " more)") }

function Report([string]$title) {
    Write-Host ""
    Write-Host "sync-install: $title" -ForegroundColor Cyan
    foreach ($m in $infos) { Write-Host "  info  $m" -ForegroundColor DarkGray }
    foreach ($m in $warns) { Write-Host "  WARN  $m" -ForegroundColor Yellow }
    foreach ($m in $fails) { Write-Host "  FAIL  $m" -ForegroundColor Red }
    if ($fails.Count -eq 0) { Write-Host "RESULT: OK ($($warns.Count) warning(s))" -ForegroundColor Green; exit 0 }
    Write-Host "RESULT: $($fails.Count) problem(s)" -ForegroundColor Red; exit 1
}

# ---------------------------------------------------------------- game folder
if (-not $GameDir) {
    $lp = Join-Path $root "source/local.props"
    if (Test-Path $lp) { $GameDir = ([regex]::Match((Get-Content $lp -Raw), '<GameDir>([^<]+)</GameDir>')).Groups[1].Value }
}
if (-not $GameDir) { $GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Driving Rogue" }
$bep = Join-Path $GameDir "BepInEx"
$pluginsDir = Join-Path $bep "plugins"
$configDir = Join-Path $bep "config"
$logFile = Join-Path $bep "LogOutput.log"
$backupRoot = Join-Path $root "backup"

# ---------------------------------------------------------------- plugin discovery
function Read-Plugin([string]$dir, [string]$rev) {
    $p = "$dir/Plugin.cs"
    $text = if ($rev) { (GitOut show "${rev}:$p") -join "`n" } elseif (Test-Path $p) { Get-Content $p -Raw } else { "" }
    if (-not $text) { return $null }
    $consts = @{}
    foreach ($m in [regex]::Matches($text, 'const\s+string\s+(\w+)\s*=\s*"([^"]*)"')) { $consts[$m.Groups[1].Value] = $m.Groups[2].Value }
    $attr = [regex]::Match($text, '\[BepInPlugin\(\s*([^,]+?)\s*,\s*([^,]+?)\s*,\s*([^\)]+?)\s*\)\]')
    if (-not $attr.Success) { return $null }
    $val = { param($tok) $t = $tok.Trim(); if ($t.StartsWith('"')) { $t.Trim('"') } elseif ($consts.ContainsKey($t)) { $consts[$t] } else { $t } }
    $csproj = if ($rev) { $null } else { Get-ChildItem $dir -Filter *.csproj -ErrorAction SilentlyContinue | Select-Object -First 1 }
    $asm = if ($csproj) { ([regex]::Match((Get-Content $csproj.FullName -Raw), '<AssemblyName>([^<]+)</AssemblyName>')).Groups[1].Value } else { (Split-Path $dir -Leaf) }
    [pscustomobject]@{ Dir = $dir; Guid = (& $val $attr.Groups[1].Value); Name = (& $val $attr.Groups[2].Value); Version = (& $val $attr.Groups[3].Value); Assembly = $asm }
}
function Get-Plugins {
    $list = @()
    foreach ($d in (Get-ChildItem (Join-Path $root "source") -Directory -ErrorAction SilentlyContinue)) {
        $p = Read-Plugin ("source/" + $d.Name) $null
        if ($p) { $list += $p }
    }
    return ,$list
}

# ---------------------------------------------------------------- config parsing
function Get-Binds([string]$dir, [string]$rev) {
    # section|key -> normalized default, from every Config.Bind / cfg.Bind call in the plugin's sources
    $binds = @{}
    $files = if ($rev) { GitOut ls-tree -r --name-only $rev "$dir/" | Where-Object { $_ -like '*.cs' } } else { Get-ChildItem $dir -Filter *.cs -Recurse | ForEach-Object { $_.FullName } }
    foreach ($f in $files) {
        $text = if ($rev) { (GitOut show "${rev}:$f") -join "`n" } else { Get-Content $f -Raw }
        # section may be a literal ("View") or a variable (DriverCam binds MirrorLeft/MirrorRight through `section`):
        # a variable section is stored as "*" and matches any section with that key
        foreach ($m in [regex]::Matches($text, '\.Bind\(\s*(?<s>"[^"]+"|[A-Za-z_]\w*)\s*,\s*"(?<k>[^"]+)"\s*,\s*(?<d>"[^"]*"|[^,\)]+)')) {
            $s = $m.Groups['s'].Value
            $section = if ($s.StartsWith('"')) { $s.Trim('"') } else { "*" }
            $binds["$section|$($m.Groups['k'].Value)"] = (Norm $m.Groups['d'].Value)
        }
    }
    return $binds
}
function Norm([string]$v) {
    $v = $v.Trim().Trim('"')
    if ($v -match '^-?\d+(\.\d+)?f?$') { return ([double]($v.TrimEnd('f'))).ToString([Globalization.CultureInfo]::InvariantCulture) }
    if ($v -match '^(true|false)$') { return $v.ToLowerInvariant() }
    return $v
}
function Read-Cfg([string]$path) {
    $vals = @{}; $section = ""
    if (-not (Test-Path $path)) { return $vals }
    foreach ($line in (Get-Content $path)) {
        if ($line -match '^\s*\[(.+)\]\s*$') { $section = $Matches[1]; continue }
        if ($line -match '^\s*([^#=][^=]*?)\s*=\s*(.*)$') { $vals["$section|$($Matches[1])"] = (Norm $Matches[2]) }
    }
    return $vals
}

# ---------------------------------------------------------------- game process
function Get-GameProcess {
    # only a game running from THIS install counts (a copy elsewhere doesn't lock these DLLs)
    $full = [IO.Path]::GetFullPath($GameDir).TrimEnd('\') + '\'
    Get-Process "Driving Rogue" -ErrorAction SilentlyContinue | Where-Object { -not $_.Path -or $_.Path.StartsWith($full, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
}
function Game-Running { return [bool](Get-GameProcess) }
function Close-Game {
    $p = Get-GameProcess
    if (-not $p) { return $true }
    [void]$p.CloseMainWindow()
    for ($i = 0; $i -lt 20 -and -not $p.HasExited; $i++) { Start-Sleep 1; $p.Refresh() }
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; Start-Sleep 2 }
    return -not (Game-Running)
}

# ---------------------------------------------------------------- backup / rollback
function Backup-Install($plugins) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $dest = Join-Path $backupRoot $stamp
    New-Item -ItemType Directory -Force (Join-Path $dest "plugins"), (Join-Path $dest "config") | Out-Null
    foreach ($p in $plugins) {
        $dll = Join-Path $pluginsDir "$($p.Assembly).dll"
        if (Test-Path $dll) { Copy-Item $dll (Join-Path $dest "plugins") }
        $assets = Join-Path $pluginsDir $p.Assembly
        if (Test-Path $assets) { Copy-Item $assets (Join-Path $dest "plugins") -Recurse }
        $cfg = Join-Path $configDir "$($p.Guid).cfg"
        if (Test-Path $cfg) { Copy-Item $cfg (Join-Path $dest "config") }
        $cars = Join-Path $configDir "$($p.Assembly)_cars"
        if (Test-Path $cars) { Copy-Item $cars (Join-Path $dest "config") -Recurse }
    }
    # keep the 10 newest backups
    Get-ChildItem $backupRoot -Directory | Sort-Object Name -Descending | Select-Object -Skip 10 | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    return $dest
}

if ($Rollback) {
    $src = if ($Rollback -eq "latest") { Get-ChildItem $backupRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1 } else { Get-Item (Join-Path $backupRoot $Rollback) -ErrorAction SilentlyContinue }
    if (-not $src) { Fail "no backup '$Rollback' in $backupRoot"; Report "rollback" }
    if (Game-Running) { if ($CloseGame) { if (-not (Close-Game)) { Fail "could not close the game"; Report "rollback" } } else { Fail "game is running - close it or pass -CloseGame"; Report "rollback" } }
    Copy-Item (Join-Path $src.FullName "plugins\*") $pluginsDir -Recurse -Force
    Copy-Item (Join-Path $src.FullName "config\*") $configDir -Recurse -Force
    Info "restored $($src.Name) into $bep"
    Report "rollback"
}

# ================================================================ 1. preflight
if (-not (Test-Path (Join-Path $bep "core\BepInEx.Unity.IL2CPP.dll"))) { Fail "BepInEx 6 IL2CPP not found in $GameDir (set source/local.props or -GameDir)"; Report "preflight" }
if (-not (Test-Path (Join-Path $bep "interop\Assembly-CSharp.dll"))) { Fail "BepInEx/interop missing - start the game once with BepInEx so it generates the interop assemblies"; Report "preflight" }
if (-not (Test-Path (Join-Path $GameDir "dotnet\System.Private.CoreLib.dll"))) { Fail "the game's dotnet folder (BepInEx's .NET 6 runtime) is missing - reinstall BepInEx 6 IL2CPP; plugins compile against it"; Report "preflight" }
$bepVer = (Get-Item (Join-Path $bep "core\BepInEx.Unity.IL2CPP.dll")).VersionInfo.ProductVersion
Info "game: $GameDir  (BepInEx $bepVer)"

$dirty = GitOut status --porcelain
$dirtyTracked = $dirty | Where-Object { $_ -notlike '`?`?*' }
if ($dirtyTracked -and -not $Force) { Fail "uncommitted changes in the repo: $(Short ($dirtyTracked | ForEach-Object { $_.Substring(3) })) - commit/stash them (or -Force)" }

& git.exe fetch $Remote --quiet 2>$null
$up = "$Remote/$Branch"
$preRev = (GitOut rev-parse HEAD)[0]
$behind = [int](GitOut rev-list --count "HEAD..$up")[0]
$ahead = [int](GitOut rev-list --count "$up..HEAD")[0]
if ($behind -gt 0) {
    Info "incoming ($behind): " + ((GitOut log --format="%h %an: %s" "HEAD..$up") -join " | ")
    if ($ahead -gt 0) { Fail "your branch and $up have diverged ($ahead local / $behind remote commits) - run git pull --rebase yourself, resolve, then re-run" }
} else { Info "repo already at $up ($($preRev.Substring(0,7)))" }

$running = Game-Running
if ($running -and -not $DryRun) {
    if ($CloseGame) { if (Close-Game) { Info "closed the running game" } else { Fail "could not close the game" } }
    else { Fail "the game is running and locks plugin DLLs - close it or pass -CloseGame" }
}
if ($fails.Count -gt 0) { Report "preflight" }

$targetRev = if ($DryRun) { $up } else { "HEAD" }
$compareFrom = if ($FromRev) { $FromRev } else { $preRev }

# ================================================================ 2-3. backup + update
if (-not $DryRun) {
    $pluginsBefore = Get-Plugins
    $bk = Backup-Install $pluginsBefore
    Info "backup: $bk  (restore with: tools/sync-install.ps1 -Rollback $(Split-Path $bk -Leaf))"
    if ($behind -gt 0) {
        & git.exe pull --ff-only $Remote $Branch --quiet 2>$null
        if ($LASTEXITCODE -ne 0) { Fail "git pull --ff-only failed"; Report "update" }
        Info "pulled to $((GitOut rev-parse --short HEAD)[0])"
    }
}

$plugins = Get-Plugins
if ($DryRun -and $behind -gt 0) {
    # describe the incoming versions without touching the working tree
    $plugins = @(); foreach ($d in (GitOut ls-tree -d --name-only "$up" "source/")) { $p = Read-Plugin $d $up; if ($p) { $plugins += $p } }
}

foreach ($p in $plugins) {
    $old = Read-Plugin $p.Dir $compareFrom
    $from = if ($old) { $old.Version } else { "(new plugin)" }
    Info "$($p.Name): $from -> $($p.Version)"
}

if (-not $DryRun) {
    foreach ($p in $plugins) {
        $proj = Join-Path $root $p.Dir
        $out = & dotnet build $proj -c Release -nologo "-p:GameDir=$GameDir" 2>&1
        $errs = $out | Select-String -Pattern ' error ' | Select-Object -First 3
        $locked = $out | Select-String -Pattern 'MSB3021|MSB3027' | Select-Object -First 1
        if ($LASTEXITCODE -ne 0 -or $errs) { Fail "$($p.Name) build failed: $(($errs | ForEach-Object { $_.Line.Trim() }) -join ' | ')" }
        elseif ($locked) { Fail "$($p.Name) built but could not be copied into the game (file locked - is the game running?)" }
    }
}

# ================================================================ 4. verify
function Hash($f) { if (Test-Path $f) { (Get-FileHash $f -Algorithm SHA256).Hash } else { $null } }

# Files a project's DeployToGame target copies (besides the DLL itself), as {Src, Dst} pairs.
function Get-DeployMap([string]$projDir) {
    $csproj = Get-ChildItem $projDir -Filter *.csproj | Select-Object -First 1
    if (-not $csproj) { return ,@() }
    $xml = Get-Content $csproj.FullName -Raw
    $target = [regex]::Match($xml, '(?s)<Target\s+Name="DeployToGame".*?</Target>').Value
    if (-not $target) { return ,@() }
    $expand = { param($s) $s.Replace('$(BepInExDir)', $bep).Replace('$(GameDir)', $GameDir) }
    $items = @{}
    foreach ($m in [regex]::Matches($target, '<(\w+)\s+Include="([^"]+)"')) { $items[$m.Groups[1].Value] = $m.Groups[2].Value }
    $result = @()
    foreach ($c in [regex]::Matches($target, '<Copy\s+[^>]*SourceFiles="([^"]+)"[^>]*DestinationFolder="([^"]+)"')) {
        $srcSpec = $c.Groups[1].Value; $dest = & $expand $c.Groups[2].Value
        if ($srcSpec -like '*$(TargetPath)*') { continue }                     # the DLL, checked separately
        $patterns = if ($srcSpec -match '^@\((\w+)\)$') { @($items[$Matches[1]] -split ';') } else { @($srcSpec -split ';') }
        foreach ($pat in $patterns) {
            if (-not $pat) { continue }
            foreach ($f in (Get-ChildItem (Join-Path $projDir $pat) -File -ErrorAction SilentlyContinue)) {
                $result += [pscustomobject]@{ Src = $f.FullName; Dst = (Join-Path $dest $f.Name) }
            }
        }
    }
    return ,$result
}
if (-not $DryRun) {
    foreach ($p in $plugins) {
        $built = Join-Path $root "$($p.Dir)/bin/Release/$($p.Assembly).dll"
        $inst = Join-Path $pluginsDir "$($p.Assembly).dll"
        if (-not (Test-Path $inst)) { Fail "$($p.Assembly).dll is not installed in $pluginsDir" }
        elseif ((Hash $built) -ne (Hash $inst)) { Fail "$($p.Assembly).dll in the game differs from the build output" }
        else { Info "$($p.Assembly).dll installed (matches build)" }

        # assets: exactly what the project's DeployToGame target copies (read from the .csproj, so it follows changes)
        $map = Get-DeployMap (Join-Path $root $p.Dir)
        $missing = @(); $differ = @()
        foreach ($e in $map) {
            if (-not (Test-Path $e.Dst)) { $missing += (Split-Path $e.Dst -Leaf) } elseif ((Hash $e.Src) -ne (Hash $e.Dst)) { $differ += (Split-Path $e.Dst -Leaf) }
        }
        if ($missing) { Fail "$($p.Name): shipped files missing in the game: $(Short $missing)" }
        if ($differ) { Fail "$($p.Name): files in the game differ from the repo: $(Short $differ)" }
        if ($map.Count -gt 0 -and -not $missing -and -not $differ) { Info "$($p.Name): $($map.Count) shipped asset file(s) in place" }
        foreach ($folder in ($map | ForEach-Object { Split-Path $_.Dst -Parent } | Sort-Object -Unique)) {
            if ($folder -eq $pluginsDir -or -not (Test-Path $folder)) { continue }
            $names = $map | ForEach-Object { Split-Path $_.Dst -Leaf }
            $extra = Get-ChildItem $folder -File | Where-Object { $names -notcontains $_.Name } | ForEach-Object Name
            if ($extra) { Warn "$($p.Name): files in $(Split-Path $folder -Leaf)/ the repo doesn't ship (stale from an older version, or made by you/the plugin): $(Short $extra)" }
        }
    }
}

# stale / duplicate plugin DLLs
if (Test-Path $pluginsDir) {
    $asmNames = $plugins | ForEach-Object Assembly
    foreach ($dll in (Get-ChildItem $pluginsDir -Recurse -Filter *.dll)) {
        $isOurs = $asmNames -contains $dll.BaseName -and $dll.DirectoryName -eq $pluginsDir
        if ($isOurs) { continue }
        $lookalike = $asmNames | Where-Object { $dll.BaseName -match [regex]::Escape($_) }
        if ($lookalike) { Fail "possible duplicate of $($lookalike -join '/'): $($dll.FullName) - BepInEx may load two copies; delete it" }
        else { Info "other plugin in BepInEx/plugins: $($dll.Name)" }
    }
}

# config transition
foreach ($p in $plugins) {
    $cfgPath = Join-Path $configDir "$($p.Guid).cfg"
    $cfg = Read-Cfg $cfgPath
    $newBinds = if ($DryRun -and $behind -gt 0) { Get-Binds $p.Dir $up } else { Get-Binds (Join-Path $root $p.Dir) $null }
    $oldBinds = Get-Binds $p.Dir $compareFrom
    if ($cfg.Count -eq 0) { Info "$($p.Name): no config yet ($($p.Guid).cfg is created with defaults on first launch)"; continue }

    $known = { param($binds, $k) $binds.ContainsKey($k) -or $binds.ContainsKey("*|" + ($k -split '\|', 2)[1]) }
    $added = $newBinds.Keys | Where-Object { $_ -notlike '`*|*' -and -not $cfg.ContainsKey($_) }
    if ($added) { Info "$($p.Name): new settings (get their defaults on next launch): $(Short ($added | ForEach-Object { $_.Replace('|', '.') }))" }

    $orphans = $cfg.Keys | Where-Object { -not (& $known $newBinds $_) }
    if ($orphans) { Warn "$($p.Name): settings in your .cfg the code no longer reads (removed or renamed - your value is ignored): $(Short ($orphans | ForEach-Object { $_.Replace('|', '.') }))" }

    foreach ($k in $newBinds.Keys) {
        if (-not $oldBinds.ContainsKey($k) -or -not $cfg.ContainsKey($k)) { continue }
        if ($oldBinds[$k] -ne $newBinds[$k] -and $cfg[$k] -eq $oldBinds[$k]) {
            Warn "$($p.Name): default for $($k.Replace('|', '.')) changed $($oldBinds[$k]) -> $($newBinds[$k]) but your .cfg still has the old default $($cfg[$k]) (BepInEx keeps saved values). Edit $($p.Guid).cfg if you want the new one."
        }
    }
}

# DriverCam-style per-car settings shipped in the repo vs installed
foreach ($p in $plugins) {
    $repoCars = Join-Path $root "$($p.Dir)/SavedSettings/$($p.Assembly)_cars"
    $gameCars = Join-Path $configDir "$($p.Assembly)_cars"
    if (-not (Test-Path $repoCars)) { continue }
    $diff = Get-ChildItem $repoCars -File | Where-Object { (Hash $_.FullName) -ne (Hash (Join-Path $gameCars $_.Name)) } | ForEach-Object BaseName
    if ($diff) { Info "$($p.Name): tuned per-car settings in the repo differ from yours for: $(Short $diff) (not copied - copy $($p.Dir)/SavedSettings/$($p.Assembly)_cars to BepInEx/config/ if you want them)" }
}

if ($DryRun) { Report "dry run (nothing changed)" }

# ================================================================ 5. launch check
if ($Launch) {
    $t0 = if (Test-Path $logFile) { (Get-Item $logFile).LastWriteTime } else { Get-Date "2000-01-01" }
    Start-Process "steam://rungameid/3088700"
    $ok = $false
    for ($i = 0; $i -lt 120; $i++) {
        Start-Sleep 2
        if ((Test-Path $logFile) -and (Get-Item $logFile).LastWriteTime -gt $t0 -and (Select-String -Path $logFile -Pattern 'Chainloader startup complete' -Quiet)) { $ok = $true; break }
    }
    if (-not $ok) { Fail "BepInEx did not finish loading within 4 minutes (see $logFile)" }
    else {
        Start-Sleep 5
        $log = Get-Content $logFile
        foreach ($p in $plugins) {
            $line = $log | Where-Object { $_ -match ("Loading \[" + [regex]::Escape($p.Name) + " ([^\]]+)\]") } | Select-Object -Last 1
            if (-not $line) { Fail "$($p.Name) was not loaded by BepInEx" }
            else {
                $v = [regex]::Match($line, "Loading \[" + [regex]::Escape($p.Name) + " ([^\]]+)\]").Groups[1].Value
                if ($v -ne $p.Version) { Fail "$($p.Name) loaded as $v, expected $($p.Version) (old DLL still in use?)" } else { Info "$($p.Name) $v loaded" }
            }
            $errs = $log | Where-Object { $_ -match ("^\[(Error|Fatal)\s*:\s*" + [regex]::Escape($p.Name) + "\]") } | Select-Object -First 3
            if ($errs) { Fail "$($p.Name) logged errors: $($errs -join ' | ')" }
        }
        $skips = $log | Where-Object { $_ -match 'Skipping \[|duplicate|already loaded' } | Select-Object -First 3
        if ($skips) { Warn "BepInEx skipped/duplicate plugins: $($skips -join ' | ')" }
    }
}

Report "install updated"
