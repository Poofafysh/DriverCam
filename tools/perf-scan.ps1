<#
.SYNOPSIS
  Static per-frame cost scan of the plugins' C# code. Read-only; no build, no game.
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/perf-scan.ps1 [-Plugin A,B] [-Depth 3]

  1. Entry points (code that runs every frame), per plugin (its source/<Plugin>/**/*.cs plus the source/Shared/*.cs
     files its .csproj links):
       - methods named Update, LateUpdate, FixedUpdate, OnGUI, OnPreCull, OnPreRender, OnPostRender, OnRenderObject,
         OnWillRenderObject
       - Harmony patch methods (named Prefix / Postfix, or marked [HarmonyPrefix] / [HarmonyPostfix]) whose
         [HarmonyPatch(...)] within the 15 lines above names Update / LateUpdate / FixedUpdate
       - methods named in 'new HarmonyMethod(typeof(T), nameof(M) | "M")' within 5 lines of a
         '// harmony-target:' comment that names an Update / LateUpdate / FixedUpdate method
       - methods subscribed with '+=' to onBeforeRender, onPreCull, onPreRender, onPostRender, beginCameraRendering,
         endCameraRendering, beginFrameRendering, willRenderCanvases
  2. Reach: every method of the same plugin whose name is called from a reached body, by name (all overloads,
     any class), up to -Depth levels (default 3).
  3. Hits: each line of a reached body (comments and string contents blanked first) is checked for:
       SCAN     FindObjectOfType / FindObjectsOfType / FindObjectsByType / FindFirstObjectByType /
                FindAnyObjectByType / FindObjectsOfTypeAll / GameObject.Find / FindWithTag / FindGameObjectsWithTag
       GETCOMP  GetComponent / GetComponents / ...InChildren / ...InParent
       LINQ     .Where( .Select( .SelectMany( .Any( .All( .First( .FirstOrDefault( .Last( .LastOrDefault( .OrderBy(
                .OrderByDescending( .ThenBy( .ToList( .ToArray( .ToDictionary( .Count( .Sum( .Max( .Min( .Average(
                .Distinct( .GroupBy( .Skip( .Take( .Concat(   (not Math.* / Mathf.*)
       ALLOC    'new T(' / 'new T[' / 'new[]' / 'new T {' for a reference type (Unity / System value types excluded:
                Vector2/3/4, Vector2Int/3Int, Quaternion, Color, Color32, Rect, Bounds, Ray, Matrix4x4, Plane,
                RaycastHit, Keyframe, KeyValuePair, TimeSpan, DateTime, LayerMask, ValueTuple), 'new(' (NEW? =
                target-typed, type unknown)
       STRING   $"..." / @$"..." , '"..." +' or '+ "..."', string.Format / Concat / Join, .ToString(
       LAMBDA   '=>' inside the body (a lambda; allocates when it captures)
       ERRPATH  the line has 'throw' or 'catch': reported as ERRPATH instead of LOG / STRING / ALLOC / NEW? / LAMBDA
                (an error path, not a per-frame cost)
       LOG      the line calls a logger (.LogInfo / .LogWarning / .LogError / .LogDebug / .LogMessage / Log.Log*):
                reported as LOG instead of STRING / ALLOC / NEW? / LAMBDA (usually throttled or once)
  Output: 'ENTRY', 'HIT' and 'PLUGIN' lines, then 'RESULT: OK'. Exit 0 = scan ran (hits are facts, not a verdict:
  the perf-budget agent judges them), 2 = could not run.
#>
[CmdletBinding()]
param([string[]]$Plugin, [int]$Depth = 3)

$ErrorActionPreference = "Stop"
$root = (git rev-parse --show-toplevel 2>$null)
if (-not $root) { $root = Split-Path -Parent $PSScriptRoot }
Set-Location $root
if (-not (Test-Path "source")) { Write-Output "ERROR no source/ folder under $root"; Write-Output "RESULT: FAIL (could not run)"; exit 2 }

$code = @'
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

public static class RoguePerfScan
{
    public class Method { public string File; public string Name; public string Class; public int Line; public int BodyStart; public int BodyEnd; }
    public class Src { public string Path; public string Text; public string Clean; public int[] LineStarts; public string[] Lines; public string[] CleanLines; }

    // comments -> spaces, string / char literal contents -> spaces (quotes and $ / @ prefixes kept), newlines kept
    public static string Strip(string s)
    {
        var b = new StringBuilder(s);
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (c == '/' && i + 1 < n && s[i + 1] == '/') { while (i < n && s[i] != '\n') { b[i] = ' '; i++; } continue; }
            if (c == '/' && i + 1 < n && s[i + 1] == '*') { b[i] = ' '; b[i + 1] = ' '; i += 2; while (i < n && !(s[i] == '*' && i + 1 < n && s[i + 1] == '/')) { if (s[i] != '\n') b[i] = ' '; i++; } if (i < n) { b[i] = ' '; if (i + 1 < n) b[i + 1] = ' '; i += 2; } continue; }
            if (c == '"' && i + 2 < n && s[i + 1] == '"' && s[i + 2] == '"')
            {   // raw string literal
                int j = i + 3; while (j + 2 < n && !(s[j] == '"' && s[j + 1] == '"' && s[j + 2] == '"')) { if (s[j] != '\n') b[j] = ' '; j++; }
                i = Math.Min(n, j + 3); continue;
            }
            if (c == '"')
            {
                bool verbatim = (i > 0 && s[i - 1] == '@') || (i > 1 && s[i - 1] == '$' && s[i - 2] == '@');
                int j = i + 1;
                while (j < n)
                {
                    if (verbatim) { if (s[j] == '"') { if (j + 1 < n && s[j + 1] == '"') { b[j] = ' '; b[j + 1] = ' '; j += 2; continue; } break; } }
                    else { if (s[j] == '\\' && j + 1 < n) { b[j] = ' '; if (s[j + 1] != '\n') b[j + 1] = ' '; j += 2; continue; } if (s[j] == '"' || s[j] == '\n') break; }
                    if (s[j] != '\n') b[j] = ' ';
                    j++;
                }
                i = j + 1; continue;
            }
            if (c == '\'')
            {
                int j = i + 1;
                if (j < n && s[j] == '\\') j += 2; else j += 1;
                if (j < n && s[j] == '\'') { for (int k = i + 1; k < j; k++) b[k] = ' '; i = j + 1; continue; }
            }
            i++;
        }
        return b.ToString();
    }

    static readonly HashSet<string> Kw = new HashSet<string>(new[] { "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "fixed", "return", "new", "else", "do", "try", "nameof", "typeof", "sizeof", "default", "when", "get", "set", "base", "this", "await", "throw", "yield", "case", "goto", "checked", "unchecked", "stackalloc", "is", "as", "in", "out", "ref", "var", "delegate", "operator", "implicit", "explicit", "class", "struct", "interface", "enum", "record", "namespace" });
    static readonly Regex Decl = new Regex(@"^[ \t]*(?:\[[^\]\n]*\][ \t]*)*(?:(?:public|private|protected|internal|static|override|virtual|unsafe|async|extern|sealed|new|readonly|abstract|partial)[ \t]+)*([\w\.\[\],<>\?]+(?:<[^<>\n()]*>)?)[ \t]+(\w+)[ \t]*(?:<[^<>\n()]*>)?[ \t]*\(", RegexOptions.Multiline);
    static readonly Regex ClassDecl = new Regex(@"\b(?:class|struct|record)\s+(\w+)");

    public static Src Load(string path)
    {
        var src = new Src();
        src.Path = path;
        src.Text = System.IO.File.ReadAllText(path);
        src.Clean = Strip(src.Text);
        var starts = new List<int> { 0 };
        for (int i = 0; i < src.Text.Length; i++) if (src.Text[i] == '\n') starts.Add(i + 1);
        src.LineStarts = starts.ToArray();
        src.Lines = src.Text.Split('\n');
        src.CleanLines = src.Clean.Split('\n');
        return src;
    }

    public static int LineOf(Src s, int pos) { int i = Array.BinarySearch(s.LineStarts, pos); if (i < 0) i = ~i - 1; return i + 1; }

    static int Match(string t, int open, char o, char c)
    {
        int d = 0;
        for (int i = open; i < t.Length; i++) { if (t[i] == o) d++; else if (t[i] == c) { d--; if (d == 0) return i; } }
        return -1;
    }

    public static List<Method> Methods(Src s)
    {
        var list = new List<Method>();
        string t = s.Clean;
        foreach (Match m in Decl.Matches(t))
        {
            string type = m.Groups[1].Value, name = m.Groups[2].Value;
            if (Kw.Contains(type) || Kw.Contains(name)) continue;
            int open = m.Index + m.Length - 1;
            int close = Match(t, open, '(', ')');
            if (close < 0) continue;
            int k = close + 1;
            while (k < t.Length && char.IsWhiteSpace(t[k])) k++;
            if (k + 5 < t.Length && t.Substring(k, 5) == "where") { while (k < t.Length && t[k] != '{' && t[k] != ';' && !(t[k] == '=' && k + 1 < t.Length && t[k + 1] == '>')) k++; }
            if (k >= t.Length) continue;
            int bs, be;
            if (t[k] == '{') { bs = k; be = Match(t, k, '{', '}'); if (be < 0) continue; }
            else if (t[k] == '=' && k + 1 < t.Length && t[k + 1] == '>')
            {
                bs = k; int d = 0; be = -1;
                for (int i = k + 2; i < t.Length; i++) { char ch = t[i]; if (ch == '(' || ch == '{' || ch == '[') d++; else if (ch == ')' || ch == '}' || ch == ']') d--; else if (ch == ';' && d <= 0) { be = i; break; } }
                if (be < 0) continue;
            }
            else continue;
            string cls = "";
            foreach (Match c in ClassDecl.Matches(t.Substring(0, m.Index))) cls = c.Groups[1].Value;
            list.Add(new Method { File = s.Path, Name = name, Class = cls, Line = LineOf(s, m.Index + m.Groups[2].Index - m.Index), BodyStart = bs, BodyEnd = be });
        }
        return list;
    }

    static readonly Regex Call = new Regex(@"\b([A-Za-z_]\w*)\s*(?:<[^<>()\n]*>)?\s*\(");
    public static List<string> Calls(Src s, Method m)
    {
        var r = new List<string>();
        string body = s.Clean.Substring(m.BodyStart, m.BodyEnd - m.BodyStart + 1);
        foreach (Match c in Call.Matches(body)) { string nm = c.Groups[1].Value; if (!Kw.Contains(nm)) r.Add(nm); }
        // method groups passed as values: identifiers followed by , ) or ; (e.g. ConvertDelegate<Action>(Tick))
        return r;
    }

    static readonly string ValueTypes = @"(?:UnityEngine\.)?(?:Vector[234]|Vector[23]Int|Quaternion|Color|Color32|Rect|Bounds|Ray|Matrix4x4|Plane|RaycastHit|Keyframe|KeyValuePair|TimeSpan|DateTime|LayerMask|(?:System\.)?ValueTuple)";
    static readonly Regex RScan = new Regex(@"\b(FindObjectOfType|FindObjectsOfType|FindObjectsByType|FindFirstObjectByType|FindAnyObjectByType|FindObjectsOfTypeAll|FindWithTag|FindGameObjectsWithTag)\b|\bGameObject\s*\.\s*Find\s*\(");
    static readonly Regex RGet = new Regex(@"\bGetComponents?(InChildren|InParent)?\s*[<(]");
    static readonly Regex RLinq = new Regex(@"(?<!\bMathf?)\s*\.\s*(Where|Select|SelectMany|Any|All|First|FirstOrDefault|Last|LastOrDefault|OrderBy|OrderByDescending|ThenBy|ToList|ToArray|ToDictionary|Count|Sum|Max|Min|Average|Distinct|GroupBy|Skip|Take|Concat)\s*(?:<[^<>()]*>)?\s*\(");
    static readonly Regex RNewRef = new Regex(@"\bnew\s+(?!" + ValueTypes + @"\s*[\(\{\[])[A-Za-z_][\w\.]*(?:\s*<[^;=]*?>)?\s*[\(\[\{]|\bnew\s*\[\s*\]");
    static readonly Regex RNewTarget = new Regex(@"\bnew\s*\(");
    static readonly Regex RString = new Regex(@"\$@?""|@\$""|""\s*\+|\+\s*""|\bstring\s*\.\s*(Format|Concat|Join)\s*\(|\.\s*ToString\s*\(");
    static readonly Regex RLambda = new Regex(@"=>");
    static readonly Regex RErr = new Regex(@"\b(throw|catch)\b");
    static readonly Regex RLog = new Regex(@"\.\s*Log(Info|Warning|Error|Debug|Message|Fatal)\s*\(|\bLog\s*\.\s*Log\w*\s*\(");

    public static List<string> Categories(string clean, bool isDeclLine)
    {
        var cats = new List<string>();
        if (RScan.IsMatch(clean)) cats.Add("SCAN");
        if (RGet.IsMatch(clean)) cats.Add("GETCOMP");
        if (RLinq.IsMatch(clean)) cats.Add("LINQ");
        bool log = RLog.IsMatch(clean);
        bool err = RErr.IsMatch(clean);
        bool alloc = RNewRef.IsMatch(clean), target = RNewTarget.IsMatch(clean), str = RString.IsMatch(clean), lam = !isDeclLine && RLambda.IsMatch(clean);
        if (err && (log || alloc || target || str || lam)) cats.Add("ERRPATH");
        else if (log && (alloc || target || str || lam)) cats.Add("LOG");
        else
        {
            if (alloc) cats.Add("ALLOC");
            if (target) cats.Add("NEW?");
            if (str) cats.Add("STRING");
            if (lam) cats.Add("LAMBDA");
        }
        return cats;
    }
}
'@
if (-not ("RoguePerfScan" -as [type])) { Add-Type -TypeDefinition $code -Language CSharp }

$frameNames = @("Update", "LateUpdate", "FixedUpdate", "OnGUI", "OnPreCull", "OnPreRender", "OnPostRender", "OnRenderObject", "OnWillRenderObject")
$events = 'onBeforeRender|onPreCull|onPreRender|onPostRender|beginCameraRendering|endCameraRendering|beginFrameRendering|willRenderCanvases'
$rootFwd = ($root -replace '\\', '/').TrimEnd('/')
function Rel([string]$full) { $r = ($full -replace '\\', '/'); if ($r.StartsWith($rootFwd + '/', [StringComparison]::OrdinalIgnoreCase)) { $r = $r.Substring($rootFwd.Length + 1) }; return $r }

$all = @(Get-ChildItem source -Directory | Where-Object { @(Get-ChildItem $_.FullName -Filter *.csproj -File).Count -gt 0 } | Sort-Object Name)
if ($Plugin -and $Plugin.Count -gt 0) {
    $want = @($Plugin | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $sel = @()
    foreach ($w in $want) {
        $hit = @($all | Where-Object { $_.Name -ieq $w })
        if ($hit.Count -eq 0) { Write-Output "ERROR unknown plugin '$w' (have: $(($all | ForEach-Object Name) -join ', '))"; Write-Output "RESULT: FAIL (could not run)"; exit 2 }
        $sel += $hit[0]
    }
    $all = $sel
}

$srcCache = @{}
function Get-Src([string]$path) { if (-not $srcCache.ContainsKey($path)) { $srcCache[$path] = [RoguePerfScan]::Load($path) }; return $srcCache[$path] }

$reported = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($pd in $all) {
    $name = $pd.Name
    $files = @(Get-ChildItem $pd.FullName -Recurse -Filter *.cs -File | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | ForEach-Object FullName)
    $csproj = @(Get-ChildItem $pd.FullName -Filter *.csproj -File)[0].FullName
    foreach ($m in [regex]::Matches([IO.File]::ReadAllText($csproj), '<Compile\s+Include="([^"]+\.cs)"')) {
        $p = [IO.Path]::GetFullPath((Join-Path $pd.FullName $m.Groups[1].Value))
        if (Test-Path -LiteralPath $p) { $files += $p }
    }
    $methods = New-Object System.Collections.Generic.List[object]
    foreach ($f in $files) { foreach ($mt in [RoguePerfScan]::Methods((Get-Src $f))) { $methods.Add($mt) } }
    $byName = @{}
    foreach ($mt in $methods) { if (-not $byName.ContainsKey($mt.Name)) { $byName[$mt.Name] = New-Object System.Collections.Generic.List[object] }; $byName[$mt.Name].Add($mt) }

    # entries
    $entryNames = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($fn in $frameNames) { if ($byName.ContainsKey($fn)) { [void]$entryNames.Add($fn) } }
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($mt in $methods) { if ($frameNames -contains $mt.Name) { $entries.Add($mt) } }
    foreach ($f in $files) {
        $s = Get-Src $f
        # attribute patches
        foreach ($mt in @($methods | Where-Object { $_.File -eq $f })) {
            $isPatch = ($mt.Name -eq "Prefix" -or $mt.Name -eq "Postfix")
            $from = [Math]::Max(0, $mt.Line - 16); $upto = $mt.Line - 2
            $above = ""; if ($upto -ge $from) { $above = ($s.Lines[$from..$upto] -join "`n") }
            if (-not $isPatch -and $mt.Line -ge 2) { $near = ($s.Lines[[Math]::Max(0, $mt.Line - 4)..($mt.Line - 2)] -join "`n"); if ($near -match '\[Harmony(Prefix|Postfix)\]') { $isPatch = $true } }
            if ($isPatch -and $above -match 'HarmonyPatch\([^\n]*(nameof\(\w+\.(Late|Fixed)?Update\)|"(Late|Fixed)?Update")') { if (-not $entries.Contains($mt)) { $entries.Add($mt) } }
        }
        # hand patches next to a harmony-target comment naming an Update method
        for ($i = 0; $i -lt $s.Lines.Count; $i++) {
            if ($s.Lines[$i] -match '//\s*harmony-target:.*\.(Late|Fixed)?Update\b') {
                $lo = [Math]::Max(0, $i - 5); $hi = [Math]::Min($s.Lines.Count - 1, $i + 5)
                foreach ($hm in [regex]::Matches(($s.Lines[$lo..$hi] -join "`n"), 'HarmonyMethod\(\s*typeof\(\s*[\w\.]+\s*\)\s*,\s*(?:nameof\(\s*(?:\w+\.)*(\w+)\s*\)|"(\w+)")')) {
                    $nm = $(if ($hm.Groups[1].Success) { $hm.Groups[1].Value } else { $hm.Groups[2].Value })
                    if ($byName.ContainsKey($nm)) { foreach ($mt in $byName[$nm]) { if (-not $entries.Contains($mt)) { $entries.Add($mt) } } }
                }
            }
            if ($s.CleanLines[$i] -match "\b($events)\s*\+=\s*(.+)$") {
                foreach ($id in [regex]::Matches($Matches[2], '\b([A-Za-z_]\w*)\b')) {
                    $nm = $id.Groups[1].Value
                    if ($byName.ContainsKey($nm)) { foreach ($mt in $byName[$nm]) { if (-not $entries.Contains($mt)) { $entries.Add($mt) } } }
                }
            }
        }
    }

    # reach
    $via = @{}
    $queue = New-Object System.Collections.Generic.Queue[object]
    foreach ($e in $entries) { $key = "$($e.File)|$($e.BodyStart)"; if (-not $via.ContainsKey($key)) { $via[$key] = @{ M = $e; Via = "$($e.Class).$($e.Name)"; D = 0 }; $queue.Enqueue($via[$key]) } }
    while ($queue.Count -gt 0) {
        $cur = $queue.Dequeue()
        if ($cur.D -ge $Depth) { continue }
        foreach ($nm in [RoguePerfScan]::Calls((Get-Src $cur.M.File), $cur.M)) {
            if (-not $byName.ContainsKey($nm)) { continue }
            foreach ($mt in $byName[$nm]) {
                $key = "$($mt.File)|$($mt.BodyStart)"
                if ($via.ContainsKey($key)) { continue }
                $via[$key] = @{ M = $mt; Via = $cur.Via; D = $cur.D + 1 }
                $queue.Enqueue($via[$key])
            }
        }
    }

    foreach ($e in ($entries | Sort-Object File, Line)) { Write-Output ("ENTRY {0} {1}.{2} {3}:{4}" -f $name, $e.Class, $e.Name, (Rel $e.File), $e.Line) }
    $counts = [ordered]@{ SCAN = 0; GETCOMP = 0; LINQ = 0; ALLOC = 0; "NEW?" = 0; STRING = 0; LAMBDA = 0; LOG = 0; ERRPATH = 0 }
    $hits = New-Object System.Collections.Generic.List[object]
    foreach ($r in $via.Values) {
        $mt = $r.M; $s = Get-Src $mt.File
        $l0 = [RoguePerfScan]::LineOf($s, $mt.BodyStart); $l1 = [RoguePerfScan]::LineOf($s, $mt.BodyEnd)
        for ($ln = $l0; $ln -le $l1; $ln++) {
            $clean = $s.CleanLines[$ln - 1]
            $cats = [RoguePerfScan]::Categories($clean, ($ln -eq $mt.Line))
            if ($cats.Count -eq 0) { continue }
            $key = "$($mt.File)|$ln"
            if (-not $reported.Add($key)) { continue }
            $hits.Add([pscustomobject]@{ File = (Rel $mt.File); Line = $ln; Cats = ($cats -join ','); Via = $r.Via; Text = $s.Lines[$ln - 1].Trim() })
            foreach ($c in $cats) { $counts[$c]++ }
        }
    }
    foreach ($h in ($hits | Sort-Object File, Line)) {
        $t = $h.Text; if ($t.Length -gt 110) { $t = $t.Substring(0, 110) + "..." }
        Write-Output ("HIT {0} {1}:{2} {3} via {4} | {5}" -f $name, $h.File, $h.Line, $h.Cats, $h.Via, $t)
    }
    $parts = @(); foreach ($k in $counts.Keys) { $parts += "$k=$($counts[$k])" }
    Write-Output ("PLUGIN {0} entries={1} reached={2} {3}" -f $name, $entries.Count, $via.Count, ($parts -join ' '))
}
Write-Output "RESULT: OK"
exit 0
