# winuse

winuse is an MCP server and CLI that lets an AI observe and operate Windows desktop apps (UI Automation, WebView2/Playwright, input, windows, processes, files). Grounding and verification checks can use the Jev API. Sessions can be saved as JSON scenarios and replayed without AI.

Requires Windows and the .NET 10 SDK.

## Build

```powershell
dotnet build winuse.sln
dotnet publish src/WinUse -c Release -o out
```

## Configuration

Create a `.env` next to `winuse.exe` or in the working directory (never commit it):

```
JEV_API_KEY=your-key
```

Optional: `JEV_BASE_URL`, `JEV_MODEL`, `WINUSE_ALLOWED_ROOTS` (`;`-separated), `WINUSE_KILLABLE_PROCESSES` (`;`-separated). Environment variables override `.env`.

## Register with Claude Code

```powershell
claude mcp add winuse -- C:\Users\mtmar\source\repos\winuse\out\winuse.exe --mcp
```

## CLI

```
winuse --mcp                                          MCP server over stdio
winuse run <scenario.json> [--heal] [--report <dir>]  replay a scenario without AI
winuse doctor                                         check the environment
```

`run` logs one line per step to stderr (`[PASS] 1 process_run (812ms)`), writes `report.json` and before/after screenshots to `--report <dir>`, and with `--heal` saves updated locators to `<scenario>.healed.json`. It needs an interactive desktop session.

Exit codes: `0` PASS, `1` FAIL (an assertion failed), `2` error (bad scenario, tool error, no desktop session).

`doctor` reports `.env` detection, Jev configuration and a live probe, UIA window count, WebView2 runtime version and the Playwright driver.

## Tools

- Observe: `screenshot`, `list_windows`, `get_ui_tree`, `get_dom`, `get_element`, `get_text`, `get_table`, `get_list_items`
- Intent: `act`, `find`, `check`
- Input: `click`, `hover`, `drag`, `scroll`, `type_text`, `press_keys`
- Element: `set_text`, `select_item`, `set_checked`, `focus`, `invoke`
- Window: `window_activate`, `window_close`, `window_set_state`, `window_move`, `window_exists`
- Wait: `wait_for`
- Process: `process_run`, `process_kill`, `process_exists`
- File: `file_exists`, `read_text`, `write_text`, `append_text`, `copy`, `move`, `delete`, `list_dir`, `zip`, `unzip`
- Assert: `assert_text`, `assert_exists`, `assert_window`, `assert_file`
- Scenario: `scenario_save`, `scenario_list`, `scenario_run`

## Scenario JSON

```json
{
  "name": "notepad-smoke",
  "version": 1,
  "env": { "FILE": "C:\\Temp\\a.txt" },
  "steps": [
    { "tool": "write_text", "args": { "path": "${FILE}", "text": "hello" } },
    { "tool": "read_text", "args": { "path": "${FILE}" } },
    { "tool": "check", "args": { "question": "Is the file saved?" }, "soft": true, "min_probability": 0.8 }
  ]
}
```

`${NAME}` expands from `env`, then process environment variables. A step with `soft: true` only warns on failure. Steps may carry a `resolved` locator recorded at save time.
