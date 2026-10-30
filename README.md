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

Register them once as user environment variables so that both the AI client (which launches `winuse --mcp`) and `winuse run` see them:

```powershell
[Environment]::SetEnvironmentVariable('JEV_API_KEY', 'your-key', 'User')
# or
[Environment]::SetEnvironmentVariable('CLOUDFLARE_ACCOUNT_ID', 'your-account-id', 'User')
[Environment]::SetEnvironmentVariable('CLOUDFLARE_API_TOKEN', 'your-token', 'User')
```

Restart the terminal / AI client afterwards. As a development fallback, a `.env` file next to `winuse.exe` or in the working directory is also read (`KEY=VALUE` lines, never commit it); environment variables take precedence over `.env`.

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

`winuse doctor` reports the client window and the JVMs connected to it, whether the user profile enables accessibility, and every Java window with its JVM (`jvm.dll` path and version, bitness, which `JavaAccessBridge*.dll` it loaded and its wire format, the Java and bridge versions the JVM reports) and whether the bridge answers for it. When a Java window cannot be read, `get_ui_tree` returns `java_access_bridge` with the reason, `java_access_bridge_diagnostics` with the facts behind it, and `act` reports `no elements (java access bridge: ...)`; the window then falls back to visual grounding. The diagnostics say how far the conversation with that window's JVM got, so the caller can tell the cases apart without digging through logs:

| `stage` | What happened | What to do |
|---|---|---|
| `client_unavailable` | `WINUSE_JAB=off`, or the client window could not be created | see `reason` |
| `no_hello` | the JVM (`jvm_pid`) never announced itself to the client window; `bridge_module` says whether it has a `JavaAccessBridge*.dll` loaded at all | no DLL: accessibility was not enabled when the app started (`winuse jab enable` or `process_run`, then restart it); DLL loaded but silent: the app is elevated / in another session, or its bridge thread is stuck |
| `handshake_failed` | the JVM said hello but the memory-mapped-file handshake failed (`reason` names the step) | restart the app; check for an elevated app |
| `timeout` | the JVM is connected but did not answer a request within `timeout_ms` (4000) | the Java side is not processing bridge requests: its event thread is blocked or the process is suspended. With `blocked_by_action_at` set, an `invoke` sent at that time never returned: a Swing action that opens a modal dialog answers only when the dialog closes, so the dialog is up and holds the bridge; read it with `screenshot` or the UIA tree, operate it with `click` / `type_text` / `press_keys` or close it, and the Java tree is readable again |
| `send_failed` | the JVM is connected but the request could not be delivered | the JVM is exiting |
| `no_answer` | the JVM acknowledged the request but wrote no answer | wire format mismatch (`wire_format`, `wire_format_assumed`) |
| `not_owned` | the JVM answers but says the window is not one of its Java windows | not an AWT top-level window, or not shown yet |

The object also lists `connected_jvms` (pid, wire format, bitness and the last failed request of each) and `client_window`; it carries no window titles or element data. `winuse doctor` prints the same reason after `bridge NOT responding:`.

Swing tables: a `JTable` is one `Table` node in the tree (its cells are not walked in `interactive_only` mode, so a long list does not eat the node budget); `get_table` on its ref returns the header row and every cell, `max_rows` caps the rows and `row_count` / `column_count` tell the size of the table (an empty table gives `row_count: 0`); `include_header: false` reads no header cell at all, so `max_rows: 0, include_header: false` yields the counts without any cell text reaching the result or the log. A cell the bridge cannot hand out or describe is `null` (an empty cell is `""`) and `unreadable_cells` counts them, so a caller never mistakes a read failure for a blank. Scope `get_ui_tree` to the table ref to list the cells; the bridge reports no position for a cell, so cells carry the table's rectangle. The Java tree result says when it may be incomplete: `node_limit_reached: true` with `node_limit: 5000` when the walk stopped after 5000 bridge round trips, `depth_limit_reached: true` when an element at `max_depth` had children that were not walked, and `unreadable_nodes: n` when the bridge gave no handle or no info for n children (they are skipped). Narrow the walk with `scope`, `control_type` or `max_depth`, or treat "element absent" as unknown while any of these is set.
A Java tree always states all three (`false` / `0` when the walk was complete), and every `get_ui_tree` result carries `backend` (`java_access_bridge` or `uia`) and `server_version`, so a complete tree is distinguishable from the answer of an older server that did not report them.

Visibility: Swing reports `showing` for everything in a displayed hierarchy, including what a scroll pane has scrolled out of view or what lies beyond the window's edge after the window shrank (a fixed layout after a resolution change). winuse clips every Java element to its window and its `viewport` ancestors: `rect` is the part on screen, an element with no on-screen part is `offscreen: true` (listed only with `include_offscreen`, with its full rect so you know where to scroll) and `click` / `invoke` with `method: "mouse"` refuse it with the reason instead of clicking whatever is at those coordinates. The same check runs at click time, so a ref from before a resize is safe to use.

Actions: a Swing action runs on the JVM's event thread and the bridge answers when it returns. A button whose handler opens a **modal dialog** therefore does not answer until the dialog closes: the request times out after 4 s although the action ran, and every later bridge request to that JVM times out too while the dialog is up (`get_ui_tree` falls back to UIA, which shows a Swing dialog as an empty frame). `invoke` avoids this for visible push buttons by clicking them with the mouse (`method: "mouse"`; the window is brought to the front first and the click is refused when the point belongs to another process or the button is not on screen), and reports `method`, `action` and `outcome` in every case. When an action does time out the result is `outcome: "unknown"` with a `note`, `foreground` and `new_windows` (what the app opened), not an error: do not repeat it, drive the dialog with `screenshot` + `click` / `type_text` / `press_keys`, and prefer `method: "mouse"` for that button next time. `method: "action"` forces the accessibility action (what menu items, check boxes and tabs use anyway).

`set_text` on a Swing field reports `method: "accessible_text"` when `AccessibleEditableText` took the text (the field sees no key events; a key listener that upper-cases or validates on key does not run) or `method: "keyboard"` when the field was focused and the text typed; `method: "keyboard"` forces the latter when the field's behaviour depends on key events. The result never carries the text.

Settings: `WINUSE_JAB` (`auto`, default, or `off`), `WINUSE_JAVA_ACCESSIBILITY` (`on`, default, or `off`).

The Swing integration tests need Java under `tools\` (git-ignored): `pwsh -File scripts/fetch-dev-java.ps1` installs a Temurin 21 JDK x64 and a Temurin 8 JRE x86.

Limits: A JRE trimmed of `jdk.accessibility` (Java 8: `bin\JavaAccessBridge-32.dll` and `lib\ext\access-bridge-32.jar`) cannot be read at all, and **a JVM on such a JRE refuses to start when accessibility is requested** (`AWTError: Assistive Technology not found`): for apps on a trimmed JRE set `WINUSE_JAVA_ACCESSIBILITY=off` before `process_run`, and do not run `winuse jab enable`. `winuse doctor` points this out for every Java window whose JRE lacks the bridge files. A Java app running elevated or as another user is out of reach (its messages are blocked and the user-profile setting does not apply).

## Register with an AI client

winuse speaks MCP over stdio, so any MCP-capable client can launch `<repo>\out\winuse.exe --mcp`. Replace `<repo>` with the absolute path of this checkout (use `\\` in JSON/TOML strings). The decision-model keys are read from the environment, so register them as user environment variables (see Configuration) rather than per client.

### Claude Code

```powershell
claude mcp add winuse -- <repo>\out\winuse.exe --mcp
```

### Codex CLI

```powershell
codex mcp add winuse -- <repo>\out\winuse.exe --mcp
```

Or add it to `~\.codex\config.toml` (`.codex\config.toml` inside a project scopes it to that project):

```toml
[mcp_servers.winuse]
command = "<repo>\\out\\winuse.exe"
args = ["--mcp"]
```

### opencode

Add it to `~\.config\opencode\opencode.json` (global) or `opencode.json` in the project root:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "winuse": {
      "type": "local",
      "command": ["<repo>\\out\\winuse.exe", "--mcp"],
      "enabled": true
    }
  }
}
```

### GitHub Copilot CLI

In an interactive session run `/mcp add`, choose **Local**, enter `<repo>\out\winuse.exe --mcp` as the command and `*` as the tools, then press Ctrl+S. Or add it to `~\.copilot\mcp-config.json` (user-level) or `.mcp.json` in the project root:

```json
{
  "mcpServers": {
    "winuse": {
      "type": "local",
      "command": "<repo>\\out\\winuse.exe",
      "args": ["--mcp"],
      "tools": ["*"]
    }
  }
}
```

### Claude Code plugin: winuse-gate

[winuse-gate](plugins/winuse-gate/README.md) makes Claude verify its UI changes with winuse before it reports them as done or commits them, and ships the `winuse-operator` subagent that drives the app in its own context so UI trees and screenshots stay out of the main conversation. Install it with `/plugin marketplace add straygizmo/winuse` and `/plugin install winuse-gate@winuse`, then add `.claude/winuse-gate.json` to the project.

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

- Observe: `screenshot` (`target`: `screen` = the primary monitor or `monitor: <index>`, `virtual` = all monitors in one image, `window`, `element`; returns `origin {x, y}`, `method` and, for screen captures, `monitors`), `list_windows` (each window: `ref`, `title`, `process`, `pid`, `rect`, `visible_rect` = the frame actually drawn, without the invisible resize borders, `state`; a window keeps its `win{n}` ref for the session), `get_ui_tree` (its `window` object carries the same fields; `control_type`, `name`, `compact` and `max_nodes` cut the output down; `include_values: false` reads no field values), `get_dom`, `get_element`, `get_text`, `get_table` (UIA grids and Swing tables; `max_rows`, `include_header`, `row_count`, `column_count`, `omitted_rows`, `unreadable_cells`), `get_list_items`
- Intent: `act`, `find`, `check` (see "Visual grounding" above for windows without elements)
- Input: `click` (a ref click resolves the on-screen rectangle at click time and is refused, with the reason and nothing clicked, when the element has no on-screen part or the point lies on another process's window; `lands_on` names the window under the point), `hover`, `drag`, `scroll` (with `window`, x/y are relative to that window and it is activated first), `type_text`, `press_keys` (with `window`, that window is activated first so the keys cannot land elsewhere)
- Clipboard: `clipboard_get`, `clipboard_set`, `clipboard_set_image`, `paste_text`, `paste_image` (the way into rich editors such as CodeMirror or Monaco that UIA cannot fill: `paste_text` sets the clipboard, presses ctrl+v and restores the previous text; read an editor back with `press_keys "ctrl+a ctrl+c"` + `clipboard_get`)
- Element: `set_text` (reports `method`: `value_pattern` / `accessible_text` / `keyboard` / `fill`; `method: "keyboard"` or `"api"` forces one path; `secret: true` keeps the text out of the log and journal), `select_item` (clicks a combo box item in its open popup, as a user commits it, and reads the selection back: `item`, `selected`, `method`, `actions` and `outcome: "done"`; a read-back naming another item is an error, `outcome: "unknown"` with `note` when the container exposes no selection to read; `single_action: true` never falls back to the other path, so at most one input reaches the app), `set_checked` (reads the state back: `method`, `toggles`, `checked`, `outcome`; `single_action: true` toggles at most once and reports `outcome: "unknown"` instead of toggling again), `focus`, `invoke` (reports `method`, `action` and `outcome`: `done`, or `unknown` with `note`, `foreground` and `new_windows` when a Swing action opened a modal dialog and never answered; `method: "action"` / `"mouse"`)
- Window: `window_activate`, `window_close` (waits `timeout_ms`, default 2 s; `close_requested`, `closed`, `window_exists_after_request`, `process_alive`; when a save prompt or exit confirmation keeps the window open: `reason: "still_open"`, the prompt under `new_windows` (with a ref) and `foreground`, and a `hint`), `window_set_state`, `window_move`, `window_exists`
- Wait: `wait_for` (conditions `window`, `window_closed`, `element`, `text`, `enabled`, `time`), `sleep`
- Process: `process_run`, `process_kill`, `process_exists`
- File: `file_exists`, `read_text`, `write_text`, `append_text`, `copy`, `move`, `delete`, `list_dir`, `zip`, `unzip`
- Assert: `assert_text`, `assert_exists`, `assert_window`, `assert_file`
- Scenario: `scenario_save`, `scenario_list`, `scenario_run`

### Screenshots, coordinates and dialogs

- `screenshot` of a window copies it from the screen when the window, or a dialog it owns, is in the foreground (`method: "screen"`): the image is what the user sees, including modal save prompts and WebView2 overlays, and never a stale frame. A window that is not in the foreground is read with PrintWindow (`method: "print"`), which misses owned dialogs and can lag behind in Chromium apps; activate the window or pass `method: "screen"` to get the live picture, and `settle_ms` to let the app repaint after an input first.
- To click a pixel of a window screenshot, pass the same `window` and the pixel as `x, y` to `click` / `hover` / `scroll` / `drag`: the window rect is resolved at click time, so no origin arithmetic is needed. Without `window`, x/y are screen coordinates (`screen_x = origin.x + pixel_x / scale`).
- A modal dialog is an owned, usually untitled window: `list_windows` does not list it, but `get_ui_tree` of the owning window shows it as a focused child `Window` with its buttons, and `window_close` reports it under `foreground` (a titled one also under `new_windows`, with a ref).
- A title regex that matches nothing is retried for 1.5 s before a tool fails (single-page apps re-title themselves while they render). The `win{n}` ref that `wait_for` or `list_windows` returned is still the more reliable way to name a window.

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

A `set_text` / `type_text` / `paste_text` / `clipboard_set` step recorded with `secret: true` holds `"***"` instead of its text; replace it with `"${NAME}"` and provide `NAME` in `env` or the environment before replaying it, or the run stops at that step with `secret text was not recorded`.

### Secrets and the log

Every tool call is written to `%LOCALAPPDATA%\winuse\logs\winuse-{date}.log` with its arguments and the first 2000 characters of its result, and kept in the session journal that `scenario_save` writes out. For a password or token:

- pass `secret: true` to `set_text`, `type_text`, `paste_text` or `clipboard_set`: the text is used but the log, the journal and any saved scenario hold `"***"`;
- pass `include_values: false` to `get_ui_tree` when a form holds credentials: no field value is read, so none reaches the result or the log (`get_text` / `get_element` still read on request).

A Swing password field reports its echo characters, never the password; an `act` instruction that spells out a secret is logged as given, so use `set_text` with `secret: true` on the field ref instead.

## Driving winuse without an AI client

`scripts/mcp_batch.py` (Python 3.11+, stdlib only) runs a list of tool calls against one `winuse --mcp` process, so refs and step ids persist across calls:

```powershell
python scripts/mcp_batch.py --exe out\winuse.exe --calls calls.json --out out\batch
python scripts/mcp_batch.py --exe "dotnet run --project src/WinUse --" --calls calls.json
```

`calls.json` is an array of `{ "tool": "...", "args": { ... } }` (optionally `"stop_on_error": true`). Each result is printed as one JSON line `{"i", "tool", "result", "isError", "ms"}`; images are saved as `<out>/<i>-<tool>.png` and the server's stderr goes to `<out>/server.log`. An argument string `"$<i>.<path>"` (e.g. `"$7.target.ref"`) is replaced by that field of call `i`'s result. End the file with `scenario_save` to turn the session into a scenario.
