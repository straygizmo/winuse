# PostToolUse hook (Edit / Write / MultiEdit / NotebookEdit, async): remembers repositories the
# session edits outside its cwd repository (for example a worktree), so Stop and evidence cover them.
param([string]$DataDir)
. "$PSScriptRoot\gate-lib.ps1"

try {
    $hook = Read-HookInput
    if ($null -eq $hook) { exit 0 }
    $file = [string]$hook.tool_input.file_path
    if (-not $file) { $file = [string]$hook.tool_input.notebook_path }
    if (-not $file) { exit 0 }
    if (-not [System.IO.Path]::IsPathRooted($file)) { $file = Join-Path ([string]$hook.cwd) $file }
    $root = Get-RepoRoot (Split-Path -Parent $file)
    if (-not $root) { exit 0 }
    $cwdRoot = Get-RepoRoot ([string]$hook.cwd)
    if ($cwdRoot -and $root -ieq $cwdRoot) { exit 0 }
    Add-SessionRepo $DataDir $hook.session_id $root
} catch {
    Write-GateError $_.Exception.Message
}
exit 0
