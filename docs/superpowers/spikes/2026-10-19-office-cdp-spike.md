# Office CDP spike (2026-10-19)

Question: can winuse reach the officecli-agent add-in task pane (WebView2 in Word) over CDP?

**Verdict: yes.** `process_run(webview_debug=true)` on WINWORD gave `cdp_connected=true`, and `get_dom` returned the add-in pane's elements.

## Environment
- Word 16.0.20430.20182 (`C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE`), Japanese UI, Windows 11.
- officecli-agent installed as a VSTO add-in. `HKCU\Software\Microsoft\Office\Word\Addins\OfficeCliAgent.Word` exists with LoadBehavior=3 and Manifest pointing at the repo's `bin/Debug/*.vsto`.
- WINWORD was not running at the start (`process_exists` -> `exists:false`). It was closed with `window_close` between runs. No save prompt appeared, as no document was edited.

## Results
1. `process_run(path, webview_debug=true, require_fresh=true, wait_window="Word")` -> `pid 12280, cdp_port 63235, cdp_connected true, cdp_error null`. Ports differ per run (54942, 61394, 63235), so the free port is picked each time and no conflict was observed.
2. Word opens on the start screen (window title exactly `Word`, class `OpusApp`). After a blank document is created, the title is `文書 1 - Word`. The "白紙の文書" ListItem was invoked via UIA (`u18`).
3. `get_dom(interactive_only=false)` returned `pages: [{id:"p1", url:"https://officecli-agent.local/taskpane.html?hostApp=Word&lang=ja", title:"OfficeCLI Agent"}]`.
   - There was exactly one CDP page. No sign-in or other add-in WebView2 pages were present. The add-in pane is the only page.
   - Nodes included the buttons 画面を再読み込み / リセット / 設定 / 送信 and the textbox `依頼を入力 (Enterで送信 / Shift+Enterで改行)`, plus the model label "OpenAI / gpt-6-luna".
   - On the Word start screen alone (an earlier run, before any document), the page list was `about:blank` only; the pane page appeared once the pane/document existed.
4. The UIA tree of the document window shows the ribbon Home tab with the button `Agent` (right after `アドイン` and `Claude`, no automationId) and the pane as a child Window containing the same four buttons. In one run the pane was already open when the document opened (the add-in had restored its pane state). In another run it was not visible and the pane page was still listed. I did not find a way to tell "pane hidden" from "pane open" via CDP.
5. Screenshot: `screenshot(window=...)` returned a 3840x1085 image of the whole two-monitor desktop, not just the window (images are in git-ignored `out/office/`).

## Observations on winuse
- `process_run` with `wait_window="Word"` is a regex search over all windows. It matched a Google Chrome window whose title contained "Word" and returned that as `window`. Use `^Word$`.
- The CDP connection lives in the `winuse --mcp` process. A second `mcp_batch.py` run starts a new server that is not connected (`Web service is not connected`), so the whole flow (run, open, `get_dom`) must be in one batch/session.
- The UIA tree of Word is built lazily. The first `get_ui_tree` right after launch or document creation showed only the system menu. A second call a moment later returned the full ribbon. A retry/wait is needed.
- A ref (`u18`) from a tree call is only valid within that server session. Calls files that pass refs must come from a tree walked in the same batch.
- No sleep tool exists. `wait_for(window, title)` served as the wait.
- Batch JSON must be UTF-8 (set `PYTHONUTF8=1` on Windows) for Japanese names.

## Fallback and design input for phase 1.5
- Page selection rule: `get_dom` with `page="officecli-agent.local"` (URL substring) or `page="taskpane.html"`. Do not rely on the first page; Word itself exposes `about:blank` before the pane exists.
- Pane-open procedure: find the Home-tab button named `Agent` in the UIA tree (locale-independent name), `invoke` it only when `get_dom` does not already list the taskpane URL; retry the tree until the ribbon is populated.
- If CDP is not connected (e.g. Word already running, which `require_fresh` refuses), the UIA-only path works: the pane's buttons appear in the UIA tree too (`u11`-`u20`), but the textbox contents and chat output are not readable as DOM.
- Not tested: pane toggle off/on, Excel/PowerPoint, Word already running, sending a message through the pane.
