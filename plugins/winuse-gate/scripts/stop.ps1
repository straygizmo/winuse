# Stop hook: blocks the end of a turn once per unverified change, asking Claude to verify
# UI changes with winuse instead of reporting them as done or handing the check to the user.
# Covers the cwd repository and every other repository (worktree) the session edited files in.
param([string]$DataDir)
. "$PSScriptRoot\gate-lib.ps1"

try {
    $hook = Read-HookInput
    if ($null -eq $hook) { exit 0 }
    if ($hook.stop_hook_active -eq $true) { exit 0 }
    $cwdRoot = Get-RepoRoot $hook.cwd
    $roots = Get-SessionRoots $DataDir $hook.session_id $cwdRoot
    if ($roots.Count -eq 0) { exit 0 }
    $index = Get-SessionIndex $DataDir $hook.session_id

    $newKeys = @()
    $paths = @()
    $broken = @()
    foreach ($root in $roots) {
        $config = Get-GateConfig $root
        if ($config.Status -eq 'broken') {
            $key = "broken|$root"
            if ($index.Blocked -notcontains $key) { $newKeys += $key; $broken += "$root\.claude\winuse-gate.json ($($config.Error))" }
            continue
        }
        if ($config.Status -ne 'ok') { continue }
        $status = Get-GateStatus $root $config (Get-StateDir $DataDir $root) $hook.session_id
        if ($status.Unverified.Count -eq 0) { continue }
        $key = "$root|$($status.Fingerprint)"
        if ($index.Blocked -contains $key) { continue }
        $newKeys += $key
        foreach ($p in (Sort-Ordinal @($status.Unverified.Keys))) {
            if ($root -ieq $cwdRoot) { $paths += $p } else { $paths += "$p (in $root)" }
        }
    }
    if ($newKeys.Count -eq 0) { exit 0 }
    $index.Blocked = @($index.Blocked) + $newKeys
    Save-SessionIndex $DataDir $hook.session_id $index

    $sections = @()
    if ($paths.Count -gt 0) {
        $shown = @($paths | Select-Object -First 5)
        $list = ($shown | ForEach-Object { "  - $_" }) -join "`n"
        if ($paths.Count -gt $shown.Count) { $list += "`n  - ... and $($paths.Count - $shown.Count) more" }
        $sections += @"
winuse-gate: $($paths.Count) UI file(s) changed in this session have not been verified with winuse:
$list

Look at your last message:
- If it reports the work as done, or asks the user to run, check or smoke-test it: do not hand that to the user. Verify it yourself with winuse (launch the app as described by "launch" in .claude/winuse-gate.json and follow the winuse-e2e skill) until at least one winuse assert_* call passes on the changed behavior, then report again. Keep the check proportional to the change.
- If it is a genuine question for the user, or a progress or blocked report, you may end the turn as it is.
- Ask the user only for subjective review (look and feel), only after the functional assertions pass, and attach a winuse screenshot.
- If verification is truly impossible, tell the user so explicitly and why.
"@
    }
    foreach ($b in $broken) {
        $sections += "winuse-gate: $b is invalid, so UI changes in that repository are not gated. Tell the user."
    }
    Write-Raw (@{ decision = 'block'; reason = ($sections -join "`n`n") } | ConvertTo-Json -Compress)
} catch {
    Write-GateError $_.Exception.Message
}
exit 0
