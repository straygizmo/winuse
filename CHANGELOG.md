# Changelog

## Unreleased

Added

- `list_windows` and `get_ui_tree` report `visible_rect` (DWM extended frame bounds: the frame the user sees, without the invisible resize borders `rect` includes) so a client can crop or record exactly one window. `get_ui_tree`'s `window` object now carries `pid`, `rect`, `visible_rect` and `state` too.

Changed

- A window keeps its `win{n}` ref for the whole session: observing the same HWND again (`list_windows`, `get_ui_tree`, `process_run` ...) returns the ref already handed out instead of a new number, with the stored locator refreshed to the current title. Element refs (`u{n}` ...) still number up per observation.

- Claude Code plugin `winuse-gate` (`plugins/winuse-gate`, listed in `.claude-plugin/marketplace.json`): hooks that block Claude's turn end once per change, and deny `git commit`, while UI files changed in the session have no passing winuse assertion, plus the `winuse-e2e` skill that tells Claude how to verify. Configured per project with `.claude/winuse-gate.json`.

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
