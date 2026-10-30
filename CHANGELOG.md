# Changelog

## Unreleased

Added

- winuse-gate plugin: the `winuse-operator` subagent (`plugins/winuse-gate/agents/winuse-operator.md`). The main conversation hands it the window and the goal; it reads trees and screenshots, drives the app and runs the assertions in its own context, and returns a short text report, so the main context is not filled with UI dumps. Its passing assertions count for the gate. The `winuse-e2e` skill now delegates the driving to it. Plugin version 0.2.0.

## 0.2.6 - 2026-10-30

Added

- `invoke` reports how it acted: `method` (`action`, `mouse`, `selection`), the accessibility `action` name when one ran, and `outcome`: `done` when the target confirmed it, `unknown` when the request was delivered but never answered. On a Swing element that is the signature of an action that opened a modal dialog (it returns only when the dialog closes), so the result then carries `note`, `foreground` and `new_windows` (the windows the app opened meanwhile) instead of a misleading `Action 'click' failed`. `method: "action"` / `"mouse"` force one path.
- `set_text` reports `method`: `value_pattern` (UIA) or `accessible_text` (Java) when the accessibility API took the text, `keyboard` when the field was focused and the text typed (key listeners such as upper-casing run), `fill` for the DOM. `method: "keyboard"` forces typing, `method: "api"` fails instead of falling back. The text itself is never in the result.
- `window_close` separates the request from the effect: `close_requested`, `closed`, `window_exists_after_request`, `process_alive` (when the window is gone), `new_windows` (windows the same process opened after the request, with refs: the exit confirmation or save prompt), `foreground` and a `hint` saying how to answer the prompt and wait for the window.
- `get_ui_tree` carries `backend` (`java_access_bridge` or `uia`) and `server_version` in every result, and a Java tree always states `node_limit_reached`, `depth_limit_reached` and `unreadable_nodes` (`false` / `0` when the walk was complete), so a complete tree from this version is distinguishable from an older server that did not report them.
- Java elements are clipped to the on-screen area their window and scroll viewports leave: `rect` is the visible part, an element scrolled out of view or lying beyond the window's edge (a layout that did not follow a resolution change) is `offscreen: true`, listed only with `include_offscreen` and with its full rect so a caller knows where to scroll. `click`, `invoke` and `get_element` resolve the same visibility at call time: a click on such an element is refused with the reason and nothing is clicked.
- `click` on a ref checks, before clicking, that the resolved point lies on a window of the process that owns the ref (popups and dialogs of the same process count); a point on another process's window (the taskbar or the Start button after the screen shrank, a window covering the target) or on no window fails with the reason, and the result names the window under the point as `lands_on`. Coordinates (`x`, `y`) are clicked as given.
- `java_access_bridge_diagnostics` carries `blocked_by_action_at` and the `timeout` reason explains it when an `invoke` that never returned is what blocks the JVM's bridge: a modal dialog is up, read it with a screenshot or the UIA tree, operate or close it, and the Java tree is readable again.

Changed

- `invoke` on a visible Swing push button clicks it with the mouse instead of running its `click` action (after bringing the window to the front and checking that the point belongs to the app). A button's action runs on the JVM's event thread and answers only when it returns, so a button that opens a modal dialog held the Java Access Bridge for the whole life of the dialog, which then could only be driven blind. Menu items, check boxes, tabs and everything else keep using the accessibility action; `method: "action"` restores it for buttons, and the mouse path falls back to the action when the button is not on screen or covered.

## 0.2.5 - 2026-10-30

Added

- `get_table` takes `include_header: false`: the column header is never read (no bridge call, nothing in the result or the log) and `rows` holds data rows only. The result always carries `column_count`, so `max_rows: 0, include_header: false` returns the table size alone.
- `get_table` tells a read failure from an empty cell: a cell the Java Access Bridge (or UIA) could not hand out or describe is `null` instead of `""`, and `unreadable_cells` counts them.
- `get_ui_tree` on a Java window reports `depth_limit_reached: true` when an element at `max_depth` had children that were not walked, and `unreadable_nodes: n` when the bridge gave no handle or no context info for n children (they are skipped, as before, but no longer silently). Together with `node_limit_reached` these say when an element that exists may be missing from the tree.

## 0.2.4 - 2026-10-30

Added

- `get_table` reads Swing tables (`JTable`) through the Java Access Bridge table interface: header row first, then every cell (text, numbers, boolean cells as `true` / `false`); `max_rows` caps the data rows and the result carries `row_count` and `omitted_rows` for UIA grids as well. An empty table gives `row_count: 0`.
- `get_ui_tree` on a Java window the bridge cannot read returns `java_access_bridge_diagnostics` next to the `java_access_bridge` reason: `stage` (`client_unavailable`, `no_hello`, `handshake_failed`, `timeout`, `send_failed`, `no_answer`, `not_owned`), `jvm_pid`, `jvm_connected`, `bridge_module`, `wire_format`, `bitness`, `client_window`, `timeout_ms` and `connected_jvms` with the last failed request of each. The reason string names the JVM pid and the step that failed instead of a generic "not responding"; `winuse doctor` prints the same reason.
- `get_ui_tree` reports `node_limit_reached: true` and `node_limit: 5000` when the Java tree walk stopped at its internal bridge round-trip limit (previously only a log warning).
- `get_ui_tree` takes `include_values: false`: no element value is read, so field contents stay out of the result and the log.
- `set_text`, `type_text`, `paste_text` and `clipboard_set` take `secret: true`: the text is used but the log, the journal and saved scenarios hold `"***"`. Replaying such a step fails with `secret text was not recorded` until the scenario names the value as `${NAME}`.

Changed

- A Swing table is one `Table` node in the interactive tree; its cells are no longer listed there (read them with `get_table`, or scope the tree to the table ref, where they appear with the table's rectangle since the bridge reports no position for a cell).

## 0.2.3 - 2026-10-30

Added

- `sleep {ms}` tool and `wait_for {condition: "time"}` for a plain wait (#12).
- Clipboard tools `clipboard_get`, `clipboard_set`, `clipboard_set_image` (PNG/JPEG/BMP/GIF under the allowed roots, put on the clipboard as CF_DIB plus PNG) and `paste_text` / `paste_image` (set the clipboard, press ctrl+v, restore the previous text), so rich editors that UIA cannot fill are reachable without a PowerShell detour (#13).
- `click`, `hover`, `scroll` and `drag` take `window` (ref or title regex): the window is activated first and x/y are relative to its rect, i.e. the pixel of a window screenshot can be passed as is (#14).
- `type_text` and `press_keys` take `window`: the window is activated before the input and the call fails when it does not exist or cannot take the foreground; the result reports where the input went (#15).
- `get_ui_tree` takes `control_type` (comma-separated), `name` (regex), `compact` and `max_nodes`; matching nodes are hoisted so the output is as flat as the filter allows, `compact` drops null / default fields and prints `rect` as `x,y,w,h`, and a truncated result reports `omitted_nodes` (#17).
- `screenshot` takes `monitor`, `method` (`auto` / `screen` / `print`) and `settle_ms`, supports `target: "virtual"`, and reports `method` and (for screen captures) `monitors` with index, rect, work area and primary flag (#10, #11, #18).

Changed

- `screenshot {target: "screen"}` captures the primary monitor at its native resolution with origin (0,0); the former whole-virtual-screen image is `target: "virtual"` (#18).
- A window screenshot is copied from the screen when the window or a dialog it owns is in the foreground, so modal save prompts and WebView2 overlays are in the picture and Chromium windows no longer hand out a frame that predates the last input; PrintWindow is only used for a window that is not in the foreground (#10, #11).
- `get_ui_tree` reads once more after a short wait when a focused child `Window` (a dialog that just opened) has no content yet (#11).
- `window_close` waits for the window to go away (`timeout_ms`, default 2 s) and returns `closed: false` with `reason: "still_open"` and the foreground window (the save prompt) when it is still there, instead of `closed: true` for a posted WM_CLOSE (#16).
- A window title regex that matches nothing is retried for 1.5 s before `No window matches` is raised, so the title `wait_for` just matched still resolves while an SPA re-titles its page; `wait_for` now points at the `win{n}` ref as the more reliable handle (#9).

## 0.2.2 - 2026-10-29

Added

- `list_windows` and `get_ui_tree` report `visible_rect` (DWM extended frame bounds: the frame the user sees, without the invisible resize borders `rect` includes) so a client can crop or record exactly one window. `get_ui_tree`'s `window` object now carries `pid`, `rect`, `visible_rect` and `state` too.
- Claude Code plugin `winuse-gate` (`plugins/winuse-gate`, listed in `.claude-plugin/marketplace.json`): hooks that block Claude's turn end once per change, and deny `git commit`, while UI files changed in the session have no passing winuse assertion, plus the `winuse-e2e` skill that tells Claude how to verify. Configured per project with `.claude/winuse-gate.json`.

Changed

- A window keeps its `win{n}` ref for the whole session: observing the same HWND again (`list_windows`, `get_ui_tree`, `process_run` ...) returns the ref already handed out instead of a new number, with the stored locator refreshed to the current title. Element refs (`u{n}` ...) still number up per observation.

## 0.2.1 - 2026-10-23

Changed

- Every tool now declares all four MCP annotation hints (`readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`), so hosts that confirm before destructive or open-world calls get an explicit answer per tool. Tools that act on the live desktop (`act`, `click`, `invoke`, `press_keys`, `window_close`, `process_run`, `process_kill`, `scenario_run`, ...) are open-world and, where a call could trigger anything in the target app, destructive; file tools stay inside the allowed roots and are closed-world, with `write_text`, `copy`, `move`, `delete`, `unzip` and `scenario_save` marked destructive because they can replace existing files.

## 0.2.0 - 2026-10-22

Added

- Java Swing support through Java Access Bridge: Swing windows (`SunAwt*`) are read into the same element model as UIA (`j{n}` refs), so `get_ui_tree`, `act` / `find` / `check`, `click`, `invoke`, `set_text`, `set_checked`, `select_item`, `get_text`, `get_list_items`, `assert_*`, `wait_for` and scenario replay work on them. Menu items are listed and invokable while their menu is closed. 32-bit and 64-bit JVMs (tested on Temurin 8 x86 and Temurin 21 x64).
- winuse talks the Java Access Bridge wire protocol itself (hidden client window, hello handshake, one memory-mapped file per JVM); no `WindowsAccessBridge-64.dll` is loaded or shipped. Both wire formats are spoken: the wide one of `JavaAccessBridge-32/64.dll` and Java 9+'s `javaaccessbridge.dll`, and the legacy one of the suffix-less `JavaAccessBridge.dll` that 32-bit Java 7/8 JREs ship (whose 32-bit object handles made it unreadable for the OpenJDK 64-bit client). Several JVMs of mixed formats at once. Tested on Temurin 21 x64, Temurin 8 x86 and Temurin 8 x86 forced onto its legacy bridge.
- `doctor` shows which `JavaAccessBridge*.dll` each Java window's JVM loaded, its wire format, and the Java and bridge versions the JVM reports; the legacy `JavaAccessBridge.dll` / `access-bridge.jar` count as a bridge (no false "no Access Bridge" warning for a 32-bit JRE that ships only those).
- `scripts/fetch-dev-java.ps1` fetches the JDK/JRE the integration tests use.
- `winuse jab enable|disable|status` manages `%USERPROFILE%\.accessibility.properties`.
- `winuse doctor` reports the client window and the JVMs connected to it, the user-profile enablement and every Java window with its JVM.
- Settings `WINUSE_JAB`, `WINUSE_JAVA_ACCESSIBILITY`.

Changed

- `process_run` starts its children with `JAVA_TOOL_OPTIONS` carrying the accessibility flag (Java processes print one `Picked up JAVA_TOOL_OPTIONS` line to stderr); `WINUSE_JAVA_ACCESSIBILITY=off` restores the old behaviour.
- `get_ui_tree` on a Java window the bridge cannot read returns `java_access_bridge` with the reason; `act` on such a window reports `no elements (java access bridge: ...)`.
- Candidates inside a menu carry the menu's name as their container (affects only menus whose items are visible to the tree, i.e. Swing).

Known issues

- `get_table` does not read Swing tables yet (`get_table is not supported for Java elements yet`).
- A JRE without the Java side of the bridge (trimmed Java 8 without `JavaAccessBridge-32.dll` / `access-bridge-32.jar`, or a `jlink` image without `jdk.accessibility`) cannot be read, and a JVM on such a JRE fails to start when accessibility is requested (`AWTError: Assistive Technology not found`): set `WINUSE_JAVA_ACCESSIBILITY=off` for apps on such a JRE and do not use `winuse jab enable`. `doctor` reports the missing files and this consequence. A Java app running elevated or as another user is out of reach.

## 0.1.0 - 2026-10-20

First release.

- MCP server over stdio (`winuse --mcp`) with 48 tools: observe (screenshot, UI Automation tree, WebView2 DOM via Playwright over CDP), intent (`act`, `find`, `check`), input, element, window, wait, process, file, assert and scenario tools.
- Scenario replay without AI (`winuse run`, `scenario_run`): recorded locators resolve deterministically, `--heal` re-grounds `act` steps whose element is gone.
- Decision model: TypeSafe Jev (`JEV_API_KEY`) or Cloudflare Clef on Workers AI (`CLOUDFLARE_ACCOUNT_ID` + `CLOUDFLARE_API_TOKEN`), chosen automatically or forced with `WINUSE_DECISION_PROVIDER`.
- Visual grounding for windows without UI elements (VM consoles, emulators, VNC): `act` / `find` / `check` work from a screenshot, return `pt{n}` refs, and replay from a window-relative point (`WINUSE_SCREENSHOTS`, `WINUSE_VISUAL_LOCATOR`).
- `winuse doctor` reports the decision model, keys, endpoint, probe, UIA, WebView2 and the Playwright driver.

Known issues

- `type_text` with Japanese text can be garbled under the Japanese IME (intermittent, under investigation).
- Clef and visual grounding are covered by unit tests with a fake model; they have not yet been exercised against a live account or a real VM window.
