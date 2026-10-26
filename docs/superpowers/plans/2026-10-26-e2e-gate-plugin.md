# winuse-gate E2E Gate Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a Claude Code plugin `winuse-gate` from the winuse repo that blocks "done" turn-ends and `git commit` while UI files changed in this session are not yet verified by a passing winuse assertion.

**Architecture:** Four PowerShell hook scripts (SessionStart, PostToolUse, Stop, PreToolUse) share one dot-sourced library. State lives under the plugin data dir, keyed by git toplevel; "verified" is decided per file by git blob id, never by time. Tests are xUnit tests in the existing test project that run the scripts with `powershell.exe` against throwaway git repos.

**Tech Stack:** Windows PowerShell 5.1, git, Claude Code plugin/hooks, xUnit (existing `tests/WinUse.Core.Tests`).

**Spec:** `docs/superpowers/specs/2026-10-26-e2e-gate-plugin-design.md`

## Global Constraints

- Scripts run on Windows PowerShell 5.1 (`powershell.exe`); do not require pwsh 7.
- Script files contain ASCII only (no BOM, no non-ASCII literals). Non-ASCII *data* (paths, messages) must still work.
- Hooks are exec form: `"command": "powershell.exe", "args": ["-NoProfile","-ExecutionPolicy","Bypass","-File","${CLAUDE_PLUGIN_ROOT}/scripts/<name>.ps1","-DataDir","${CLAUDE_PLUGIN_DATA}"]`.
- Fail open: any internal error (no git, not a repo, unreadable state) writes one line to stderr and exits 0 with no decision. Only the explicit gate conditions block.
- Never write inside the target repository. State goes under `-DataDir` (fallback: `$env:CLAUDE_PLUGIN_DATA`, then `$env:LOCALAPPDATA\winuse-gate`).
- Text shown to Claude (block reasons, additionalContext) is English.
- Each hook should finish in about 1 second on a normal repo.
- Plugin name `winuse-gate`; config file `.claude/winuse-gate.json` at the git toplevel; skip marker `winuse-gate: skip <reason>`.

## Review Focus

1. Non-ASCII file names (e.g. `src/画面.tsx`) and paths with spaces: must be detected, hashed and matched. Read git output as UTF-8 with `-z` and `core.quotepath=off`. Test in Task 2.
2. Non-ASCII hook stdin (Japanese `last_assistant_message`): stdin must be decoded as UTF-8 under PS 5.1. Test in Task 4.
3. Two PostToolUse hooks writing evidence at the same time (parallel winuse asserts): neither record may be lost and the file must stay valid JSON. Test in Task 3.
4. Repo with no commits yet (unborn HEAD), or cwd that is not a git repo: hooks pass silently. Test in Task 2 and Task 4.
5. Hook `cwd` in a subdirectory of the repo: config and paths resolve from the toplevel. Test in Task 2.

---

## File Structure

```
.claude-plugin/marketplace.json
plugins/winuse-gate/
  .claude-plugin/plugin.json
  hooks/hooks.json
  scripts/gate-lib.ps1          # shared: input, config, globs, git file hashes, state, gate status
  scripts/session-start.ps1     # baseline + config notices
  scripts/post-tool.ps1         # record evidence from winuse asserts / scenario_run
  scripts/stop.ps1              # turn-end gate (block once per fingerprint)
  scripts/pre-commit.ps1        # git commit gate
  skills/winuse-e2e/SKILL.md
  README.md
tests/WinUse.Core.Tests/Gate/
  GateFixture.cs                # temp git repo + script runner
  GateLibTests.cs
  SessionStartTests.cs
  PostToolTests.cs
  StopTests.cs
  PreCommitTests.cs
```

## Shared definitions (all tasks)

State layout under `<DataDir>/state/<sha1 of lowercased toplevel path>/`:
- `verified.json`: `{ "entries": [ { "path": "src/a.tsx", "hash": "<blob id or 'deleted'>" } ] }`, newest last, capped at 500 entries.
- `sessions/<session_id>.json`: `{ "baseline": { "<path>": "<hash>" }, "blocked": [ "<fingerprint>" ] }`. Missing file = baseline is HEAD (empty map), nothing blocked.

Hash of a file = git blob id (`git hash-object`), or the literal `deleted`. Paths are repo-relative with `/`.

---

### Task 1: Test harness, config loading and glob matching

**Files:**
- Create: `plugins/winuse-gate/scripts/gate-lib.ps1`
- Create: `tests/WinUse.Core.Tests/Gate/GateFixture.cs`, `tests/WinUse.Core.Tests/Gate/GateLibTests.cs`

**Interfaces:**
- Produces (C#): `sealed class GateFixture : IDisposable` with `string Repo` (temp dir, `git init`, user.name/email set, one initial commit containing `README.md`), `string DataDir`, `void Write(string relPath, string text)` (UTF-8, creates dirs), `void Git(params string[] args)`, `void Commit(string msg)` (add -A + commit), `void WriteConfig(string json)`, `HookResult RunHook(string script, object stdin)` (serializes stdin to JSON UTF-8, runs `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <scriptsDir>\<script> -DataDir <DataDir>` with cwd = Repo, 30 s timeout), `string RunLib(string psExpression)` (dot-sources gate-lib.ps1 then evaluates the expression, returns stdout trimmed). `record HookResult(int ExitCode, string Stdout, string Stderr)` with `JsonNode? Json` (stdout parsed when non-empty). Scripts dir is found by walking up from `AppContext.BaseDirectory` to the folder containing `winuse.sln`.
- Produces (PS, gate-lib.ps1):
  - `Read-HookInput` -> `[pscustomobject]` (stdin read as UTF-8; `$null` on empty)
  - `Get-RepoRoot([string]$Cwd)` -> toplevel path or `$null` (not a repo / git missing)
  - `Get-GateConfig([string]$Root)` -> `@{ Status = 'missing'|'broken'|'ok'; Ui = [regex[]]; Exclude = [regex[]]; Error = [string] }` (`ui` must be a non-empty string array, else `broken`)
  - `Convert-GlobToRegex([string]$Glob)` -> `[regex]` (case-insensitive; `**/` = zero or more dirs, `*` = no `/`, `?` = one non-`/` char, anchored)
  - `Test-UiPath([string]$Path, $Config)` -> `[bool]`

- [ ] **Step 1: Write failing tests** in `GateLibTests.cs`:
  - `Glob_DoubleStar_MatchesAnyDepth`: `src/**/*.tsx` matches `src/a.tsx`, `src/x/y/a.tsx`; not `lib/a.tsx`, not `src/a.ts`.
  - `Glob_Star_DoesNotCrossSlash`: `src/*.ts` matches `src/a.ts`, not `src/x/a.ts`.
  - `Glob_IsCaseInsensitive`: `src/**/*.TSX` matches `src/A.tsx`.
  - `UiPath_ExcludeWins`: config `ui: ["src/**/*.tsx"], exclude: ["**/*.test.tsx"]`; `src/a.test.tsx` -> false, `src/a.tsx` -> true.
  - `Config_Missing_Broken_Ok`: no file -> `missing`; `{` -> `broken`; `{"ui":[]}` -> `broken`; `{"ui":["src/**"]}` -> `ok`.
  - `RepoRoot_FromSubdirectory_ReturnsToplevel` and `RepoRoot_OutsideRepo_ReturnsNull` (Review Focus 5).
- [ ] **Step 2: Run** `dotnet test tests/WinUse.Core.Tests --filter FullyQualifiedName~Gate` — expect FAIL (script missing).
- [ ] **Step 3: Implement** `GateFixture` and the five functions above. Set `[Console]::InputEncoding`/`OutputEncoding` to UTF-8 at the top of gate-lib.ps1.
- [ ] **Step 4: Run** the same filter — expect PASS.
- [ ] **Step 5: Commit** `feat(gate): plugin library config and glob matching`.

### Task 2: UI file hashes, baseline and gate status

**Files:**
- Modify: `plugins/winuse-gate/scripts/gate-lib.ps1`
- Create: `plugins/winuse-gate/scripts/session-start.ps1`
- Test: `GateLibTests.cs`, `SessionStartTests.cs`

**Interfaces:**
- Consumes: Task 1 functions.
- Produces (PS):
  - `Get-DirtyUiFiles([string]$Root, $Config)` -> `[hashtable]` path -> hash for every UI file that differs from HEAD (modified, added, deleted, untracked-not-ignored). Use `git -c core.quotepath=off status --porcelain=v1 -z --untracked-files=all --no-renames` and one batched `git hash-object -- <paths>`. Unborn HEAD: every existing UI file counts as dirty.
  - `Get-StateDir([string]$DataDir, [string]$Root)` -> path (created).
  - `Read-JsonFile([string]$Path, $Default)` / `Write-JsonFile([string]$Path, $Object)` (write to temp file then `Move-Item -Force`; retry up to 10 times on IO error).
  - `Get-SessionState($StateDir, $SessionId)` / `Save-SessionState(...)`, `Get-VerifiedSet($StateDir)` -> `HashSet[string]` of `"path|hash"`.
  - `Get-GateStatus([string]$Root, $Config, [string]$StateDir, [string]$SessionId)` -> `@{ Targets = [hashtable]; Unverified = [hashtable]; Fingerprint = [string] }`. Targets = dirty files whose hash differs from `baseline[path]` (absent baseline entry = not equal). Unverified = targets whose `"path|hash"` is not in the verified set. Fingerprint = SHA1 of sorted `"path|hash"` lines of Unverified (empty string when none).
- session-start.ps1 behavior: no repo -> exit 0 silently. Config `missing` -> stdout `{"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":"winuse-gate is installed but this repository has no .claude/winuse-gate.json, so UI changes are not gated."}}`. Config `broken` -> same shape, text `winuse-gate: .claude/winuse-gate.json is invalid (<Error>); the gate is off. Tell the user.` Config `ok` and no session file yet -> write `baseline` = current `Get-DirtyUiFiles`; existing session file (resume/compact) is left untouched.

- [ ] **Step 1: Write failing tests**:
  - `Dirty_DetectsModifiedAddedDeletedUntracked`: commit `src/a.tsx`, `src/b.tsx`; modify a, delete b, add untracked `src/c.tsx`, add ignored `src/d.tsx` (in `.gitignore`) -> keys exactly a, b, c; b's hash = `deleted`.
  - `Dirty_NonAsciiAndSpaces`: untracked `src/画面 1.tsx` is reported with that exact path and its blob id equals `git hash-object` (Review Focus 1).
  - `Dirty_UnbornHead_CountsAllUiFiles` (Review Focus 4).
  - `SessionStart_RecordsBaseline_UserEditsNotTargets`: dirty `src/a.tsx` before SessionStart; after it, `Get-GateStatus` has no targets; edit a again -> a is a target and unverified.
  - `SessionStart_ResumeKeepsBaseline`: second SessionStart with same session_id after editing a does not overwrite baseline.
  - `SessionStart_MissingConfig_EmitsNotice` and `SessionStart_BrokenConfig_EmitsNotice`: `Json["hookSpecificOutput"]["additionalContext"]` contains `no .claude/winuse-gate.json` / `is invalid`.
  - `Status_Fingerprint_ChangesWithContent`: two different edits of a give different fingerprints; same content gives the same.
- [ ] **Step 2: Run** filter `~Gate` — expect FAIL.
- [ ] **Step 3: Implement** the functions and `session-start.ps1` (input fields used: `session_id`, `cwd`).
- [ ] **Step 4: Run** filter `~Gate` — expect PASS.
- [ ] **Step 5: Commit** `feat(gate): session baseline and per-file verification status`.

### Task 3: Evidence recording (PostToolUse)

**Files:**
- Create: `plugins/winuse-gate/scripts/post-tool.ps1`
- Modify: `gate-lib.ps1` (add `Get-ToolPassed`)
- Test: `PostToolTests.cs`

**Interfaces:**
- Produces (PS): `Get-ToolPassed($ToolResponse)` -> `$true` only when a `passed` field equal to boolean `true` is found. Accept every shape: a JSON string; an object with `passed`; an array of content blocks (`{type:"text", text:"<json>"}`); an object with `content` (array) or `structuredContent`. Anything else -> `$false`.
- post-tool.ps1: if `tool_name` matches `^mcp__.+__(assert_text|assert_exists|assert_window|assert_file|scenario_run)$` and `Get-ToolPassed(tool_response)`, append every current target (`Get-GateStatus().Targets`) as `{path, hash}` to `verified.json` (dedupe, cap 500, newest last). Never outputs a decision; always exit 0.

- [ ] **Step 1: Write failing tests**:
  - `Passed_Shapes`: for each of `"{\"step_id\":\"s1\",\"passed\":true}"`, `{passed:true}`, `[{type:"text",text:"{\"passed\":true}"}]`, `{content:[{type:"text",text:"{\"passed\":true}"}]}` -> true; `{passed:false}`, `{passed:"true"}`, `{}` -> false.
  - `AssertPass_MarksTargetsVerified`: after SessionStart, edit `src/a.tsx`, run post-tool with `tool_name = "mcp__winuse__assert_text"` and passing response -> `Get-GateStatus().Unverified` empty; edit a again -> a unverified.
  - `PluginPrefixedToolName_Counts`: `mcp__plugin_x_winuse__assert_exists` also records.
  - `FailedAssert_Or_Click_DoesNotRecord`: `passed:false`, and `mcp__winuse__click` with any response -> still unverified.
  - `ScenarioRunPass_Records`: `mcp__winuse__scenario_run` with `{passed:true, exit_code:0}` -> verified.
  - `ConcurrentRecords_BothKept`: two post-tool runs started in parallel, each after editing a different file -> both files verified and `verified.json` parses (Review Focus 3).
- [ ] **Step 2: Run** filter `~Gate` — expect FAIL.
- [ ] **Step 3: Implement** `Get-ToolPassed` and `post-tool.ps1`. Guard concurrent writers with a named mutex `Global\winuse-gate-<state dir sha1>` around read-modify-write.
- [ ] **Step 4: Run** filter `~Gate` — expect PASS.
- [ ] **Step 5: Commit** `feat(gate): record winuse assertion evidence`.

### Task 4: Turn-end gate (Stop)

**Files:**
- Create: `plugins/winuse-gate/scripts/stop.ps1`
- Test: `StopTests.cs`

**Interfaces:**
- Consumes: `Get-GateStatus`, session state.
- stop.ps1 order: `stop_hook_active` true -> exit 0. No repo / config not `ok` -> exit 0. No unverified -> exit 0. Fingerprint already in `blocked` -> exit 0. Else add fingerprint to `blocked`, save, and print `{"decision":"block","reason": <text>}`.
- Reason text (exact wording is the builder's; it must contain all of these points):
  1. `winuse-gate:` prefix, the count of unverified UI files and up to 5 paths.
  2. If your last message reports the work as done, or asks the user to run/check/smoke-test it, do not hand that to the user: verify it yourself with winuse (launch per `launch` in `.claude/winuse-gate.json`, follow the `winuse-e2e` skill) until at least one winuse `assert_*` passes, then report again. Keep the check proportional to the change.
  3. If your last message is a genuine question for the user, or a progress or blocked report, you may end the turn as is.
  4. Ask the user only for subjective review (look and feel), only after the functional assertions pass, and attach a winuse `screenshot`.
  5. If verification is truly impossible, say so and why to the user explicitly.

- [ ] **Step 1: Write failing tests**:
  - `Unverified_BlocksOnce_ThenPasses`: edit `src/a.tsx`; first Stop -> `Json["decision"] == "block"` and `reason` contains `src/a.tsx` and `assert_`; second Stop with same content and `stop_hook_active=false` -> no output, exit 0.
  - `NewContent_BlocksAgain`: after the above, edit a again -> blocks.
  - `StopHookActive_Passes`: `stop_hook_active=true` with unverified file -> no output.
  - `Verified_Passes`: record passing assert, then Stop -> no output.
  - `NonUiChange_Passes`: edit only `docs/x.md` -> no output.
  - `NonAsciiStdin_Ok`: `last_assistant_message` = `"実装しました。スモークをお願いします"` -> still blocks and exit 0 (Review Focus 2).
  - `NotARepo_Passes`: cwd = empty temp dir -> exit 0, no output (Review Focus 4).
- [ ] **Step 2: Run** filter `~Gate` — expect FAIL.
- [ ] **Step 3: Implement** `stop.ps1`.
- [ ] **Step 4: Run** filter `~Gate` — expect PASS.
- [ ] **Step 5: Commit** `feat(gate): block unverified turn ends once per change`.

### Task 5: Commit gate (PreToolUse)

**Files:**
- Create: `plugins/winuse-gate/scripts/pre-commit.ps1`
- Modify: `gate-lib.ps1` (add `Get-CommitPlan`)
- Test: `PreCommitTests.cs`

**Interfaces:**
- Produces (PS): `Get-CommitPlan([string]$Command, [string]$Cwd)` -> `$null` when the command is not a git commit, else `@{ Dir = <cwd, or cwd joined with the -C value>; Mode = 'staged'|'worktree'; Skip = [bool] }`. A commit is `git` followed by any of `-C <p>` / `-c <k=v>` and then `commit` (match anywhere in the command, so `cd x && git commit ...` works). `Mode='worktree'` when the commit args contain `-a`, `--all`, a short-flag cluster containing `a` (`-am`), `-o`/`--only`/`-i`/`--include`, or a `--` pathspec; otherwise `staged`. `Skip` when the command contains `winuse-gate:\s*skip\s+\S`.
- pre-commit.ps1: input `tool_input.command`. Not a commit / skip / no repo / config not ok -> exit 0. Committed UI files: `staged` = `git diff --cached --name-only -z --no-renames` filtered by `Test-UiPath`, hashed from the index (`git ls-files -s`; absent = `deleted`); `worktree` = `Get-DirtyUiFiles`. A committed file violates when its committed hash differs from `baseline[path]` and `"path|hash"` is not verified. Any violation -> print `{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason": <text>}}` naming up to 5 files, saying to verify with winuse first, and that `winuse-gate: skip <reason>` in the message is allowed only when verification is impossible and the user was told.

- [ ] **Step 1: Write failing tests**:
  - `CommitPlan_Parsing`: `git commit -m "x"` -> staged; `git -C sub commit -am "x"` -> worktree, Dir ends with `sub`; `cd a && git commit --all -m x` -> worktree; `git status` -> null; `git commit -m "y\n\nwinuse-gate: skip no display"` -> Skip.
  - `StagedUnverifiedUi_Denied`: edit+stage `src/a.tsx` -> `permissionDecision == "deny"`, reason contains `src/a.tsx`.
  - `NonUiOnlyCommit_Allowed`: `src/a.tsx` edited but unstaged, stage only `docs/x.md`, `git commit -m x` -> no output.
  - `VerifiedStaged_Allowed`: stage a, record passing assert -> no output.
  - `StagedContentDiffersFromVerified_Denied`: verify a, stage a, edit a again, stage -> deny.
  - `UserBaselineFile_Allowed`: dirty a before SessionStart, stage unchanged a -> no output.
  - `Skip_Allowed`.
- [ ] **Step 2: Run** filter `~Gate` — expect FAIL.
- [ ] **Step 3: Implement** `Get-CommitPlan` and `pre-commit.ps1`.
- [ ] **Step 4: Run** filter `~Gate` — expect PASS.
- [ ] **Step 5: Commit** `feat(gate): deny commits of unverified UI files`.

### Task 6: Plugin packaging, skill and docs

**Files:**
- Create: `.claude-plugin/marketplace.json`, `plugins/winuse-gate/.claude-plugin/plugin.json`, `plugins/winuse-gate/hooks/hooks.json`, `plugins/winuse-gate/skills/winuse-e2e/SKILL.md`, `plugins/winuse-gate/README.md`
- Modify: `README.md` (short "Claude Code plugin: winuse-gate" section linking the plugin README), `CHANGELOG.md`

**Interfaces:**
- marketplace.json: `name` `winuse`, `owner.name` `straygizmo`, one plugin `{ "name": "winuse-gate", "source": "./plugins/winuse-gate", "description": ... }`. plugin.json `name` `winuse-gate` (must equal the entry name).
- hooks.json (top-level `"hooks"` key), all exec form per Global Constraints:
  - `SessionStart` -> session-start.ps1
  - `PostToolUse` matcher `mcp__.*__(assert_text|assert_exists|assert_window|assert_file|scenario_run)` -> post-tool.ps1
  - `Stop` -> stop.ps1
  - `PreToolUse` matcher `Bash` with `"if": "Bash(git *)"`, and matcher `PowerShell` with `"if": "PowerShell(git *)"` -> pre-commit.ps1 (the script decides whether it is a commit).
  - `timeout` 30 on every hook.
- SKILL.md (`name: winuse-e2e`, description says to use it when verifying UI changes in a desktop app or when winuse-gate blocks): content per spec §4.7 — read `launch` from the config, proportional checks, clicks are not evidence (an `assert_*` must pass), never hand functional checks to the user, subjective review only with a screenshot after asserts, `scenario_save` for reusable checks, `winuse-gate: skip` only when impossible and told to the user.
- Plugin README (Japanese, matching the repo's docs): prerequisites (winuse registered as an MCP server), install (`/plugin marketplace add straygizmo/winuse`, `/plugin install winuse-gate@winuse`), config file format with an example, what is gated and how to skip, known limits (commit detection covers normal forms only; `-F` message files are not read for the skip marker; hook `cwd` decides the repo).

- [ ] **Step 1: Write** the files above.
- [ ] **Step 2: Run** `claude plugin validate .` and `claude plugin validate ./plugins/winuse-gate` — expect `✔ Validation passed` for both.
- [ ] **Step 3: Run** the whole test project `dotnet test tests/WinUse.Core.Tests --filter FullyQualifiedName~Gate` — expect PASS; and confirm the scripts are ASCII: `Select-String -Path plugins/winuse-gate/scripts/*.ps1 -Pattern '[^\x00-\x7F]'` prints nothing.
- [ ] **Step 4: Commit** `feat(gate): package winuse-gate plugin with hooks and skill`.

### Task 7: Install locally and trial on MDium

This task needs the user's desktop; the implementer prepares it and the trial items are run with the user.

**Files:**
- Create in MDium (`C:\Users\mtmar\source\repos\mdium`, on a work branch, not main): `.claude/winuse-gate.json` with `ui` covering `src/**/*.ts`, `src/**/*.tsx`, `src/**/*.css`, `src-tauri/src/**/*.rs`, `exclude` covering `**/*.test.ts`, `**/*.test.tsx`, `**/__tests__/**`, and `launch` describing `npm run tauri dev`, window title `^MDium`, and `process_run` with `webview_debug=true` for the built exe.
- Modify: spec `docs/superpowers/specs/2026-10-26-e2e-gate-plugin-design.md` (append "試運転の結果").

- [ ] **Step 1:** `claude plugin marketplace add C:\Users\mtmar\source\repos\winuse` then `claude plugin install winuse-gate@winuse`; `claude plugin details winuse-gate` lists 4 hook events and the skill.
- [ ] **Step 2:** Add the MDium config and commit it on the work branch.
- [ ] **Step 3:** With the user, run the spec §7 trial items (one-word UI text fix passes with one `assert_text`; a genuine question ends the turn; a "please smoke test" ending gets blocked and Claude verifies; commit deny and `winuse-gate: skip`; the once-per-change loophole; pre-existing user edits do not block). Also confirm `PreToolUse` `if: "Bash(git *)"` fires for `cd x && git commit`.
- [ ] **Step 4:** Append findings to the spec and commit in winuse.
