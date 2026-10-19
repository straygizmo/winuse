# winuse 設計書 — AI が Windows を操作するための MCP サーバー（.NET 10）

日付: 2026-10-19
状態: ドラフト（ビルダーチェック前）

## 1. 目的と背景

**何を作るか**: AI（主に Claude Code）が Windows デスクトップを観察・操作できる MCP サーバー `winuse`。
.NET 10 で実装し、1 つの実行ファイルが「MCP サーバー（stdio）」と「回帰シナリオ再生 CLI」の両方を担う。

**なぜ**: 自作ツールの E2E テストを AI に実行させたい。当面の対象は次の 2 つ。

- **MDium**: Tauri 2 製デスクトップアプリ。UI は WebView2（Chromium）上の Web アプリ。
- **officecli-agent**: Word / Excel / PowerPoint の VSTO アドイン。作業ウィンドウの React UI が WebView2 に載る。

どちらも「ネイティブ層（ウィンドウ枠、Office リボン、ファイルダイアログ）」と「WebView2 内の Web UI」の二層構造。
提供する操作の範囲は、一般的なデスクトップ RPA が備えるもの（マウス、キーボード、ウィンドウ、UI 要素、画面キャプチャ、プロセス、ファイル）を基準にする。
既存コードの移植や参照はせず、すべて .NET 10 で新規に実装する。既存の社内ツールや外部実行ファイルへの依存は持ち込まない。

**2 つの使い方を両立する**:

1. **対話モード**: AI が MCP ツールを呼びながら探索的に E2E テストを実行する。
2. **回帰モード**: AI がテストを確立したあと、整理済みの手順を**シナリオファイル**として明示的に保存し、以後は AI なしで決定的に再生する（CI からも実行可能）。

**要素解決のコスト問題と Jev**: AI が毎回 UI ツリーや DOM を読んで操作対象を決めるとトークンと往復が嵩む。
そこで TypeSafe AI の型付き判断モデル **Jev** をサーバー側の「要素解決エンジン」として使う。
AI は「保存ボタンをクリック」のような**意図**だけを渡し、サーバーが候補要素を集めて Jev に択一させ、実行する。
ユーザーは Jev の API キーを保有しており、リポジトリ直下の `.env` に `JEV_API_KEY` として置いている。

## 2. ゴール / 非ゴール / アンチゴール

**ゴール**
- Claude Code から `winuse` を MCP サーバーとして登録すれば、MDium と officecli-agent の E2E を AI が対話的に実施できる。
- AI が確立した手順をシナリオとして保存し、`winuse run <scenario>` で AI なしに再生・判定できる。
- 意図レベルのツール（`act` / `find` / `check`）により、AI が UI ツリー全文を読まずに操作できる。
- Jev なしでも動く（確信度不足や未設定時は候補一覧を AI に返す）。

**非ゴール**
- Windows 以外の OS。
- 人間向けダイアログ（入力ボックス、メッセージボックス）の表示。
- 制御構文や変数管理（分岐・ループは AI 側、またはシナリオを呼ぶ側が担う）。
- 汎用の自律エージェント（計画は AI 側が担い、winuse は観察と実行に徹する）。
- 他の RPA 製品やそのシナリオ形式との互換。

**アンチゴール（技術的に動いても失敗とみなす）**
- 回帰再生が Jev の確率に依存して非決定的になる。
- MCP の stdout にログが混ざってプロトコルが壊れる。
- AI がファイル削除やプロセス終了を無制限に実行できる。
- 既存の社内ツール由来の名称・資産・独自実行ファイルへの依存が入り込む。
  - 標準ライブラリが同梱するランタイム（Microsoft.Playwright の Node ドライバなど）はこれに含めない（ユーザー確認済み）。配布時は exe の横にドライバ一式を同梱する。

## 3. 制約

- .NET 10（SDK 10.0.102 が導入済み）、TFM は `net10.0-windows`。
- MCP C# SDK: NuGet `ModelContextProtocol` 2.2.0。stdio トランスポート。
- UIA: NuGet `FlaUI.UIA3` 5.0.0。
- WebView2 内 DOM: NuGet `Microsoft.Playwright` 1.63.0 を CDP 接続（`ConnectOverCDP`）で使う。
- 画像照合: NuGet `OpenCvSharp4.Windows` 4.13（フェーズ 2）。純粋にライブラリとして使い、独自の外部 exe は起動しない。
- Jev: `POST https://api.typesafe.ai/v1/systemone`、Bearer 認証、`model` / `state` / `questions` の JSON。.NET SDK は無いので HttpClient で直接叩く。
  - 質問型: `noul`（yes/no 確率）、`choice`（最大 255 択、`choice` / `probabilities` / `confidence` を返す）、`score`、`box`（1.14、実験的）。
  - state + 全質問で約 64k トークン、state + 1 質問で約 32k トークン。
  - 同じ要求形式で動く Cloudflare の Clef を、設定でベース URL とモデル名を差し替えて使えるようにする。
- WebView2 のリモートデバッグは、対象アプリの起動時に環境変数 `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=<port> --remote-allow-origins=*` を与えて有効化する。アプリ側がコードで指定した引数と併用される。昇格（管理者）プロセスはこの環境変数を無視する。
  - したがって WebView2 の DOM を扱うには **winuse が対象プロセスを起動する**必要がある（既に起動済みの Office には繋げない）。
- stdio モードでは stdout を MCP 専用にし、ログは stderr とファイルへ出す。
- `.env` は git 管理外（`.gitignore` に追加）。
- 実行環境は Windows ネイティブの対話型デスクトップセッション（ログオン済み）を前提とする（私の判断）。
  - GitHub-hosted ランナーや session 0 では UIA / SendInput / WebView2 が動かないため対象外。回帰モードは「ローカル PC での手動 / スケジュール実行」または「自動ログオン済みの self-hosted Windows ランナー」で動かす。
  - `winuse run` は起動時に対話型セッションかを検出し、非対話なら終了コード 2 で即終了する。
  - Claude Code は Windows ネイティブで起動する前提。WSL からの起動（`/mnt/c` パス、cwd の解釈）はサポートしない。

## 4. 全体アーキテクチャ

```
Claude Code ──stdio(MCP)──▶ winuse.exe --mcp
                                │
             ┌──────────────────┼──────────────────────┐
             ▼                  ▼                      ▼
        Tools 層           Scenario 層            CLI (winuse run)
   (MCP ツール定義)     (journal / save / run)   (同じ Tools 実装を呼ぶ)
             │
             ▼
        Core サービス（単一の自動化スレッドで直列実行）
   ┌────────────┬──────────────┬─────────────┬──────────────┬─────────────┐
   │ Window     │ Input        │ Uia         │ Web          │ Screen      │
   │ (Win32)    │ (SendInput)  │ (FlaUI)     │ (Playwright  │ (GDI        │
   │            │              │             │  over CDP)   │  capture)   │
   ├────────────┼──────────────┼─────────────┼──────────────┼─────────────┤
   │ Process    │ File         │ Grounding   │ JevClient    │ Image/OCR   │
   │            │ (allowlist)  │ (候補生成)   │ (HTTP)       │ (phase 2)   │
   └────────────┴──────────────┴─────────────┴──────────────┴─────────────┘
```

**プロジェクト構成**（私の判断）
- `src/WinUse/` 実行ファイル本体（`net10.0-windows`、`OutputType=Exe`）。
- `src/WinUse.Core/` サービス層（テスト容易性のため分離）。
- `tests/WinUse.Core.Tests/` 単体テスト（xUnit）。
- `tests/WinUse.Integration.Tests/` 実機テスト（メモ帳・電卓・小さな WebView2 サンプルを対象。CI では任意実行）。
- `scenarios/` サンプルシナリオ。

**スレッドモデル**（私の判断）: UIA / SendInput / COM を扱う処理はすべて専用の 1 スレッド（STA、メッセージループあり）にキューで投入し直列実行する。MCP の各ツール呼び出しはそのキューに `Task` を積んで待つ。Playwright は自身の非同期モデルで動かし、自動化スレッドと混在させない。

**実行モード**
- `winuse --mcp` : MCP サーバー（stdio）。
- `winuse run <scenario.json> [--heal] [--report <dir>]` : シナリオ再生。終了コードで合否。
- `winuse doctor` : Jev 接続、WebView2 ランタイム、UIA の疎通確認。

## 5. 要素の参照モデル（ref）

AI とサーバーの間で要素を指す共通の短い ID を **ref** と呼ぶ。

- 形式: `u12`（UIA 要素）、`w7`（WebView2 DOM 要素）、`win3`（トップレベルウィンドウ）。
- ref は「スナップショット単位」で有効。`get_ui_tree` や `get_dom` や `find` が返した ref は、次の操作ツールでそのまま使える。
- サーバーは ref ごとに**ロケータ**を保持する。
  - UIA: ウィンドウ特定（タイトル / クラス / プロセス名）+ 祖先からのパス（ControlType と AutomationId / Name の列）。
  - DOM: Playwright 流の安定セレクタ（`data-testid` > `role+name` > `id` > CSS パス の優先順）。
- ロケータはシナリオ保存時にそのまま記録され、再生時に決定的に解決される。

## 6. MCP ツール一覧

名前は英語の snake_case。各ツールの説明文は AI 向けに英語で書く（私の判断）。
戻り値は原則 JSON テキスト。スクリーンショットは MCP の image content で返す。
以下の引数名は MCP の実際のワイヤ名（snake_case）である。シナリオ再生ではツールが受け取らない引数名を `unknown argument: <name>` のエラーにする（`resolved_refs` を除く）。

### 6.1 観察（Observe）
| ツール | 引数 | 戻り |
|---|---|---|
| `screenshot` | `target`: screen / window / element（省略時、`window` か `title` があれば window、なければ screen）, `window`(ref or title regex。他ツールの `window` と同じ解決), `ref`(window ref または要素 ref), `title`(title regex), `scale`(0.25–1) | PNG image + `{ width, height, scale, origin: {x, y} }`。`origin` は画像の画素 (0,0) の仮想スクリーン座標（物理ピクセル。マルチモニタでは負もあり得る。screen は `SM_XVIRTUALSCREEN/SM_YVIRTUALSCREEN`、window / element はその矩形の左上）。画像上の点をクリック座標に直すには `screen_x = origin.x + pixel_x / scale`（y も同様）。DOM 要素のキャプチャでは `origin` は null |
| `list_windows` | `title_regex`(任意), `process`(任意) | ウィンドウ一覧（ref, title, class, process, pid, rect, state） |
| `get_ui_tree` | `window`(ref/title), `scope`(ref), `max_depth`, `interactive_only`(既定 true), `include_offscreen`(既定 false) | 圧縮した UIA ツリー（ref, type, name, automationId, value, state, rect）。`scope` なしでルートに子がない場合は 300 ms 待って 1 回だけ取り直す（Chromium / Office の初回取得対策） |
| `get_dom` | `page`(任意), `interactive_only`(既定 true) | WebView2 のアクセシビリティ / インタラクティブ要素一覧（ref, role, name, tag, value, state, rect, selector） |
| `get_element` | `ref` | 単一要素の詳細（UIA 全プロパティ or DOM 属性） |
| `get_text` | `ref` | テキスト（UIA Value / Text パターン、DOM innerText） |
| `get_table` | `ref` | 表を 2 次元配列で |
| `get_list_items` | `ref` | リスト / コンボの項目一覧 |

### 6.2 意図レベル（Jev）
| ツール | 引数 | 動作 |
|---|---|---|
| `act` | `intent`(自然文), `window`(任意), `kind`(click / type / select / toggle / auto), `text`(type 用), `min_confidence`(任意) | 候補生成 → Jev choice → 閾値以上なら実行 → 実行後に Jev noul で達成確認 → 結果 |
| `find` | `intent`, `window`, `top_k` | 実行せず上位候補（ref, label, probability）を返す |
| `check` | `question`(yes/no の自然文), `window` | 現在の観察を state にして Jev noul → 確率を返す |

`act` の確信度が `min_confidence`（既定 0.55、`confidence` 0.35）未満なら実行せず、上位候補とスクリーンショット（対象ウィンドウ、縮尺 0.5 の image content）を返して AI に委ねる。
Jev 未設定時は `act` は常に候補返却モードになる。
`act` の結果には `executed` が付き、ジャーナルにも記録する。`scenario_save` は `executed=false` の `act` ステップを保存しない（再生できないため。明確なエラーを返す）。

### 6.3 低レベル操作（Act）
- マウス: `click(ref | x,y; button=left/right/middle; count=1/2; offset_x, offset_y)`, `hover(ref | x,y)`, `drag(from_ref | from_x,from_y; to_ref | to_x,to_y)`, `scroll(ref | x,y; direction; amount)`。`x,y` は仮想スクリーンの物理ピクセル（`screenshot` の `origin` から換算できる）
- キーボード: `type_text(text; per_char_delay_ms)`, `press_keys(keys="ctrl+s")`（修飾キー + 特殊キー名の組合せ）
- 要素: `set_text(ref, text)`, `select_item(ref, item)`, `set_checked(ref, checked)`, `focus(ref)`, `invoke(ref)`（UIA Invoke / DOM click）
- ウィンドウ: `window_activate(ref | title)`, `window_close(ref | title)`, `window_set_state(ref | title; state=minimized/maximized/normal)`, `window_move(x, y, width, height; ref | title)`, `window_exists(title; process)`
- 待機: `wait_for(condition, timeout_ms=10000, title, ref, selector, expected)` — condition は `window(title)`, `element(ref|selector)`, `text(ref, expected)`, `window_closed(title)`, `enabled(ref)` のいずれか
- プロセス: `process_run(path, args, cwd, env, webview_debug=bool, wait_window=title regex, require_fresh=bool)`, `process_kill(name_or_pid)`, `process_exists(name)`
  - `wait_window` は起動したプロセス（同じ pid、または同じプロセス名）のウィンドウを優先して最大 30 秒待つ。現れなければタイトル一致だけのウィンドウにフォールバックし、結果の `window.matched_by` を `"title-only"` にする（通常は `"pid"` / `"process"`）。
- ファイル: `file_exists(path)`, `read_text(path)`, `write_text(path, text)`, `append_text(path, text)`, `copy(src, dst, overwrite)`, `move(src, dst)`, `delete(path)`, `list_dir(path)`, `zip(dir, zip_path)`, `unzip(zip_path, dir)` — すべて設定の許可ルート配下に限定。ディレクトリを自分自身の配下へ `copy` することは拒否する
- 画像（フェーズ 2）: `image_find(template_path, region, threshold)`, `image_wait`（OpenCvSharp のテンプレートマッチ）
- OCR（フェーズ 2）: `ocr_read(region)`（Windows.Media.Ocr）

### 6.4 検証（Assert）
回帰シナリオの判定ステップになる。失敗時は `passed:false` と理由を返す（例外にしない）。

- `assert_text(ref, expected, mode=equals/contains/regex)`
- `assert_exists(ref|selector, expected=true)`
- `assert_window(title, expected=true)`
- `assert_file(path, exists=true, contains)`
- `assert_screenshot(baseline_path, region, tolerance)`（フェーズ 2、OpenCvSharp）
- `check`（Jev）は **soft** 判定として記録可能（後述）

### 6.5 シナリオ（Scenario）
- 各ツール呼び出しの結果には `step_id` が付く。サーバーはセッション中の呼び出し（ツール名、引数、解決済みロケータ、所要時間）を**ジャーナル**に保持する。
- `scenario_save(name, step_ids[], path, description)` : 指定した step だけを順に並べてシナリオファイルを保存する（**明示保存**。自動記録は行わない）。
- `scenario_list(dir)`, `scenario_run(path, heal=false, report_dir)` : MCP からも再生できる。
- シナリオの編集機能は持たない。編集は AI がファイルを直接書き換える（JSON なので可能）。

## 7. シナリオファイル形式

JSON。ツール名と引数をそのまま並べる。意図とロケータの両方を記録する。

```json
{
  "name": "mdium-open-and-save",
  "version": 1,
  "env": { "MDIUM_EXE": "C:/.../mdium.exe" },
  "steps": [
    { "tool": "process_run", "args": { "path": "${MDIUM_EXE}", "webview_debug": true, "wait_window": "MDium" } },
    { "tool": "act", "args": { "intent": "Click the Save button", "kind": "click" },
      "resolved": { "ref_kind": "dom", "selector": "role=button[name=\"Save\"]", "window": "MDium" } },
    { "tool": "type_text", "args": { "text": "hello" } },
    { "tool": "assert_text", "args": { "expected": "hello", "mode": "contains" },
      "resolved": { "ref_kind": "dom", "selector": "[data-testid=\"editor\"]" } },
    { "tool": "check", "args": { "question": "Is the document shown as saved?" }, "soft": true, "min_probability": 0.8 }
  ]
}
```

**再生の規則**
1. `resolved` があるステップはロケータで決定的に解決する。Jev は呼ばない。
   - 再生ランナーはツール呼び出しに `IsReplay=true` を付ける（`ToolInvocation.IsReplay`、既定 false）。`IsReplay` のとき、`resolved` のない `act` ステップは Jev を呼ばずに FAIL（`act step has no resolved locator`）にする。`--heal` 指定時だけ `intent` から Jev で解決する。対話中の MCP の `act`（`IsReplay=false`）はこの規則の影響を受けない。
2. ロケータ解決に失敗したとき、`--heal` 指定時のみ `intent` から Jev で再解決し、成功したら `resolved` を更新した「修復済みシナリオ」を別名（`<名前>.healed.json`。例: `scenarios/mdium-smoke.json` → `scenarios/mdium-smoke.healed.json`。CLI と `scenario_run` で共通）で書き出す。未指定なら FAIL（`locator not found: <引数名>`、終了コード 1）。
3. `soft: true` のステップ（Jev の `check`）は失敗しても全体を FAIL にせず WARN として報告する。
   - `JEV_API_KEY` が未設定、または Jev が 429 / 529 / タイムアウトで応答しない場合も、そのステップは WARN（skipped）として扱い、再生全体の合否には影響させない（私の判断。再生が Jev に依存しないというアンチゴールを守るため）。
   - `--heal` による再解決も同様で、Jev が使えなければ単にロケータ失敗として FAIL にする。
4. 各ステップの前後でスクリーンショットを撮り、レポートディレクトリに保存する（HTML レポートはフェーズ 2）。撮影に失敗しても再生は止めず、そのステップのメッセージに `screenshot failed: <理由>` を付ける。
5. 終了コード: 0 = PASS、1 = FAIL（判定の失敗、ロケータが見つからない、`act` が実行されない）、2 = 実行エラー（対象が起動しない、シナリオの形式不正、未知の引数等）。

## 8. Grounding（候補生成）と Jev 連携

### 8.1 候補生成
`act` / `find` / `check` は次の手順で**観察スナップショット**を作る。

1. 対象ウィンドウを決める（引数 > 直前に操作したウィンドウ > 前面ウィンドウ）。
2. UIA から、可視・有効・操作可能な ControlType（Button, Edit, ComboBox, CheckBox, RadioButton, MenuItem, TabItem, ListItem, TreeItem, Hyperlink, Document。Text はラベル参照用）を収集。
3. そのウィンドウに WebView2 が含まれ、CDP 接続があれば、Playwright のアクセシビリティスナップショットからインタラクティブ要素を収集。「含まれる」とは、ウィンドウ配下に `Chrome_RenderWidgetHostHWND` の矩形があり、**かつ** CDP 接続が自分のものであること（ウィンドウの pid が `process_run` で起動した pid と一致する、またはレンダーホスト HWND の所有プロセス名が CDP 所有プロセス名と一致する）。Chromium 系のウィンドウはどれもレンダーホストを持つため、矩形だけでは判定しない。
   - UIA の初回取得でルートしか返らない（子が 0）場合は 300 ms 待って 1 回だけ取り直す。
4. 両方を 1 つの候補リストに統合し、各要素を次に圧縮する:
   `{ id, role, label, value, enabled, focused, editable, checked, region }`
   - `region` は "top-left" など 9 分割の粗い位置。座標とハンドルは渡さない。
   - 同一要素が UIA と DOM の両方に現れる場合（WebView2 内の要素）は DOM 側を優先し、UIA 側を落とす。
5. 候補が 255 を超える場合は**二段階**にする。まず領域 / コンテナ（リボンタブ、作業ウィンドウ、ダイアログ、エディタ）を choice で選ばせ、次にその配下の要素で choice する。

### 8.2 Jev への質問
`state` は `{ goal, window: {title, process}, elements: [...], recent_actions: [...] }`。
`questions` は目的に応じて次を組み合わせる。

- `target`: choice。選択肢は候補 id + `none`。instructions に意図（`goal`）を含める。
- `kind`: choice（`kind=auto` のときだけ）。click / type / select / toggle / none。
- `satisfied`: noul。実行後、`SettleMs`（`WinUseConfig.SettleMs`、既定 400 ms、環境変数 `WINUSE_SETTLE_MS`）待ってから取り直したスナップショットに対して「Given `goal`, `executed_action` and the current `elements`, is the goal now accomplished?」と問う。state には `executed_action: { kind, target_label, text }` を加え、`recent_actions` にも今実行した操作を含める（ジャーナルへの記録はツール終了後のため）。
- `check` ツールは noul 1 問のみ。

閾値（私の判断、設定で変更可）: 通常操作 probability ≥ 0.55 かつ confidence ≥ 0.35。達成確認 noul ≥ 0.7 で `verified:true`、未満は `verified:false` を返すがエラーにはしない（AI が判断する）。

### 8.3 Jev クライアント
- `HttpClient`、タイムアウト 10 秒、429 / 529 は指数バックオフで 3 回まで再試行。
- 設定: `JEV_API_KEY`（`.env` または環境変数）、`JEV_BASE_URL`（既定 TypeSafe、Clef 用に差し替え）、`JEV_MODEL`（既定 `jev-latest`）。
- 起動時にカレントディレクトリと実行ファイルのディレクトリの `.env` を読む。
- 要求と応答を（キーを除いて）デバッグログに残す。

## 9. WebView2 への接続

- `process_run(..., webview_debug=true)` のとき、空きポートを 1 つ確保し、環境変数 `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` に `--remote-debugging-port=<port> --remote-allow-origins=*` を設定して起動する。
- Office（officecli-agent）の場合は `winword.exe` / `excel.exe` / `powerpnt.exe` を winuse が起動する。既に Office が起動していると同一プロセスに合流して環境変数が効かないため、`require_fresh=true` なら同名プロセスが存在する時点で拒否する。
- 起動後、`http://127.0.0.1:<port>/json/version` をポーリングして CDP が開くのを待ち、Playwright `Chromium.ConnectOverCDPAsync` で接続する。
- ページが複数あるときは `get_dom(page=...)` で選べる。既定は最後にアクティブになったページ。
- Tauri は自前の追加引数を持つが、環境変数の引数は併用されるので問題ない（公式ドキュメントで確認済み）。

## 10. 安全策

- ファイル操作ツールは設定 `allowed_roots`（既定: カレントディレクトリ、`%TEMP%`、シナリオで指定した作業ディレクトリ）の配下のみ。
- `process_kill` は winuse が起動したプロセス、または設定 `killable_processes` に列挙した名前のみ。
- `act` は「送信 / 削除 / 購入」のような結果的行為の判定を**しない**（自分のアプリの E2E が目的のため）。必要になれば Jev の noul で `consequential` を追加できる拡張点を残す。
- 全ツールの実行ログ（引数、結果、所要時間）を `%LOCALAPPDATA%/winuse/logs/` に残す。

## 11. 段階分け

**フェーズ 1（最初に動かす範囲）**
- MCP サーバー骨格、CLI、ログ、設定（`.env` 読み込み含む）。
- Window / Input / Process / File サービス。
- UIA（FlaUI）: `get_ui_tree`, `get_element`, `get_text`, `set_text`, `select_item`, `set_checked`, `invoke`, `wait_for`。
- WebView2（Playwright over CDP）: `get_dom`, DOM 要素への click / fill / select。
- `screenshot`。
- Grounding + Jev: `act`, `find`, `check`。
- Scenario: ジャーナル、`scenario_save`, `scenario_run`, CLI `run`、assert 系（text / exists / window / file）。
- MDium 起動 → 新規ドキュメント → 文字入力 → 保存 → 確認、のサンプルシナリオ。
- **Office 接続の技術検証スパイク**（ユーザー確認済み）: winuse から `winword.exe` を起動し、officecli-agent の作業ウィンドウの WebView2 に CDP で繋がるかを確かめる。Office 自身が開く他の WebView2（サインイン、Web アドイン等）とポートを取り合う可能性があるため、`/json/list` で得られるページ一覧から作業ウィンドウのページを URL / タイトルで特定できるかを記録する。結果は `docs/superpowers/spikes/` に残し、成否はフェーズ 1 の合否に含めない。

**フェーズ 1.5（Office 本対応）**
- スパイクの結果を踏まえ、`get_dom(page=...)` のページ選択規則、ポート競合時の UIA のみフォールバック、作業ウィンドウを開くまでの手順（リボンの「Agent」トグル操作）を設計に追加して実装する。

**フェーズ 2**
- OpenCvSharp 画像照合（`image_find`, `assert_screenshot`）。
- OCR（Windows.Media.Ocr）。
- HTML レポート、`--heal` 自己修復。
- `get_table` / `get_list_items` の DOM 対応。
- Jev `box`（画像上の矩形）を使った視覚グラウンディングの試験。

## 12. テスト方針

- 単体テスト: 候補圧縮・二段階分割、シナリオの直列化と再生順序、Jev クライアント（HTTP をモック）、ロケータ生成、ファイル許可ルート判定。
- 統合テスト: メモ帳（UIA）と、小さな WebView2 サンプル（`tests/fixtures/`、WinForms + WebView2 で用意、私の判断）に対して実機で操作。ローカル実行のみ。
- 受け入れ: フェーズ 1 のサンプルシナリオ（MDium）が `winuse run` で PASS し、同じ操作を Claude Code からの対話で再現できる。Office はフェーズ 1 の受け入れに含めない。

## 13. ビルダーに委ねる事項

- 内部のクラス設計、DI の有無、設定ファイル形式（appsettings.json か環境変数のみか）。
- UIA ツリー圧縮の具体的なプロパティ選定と、`max_depth` の既定値。
- CDP ポートの確保方法と待機のタイムアウト。
- シナリオ JSON の `${VAR}` 展開の実装。
- ログライブラリの選定。
- 単体テストと統合テストの切り分け方法（カテゴリ属性など）。

## 14. 私（Claude）が決めた事項の一覧

- リポジトリ: `C:\Users\mtmar\source\repos\winuse`、名前は `winuse`。
- 単一 exe で MCP と CLI を兼ねる。
- UIA は FlaUI、WebView2 は Playwright over CDP、画像は OpenCvSharp（外部 exe は使わない）。
- 要素解決は Jev を既定、Clef は設定差し替え、Jev なしでも動く。
- シナリオは JSON、明示保存、ロケータ優先の決定的再生、`--heal` で Jev 再解決。
- Jev の `check` は soft 判定として扱う。
- ツール名・説明は英語。
- ファイル操作は許可ルート限定、プロセス終了は自分が起動したもの限定。
- 単一の自動化スレッドで UIA / 入力を直列化。
- 人間向けダイアログ、制御構文、他製品固有の機能は実装しない。
- 実行環境は Windows ネイティブの対話型デスクトップのみ。WSL 起動や session 0 は非対応。
- 回帰再生中の Jev 未設定・障害は WARN(skipped) とし、合否に影響させない。

## 15. ユーザーが決めた事項（ビルダーチェック後）

- フェーズ 1 の受け入れ対象は MDium のみ。Office は技術検証スパイクに留め、本対応はフェーズ 1.5。
- Microsoft.Playwright が同梱する Node ドライバの起動は許容する（禁止するのは旧ツール由来の独自 exe）。

状態: ユーザー承認待ち。承認後は実装計画（superpowers:writing-plans）へ進む。
