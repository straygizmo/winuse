# Stop hook: blocks the end of a turn once per unverified change, asking Claude to verify
# UI changes with winuse instead of reporting them as done or handing the check to the user.
param([string]$DataDir)
. "$PSScriptRoot\gate-lib.ps1"

try {
    $hook = Read-HookInput
    if ($null -eq $hook) { exit 0 }
    if ($hook.stop_hook_active -eq $true) { exit 0 }
    $root = Get-RepoRoot $hook.cwd
    if (-not $root) { exit 0 }
    $config = Get-GateConfig $root
    if ($config.Status -ne 'ok') { exit 0 }
    $stateDir = Get-StateDir $DataDir $root
    $status = Get-GateStatus $root $config $stateDir $hook.session_id
    if ($status.Unverified.Count -eq 0) { exit 0 }
    $session = Get-SessionState $stateDir $hook.session_id
    if ($session.Blocked -contains $status.Fingerprint) { exit 0 }
    $session.Blocked = @($session.Blocked) + $status.Fingerprint
    Save-SessionState $stateDir $hook.session_id $session

    $paths = Sort-Ordinal @($status.Unverified.Keys)
    $shown = @($paths | Select-Object -First 5)
    $list = ($shown | ForEach-Object { "  - $_" }) -join "`n"
    if ($paths.Count -gt $shown.Count) { $list += "`n  - ... and $($paths.Count - $shown.Count) more" }
    $reason = @"
winuse-gate: $($paths.Count) UI file(s) changed in this session have not been verified with winuse:
$list

Look at your last message:
- If it reports the work as done, or asks the user to run, check or smoke-test it: do not hand that to the user. Verify it yourself with winuse (launch the app as described by "launch" in .claude/winuse-gate.json and follow the winuse-e2e skill) until at least one winuse assert_* call passes on the changed behavior, then report again. Keep the check proportional to the change.
- If it is a genuine question for the user, or a progress or blocked report, you may end the turn as it is.
- Ask the user only for subjective review (look and feel), only after the functional assertions pass, and attach a winuse screenshot.
- If verification is truly impossible, tell the user so explicitly and why.
"@
    Write-Raw (@{ decision = 'block'; reason = $reason } | ConvertTo-Json -Compress)
} catch {
    Write-GateError $_.Exception.Message
}
exit 0
