# SessionStart hook: records the session baseline and reports a missing or invalid config.
param([string]$DataDir)
. "$PSScriptRoot\gate-lib.ps1"

try {
    $hook = Read-HookInput
    if ($null -eq $hook) { exit 0 }
    $root = Get-RepoRoot $hook.cwd
    if (-not $root) { exit 0 }
    $config = Get-GateConfig $root
    if ($config.Status -eq 'missing') {
        Write-HookContext 'SessionStart' 'winuse-gate is installed but this repository has no .claude/winuse-gate.json, so UI changes are not gated.'
        exit 0
    }
    if ($config.Status -eq 'broken') {
        Write-HookContext 'SessionStart' "winuse-gate: .claude/winuse-gate.json is invalid ($($config.Error)); the gate is off. Tell the user."
        exit 0
    }
    $stateDir = Get-StateDir $DataDir $root
    $session = Get-SessionState $stateDir $hook.session_id
    if (-not $session.Exists) {
        $session.Baseline = Get-DirtyUiFiles $root $config
        Save-SessionState $stateDir $hook.session_id $session
    }
} catch {
    Write-GateError $_.Exception.Message
}
exit 0
