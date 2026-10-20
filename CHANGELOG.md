# Changelog

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
