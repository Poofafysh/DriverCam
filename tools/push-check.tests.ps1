<#
.SYNOPSIS
  Plays out every version match / mismatch two developers can produce and checks that push-check.ps1 gives the
  right verdict. Each scenario builds a throwaway setup: a bare "GitHub" remote + clone A (the other developer)
  + clone B (you), with a tiny plugin "Foo".
    powershell -ExecutionPolicy Bypass -File tools/push-check.tests.ps1          # all scenarios
    ... -Only collision,lower                                                   # scenarios whose name contains these words
  Exit code 0 = all scenarios passed.
#>
[CmdletBinding()]
param([string[]]$Only, [switch]$KeepTemp)

$ErrorActionPreference = "Continue"
# -File passes "a,b" as one string: split it
if ($Only) { $Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
$check = Join-Path $PSScriptRoot "push-check.ps1"
$work = Join-Path ([IO.Path]::GetTempPath()) ("push-check-tests-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force $work | Out-Null

function W([string]$path, [string]$text) { New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null; [IO.File]::WriteAllText($path, $text) }
function W-Utf8([string]$path, [string]$text, [bool]$bom) { [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $bom)) }
function G([string]$dir) { & git.exe -C $dir @args 2>&1 | Out-Null }

function Plugin-Cs([string]$ver, [string]$guid = "x.foo", [string]$name = "Foo", [string]$extra = "") {
@"
using BepInEx;
namespace $name
{
    [BepInPlugin(Guid, "$name", Version)]
    public class Plugin
    {
        public const string Guid = "$guid";
        public const string Version = "$ver";
        $extra
    }
}
"@
}
function Csproj([string]$asm = "Foo", [string]$verTag = "") { "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><AssemblyName>$asm</AssemblyName>$verTag</PropertyGroup></Project>" }

# --- environment -------------------------------------------------------------------------------------------
function New-Env([string]$name) {
    $d = Join-Path $work $name
    $remote = Join-Path $d "remote.git"; $a = Join-Path $d "A"; $b = Join-Path $d "B"
    New-Item -ItemType Directory -Force $d | Out-Null
    & git.exe init --bare -q -b main $remote 2>&1 | Out-Null
    & git.exe clone -q $remote $a 2>&1 | Out-Null
    G $a config user.name "Other Dev"; G $a config user.email "a@example.com"
    W "$a/source/Foo/Plugin.cs" (Plugin-Cs "1.0.0")
    W "$a/source/Foo/Foo.csproj" (Csproj)
    W "$a/source/Foo/Code.cs" "class Code { int x = 1; }"
    W "$a/source/Foo/README.md" "# Foo`n`nCurrent version: **1.0.0**`n"
    W "$a/README.md" "| Plugin | Version |`n|---|---|`n| **Foo** | 1.0.0 |`n"
    $toolsDir = (New-Item -ItemType Directory -Force "$a/tools").FullName
    Copy-Item $check $toolsDir
    Copy-Item (Join-Path $PSScriptRoot "bump-version.ps1") $toolsDir
    G $a add -A; G $a commit -q -m "initial"; G $a push -q -u origin main
    & git.exe clone -q $remote $b 2>&1 | Out-Null
    G $b config user.name "You"; G $b config user.email "b@example.com"
    return [pscustomobject]@{ Remote = $remote; A = $a; B = $b }
}
function Set-Version([string]$dir, [string]$ver, [switch]$CodeOnlyFile) {
    $p = "$dir/source/Foo/Plugin.cs"
    W $p ((Get-Content $p -Raw) -replace 'Version = "[^"]*"', "Version = `"$ver`"")
    if (-not $CodeOnlyFile) {
        W "$dir/source/Foo/README.md" "# Foo`n`nCurrent version: **$ver**`n"
        W "$dir/README.md" ((Get-Content "$dir/README.md" -Raw) -replace '\| \*\*Foo\*\* \| [^|]+ \|', "| **Foo** | $ver |")
    }
}
function Bump([string]$dir, [string]$to) { Push-Location $dir; & powershell -NoProfile -ExecutionPolicy Bypass -File "$dir/tools/bump-version.ps1" -Plugin Foo -To $to | Out-Null; Pop-Location }
function Change-Code([string]$dir, [string]$tag = "") { Add-Content "$dir/source/Foo/Code.cs" "// change $tag $([guid]::NewGuid())" }
function Commit([string]$dir, [string]$msg = "work") { G $dir add -A; G $dir commit -q -m $msg }
function Push([string]$dir) { G $dir push -q origin HEAD:main }
# Foo compiles in source/Shared/Perf.cs (pushed by A, pulled by B)
function Link-Shared($e) {
    W "$($e.A)/source/Shared/Perf.cs" "static class Perf { }"
    W "$($e.A)/source/Foo/Foo.csproj" "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><AssemblyName>Foo</AssemblyName></PropertyGroup><ItemGroup><Compile Include=`"..\Shared\Perf.cs`" Link=`"Perf.cs`" /></ItemGroup></Project>"
    Commit $e.A "shared perf"; Push $e.A; G $e.B pull -q --rebase
}
# a second plugin Bar (pushed by A, pulled by B) with extra source
function Add-Bar($e, [string]$code) {
    W "$($e.A)/source/Bar/Plugin.cs" (Plugin-Cs "1.0.0" "x.bar" "Bar")
    W "$($e.A)/source/Bar/Bar.csproj" (Csproj "Bar")
    W "$($e.A)/source/Bar/Patches.cs" $code
    Commit $e.A "bar"; Push $e.A; G $e.B pull -q --rebase
}
# CLAUDE.md developer table: Foo is owned by the developer whose git author is $author (pushed by A, pulled by B)
function Set-Owner($e, [string]$author) {
    $who = if ($author -eq "You") { "Me" } else { "Other" }
    W "$($e.A)/CLAUDE.md" "| Developer | Owns | Git author |`n|---|---|---|`n| **$who** | ``source/Foo/`` (the foo plugin) | $author |`n"
    Commit $e.A "owners"; Push $e.A; G $e.B pull -q --rebase
}
function Run-Check([string]$dir, [string[]]$extra = @()) {
    Push-Location $dir
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File "$dir/tools/push-check.ps1" -NoGitHub @extra 2>&1 | Out-String
    $code = $LASTEXITCODE
    Pop-Location
    [pscustomobject]@{ Code = $code; Out = $out }
}

# --- scenarios -----------------------------------------------------------------------------------------------
# Each: name, setup scriptblock (gets $e), expected exit code, regexes that must appear, regexes that must not,
# and optionally a Verify scriptblock (gets $e after the check) that returns problem strings.
$scenarios = @(
    @{ Name = "up-to-date: code change, no bump"; Expect = 1; Must = @('Foo: code changed .* still 1\.0\.0.*Set it to 1\.0\.1');
       Setup = { param($e) Change-Code $e.B; Commit $e.B } },
    @{ Name = "up-to-date: code change with bump"; Expect = 0; Must = @('1\.0\.0 -> 1\.0\.1');
       Setup = { param($e) Change-Code $e.B; Set-Version $e.B "1.0.1"; Commit $e.B } },
    @{ Name = "untracked: a new untracked file in the plugin is not a code change"; Expect = 0; Must = @('untracked files, not part of the push'); MustNot = @('FAIL', 'code changed');
       Setup = { param($e) W "$($e.B)/source/Foo/Wip.cs" "class Wip { }" } },
    @{ Name = "untracked: an untracked file is ignored, a staged edit counts"; Expect = 1; Must = @('Foo: code changed \(source/Foo/Code\.cs\)'); MustNot = @('code changed \([^)]*Wip');
       Setup = { param($e) W "$($e.B)/source/Foo/Wip.cs" "class Wip { }"; Change-Code $e.B; G $e.B add source/Foo/Code.cs } },
    @{ Name = "up-to-date: docs only, no bump needed"; Expect = 0; MustNot = @('FAIL');
       Setup = { param($e) Add-Content "$($e.B)/source/Foo/README.md" "More docs."; Commit $e.B } },
    @{ Name = "up-to-date: bump without code change"; Expect = 0; Must = @('bumped 1\.0\.0 -> 1\.1\.0 without code changes');
       Setup = { param($e) Set-Version $e.B "1.1.0"; Commit $e.B } },
    @{ Name = "diverged: same-version collision"; Expect = 1; Must = @('VERSION COLLISION.*both chose 1\.1\.0.*set Foo to 1\.1\.1', 'DIVERGED');
       Setup = { param($e) Change-Code $e.A "a"; Set-Version $e.A "1.1.0"; Commit $e.A; Push $e.A
                 Change-Code $e.B "b"; Set-Version $e.B "1.1.0"; Commit $e.B } },
    @{ Name = "diverged: other dev pushed higher"; Expect = 1; Must = @('already pushed 2\.0\.0, higher than your 1\.5\.0.*set Foo to 2\.0\.1');
       Setup = { param($e) Change-Code $e.A; Set-Version $e.A "2.0.0"; Commit $e.A; Push $e.A
                 Change-Code $e.B; Set-Version $e.B "1.5.0"; Commit $e.B } },
    @{ Name = "diverged: both bumped, yours higher"; Expect = 1; Must = @('Yours is higher.*keep 1\.3\.0', 'DIVERGED'); MustNot = @('COLLISION');
       Setup = { param($e) Change-Code $e.A; Set-Version $e.A "1.2.0"; Commit $e.A; Push $e.A
                 Change-Code $e.B; Set-Version $e.B "1.3.0"; Commit $e.B } },
    @{ Name = "diverged: they released, you changed code without bump"; Expect = 1; Must = @('released 1\.1\.0 while you changed Foo code without a bump.*set Foo to 1\.1\.1');
       Setup = { param($e) Change-Code $e.A; Set-Version $e.A "1.1.0"; Commit $e.A; Push $e.A
                 Change-Code $e.B; Commit $e.B } },
    @{ Name = "diverged: both changed code, neither bumped"; Expect = 1; Must = @('neither bumped 1\.0\.0.*set Foo to 1\.0\.1');
       Setup = { param($e) Change-Code $e.A; Commit $e.A; Push $e.A
                 Change-Code $e.B; Commit $e.B } },
    @{ Name = "behind only: other dev pushed, you have nothing"; Expect = 1; Must = @('behind origin/main'); MustNot = @('COLLISION', 'neither bumped');
       Setup = { param($e) Change-Code $e.A; Set-Version $e.A "1.1.0"; Commit $e.A; Push $e.A } },
    @{ Name = "after rebase: your version lower than origin"; Expect = 1; Must = @('LOWER than 2\.0\.0.*Set it to 2\.0\.1');
       Setup = { param($e) Change-Code $e.A; Set-Version $e.A "2.0.0"; Commit $e.A; Push $e.A
                 G $e.B pull -q --rebase; Change-Code $e.B; Set-Version $e.B "1.9.0"; Commit $e.B } },
    @{ Name = "prerelease: 1.1.0-beta is lower than 1.1.0"; Expect = 1; Must = @('LOWER than 1\.1\.0');
       Setup = { param($e) Set-Version $e.A "1.1.0"; Commit $e.A; Push $e.A
                 G $e.B pull -q --rebase; Change-Code $e.B; Set-Version $e.B "1.1.0-beta"; Commit $e.B } },
    @{ Name = "prerelease: beta.10 is higher than beta.2"; Expect = 0; Must = @('1\.1\.0-beta\.2 -> 1\.1\.0-beta\.10');
       Setup = { param($e) Set-Version $e.A "1.1.0-beta.2"; Commit $e.A; Push $e.A
                 G $e.B pull -q --rebase; Change-Code $e.B; Set-Version $e.B "1.1.0-beta.10"; Commit $e.B } },
    @{ Name = "format: two-part version warns"; Expect = 0; Must = @('only 2 part'); MustNot = @('FAIL');
       Setup = { param($e) Change-Code $e.B; Set-Version $e.B "1.1"; Commit $e.B } },
    @{ Name = "format: v-prefix and 1.0 == 1.0.0"; Expect = 1; Must = @('still v1\.0');
       Setup = { param($e) Change-Code $e.B; Set-Version $e.B "v1.0"; Commit $e.B } },
    @{ Name = "format: invalid version"; Expect = 1; Must = @("'banana' is not a valid version");
       Setup = { param($e) Change-Code $e.B; Set-Version $e.B "banana"; Commit $e.B } },
    @{ Name = "consistency: README not updated"; Expect = 1; Must = @('written differently.*README\.md=1\.0\.0');
       Setup = { param($e) Change-Code $e.B; Set-Version $e.B "1.0.1" -CodeOnlyFile; Commit $e.B } },
    @{ Name = "consistency: csproj <Version> disagrees"; Expect = 1; Must = @('written differently.*\.csproj <Version>=1\.0\.0');
       Setup = { param($e) W "$($e.B)/source/Foo/Foo.csproj" (Csproj "Foo" "<Version>1.0.0</Version>"); Change-Code $e.B; Set-Version $e.B "1.0.1"; Commit $e.B } },
    @{ Name = "consistency: attribute literal vs Version const"; Expect = 1; Must = @('written differently.*const Version=1\.0\.0');
       Setup = { param($e) $p = "$($e.B)/source/Foo/Plugin.cs"; W $p ((Get-Content $p -Raw).Replace('[BepInPlugin(Guid, "Foo", Version)]', '[BepInPlugin(Guid, "Foo", "1.0.1")]'))
                 W "$($e.B)/source/Foo/README.md" "# Foo`n`nCurrent version: **1.0.1**`n"; W "$($e.B)/README.md" "| Plugin | Version |`n|---|---|`n| **Foo** | 1.0.1 |`n"; Change-Code $e.B; Commit $e.B } },
    @{ Name = "consistency: hard-coded old version in a log string"; Expect = 0; Must = @("hard-coded 'Foo 0\.8'");
       Setup = { param($e) Add-Content "$($e.B)/source/Foo/Code.cs" 'class L { string s = "Foo 0.8 loaded"; }'; Set-Version $e.B "1.0.1"; Commit $e.B } },
    @{ Name = "tags: version already tagged with different code"; Expect = 1; Must = @("tag 'Foo-v1\.0\.1' already exists with different Foo code");
       Setup = { param($e) G $e.A checkout -q -b side; Change-Code $e.A "side"; Set-Version $e.A "1.0.1"; Commit $e.A; G $e.A tag Foo-v1.0.1; G $e.A push -q origin Foo-v1.0.1
                 Change-Code $e.B "mine"; Set-Version $e.B "1.0.1"; Commit $e.B } },
    @{ Name = "tags: tag on the same code is fine"; Expect = 0; Must = @("matches tag 'v1\.0\.1'");
       Setup = { param($e) Change-Code $e.B; Set-Version $e.B "1.0.1"; Commit $e.B; G $e.B tag v1.0.1 } },
    @{ Name = "identity: GUID changed"; Expect = 1; Must = @('plugin GUID changed .x\.foo. -> .x\.foo2.');
       Setup = { param($e) W "$($e.B)/source/Foo/Plugin.cs" (Plugin-Cs "1.0.1" "x.foo2"); Set-Version $e.B "1.0.1"; Commit $e.B } },
    @{ Name = "identity: GUID change allowed with flag"; Expect = 0; Args = @('-AllowIdentityChange'); Must = @('WARN.*GUID changed');
       Setup = { param($e) W "$($e.B)/source/Foo/Plugin.cs" (Plugin-Cs "1.0.1" "x.foo2"); Set-Version $e.B "1.0.1"; Commit $e.B } },
    @{ Name = "identity: DLL renamed warns"; Expect = 0; Must = @("DLL name changed 'Foo\.dll' -> 'FooMod\.dll'");
       Setup = { param($e) W "$($e.B)/source/Foo/Foo.csproj" (Csproj "FooMod"); Set-Version $e.B "1.0.1"; Commit $e.B } },
    @{ Name = "identity: duplicate GUID across plugins"; Expect = 1; Must = @("duplicate plugin Guid: 'x\.foo'");
       Setup = { param($e) W "$($e.B)/source/Bar/Plugin.cs" (Plugin-Cs "0.1.0" "x.foo" "Bar"); W "$($e.B)/source/Bar/Bar.csproj" (Csproj "Bar"); Commit $e.B } },
    @{ Name = "new plugin added"; Expect = 0; Must = @('Bar: new plugin');
       Setup = { param($e) W "$($e.B)/source/Bar/Plugin.cs" (Plugin-Cs "0.1.0" "x.bar" "Bar"); W "$($e.B)/source/Bar/Bar.csproj" (Csproj "Bar"); Commit $e.B } },
    @{ Name = "uncommitted version change warns"; Expect = 0; Must = @('version changed to 1\.0\.1 in the working tree but not committed');
       Setup = { param($e) Change-Code $e.B; Commit $e.B "code"; Set-Version $e.B "1.0.1" } },
    @{ Name = "content: shipped SavedSettings change needs a bump"; Expect = 1; Must = @('code changed .*SavedSettings');
       Setup = { param($e) W "$($e.B)/source/Foo/SavedSettings/cars/Car.cfg" "a = 1"; Commit $e.B } },
    @{ Name = "bump script: patch from your version"; Expect = 0; Must = @('1\.0\.0 -> 1\.0\.1'); MustNot = @('written differently');
       Setup = { param($e) Change-Code $e.B; Bump $e.B "patch"; Commit $e.B } },
    @{ Name = "bump script: counts up from the higher origin version"; Expect = 0; Must = @('2\.0\.0 -> 2\.0\.1');
       Setup = { param($e) Change-Code $e.A; Set-Version $e.A "2.0.0"; Commit $e.A; Push $e.A
                 G $e.B pull -q --rebase; Change-Code $e.B; Bump $e.B "patch"; Commit $e.B } },
    @{ Name = "bump script: minor resolves a collision after rebase"; Expect = 0; Must = @('1\.1\.0 -> 1\.2\.0');
       Setup = { param($e) Change-Code $e.A "a"; Set-Version $e.A "1.1.0"; Commit $e.A; Push $e.A
                 W "$($e.B)/source/Foo/Other.cs" "class Other { }"; Commit $e.B; G $e.B pull -q --rebase
                 Bump $e.B "minor"; Commit $e.B } },
    # --- shared source linked into a plugin (source/Shared/*.cs) ---
    @{ Name = "shared: changing a linked shared file needs a bump"; Expect = 1; Must = @('Foo: code changed .*source/Shared/Perf\.cs.*Set it to 1\.0\.1');
       Setup = { param($e) Link-Shared $e; Add-Content "$($e.B)/source/Shared/Perf.cs" "// faster"; Commit $e.B "perf" } },
    @{ Name = "shared: linked shared file change with bump"; Expect = 0; Must = @('1\.0\.0 -> 1\.0\.1');
       Setup = { param($e) Link-Shared $e; Add-Content "$($e.B)/source/Shared/Perf.cs" "// faster"; Set-Version $e.B "1.0.1"; Commit $e.B "perf" } },
    @{ Name = "shared: a shared file nobody links needs no bump"; Expect = 0; MustNot = @('FAIL');
       Setup = { param($e) Link-Shared $e; W "$($e.B)/source/Shared/Other.cs" "class Other { }"; Commit $e.B "other" } },
    # --- Harmony targets ---
    @{ Name = "harmony: hand patch without a target comment is flagged"; Expect = 0; Must = @('Foo installs Harmony patches by hand');
       Setup = { param($e) W "$($e.B)/source/Foo/Hooks.cs" "class Hooks { void I(Harmony h) { foreach (var n in new[] { `"Update`" }) h.Patch(AccessTools.Method(t, n)); } }"; Commit $e.B; Set-Version $e.B "1.0.1"; Commit $e.B } },
    @{ Name = "harmony: AccessTools hand patch clashes with another plugin's attribute"; Expect = 0; Must = @('Bar and Foo both patch: Cam\.Update|Foo and Bar both patch: Cam\.Update'); MustNot = @('installs Harmony patches by hand');
       Setup = { param($e) Add-Bar $e "[HarmonyPatch(typeof(Cam), nameof(Cam.Update))] class P { }"
                 W "$($e.B)/source/Foo/Hooks.cs" "class Hooks { void I(Harmony h) { h.Patch(AccessTools.Method(typeof(Game.Cam), `"Update`")); } }"; Set-Version $e.B "1.0.1"; Commit $e.B } },
    @{ Name = "harmony: target comment marked as cooperating is info only"; Expect = 0; Must = @('both patch Cam\.Update \(marked as cooperating\)'); MustNot = @('WARN .*both patch');
       Setup = { param($e) Add-Bar $e "[HarmonyPatch(typeof(Cam), `"Update`")] class P { }"
                 W "$($e.B)/source/Foo/Hooks.cs" "class Hooks { // harmony-target: Cam.Update, Cam.LateUpdate (cooperates with Bar)`n void I(Harmony h) { h.Patch(m); } }"; Set-Version $e.B "1.0.1"; Commit $e.B } },
    # --- ownership (CLAUDE.md developer table) ---
    @{ Name = "ownership: commit to someone else's plugin without approval"; Expect = 1; Must = @("changes source/Foo, which Other owns, and its message doesn't say");
       Setup = { param($e) Set-Owner $e "Other Dev"; Change-Code $e.B; Set-Version $e.B "1.0.1"; Commit $e.B "tweak Foo" } },
    @{ Name = "ownership: approval in the commit message"; Expect = 0; Must = @('owned by Other\) changed in .* approval is in the message');
       Setup = { param($e) Set-Owner $e "Other Dev"; Change-Code $e.B; Set-Version $e.B "1.0.1"; Commit $e.B "Foo 1.0.1: hook (approved by Other in chat)" } },
    @{ Name = "ownership: your own plugin needs no approval"; Expect = 0; MustNot = @('owns');
       Setup = { param($e) Set-Owner $e "You"; Change-Code $e.B; Set-Version $e.B "1.0.1"; Commit $e.B "tweak Foo" } },
    # Non-ASCII built from char codes: PS 5.1 may parse this script as ANSI, so literal arrows here would be garbled.
    @{ Name = "bump script: keeps non-ASCII text, BOM and line endings byte for byte"; Expect = 0; Must = @('1\.0\.0 -> 1\.1\.0'); MustNot = @('written differently');
       Setup = { param($e) $to = [string][char]0x2192; $both = [string][char]0x2194; $deg = [string][char]0xB0
                 W-Utf8 "$($e.B)/source/Foo/README.md" "# Foo`r`n`r`nCurrent version: **1.0.0**`r`n`r`n| **F8** | panel: Full $to Compact $to Hidden |`r`n" $false
                 W-Utf8 "$($e.B)/README.md" "| Plugin | Version |`n|---|---|`n| **Foo** | 1.0.0 |`n`nWalls are paired ($both) per tile.`n" $false
                 W-Utf8 "$($e.B)/source/Foo/Plugin.cs" ((Plugin-Cs "1.0.0" "x.foo" "Foo" "// curb $to wall above 20$deg") -replace "`r?`n", "`r`n") $true
                 Commit $e.B "non-ASCII docs"
                 $snap = @{}; foreach ($f in 'source/Foo/README.md', 'README.md', 'source/Foo/Plugin.cs') { $snap[$f] = [IO.File]::ReadAllBytes("$($e.B)/$f") }
                 $e | Add-Member -NotePropertyName Before -NotePropertyValue $snap
                 Change-Code $e.B; Bump $e.B "minor"; Commit $e.B }
       # Expected bytes = the bytes before, with only the ASCII version swapped (Latin-1 maps each byte to one char and back).
       Verify = { param($e) $l1 = [Text.Encoding]::GetEncoding(28591)
                  foreach ($f in $e.Before.Keys) {
                      $want = $l1.GetBytes($l1.GetString($e.Before[$f]).Replace('1.0.0', '1.1.0'))
                      $got = [IO.File]::ReadAllBytes("$($e.B)/$f")
                      if ([BitConverter]::ToString($got) -ne [BitConverter]::ToString($want)) { "$f is not byte-identical apart from the version: got '$($l1.GetString($got))'" }
                  } } }
)

# --- run -----------------------------------------------------------------------------------------------------
$passed = 0; $failed = @()
$i = 0
foreach ($s in $scenarios) {
    $i++
    if ($Only -and -not ($Only | Where-Object { $s.Name -match [regex]::Escape($_) })) { continue }
    $e = New-Env ("s{0:D2}" -f $i)
    & $s.Setup $e
    $r = Run-Check $e.B $(if ($s.Args) { $s.Args } else { @() })
    $problems = @()
    if ($r.Code -ne $s.Expect) { $problems += "exit code $($r.Code), expected $($s.Expect)" }
    foreach ($m in @($s.Must)) { if ($m -and $r.Out -notmatch $m) { $problems += "missing: $m" } }
    foreach ($m in @($s.MustNot)) { if ($m -and $r.Out -match $m) { $problems += "unexpected: $m" } }
    if ($s.Verify) { $problems += @(& $s.Verify $e) }
    if ($problems) {
        $failed += $s.Name
        Write-Host ("FAIL  {0}" -f $s.Name) -ForegroundColor Red
        $problems | ForEach-Object { Write-Host "        $_" -ForegroundColor Red }
        ($r.Out -split "`n" | Where-Object { $_ -match '(FAIL|WARN|info|RESULT)' }) | ForEach-Object { Write-Host "        | $($_.Trim())" -ForegroundColor DarkGray }
    } else {
        $passed++
        Write-Host ("ok    {0}" -f $s.Name) -ForegroundColor Green
    }
}
if (-not $KeepTemp) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host ""
if ($failed.Count -eq 0) { Write-Host "ALL $passed SCENARIOS PASSED" -ForegroundColor Green; exit 0 }
Write-Host "$($failed.Count) FAILED, $passed passed" -ForegroundColor Red
exit 1
