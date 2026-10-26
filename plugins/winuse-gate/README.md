# winuse-gate

A Claude Code plugin that keeps Claude from reporting UI changes as done, or committing them, until it has verified them in the running desktop app with [winuse](../../README.md).

- **Turn end (Stop hook):** when UI files changed in this session have no passing winuse assertion, Claude's turn end is blocked once per change. If its last message reported the work as done, or asked you to smoke-test it, Claude goes and verifies it with winuse. A genuine question to you, or a progress report, ends the turn normally.
- **Commit (PreToolUse hook):** `git commit` is denied while it would commit UI files changed in this session that have no passing winuse assertion.
- **Evidence (PostToolUse hook):** a passing `assert_text`, `assert_exists`, `assert_window`, `assert_file` or `scenario_run` marks the current content of the changed UI files as verified. Clicks, `act`, `check` and screenshots do not count.

Only files whose content changed in this session count. Uncommitted edits that were already there when the session started are left alone until Claude changes them.

The gate covers the repository the session runs in and every other repository or worktree Claude edits files in with its Edit / Write tools (recorded by an async hook, so editing is not slowed down).

## Requirements

- Windows, Windows PowerShell 5.1 (built in) and git.
- winuse registered as an MCP server in Claude Code (see the main README: `claude mcp add winuse -- <repo>\out\winuse.exe --mcp`). The plugin does not start winuse itself.

## Install

```
/plugin marketplace add straygizmo/winuse
/plugin install winuse-gate@winuse
```

## Configure a project

Commit `.claude/winuse-gate.json` at the root of the repository you want to gate:

```json
{
  "ui": ["src/**/*.ts", "src/**/*.tsx", "src/**/*.css", "src-tauri/src/**/*.rs"],
  "exclude": ["**/*.test.ts", "**/*.test.tsx"],
  "launch": "Run `npm run tauri dev`. The window title matches ^MyApp. For the release build use process_run with webview_debug=true."
}
```

- `ui` (required): globs of the files that change what the user sees. `**/` matches any number of directories, `*` anything but `/`, `?` one character; matching ignores case.
- `exclude` (optional): globs removed from `ui`.
- `launch` (optional): free text that tells Claude how to start the app and find its window.

Without this file the gate does nothing in that repository; Claude is told so once at session start. An invalid file turns the gate off and Claude is told to report it.

## Skipping

When a change truly cannot be verified with winuse, Claude tells you why and may commit with `winuse-gate: skip <reason>` in the commit message. The reason stays in the git history.

## Limits

- Commit detection covers the usual forms (`git commit ...`, `git -C <dir> commit ...`, `cd <dir> && git commit ...`, `Set-Location <dir>; git commit ...`, `git add ... && git commit ...`). Aliases and scripts that commit are not gated.
- Commits that stage on their own (`-a`, `git add ... &&`, pathspecs such as `git commit -- <paths>`) are judged by every changed UI file in the work tree, so such a commit can be denied because of an unverified UI file it does not include.
- The skip marker is read from the command line only, not from `-F <file>` messages.
- Files changed in another repository through shell commands only (not the Edit / Write tools) are not noticed until Claude works in that repository.
- The gate checks that a verification happened, not that it was thorough.
- PowerShell must be allowed to run the hook scripts (`-ExecutionPolicy Bypass`, Full Language mode). Where a policy forbids that, the hooks only print an error and the gate is off.

State is kept under the plugin data directory, never inside your repository.
