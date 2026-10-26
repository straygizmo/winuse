# PostToolUse hook: records the current target files as verified when a winuse assertion
# or scenario run passes. Never blocks.
param([string]$DataDir)
. "$PSScriptRoot\gate-lib.ps1"

try {
    $hook = Read-HookInput
    if ($null -eq $hook) { exit 0 }
    if ([string]$hook.tool_name -notmatch '^mcp__.+__(assert_text|assert_exists|assert_window|assert_file|scenario_run)$') { exit 0 }
    if (-not (Get-ToolPassed $hook.tool_response)) { exit 0 }
    $root = Get-RepoRoot $hook.cwd
    if (-not $root) { exit 0 }
    $config = Get-GateConfig $root
    if ($config.Status -ne 'ok') { exit 0 }
    $stateDir = Get-StateDir $DataDir $root
    $status = Get-GateStatus $root $config $stateDir $hook.session_id
    Add-VerifiedEntries $stateDir $status.Targets
} catch {
    Write-GateError $_.Exception.Message
}
exit 0
