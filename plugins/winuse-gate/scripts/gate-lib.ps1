# Shared helpers for the winuse-gate hooks. Dot-source this file.
# Windows PowerShell 5.1 compatible. Keep this file ASCII only.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$script:Utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $script:Utf8

# Reads the hook input JSON from stdin as UTF-8. Returns $null when stdin is empty.
function Read-HookInput {
    $reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $script:Utf8)
    $text = $reader.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return $text | ConvertFrom-Json
}

# Quotes one argument for the Windows command line (CommandLineToArgvW rules).
function ConvertTo-ArgString([string]$Arg) {
    if ($Arg.Length -gt 0 -and $Arg -notmatch '[\s"]') { return $Arg }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    $slashes = 0
    foreach ($ch in $Arg.ToCharArray()) {
        if ($ch -eq '\') { $slashes++; continue }
        if ($ch -eq '"') { [void]$sb.Append('\', $slashes * 2 + 1); [void]$sb.Append('"') }
        else { [void]$sb.Append('\', $slashes); [void]$sb.Append($ch) }
        $slashes = 0
    }
    [void]$sb.Append('\', $slashes * 2)
    [void]$sb.Append('"')
    return $sb.ToString()
}

# Runs git in $Dir and returns @{ Code; Out } with stdout decoded as UTF-8.
# $InputText is written to git's stdin when given.
function Invoke-Git([string]$Dir, [string[]]$GitArgs, [string]$InputText = $null) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'git'
    $psi.Arguments = (@('-c', 'core.quotepath=off') + $GitArgs | ForEach-Object { ConvertTo-ArgString $_ }) -join ' '
    $psi.WorkingDirectory = $Dir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardInput = $true
    $psi.StandardOutputEncoding = $script:Utf8
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $stdin = New-Object System.IO.StreamWriter($p.StandardInput.BaseStream, $script:Utf8)
    if ($InputText) { $stdin.Write($InputText) }
    $stdin.Close()
    $errTask = $p.StandardError.ReadToEndAsync()
    $out = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()
    [void]$errTask.Result
    return @{ Code = $p.ExitCode; Out = $out }
}

# Returns the git toplevel for $Cwd, or $null when $Cwd is not inside a repository (or git is missing).
function Get-RepoRoot([string]$Cwd) {
    if (-not $Cwd -or -not (Test-Path -LiteralPath $Cwd -PathType Container)) { return $null }
    try { $r = Invoke-Git $Cwd @('rev-parse', '--show-toplevel') } catch { return $null }
    if ($r.Code -ne 0) { return $null }
    $top = $r.Out.Trim()
    if (-not $top) { return $null }
    return $top.Replace('/', '\')
}

# Converts a glob to an anchored, case-insensitive regex.
# '**/' = zero or more directories, '**' = anything, '*' = anything but '/', '?' = one char but '/'.
function Convert-GlobToRegex([string]$Glob) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('^')
    $i = 0
    while ($i -lt $Glob.Length) {
        $c = $Glob[$i]
        if ($c -eq '*' -and $i + 1 -lt $Glob.Length -and $Glob[$i + 1] -eq '*') {
            if ($i + 2 -lt $Glob.Length -and $Glob[$i + 2] -eq '/') { [void]$sb.Append('(?:.*/)?'); $i += 3 }
            else { [void]$sb.Append('.*'); $i += 2 }
            continue
        }
        if ($c -eq '*') { [void]$sb.Append('[^/]*') }
        elseif ($c -eq '?') { [void]$sb.Append('[^/]') }
        else { [void]$sb.Append([regex]::Escape([string]$c)) }
        $i++
    }
    [void]$sb.Append('$')
    return New-Object System.Text.RegularExpressions.Regex($sb.ToString(), [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
}

# Loads <Root>\.claude\winuse-gate.json.
# Returns @{ Status = 'missing'|'broken'|'ok'; Ui = [regex[]]; Exclude = [regex[]]; Error = [string] }.
function Get-GateConfig([string]$Root) {
    $result = @{ Status = 'missing'; Ui = @(); Exclude = @(); Error = $null }
    $path = Join-Path $Root '.claude\winuse-gate.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $result }
    try {
        $json = [System.IO.File]::ReadAllText($path, $script:Utf8) | ConvertFrom-Json
    } catch {
        $result.Status = 'broken'; $result.Error = 'invalid JSON'; return $result
    }
    $ui = @($json.ui)
    if ($null -eq $json.ui -or -not ($json.ui -is [array]) -or $ui.Count -eq 0 -or @($ui | Where-Object { -not ($_ -is [string]) }).Count -gt 0) {
        $result.Status = 'broken'; $result.Error = '"ui" must be a non-empty array of glob strings'; return $result
    }
    $exclude = @()
    if ($null -ne $json.exclude) {
        if (-not ($json.exclude -is [array]) -or @($json.exclude | Where-Object { -not ($_ -is [string]) }).Count -gt 0) {
            $result.Status = 'broken'; $result.Error = '"exclude" must be an array of glob strings'; return $result
        }
        $exclude = @($json.exclude)
    }
    $result.Status = 'ok'
    $result.Ui = @($ui | ForEach-Object { Convert-GlobToRegex $_ })
    $result.Exclude = @($exclude | ForEach-Object { Convert-GlobToRegex $_ })
    return $result
}

# True when the repo-relative path (with '/') matches a "ui" glob and no "exclude" glob.
function Test-UiPath([string]$Path, $Config) {
    $hit = $false
    foreach ($re in $Config.Ui) { if ($re.IsMatch($Path)) { $hit = $true; break } }
    if (-not $hit) { return $false }
    foreach ($re in $Config.Exclude) { if ($re.IsMatch($Path)) { return $false } }
    return $true
}

# Writes text to stdout without PowerShell formatting.
function Write-Raw([string]$Text) { [Console]::Out.Write($Text); [Console]::Out.Flush() }

# Logs one line to stderr. Hooks fail open: they report and exit 0.
function Write-GateError([string]$Message) { [Console]::Error.WriteLine("winuse-gate: $Message") }

# Prints hookSpecificOutput.additionalContext for $EventName.
function Write-HookContext([string]$EventName, [string]$Text) {
    Write-Raw (@{ hookSpecificOutput = @{ hookEventName = $EventName; additionalContext = $Text } } | ConvertTo-Json -Compress -Depth 5)
}

# Sorts strings ordinally (culture-independent).
function Sort-Ordinal([string[]]$Items) {
    $arr = [string[]]@($Items)
    [Array]::Sort($arr, [StringComparer]::Ordinal)
    return ,$arr
}

function Get-Sha1([string]$Text) {
    $sha = [System.Security.Cryptography.SHA1]::Create()
    try { $bytes = $sha.ComputeHash($script:Utf8.GetBytes($Text)) } finally { $sha.Dispose() }
    return -join ($bytes | ForEach-Object { $_.ToString('x2') })
}

# Returns every UI file that differs from HEAD as a hashtable path -> git blob id, or 'deleted'.
# Covers modified, added, deleted and untracked (not ignored) files. Paths are repo-relative with '/'.
function Get-DirtyUiFiles([string]$Root, $Config) {
    $r = Invoke-Git $Root @('status', '--porcelain=v1', '-z', '--untracked-files=all', '--no-renames')
    if ($r.Code -ne 0) { throw "git status failed in $Root" }
    $result = @{}
    $present = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $r.Out.Split([char]0)) {
        if ($entry.Length -lt 4) { continue }
        $path = $entry.Substring(3)
        if (-not (Test-UiPath $path $Config)) { continue }
        if (Test-Path -LiteralPath (Join-Path $Root $path) -PathType Leaf) { $present.Add($path) }
        else { $result[$path] = 'deleted' }
    }
    if ($present.Count -gt 0) {
        $h = Invoke-Git $Root @('hash-object', '--stdin-paths') (($present -join "`n") + "`n")
        if ($h.Code -ne 0) { throw "git hash-object failed in $Root" }
        $hashes = @($h.Out.Split("`n") | Where-Object { $_ })
        for ($i = 0; $i -lt $present.Count; $i++) { $result[$present[$i]] = $hashes[$i].Trim() }
    }
    return $result
}

# Resolves the plugin data dir: the -DataDir argument, then CLAUDE_PLUGIN_DATA, then %LOCALAPPDATA%\winuse-gate.
function Resolve-DataDir([string]$DataDir) {
    if ($DataDir -and $DataDir -notmatch '\$\{') { return $DataDir }
    if ($env:CLAUDE_PLUGIN_DATA) { return $env:CLAUDE_PLUGIN_DATA }
    return Join-Path $env:LOCALAPPDATA 'winuse-gate'
}

# Returns (and creates) <DataDir>\state\<sha1 of lowercased toplevel>.
function Get-StateDir([string]$DataDir, [string]$Root) {
    $dir = Join-Path (Join-Path (Resolve-DataDir $DataDir) 'state') (Get-Sha1 $Root.ToLowerInvariant())
    [void](New-Item -ItemType Directory -Force -Path (Join-Path $dir 'sessions'))
    return $dir
}

function Read-JsonFile([string]$Path, $Default) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $Default }
    try { return [System.IO.File]::ReadAllText($Path, $script:Utf8) | ConvertFrom-Json } catch { return $Default }
}

# Writes JSON through a temp file and an atomic replace; retries on transient IO errors.
function Write-JsonFile([string]$Path, $Object) {
    $json = $Object | ConvertTo-Json -Depth 10
    $tmp = "$Path.$PID.tmp"
    [System.IO.File]::WriteAllText($tmp, $json, $script:Utf8)
    for ($i = 0; $i -lt 10; $i++) {
        try { Move-Item -LiteralPath $tmp -Destination $Path -Force; return }
        catch { Start-Sleep -Milliseconds 50 }
    }
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    throw "could not write $Path"
}

function ConvertTo-Hashtable($Object) {
    $h = @{}
    if ($null -ne $Object) { foreach ($p in $Object.PSObject.Properties) { $h[$p.Name] = [string]$p.Value } }
    return $h
}

function Get-SessionFile([string]$StateDir, [string]$SessionId) {
    $safe = ($SessionId -replace '[^A-Za-z0-9_-]', '_')
    if (-not $safe) { $safe = 'unknown' }
    return Join-Path (Join-Path $StateDir 'sessions') "$safe.json"
}

# Returns @{ Exists; Baseline = hashtable path -> hash; Blocked = string[] }.
function Get-SessionState([string]$StateDir, [string]$SessionId) {
    $raw = Read-JsonFile (Get-SessionFile $StateDir $SessionId) $null
    if ($null -eq $raw) { return @{ Exists = $false; Baseline = @{}; Blocked = @() } }
    return @{ Exists = $true; Baseline = (ConvertTo-Hashtable $raw.baseline); Blocked = @($raw.blocked | Where-Object { $_ }) }
}

function Save-SessionState([string]$StateDir, [string]$SessionId, $State) {
    Write-JsonFile (Get-SessionFile $StateDir $SessionId) @{ baseline = $State.Baseline; blocked = @($State.Blocked) }
}

# Returns the verified "path|hash" pairs of the repository as a HashSet.
function Get-VerifiedSet([string]$StateDir) {
    $set = New-Object 'System.Collections.Generic.HashSet[string]'
    $raw = Read-JsonFile (Join-Path $StateDir 'verified.json') $null
    if ($null -ne $raw) { foreach ($e in @($raw.entries)) { if ($e) { [void]$set.Add("$($e.path)|$($e.hash)") } } }
    return ,$set
}

# Runs $Body while holding a per-state-dir mutex, so concurrent hooks do not lose writes.
function Invoke-WithStateLock([string]$StateDir, [scriptblock]$Body) {
    $name = 'Local\winuse-gate-' + (Get-Sha1 $StateDir.ToLowerInvariant())
    $mutex = New-Object System.Threading.Mutex -ArgumentList $false, $name
    $owned = $false
    try {
        try { $owned = $mutex.WaitOne(10000) } catch [System.Threading.AbandonedMutexException] { $owned = $true }
        & $Body
    } finally {
        if ($owned) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

# Appends path|hash pairs to verified.json (deduplicated, newest last, capped at 500 entries).
function Add-VerifiedEntries([string]$StateDir, [hashtable]$Files) {
    if ($Files.Count -eq 0) { return }
    Invoke-WithStateLock $StateDir {
        $path = Join-Path $StateDir 'verified.json'
        $raw = Read-JsonFile $path $null
        $keys = @{}
        foreach ($p in $Files.Keys) { $keys["$p|$($Files[$p])"] = $true }
        $entries = New-Object System.Collections.Generic.List[object]
        if ($null -ne $raw) {
            foreach ($e in @($raw.entries)) {
                if ($e -and -not $keys.ContainsKey("$($e.path)|$($e.hash)")) { $entries.Add(@{ path = [string]$e.path; hash = [string]$e.hash }) }
            }
        }
        foreach ($p in (Sort-Ordinal @($Files.Keys))) { $entries.Add(@{ path = $p; hash = $Files[$p] }) }
        $skip = [Math]::Max(0, $entries.Count - 500)
        Write-JsonFile $path @{ entries = @($entries | Select-Object -Skip $skip) }
    }
}

# True only when the tool response carries a boolean passed=true. Accepts a JSON string,
# an object with "passed", MCP content blocks ({type,text}), "content" arrays and "structuredContent".
function Get-ToolPassed($Response) {
    if ($null -eq $Response) { return $false }
    if ($Response -is [string]) {
        try { $parsed = $Response | ConvertFrom-Json } catch { return $false }
        if ($null -eq $parsed -or $parsed -is [string] -or $parsed -is [ValueType]) { return $false }
        return Get-ToolPassed $parsed
    }
    if ($Response -is [array]) {
        foreach ($item in $Response) { if (Get-ToolPassed $item) { return $true } }
        return $false
    }
    if ($Response -is [ValueType]) { return $false }
    $names = @($Response.PSObject.Properties | ForEach-Object { $_.Name })
    if ($names -contains 'passed') { return ($Response.passed -is [bool]) -and $Response.passed }
    if ($names -contains 'text') { return Get-ToolPassed ([string]$Response.text) }
    if ($names -contains 'structuredContent' -and (Get-ToolPassed $Response.structuredContent)) { return $true }
    if ($names -contains 'content') { return Get-ToolPassed @($Response.content) }
    return $false
}

# Computes the gate status of the repository for one session.
# Targets: dirty UI files whose hash differs from the session baseline (changed in this session).
# Unverified: targets whose path|hash has no passing winuse assertion recorded.
# Fingerprint: SHA1 of the sorted unverified path|hash lines ('' when none).
function Get-GateStatus([string]$Root, $Config, [string]$StateDir, [string]$SessionId) {
    $dirty = Get-DirtyUiFiles $Root $Config
    $session = Get-SessionState $StateDir $SessionId
    $verified = Get-VerifiedSet $StateDir
    $targets = @{}
    $unverified = @{}
    foreach ($path in $dirty.Keys) {
        $hash = $dirty[$path]
        if ($session.Baseline.ContainsKey($path) -and $session.Baseline[$path] -eq $hash) { continue }
        $targets[$path] = $hash
        if (-not $verified.Contains("$path|$hash")) { $unverified[$path] = $hash }
    }
    $fingerprint = ''
    if ($unverified.Count -gt 0) {
        $lines = Sort-Ordinal @($unverified.Keys | ForEach-Object { "$_|$($unverified[$_])" })
        $fingerprint = Get-Sha1 ($lines -join "`n")
    }
    return @{ Targets = $targets; Unverified = $unverified; Fingerprint = $fingerprint }
}
