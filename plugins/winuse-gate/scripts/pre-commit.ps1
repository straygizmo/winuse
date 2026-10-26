# PreToolUse hook (Bash / PowerShell): denies a git commit that would commit UI files changed in
# this session whose committed content has no passing winuse assertion recorded.
param([string]$DataDir)
. "$PSScriptRoot\gate-lib.ps1"

try {
    $hook = Read-HookInput
    if ($null -eq $hook) { exit 0 }
    $plan = Get-CommitPlan ([string]$hook.tool_input.command) ([string]$hook.cwd)
    if ($null -eq $plan -or $plan.Skip) { exit 0 }
    $root = Get-RepoRoot $plan.Dir
    if (-not $root) { exit 0 }
    $config = Get-GateConfig $root
    if ($config.Status -ne 'ok') { exit 0 }
    $stateDir = Get-StateDir $DataDir $root
    $session = Get-SessionState $stateDir $hook.session_id
    $verified = Get-VerifiedSet $stateDir
    if ($plan.Mode -eq 'staged') { $committed = Get-StagedUiFiles $root $config } else { $committed = Get-DirtyUiFiles $root $config }

    $violations = @()
    foreach ($path in (Sort-Ordinal @($committed.Keys))) {
        $hash = $committed[$path]
        if ($session.Baseline.ContainsKey($path) -and $session.Baseline[$path] -eq $hash) { continue }
        if (-not $verified.Contains("$path|$hash")) { $violations += $path }
    }
    if ($violations.Count -eq 0) { exit 0 }

    $shown = @($violations | Select-Object -First 5)
    $list = ($shown | ForEach-Object { "  - $_" }) -join "`n"
    if ($violations.Count -gt $shown.Count) { $list += "`n  - ... and $($violations.Count - $shown.Count) more" }
    $reason = @"
winuse-gate: this commit includes $($violations.Count) UI file(s) changed in this session that have not been verified with winuse:
$list

Verify the change with winuse first (follow the winuse-e2e skill) until at least one winuse assert_* call passes, then commit again.
Only if verification is truly impossible and you have told the user why, add "winuse-gate: skip <reason>" to the commit message.
"@
    Write-Raw (@{ hookSpecificOutput = @{ hookEventName = 'PreToolUse'; permissionDecision = 'deny'; permissionDecisionReason = $reason } } | ConvertTo-Json -Compress -Depth 5)
} catch {
    Write-GateError $_.Exception.Message
}
exit 0
