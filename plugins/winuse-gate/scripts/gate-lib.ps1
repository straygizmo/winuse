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
