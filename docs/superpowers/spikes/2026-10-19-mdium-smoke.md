# Spike: MDium smoke scenario (phase 1 acceptance)

Date: 2026-10-19. Target: MDium (Tauri 2, UI in WebView2), `mdium/src-tauri/target/release/mdium.exe`, Japanese UI.
Driver: `scripts/mcp_batch.py` against `out/winuse.exe --mcp` (one server process per calls file).
Result: `scenarios/mdium-smoke.json` (16 steps) replays with `winuse run ... --report out/report` → exit 0, 16/16 PASS, twice in a row.

## Flow

`delete` (stale file) → `process_run(webview_debug, wait_window="MDium")` → `wait_for(element, role=button[name="新規ファイル"])`
→ `act "Create a new markdown file"` → `act "Type into the markdown editor" (type "winuse smoke")` → `act "Click the Save button"`
→ `wait_for(window "名前を付けて保存")` → `act "Type the file name"` (full path under `%TEMP%`) → `act "Click the Save button"` (dialog)
→ `wait_for(window_closed)` → `assert_text` (editor textarea) → `assert_file(contains)` → `check "Is the document saved?"` (soft)
→ `window_close` → `wait_for(window_closed)` → `delete` (cleanup).

## Measurements

- **CDP connection**: `process_run` including the window wait and the CDP connect took 0.75–1.4 s (7 launches); `cdp_connected=true` every time, one page (`http://tauri.localhost/`, title `MDium`).
- **DOM size**: `get_dom` (interactive) = 58 nodes once rendered; `interactive_only=false` = 72 nodes. Immediately after `process_run` the page is not rendered yet: `get_dom(interactive_only=false)` returned 0 and 29 nodes on two launches.
- **UIA**: `get_ui_tree(interactive_only=false)` = 35 nodes at the default depth 12 and 146 nodes at `max_depth=30`; interactive with `max_depth=20` = 86 nodes. The *first* `get_ui_tree` call on a fresh MDium window returned only the root node (twice, both with default and with `max_depth=30`); the second call returned the full tree. Chromium builds its UIA tree lazily on the first UIA client request.
- **Step time in replay**: 0.2–1.0 s per step, whole scenario ≈ 6 s.

## act grounding (probability / confidence)

All `act` steps executed above the 0.35 threshold; no low-level `click`/`set_text` fallback was needed. `verified` was always `false`.

| Intent | Target picked | probability / confidence |
|---|---|---|
| Create a new markdown file | `新規ファイル` button (UIA `u31` without prior `get_dom`, DOM `w82` after `get_dom`) | 0.93 / 0.93, 0.96 / 0.96, 1.0 / 1.0 |
| same, **before the explorer panel rendered** | wrong: `ファイル` sidebar button | 0.60 / 0.58 |
| Type into the markdown editor | DOM `textarea` | 0.78, 0.60, 0.99 (alt: UIA `Edit` 0.18) |
| Click the Save button (toolbar) | `保存` button (UIA or DOM) | 0.93, 1.0 |
| Type the file name (Save As dialog) | UIA `Edit` AutomationId `1001` `ファイル名:` | 1.0, 0.98 |
| Click the Save button (dialog) | UIA `保存(S)` (AutomationId `1`) | 0.80, 0.78, 0.75 (alt: MDium toolbar `保存` 0.19–0.21) |

`check("Is the document saved?")` returned 0.72 and 0.69 interactively (below the default `min_probability` 0.8 → would be WARN); in both replays the step reported PASS (≥ 0.8). The question is vague for a UI with no visible "saved" marker; the tab name `winuse-mdium-smoke.md` and the status-bar path are the only cues. `assert_file(contains)` is the real oracle.

Observation: whether `act` picks a UIA or a DOM candidate for the same button depends on whether `get_dom` ran earlier in the session (DOM refs are only candidates once the DOM was observed). Both replay fine.

## Problems and fixes

1. **Race at startup** — `wait_window="MDium"` matches the initial title before React renders the explorer; `act` then grounded the wrong button (0.60). Fix: `wait_for(condition=element, selector='role=button[name="新規ファイル"]')` before the first `act` (≈ 0.1–0.8 s).
2. **Title changes** — the window title goes from `MDium` to `MDium : <last folder>` after start. Recorded locators carried `^MDium : C:\Users\...\<folder>$`; the dispatcher's process/class fallback would cope, but the title also leaked a personal folder path into the scenario. Hand-edited every MDium `TitleRegex` to `^MDium( : .*)?$`.
3. **Save on an untitled tab opens the native Save As dialog** (`#32770`, title `名前を付けて保存`) whose initial directory is MDium's open folder (a user folder). Typing a full path into `ファイル名:` keeps the write inside `%TEMP%`. An existing target file would raise an overwrite confirmation, so the scenario deletes it first.
4. **Literal args** — `scenario_save` records literal values. Hand-edited: exe → `${MDIUM_EXE}` (with `env.MDIUM_EXE`), file path → `${TEMP}\winuse-mdium-smoke.md` (expanded from the process environment).
5. **Pollution** — none observed: MDium reopens its last folder with an empty `無題` tab; the saved temp file is not reopened. Its recent-files list (MDium data dir) gains the temp path. The scenario deletes the file at the end.
6. **Driver-side** — the shell used to write calls files collapsed `\` in JSON; the dialog received a broken file name and showed an error dialog (nothing was written). Calls files with Windows paths are now written as real JSON, not via shell heredocs.

## What the tools lack

- `report.json` (and the `run` stderr lines) do not include step results, so the `check` probability or `assert_text` actual value of a passing replay step is not visible; `run` also does not write the daily log file.
- `scenario_save` cannot parameterize values (env placeholders) or normalize volatile window titles; both needed hand edits.
- MDium has no `data-testid`/`aria-label` on the editor textarea, so its locator is an nth-child CSS path; any layout change breaks it (`--heal` only re-grounds `act` steps, not `assert_text`).
- `get_ui_tree` on a fresh WebView2 window returns only the root on the first call; a retry (or a warm-up in `process_run`) would help.
- `act` never reported `verified=true` for these steps, even when the effect (new tab, dialog opened, text typed) was clearly visible.
- The scenario depends on MDium's persisted state (a folder open, so the explorer's `新規ファイル` button exists) and on a Japanese UI (dialog title, button name selector).
