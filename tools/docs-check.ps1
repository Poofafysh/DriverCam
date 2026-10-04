<#
.SYNOPSIS
  Compares each plugin README with its code. Read-only.
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/docs-check.ps1 [-Plugin A,B]

  Log lines (README section '## Log...' or '## What to check in the log...' vs the plugin's Log*/Logger.Log* calls):
    STALE_IN_README <Plugin> "<README span>"  - a backticked span (12+ characters) in the log section none of whose
        fixed fragments (10+ characters, after dropping a leading [Tag] and cutting at <...>, '...', ' / ', numbers,
        N / n placeholders and x.y.z; ends trimmed of spaces and % ; : , . ( ) ' ") is in any of the plugin's .cs
        files (its own plus the source/Shared files its .csproj links)
    MISSING_IN_README <Plugin> <file:line> "<fragment>"  - a LogInfo call whose first fixed fragment (the literal text
        before the first {placeholder}, without a leading [Plugin] tag, 12+ characters) appears nowhere in the README
  Settings (README vs Config.Bind("Section", "Key", ...)):
    SETTING_MISSING_IN_README <Plugin> [Section] Key   - bound in code, neither `Key` nor `Section.Key` in the README
    SETTING_STALE_IN_README <Plugin> `<Section.Key>`   - a `Section.Key` span in the README with no such binding
  NO_LOG_SECTION <Plugin> / NO_README <Plugin> for plugins without one.
  Output ends with 'RESULT: OK' or 'RESULT: WARN (<n> finding(s))'. Exit 0 = ran (findings are WARN), 2 = could not run.
#>
[CmdletBinding()]
param([string[]]$Plugin)

$ErrorActionPreference = "Stop"
$root = (git rev-parse --show-toplevel 2>$null)
if (-not $root) { $root = Split-Path -Parent $PSScriptRoot }
Set-Location $root
if (-not (Test-Path "source")) { Write-Output "ERROR no source/ folder"; Write-Output "RESULT: FAIL (could not run)"; exit 2 }

$all = @(Get-ChildItem source -Directory | Where-Object { Test-Path (Join-Path $_.FullName "Plugin.cs") } | Sort-Object Name)
if ($Plugin -and $Plugin.Count -gt 0) {
    $want = @($Plugin | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $sel = @(); foreach ($w in $want) { $h = @($all | Where-Object { $_.Name -ieq $w }); if ($h.Count -eq 0) { Write-Output "ERROR unknown plugin '$w'"; Write-Output "RESULT: FAIL (could not run)"; exit 2 }; $sel += $h[0] }
    $all = $sel
}
$utf8 = New-Object Text.UTF8Encoding($false)
function ReadText([string]$p) { return [IO.File]::ReadAllText($p, $utf8) }
# cut a README span into fixed fragments: drop <...>, '...', x.y.z, numbers, lone N / n placeholders
function Fragments([string]$s) {
    $t = ($s -replace '^\[[^\]]+\]\s*', '') -replace '<[^>]*>', '|' -replace '\.\.\.', '|' -replace '\bx\.y\.z\b', '|' -replace '[-+]?\d+([.,]\d+)*', '|' -replace '\b[Nn]\b', '|' -replace ' / ', '|'
    $trim = [char[]]" `t%;:,.()'`""
    return @($t.Split('|') | ForEach-Object { $_.Trim($trim) } | Where-Object { $_.Length -ge 10 } | Sort-Object Length -Descending)
}

$n = 0
foreach ($pd in $all) {
    $name = $pd.Name
    $files = @(Get-ChildItem $pd.FullName -Recurse -Filter *.cs -File | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | ForEach-Object FullName)
    $csproj = @(Get-ChildItem $pd.FullName -Filter *.csproj -File)
    if ($csproj.Count -gt 0) { foreach ($m in [regex]::Matches((ReadText $csproj[0].FullName), '<Compile\s+Include="([^"]+\.cs)"')) { $p = [IO.Path]::GetFullPath((Join-Path $pd.FullName $m.Groups[1].Value)); if (Test-Path -LiteralPath $p) { $files += $p } } }
    $code = (($files | ForEach-Object { ReadText $_ }) -join "`n")
    $readme = Join-Path $pd.FullName "README.md"
    if (-not (Test-Path $readme)) { Write-Output "NO_README $name"; $n++; continue }
    $rt = ReadText $readme
    $sec = [regex]::Match($rt, '(?ms)^## (Log|What to check in the log)[^\n]*\n(.*?)(?=^## |\z)')
    if (-not $sec.Success) { Write-Output "NO_LOG_SECTION $name" }
    else {
        foreach ($sp in [regex]::Matches($sec.Groups[2].Value, '`([^`]*)`')) {   # pairs in order; a span may wrap a line
            $span = ($sp.Groups[1].Value -replace '\s*\r?\n\s*', ' ')
            if ($span.Length -lt 12) { continue }
            $fr = Fragments $span
            if ($fr.Count -eq 0) { continue }
            if (@($fr | Where-Object { $code.Contains($_) }).Count -eq 0) { Write-Output ("STALE_IN_README {0} ""{1}""" -f $name, $span); $n++ }
        }
        # code -> README: LogInfo calls
        foreach ($f in $files) {
            if (-not $f.StartsWith($pd.FullName)) { continue }   # shared files are documented by their own plugin
            $i = 0
            foreach ($line in [IO.File]::ReadAllLines($f, $utf8)) {
                $i++
                $m = [regex]::Match($line, '\.Log(?:ger)?\.LogInfo\(\s*\$?@?"((?:[^"\\{]|\\.)*)')
                if (-not $m.Success) { $m = [regex]::Match($line, '\bLog(?:ger)?\.LogInfo\(\s*\$?@?"((?:[^"\\{]|\\.)*)') }
                if (-not $m.Success) { continue }
                $lit = ($m.Groups[1].Value -replace '^\[[^\]]+\]\s*', '').Trim()
                $lit = ($lit -split '\d')[0].Trim()
                if ($lit.Length -lt 12) { continue }
                if (-not $rt.Contains($lit)) { Write-Output ("MISSING_IN_README {0} {1}:{2} ""{3}""" -f $name, (($f.Substring($root.Length + 1)) -replace '\\', '/'), $i, $lit); $n++ }
            }
        }
    }
    # settings
    $binds = @()
    foreach ($m in [regex]::Matches($code, 'Config\.Bind\(\s*"([^"]+)"\s*,\s*"([^"]+)"')) { $binds += [pscustomobject]@{ S = $m.Groups[1].Value; K = $m.Groups[2].Value } }
    foreach ($b in $binds) {
        if (-not ($rt.Contains("``$($b.K)``") -or $rt.Contains("``$($b.S).$($b.K)``") -or $rt.Contains("$($b.S).$($b.K)") -or $rt -match ("\b" + [regex]::Escape($b.K) + "\b"))) {
            Write-Output ("SETTING_MISSING_IN_README {0} [{1}] {2}" -f $name, $b.S, $b.K); $n++
        }
    }
    foreach ($m in [regex]::Matches($rt, '`([A-Z][A-Za-z]+)\.([A-Z][A-Za-z0-9]+)`')) {
        $s = $m.Groups[1].Value; $k = $m.Groups[2].Value
        $isSection = @($binds | Where-Object { $_.S -eq $s }).Count -gt 0
        if (-not $isSection) { continue }   # Type.Member spans, not settings
        if (@($binds | Where-Object { $_.S -eq $s -and $_.K -eq $k }).Count -eq 0) { Write-Output ("SETTING_STALE_IN_README {0} ``{1}.{2}``" -f $name, $s, $k); $n++ }
    }
}
if ($n -gt 0) { Write-Output "RESULT: WARN ($n finding(s))" } else { Write-Output "RESULT: OK" }
exit 0
