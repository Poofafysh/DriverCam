<#
.SYNOPSIS
  Ownership ledger for the Claude sessions that share this working tree: .claude/ownership.md.
  A session claims the paths it is about to edit; other sessions check before editing, auditing, building with
  -Force or shipping them. Local state only: never stage or commit the ledger.

  powershell -NoProfile -ExecutionPolicy Bypass -File tools/ownership.ps1 -List
  ... -Claim   -Paths <p1;p2> -Task "<what>" [-Hours 8] [-Session <id>]
  ... -Check   -Paths <p1;p2> [-Session <id>]
  ... -Release [-Paths <p1;p2>] [-Session <id>]

  Session: -Session, else the first 6 characters of $env:CLAUDE_CODE_SESSION_ID (a subagent has its parent's id).
  No session id at all -> exit 2 ("pass -Session").
  Paths: repo-relative, '/' or '\', wildcards * ? and ** allowed (e.g. source/Police/Daredevil*.cs;
  source/DriverCam/**). Case-insensitive.
  Overlap (conservative, so two sessions never edit the same file): two paths overlap when either matches the other
  as a wildcard pattern, or the fixed part of one (everything before its first * ? [) is a folder prefix of, or equal
  to, the fixed part of the other.
  Rows whose 'expires' time has passed are EXPIRED: ignored by -Check / -Claim and removed by any -Claim / -Release.

  Ledger format (one row per claim; written by this script only):
    | session | task | paths | since | expires |
    |---|---|---|---|---|
    | ca6687 | gauges pass | source/DriverCam/Gauges.cs;source/DriverCam/Assets/** | 2026-10-04 11:00 | 2026-10-04 19:00 |

  Output lines: CLAIM <session> | <task> | <paths> | <since> | <expires> | ACTIVE|EXPIRED   (-List)
                CONFLICT <your path> held by <session> (<task>) as <their path> until <expires>
                CLAIMED / RELEASED / PRUNED lines, then RESULT: OK | FAIL
  Exit 0 = OK, 1 = conflict (-Check / -Claim), 2 = could not run (bad arguments, unreadable ledger).
#>
[CmdletBinding()]
param([switch]$List, [switch]$Claim, [switch]$Check, [switch]$Release,
      [string]$Paths, [string]$Task, [double]$Hours = 8, [string]$Session, [string]$Ledger)

$ErrorActionPreference = "Stop"
$root = (git rev-parse --show-toplevel 2>$null)
if (-not $root) { $root = Split-Path -Parent $PSScriptRoot }
if (-not $Ledger) { $Ledger = Join-Path $root ".claude\ownership.md" }
$fmt = "yyyy-MM-dd HH:mm"
$now = Get-Date

function Stop2([string]$m) { Write-Output "ERROR $m"; Write-Output "RESULT: FAIL (could not run)"; exit 2 }
$modes = @($List, $Claim, $Check, $Release | Where-Object { $_ })
if ($modes.Count -ne 1) { Stop2 "pass exactly one of -List, -Claim, -Check, -Release" }
if (-not $Session) { $sid = "$env:CLAUDE_CODE_SESSION_ID"; if ($sid.Length -ge 6) { $Session = $sid.Substring(0, 6) } }
if (-not $List -and -not $Session) { Stop2 "no session id: pass -Session <id>" }
if ($Session -match '[|\r\n]') { Stop2 "session id may not contain | or newlines" }
if ($Task -match '[|\r\n]') { Stop2 "task may not contain | or newlines" }

function Norm([string]$p) { $q = ($p.Trim() -replace '\\', '/'); while ($q.StartsWith('./')) { $q = $q.Substring(2) }; return $q.ToLowerInvariant() }
function Split-Paths([string]$s) { if (-not $s) { return @() }; return @($s -split ';' | ForEach-Object { Norm $_ } | Where-Object { $_ }) }
function FixedPart([string]$p) { $i = $p.IndexOfAny([char[]]@('*', '?', '[')); if ($i -lt 0) { return $p }; return $p.Substring(0, $i) }
function PrefixOf([string]$a, [string]$b) {
    # a is b, or a is a folder that contains b (a ends with / or b continues with / after a)
    if ($a -eq $b) { return $true }
    if (-not $b.StartsWith($a)) { return $false }
    if ($a.EndsWith('/')) { return $true }
    return ($b.Length -gt $a.Length -and $b[$a.Length] -eq '/')
}
function Overlap([string]$a, [string]$b) {
    # '**' and '*' both match across folders here ( -like's '*' matches '/'): conservative on purpose
    $la = $a -replace '\*\*', '*'; $lb = $b -replace '\*\*', '*'
    $fa = FixedPart $a; $fb = FixedPart $b
    $wa = $fa.Length -lt $a.Length; $wb = $fb.Length -lt $b.Length
    if (-not $wa -and -not $wb) { return ((PrefixOf $a $b) -or (PrefixOf $b $a)) }       # files / folders
    if ($wa -and -not $wb) { return (($b -like $la) -or (PrefixOf $b $fa)) }              # b matches a, or b is a folder above a
    if ($wb -and -not $wa) { return (($a -like $lb) -or (PrefixOf $a $fb)) }
    return (($a -like $lb) -or ($b -like $la) -or $fa.StartsWith($fb) -or $fb.StartsWith($fa))   # two patterns
}

# ---- read
$header = @("# Session ownership ledger", "",
    "Written only by ``tools/ownership.ps1`` (``/claim``, ``/release``, ``/claims``). Local state for the sessions sharing",
    "this working tree: never stage or commit it. Rows past ``expires`` are ignored and pruned on the next write.", "",
    "| session | task | paths | since | expires |", "|---|---|---|---|---|")
$rows = New-Object System.Collections.Generic.List[object]
if (Test-Path -LiteralPath $Ledger) {
    try { $lines = [IO.File]::ReadAllLines($Ledger) } catch { Stop2 "cannot read $Ledger : $($_.Exception.Message)" }
    foreach ($l in $lines) {
        if ($l -notmatch '^\|') { continue }
        $c = @($l.Trim().Trim('|').Split('|') | ForEach-Object { $_.Trim() })
        if ($c.Count -lt 5 -or $c[0] -eq 'session' -or $c[0] -match '^-+$') { continue }
        $exp = [datetime]::MinValue; $since = $c[3]
        if (-not [datetime]::TryParseExact($c[4], $fmt, [Globalization.CultureInfo]::InvariantCulture, 'None', [ref]$exp)) { Stop2 "bad expires '$($c[4])' in $Ledger (format $fmt)" }
        $rows.Add([pscustomobject]@{ Session = $c[0]; Task = $c[1]; Paths = (Split-Paths $c[2]); Since = $since; Expires = $exp })
    }
}
function Active($r) { return $r.Expires -gt $now }
function Write-Ledger {
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($h in $header) { $out.Add($h) }
    foreach ($r in $rows) { $out.Add(("| {0} | {1} | {2} | {3} | {4} |" -f $r.Session, $r.Task, ($r.Paths -join ';'), $r.Since, $r.Expires.ToString($fmt))) }
    $dir = Split-Path -Parent $Ledger; if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    [IO.File]::WriteAllText($Ledger, (($out -join "`r`n") + "`r`n"), (New-Object Text.UTF8Encoding($false)))
}
function Prune {
    for ($i = $rows.Count - 1; $i -ge 0; $i--) {
        if (-not (Active $rows[$i])) { Write-Output ("PRUNED {0} | {1} | expired {2}" -f $rows[$i].Session, $rows[$i].Task, $rows[$i].Expires.ToString($fmt)); $rows.RemoveAt($i) }
    }
}
function Conflicts([string[]]$mine) {
    $found = @()
    foreach ($r in $rows) {
        if ($r.Session -ieq $Session -or -not (Active $r)) { continue }
        foreach ($p in $mine) { foreach ($q in $r.Paths) { if (Overlap $p $q) { $found += ("CONFLICT {0} held by {1} ({2}) as {3} until {4}" -f $p, $r.Session, $r.Task, $q, $r.Expires.ToString($fmt)) } } }
    }
    return ,$found
}

if ($List) {
    if ($rows.Count -eq 0) { Write-Output "CLAIMS: none" }
    foreach ($r in $rows) { Write-Output ("CLAIM {0} | {1} | {2} | {3} | {4} | {5}" -f $r.Session, $r.Task, ($r.Paths -join ';'), $r.Since, $r.Expires.ToString($fmt), $(if (Active $r) { "ACTIVE" } else { "EXPIRED" })) }
    Write-Output "RESULT: OK"; exit 0
}

$mine = Split-Paths $Paths
if (($Claim -or $Check) -and $mine.Count -eq 0) { Stop2 "pass -Paths <p1;p2>" }

if ($Check) {
    $c = Conflicts $mine
    foreach ($x in $c) { Write-Output $x }
    if ($c.Count -gt 0) { Write-Output "RESULT: FAIL ($($c.Count) conflict(s))"; exit 1 }
    Write-Output "RESULT: OK"; exit 0
}

if ($Claim) {
    if (-not $Task) { Stop2 "pass -Task <what you are doing>" }
    if ($Hours -le 0 -or $Hours -gt 24) { Stop2 "-Hours must be more than 0 and at most 24" }
    $c = Conflicts $mine
    if ($c.Count -gt 0) { foreach ($x in $c) { Write-Output $x }; Write-Output "RESULT: FAIL ($($c.Count) conflict(s), nothing claimed)"; exit 1 }
    Prune
    $exp = $now.AddHours($Hours)
    $row = $null; foreach ($r in $rows) { if ($r.Session -ieq $Session -and $r.Task -eq $Task) { $row = $r } }
    if ($row) { $row.Paths = @(@($row.Paths) + $mine | Select-Object -Unique); $row.Expires = $exp }
    else { $row = [pscustomobject]@{ Session = $Session; Task = $Task; Paths = $mine; Since = $now.ToString($fmt); Expires = $exp }; $rows.Add($row) }
    Write-Ledger
    Write-Output ("CLAIMED {0} | {1} | {2} | until {3}" -f $Session, $Task, ($row.Paths -join ';'), $exp.ToString($fmt))
    Write-Output "RESULT: OK"; exit 0
}

if ($Release) {
    Prune
    $n = 0
    for ($i = $rows.Count - 1; $i -ge 0; $i--) {
        $r = $rows[$i]
        if ($r.Session -ine $Session) { continue }
        if ($mine.Count -eq 0) { Write-Output ("RELEASED {0} | {1} | {2}" -f $r.Session, $r.Task, ($r.Paths -join ';')); $rows.RemoveAt($i); $n++; continue }
        $keep = @($r.Paths | Where-Object { $mine -notcontains $_ })
        $gone = @($r.Paths | Where-Object { $mine -contains $_ })
        if ($gone.Count -gt 0) { Write-Output ("RELEASED {0} | {1} | {2}" -f $r.Session, $r.Task, ($gone -join ';')); $n++ }
        if ($keep.Count -eq 0) { $rows.RemoveAt($i) } else { $r.Paths = $keep }
    }
    Write-Ledger
    if ($n -eq 0) { Write-Output "RELEASED nothing (no matching claim of $Session)" }
    Write-Output "RESULT: OK"; exit 0
}
