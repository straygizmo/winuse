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

Optional: `JEV_BASE_URL`, `JEV_MODEL`, `WINUSE_ALLOWED_ROOTS` (`;`-separated), `WINUSE_KILLABLE_PROCESSES` (`;`-separated), `WINUSE_SETTLE_MS` (wait before `act` verifies its result, default 400). Environment variables override `.env`.

## Register with Claude Code

```powershell
claude mcp add winuse -- <repo>\out\winuse.exe --mcp
```

## CLI

```
winuse --mcp                                          MCP server over stdio
winuse run <scenario.json> [--heal] [--report <dir>]  replay a scenario without AI
winuse doctor                                         check the environment
```

`run` logs one line per step to stderr (`[PASS] 1 process_run (812ms)`, with ` p=0.81` appended for steps whose result has a probability, e.g. `check`), writes `report.json` and before/after screenshots to `--report <dir>`, and with `--heal` re-grounds `act` steps whose recorded element is gone with Jev and saves updated locators next to the scenario as `<name>.healed.json` (e.g. `scenarios/mdium-smoke.json` → `scenarios/mdium-smoke.healed.json`; `scenario_run heal=true` uses the same name). It needs an interactive desktop session.

Exit codes: `0` PASS, `1` FAIL (an assertion failed, or a recorded element / `act` target can no longer be found), `2` error (bad scenario, tool error, no desktop session).

Replay never calls Jev for `act`: each `act` step replays on its recorded `resolved` locator, and an `act` step without one fails (unless `--heal`). `scenario_save` therefore refuses `act` steps that did not execute. Only `check` steps (soft) ask Jev during replay.

`doctor` reports `.env` detection, Jev configuration and a live probe, UIA window count, WebView2 runtime version and the Playwright driver.

## Tools

- Observe: `screenshot` (returns `origin {x, y}`: map an image pixel to `click(x, y)` with `screen_x = origin.x + pixel_x / scale`), `list_windows`, `get_ui_tree`, `get_dom`, `get_element`, `get_text`, `get_table`, `get_list_items`
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

## Driving winuse without an AI client

`scripts/mcp_batch.py` (Python 3.11+, stdlib only) runs a list of tool calls against one `winuse --mcp` process, so refs and step ids persist across calls:

```powershell
python scripts/mcp_batch.py --exe out\winuse.exe --calls calls.json --out out\batch
python scripts/mcp_batch.py --exe "dotnet run --project src/WinUse --" --calls calls.json
```

`calls.json` is an array of `{ "tool": "...", "args": { ... } }` (optionally `"stop_on_error": true`). Each result is printed as one JSON line `{"i", "tool", "result", "isError", "ms"}`; images are saved as `<out>/<i>-<tool>.png` and the server's stderr goes to `<out>/server.log`. An argument string `"$<i>.<path>"` (e.g. `"$7.target.ref"`) is replaced by that field of call `i`'s result. End the file with `scenario_save` to turn the session into a scenario.
