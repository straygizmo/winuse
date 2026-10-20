# winuse

winuse is an MCP server and CLI that lets an AI observe and operate Windows desktop apps (UI Automation, WebView2/Playwright, input, windows, processes, files). Grounding and verification checks (`act`, `find`, `check`) use a System One decision model: TypeSafe Jev or Cloudflare Clef. Sessions can be saved as JSON scenarios and replayed without AI.

Requires Windows and the .NET 10 SDK.

## Build

```powershell
dotnet build winuse.sln
dotnet publish src/WinUse -c Release -o out
```

## Configuration

The intent tools (`act`, `find`, `check`) and `--heal` need one decision model. Everything else works without one. Configure either:

- **Jev** (TypeSafe): set `JEV_API_KEY`.
- **Clef** (Cloudflare Workers AI): set `CLOUDFLARE_ACCOUNT_ID` and `CLOUDFLARE_API_TOKEN` (an API token with Workers AI permission). Use this when you have no Jev key.

Register them once as user environment variables so that both Claude Code (which launches `winuse --mcp`) and `winuse run` see them:

```powershell
[Environment]::SetEnvironmentVariable('JEV_API_KEY', 'your-key', 'User')
# or
[Environment]::SetEnvironmentVariable('CLOUDFLARE_ACCOUNT_ID', 'your-account-id', 'User')
[Environment]::SetEnvironmentVariable('CLOUDFLARE_API_TOKEN', 'your-token', 'User')
```

Restart the terminal / Claude Code afterwards. As a development fallback, a `.env` file next to `winuse.exe` or in the working directory is also read (`KEY=VALUE` lines, never commit it); environment variables take precedence over `.env`.

The provider is chosen automatically: Jev when `JEV_API_KEY` is set, otherwise Clef when both Cloudflare values are set. `WINUSE_DECISION_PROVIDER=jev|clef` forces one. `winuse doctor` shows which one is in use.

Optional: `JEV_BASE_URL`, `JEV_MODEL` (default `jev-latest`), `CLEF_MODEL` (`clef`, the default, or the cheaper `clef-flash`), `CLEF_BASE_URL` (default `https://api.cloudflare.com`), `WINUSE_ALLOWED_ROOTS` (`;`-separated), `WINUSE_KILLABLE_PROCESSES` (`;`-separated), `WINUSE_SETTLE_MS` (wait before `act` verifies its result, default 400). A self-hosted Clef server that speaks `/v1/systemone` can be used as the Jev provider by pointing `JEV_BASE_URL` at it.

### Visual grounding (windows without UI elements)

Some windows expose no UI Automation or DOM elements at all: VirtualBox and other VM consoles, phone emulators, VNC / RDP viewers. For those, `act`, `find` and `check` fall back to a screenshot of the window:

- `act` / `find` locate the target on the screenshot and return a screen point (`point: {x, y}`) plus a `pt{n}` ref that `click` / `hover` / `drag` accept like any other ref. `act` clicks the point (or clicks and types for `kind=type`), then verifies with a fresh screenshot.
- `check` answers from the screenshot.
- The point is journaled as a fraction of the window rectangle, so a saved scenario replays it without the model even when the window has moved.

Settings:

- `WINUSE_SCREENSHOTS`: `auto` (default: screenshots are sent only for windows without elements), `always` (also next to the element list, which helps with ambiguous labels but costs tokens), `off` (never; `act` on such a window fails with `no elements (screenshots off)`).
- `WINUSE_VISUAL_LOCATOR`: `grid` (default, works with Jev and Clef: up to three rounds of "which labelled grid cell holds the target", zooming in each time) or `box` (one Jev 1.14 `box` question; needs `JEV_MODEL=jev-preview` or newer and falls back to `grid` on Clef).

Screenshots of the window are sent to the configured decision model. Jev requires the `TypeSafe-Danger-Zone-Images` opt-in header, which winuse adds automatically, and documents images and `box` as experimental.

## Register with Claude Code

```powershell
claude mcp add winuse -- <repo>\out\winuse.exe --mcp
```

## CLI

```
winuse --mcp                                          MCP server over stdio
winuse run <scenario.json> [--heal] [--report <dir>]  replay a scenario without AI
winuse doctor                                         check the environment
winuse --version                                      print the version
```

`run` logs one line per step to stderr (`[PASS] 1 process_run (812ms)`, with ` p=0.81` appended for steps whose result has a probability, e.g. `check`), writes `report.json` and before/after screenshots to `--report <dir>`, and with `--heal` re-grounds `act` steps whose recorded element is gone with the decision model and saves updated locators next to the scenario as `<name>.healed.json` (e.g. `scenarios/mdium-smoke.json` → `scenarios/mdium-smoke.healed.json`; `scenario_run heal=true` uses the same name). It needs an interactive desktop session.

Exit codes: `0` PASS, `1` FAIL (an assertion failed, or a recorded element / `act` target can no longer be found), `2` error (bad scenario, tool error, no desktop session).

Replay never calls the decision model for `act`: each `act` step replays on its recorded `resolved` locator, and an `act` step without one fails (unless `--heal`). `scenario_save` therefore refuses `act` steps that did not execute. Only `check` steps (soft) ask the decision model during replay.

`doctor` reports the decision model in use, where each key came from (environment or `.env`), its endpoint and a live probe, UIA window count, WebView2 runtime version and the Playwright driver.

## Tools

- Observe: `screenshot` (returns `origin {x, y}`: map an image pixel to `click(x, y)` with `screen_x = origin.x + pixel_x / scale`), `list_windows`, `get_ui_tree`, `get_dom`, `get_element`, `get_text`, `get_table`, `get_list_items`
- Intent: `act`, `find`, `check` (see "Visual grounding" above for windows without elements)
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
