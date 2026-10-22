# winuse

winuse is an MCP server and CLI that lets an AI observe and operate Windows desktop apps (UI Automation, Java Swing via Java Access Bridge, WebView2/Playwright, input, windows, processes, files). Grounding and verification checks (`act`, `find`, `check`) use a System One decision model: TypeSafe Jev or Cloudflare Clef. Sessions can be saved as JSON scenarios and replayed without AI.

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

### Java Swing (Java Access Bridge)

Swing windows (class `SunAwtFrame` / `SunAwtDialog`) show UI Automation nothing but the frame. winuse reads them through Java Access Bridge (JAB) instead and returns the same kind of tree: `j{n}` refs that `get_ui_tree`, `act`, `find`, `click`, `invoke`, `set_text`, `set_checked`, `select_item`, `get_text`, `assert_exists` and scenario replay accept like `u{n}` refs. Menu items are listed while their menu is closed and `invoke` runs them without opening it. winuse implements the JAB wire protocol itself (no `WindowsAccessBridge-64.dll` is loaded or shipped): a hidden window talks to each JVM's Java-side bridge over window messages and a memory-mapped file, in the wide format of `JavaAccessBridge-32/64.dll` and Java 9+'s `javaaccessbridge.dll` or in the legacy format of the suffix-less `JavaAccessBridge.dll` that 32-bit Java 7/8 JREs ship. So the 64-bit winuse reads a 32-bit Java 7 or 8 as well as a 64-bit Java 21, several JVMs at once. The Java side of the bridge has shipped with the JRE since Java 7u6, and Java 7 reads the same `.accessibility.properties` and `-Djavax.accessibility.assistive_technologies` as Java 8+, so Java 7 apps are in scope too.

One thing must be in place: **accessibility enabled in the Java app's JVM**, which is read when the JVM starts (restart the app after changing it). Neither way touches the JRE:
   - Apps started with `process_run` get it automatically through `JAVA_TOOL_OPTIONS=-Djavax.accessibility.assistive_technologies=com.sun.java.accessibility.AccessBridge` (the JVM prints one `Picked up JAVA_TOOL_OPTIONS` line to stderr). `WINUSE_JAVA_ACCESSIBILITY=off` disables this.
   - For apps started any other way, `winuse jab enable` writes `%USERPROFILE%\.accessibility.properties` (what `jabswitch -enable` does; no admin rights). `winuse jab disable` / `winuse jab status` undo and show it.

`winuse doctor` reports the client window and the JVMs connected to it, whether the user profile enables accessibility, and every Java window with its JVM (`jvm.dll` path and version, bitness, which `JavaAccessBridge*.dll` it loaded and its wire format, the Java and bridge versions the JVM reports) and whether the bridge answers for it. When a Java window cannot be read, `get_ui_tree` returns `java_access_bridge` with the reason and `act` reports `no elements (java access bridge: ...)`; the window then falls back to visual grounding.

Settings: `WINUSE_JAB` (`auto`, default, or `off`), `WINUSE_JAVA_ACCESSIBILITY` (`on`, default, or `off`).

The Swing integration tests need Java under `tools\` (git-ignored): `pwsh -File scripts/fetch-dev-java.ps1` installs a Temurin 21 JDK x64 and a Temurin 8 JRE x86.

Limits: `get_table` does not read Swing tables yet. A JRE trimmed of `jdk.accessibility` (Java 8: `bin\JavaAccessBridge-32.dll` and `lib\ext\access-bridge-32.jar`) cannot be read at all, and **a JVM on such a JRE refuses to start when accessibility is requested** (`AWTError: Assistive Technology not found`): for apps on a trimmed JRE set `WINUSE_JAVA_ACCESSIBILITY=off` before `process_run`, and do not run `winuse jab enable`. `winuse doctor` points this out for every Java window whose JRE lacks the bridge files. A Java app running elevated or as another user is out of reach (its messages are blocked and the user-profile setting does not apply).

## Register with Claude Code

```powershell
claude mcp add winuse -- <repo>\out\winuse.exe --mcp
```

## CLI

```
winuse --mcp                                          MCP server over stdio
winuse run <scenario.json> [--heal] [--report <dir>]  replay a scenario without AI
winuse jab enable|disable|status                      Java accessibility for this user's JVMs (see "Java Swing")
winuse doctor                                         check the environment
winuse --version                                      print the version
```

`run` logs one line per step to stderr (`[PASS] 1 process_run (812ms)`, with ` p=0.81` appended for steps whose result has a probability, e.g. `check`), writes `report.json` and before/after screenshots to `--report <dir>`, and with `--heal` re-grounds `act` steps whose recorded element is gone with the decision model and saves updated locators next to the scenario as `<name>.healed.json` (e.g. `scenarios/mdium-smoke.json` → `scenarios/mdium-smoke.healed.json`; `scenario_run heal=true` uses the same name). It needs an interactive desktop session.

Exit codes: `0` PASS, `1` FAIL (an assertion failed, or a recorded element / `act` target can no longer be found), `2` error (bad scenario, tool error, no desktop session).

Replay never calls the decision model for `act`: each `act` step replays on its recorded `resolved` locator, and an `act` step without one fails (unless `--heal`). `scenario_save` therefore refuses `act` steps that did not execute. Only `check` steps (soft) ask the decision model during replay.

`doctor` reports the decision model in use, where each key came from (environment or `.env`), its endpoint and a live probe, UIA window count, Java Access Bridge status and Java windows, WebView2 runtime version and the Playwright driver.

## Tools

Element refs are `u{n}` (UI Automation), `j{n}` (Java Swing via Java Access Bridge), `w{n}` (WebView2 DOM), `win{n}` (window) and `pt{n}` (a screen point from visual grounding).

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
