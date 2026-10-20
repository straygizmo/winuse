# 視覚グラウンディング設計 — 要素が取れないウィンドウで `act` / `find` / `check` を動かす

日付: 2026-10-20
状態: 実装中（ユーザーから「お任せ」で承認。本文中の「私の判断」はビルダーの判断）
前提: `feature/clef-provider`（Jev / Clef の両プロバイダ対応、PR #1）の上に載せる。

## 1. 目的

VirtualBox、スマホのエミュレータ、VNC / RDP クライアントのように、UIA も WebView2 の DOM も要素を返さない（ビットマップしか無い）ウィンドウでも、`act` / `find` / `check` を使えるようにする。

現状は候補が 0 件だと `act` は `low confidence` で何もせず、`check` は要素一覧が空のまま質問するので意味のある答えが返らない。

## 2. ゴール / 非ゴール

**ゴール**
- 候補 0 件のウィンドウで、意図からスクリーン座標を決めてクリック / 入力する。
- `check` と `act` の達成確認が、画面の見た目を根拠に答えられる。
- Jev と Clef の両方で動く。Jev 1.14 の `box` を使える環境ではより少ない往復で済む。
- 回帰再生はモデルを呼ばない（既存のアンチゴールを守る）。視覚的に決めた操作は座標ロケータとして記録し、決定的に再生する。

**非ゴール**
- 画像照合（テンプレートマッチ、OCR）。フェーズ 2 の別件。
- 候補がある通常のウィンドウで画像を既定で送ること（設定で選べるようにはする）。
- 複数ウィンドウにまたがる視覚操作。

**アンチゴール**
- ユーザーが知らないうちにスクリーンショットが外部 API に送られる。送る条件は設定と README で明示する。
- 再生がモデルの確率に依存して非決定的になる。

## 3. 両 API の画像の渡し方（調査結果）

- **Jev**: `assets: { "<id>": { type: "image", format: "png", data_base64 } }` を要求に加え、`state` や質問文から `<id>` で参照する。参照されない asset は 400。ヘッダ `TypeSafe-Danger-Zone-Images: true` が必須。1.13 / 1.14 とも画像を受け付ける。画像は一辺 280〜1568 px、総画素 78,400〜2,458,624。`box` 質問（`{type:"box", instructions}` → `{box:{top,bottom,left,right}, confidence}`、各辺は画像サイズに対する 0〜1 の割合）は 1.14（`jev-preview` / `jev-1.14.0`）のみ。「見つからない」答えは無い。
- **Clef**: `images: ["data:image/png;base64,..."]`（最大 4 枚、PNG/JPEG/WebP、1 枚 4 MiB・16 MP、1 枚最大 1,024 トークン）。state の前に置かれ、暗黙に参照される。`box` は無い。

## 4. 設計

### 4.1 設定（環境変数）

| 変数 | 値 | 既定 | 意味 |
|---|---|---|---|
| `WINUSE_SCREENSHOTS` | `auto` / `always` / `off` | `auto` | モデルへの要求にウィンドウのスクリーンショットを添える条件。`auto` = 候補が 0 件のときだけ。`always` = 候補があっても添える。`off` = 添えない（候補 0 件の `act` は従来どおり失敗）。 |
| `WINUSE_VISUAL_LOCATOR` | `grid` / `box` | `grid` | 画像上で目標を決める方法。`box` は Jev 1.14 専用（`JEV_MODEL=jev-preview` など）。Clef で `box` を指定した場合は `grid` にフォールバックし警告を出す。 |

### 4.2 画像の準備（`WinUse.Core.Grounding.Visual`）

- 対象ウィンドウを `ScreenService.CaptureWindowAsync(hwnd, 1.0)` でフル解像度キャプチャし、視覚モジュール側で縮小・切り出し・描画を行う。
- `VisualFrame(byte[] Png, int Width, int Height, int OriginX, int OriginY, double Scale)`: 画像ピクセル → スクリーン座標は `screen = origin + px / scale`。切り出し・拡縮のたびに origin / scale を更新する。
- 送信サイズ: 長辺 1280 px 以下に縮小。短辺が 280 px 未満なら拡大して 280 以上にする（Jev の下限）。

### 4.3 目標の決め方

**grid（既定、両プロバイダ）** — 段階的に絞り込む choice 質問。
1. 全体画像に 8 列 × 6 行の格子を描き、各セルの左上に `A1`〜`H6` のラベルを置く（白地に黒文字、線は半透明）。choice の選択肢はセル ID + `none`。質問文: 「`screenshot` のどのセルに `goal` に必要な要素があるか。ラベルはセルの左上に描かれている。該当なしは none」。
2. 選ばれたセルを上下左右 25% 広げて切り出し、長辺 640 px に拡大、6 列 × 4 行の格子で再度 choice。
3. セルのスクリーン上の大きさが 24 px より大きく、段数が 3 未満なら 2 を繰り返す。
4. 最終セルの中心をスクリーン座標に変換して目標とする。各段で確率 < `MinProbability` または confidence < `MinConfidence`、または `none` なら `low confidence` で止める。
- 精度の目安: 1600×900 のウィンドウで 3 段後のセルは約 12×14 px。

**box（Jev 1.14）** — 1 回の `box` 質問「`goal` に必要な要素を `screenshot` の中で」→ 矩形の中心を目標とする。confidence < `MinConfidence` なら `low confidence`。

### 4.4 Grounder の流れ

- `ActAsync`: 候補 0 件のとき、`WINUSE_SCREENSHOTS=off` なら `no elements (screenshots off)` で失敗。それ以外は 4.3 で目標点を決め、`kind=auto` は text があれば type、無ければ click とする（kind の choice は聞かない。私の判断）。実行は `IActionExecutor.ExecuteAtAsync(x, y, kind, text)`。実行後は `SettleMs` 待って再キャプチャし、画像付きの `satisfied` noul で達成確認する。
- `FindAsync`: 候補 0 件なら目標点を 1 件だけ返す（`role: "point"`、`label: "x,y"`、`point: {x, y}`）。
- `CheckAsync`: 候補 0 件（`auto`）または `always` のときスクリーンショットを添える。
- `always` のときは候補があっても state に `screenshot` を添える。target / kind / satisfied の質問はそのまま。
- `ActResult` / `FindResult` に `Point` を追加する。

### 4.5 クライアント

- `IJevClient.AskAsync(state, questions, images = null)`。`ImageAttachment(string Id, byte[] Png)`。
- Jev: `assets` に `data_base64` で入れ、ヘッダ `TypeSafe-Danger-Zone-Images: true` を画像があるときだけ付ける。Grounder は `state["screenshot"] = id` で参照する。
- Clef: `images` に data URL を入れる。state の `screenshot` は文字列のまま残る（無害）。
- `BoxQuestion(Instructions)` / `BoxAnswer(Top, Bottom, Left, Right, Confidence)` を追加し、`box` 型の答えを解析する。

### 4.6 実行と再生

- `IActionExecutor.ExecuteAtAsync(int x, int y, ActKind kind, string? text)`: ウィンドウを前面化してからクリック。type はクリック後に `type_text`。select / toggle はクリック。
- 新ロケータ `PointLocator(WindowLocator Window, double Fx, double Fy)`（JSON 判別子 `point`）。`Fx` / `Fy` はウィンドウ矩形に対する割合（移動やサイズ変更に追従させる。私の判断）。
- `RefKind.Point`、ref ID 接頭辞 `pt`。`click(ref)` でも使えるよう `CenterOfAsync` は点を返す。
- 再生: `PointLocator` はウィンドウを解決して座標を計算し、モデルを呼ばずに実行する。`--heal` は従来どおり `act` を再グラウンディングする（視覚経路も通る）。

### 4.7 安全・コスト

- 画像を送る条件を README に明記する。`auto` では候補のあるウィンドウの画像は送らない。
- grid は最大 3 回 + 達成確認 1 回の要求。Clef は 1 枚 1,024 トークン上限。Jev は画像ピクセル数に応じた入力トークン課金。
- Jev の画像・box は「実験的」とベンダーが明記している。README に記す。

## 5. 実装順（各段で失敗するテストを先に書く）

1. 設定: `Screenshots` / `VisualLocator` の読み込み。
2. クライアント: 画像添付（Jev assets + ヘッダ、Clef images）、`box` 質問の送受信。
3. 視覚モジュール: `VisualFrame` の座標変換、格子の描画とセル幾何（純粋関数）、段階的切り出し。
4. `PointLocator` / `RefKind.Point` / `ExecuteAtAsync` / JSON ラウンドトリップ。
5. Grounder: 候補 0 件の `act` / `find` / `check`、`always` モード、達成確認。
6. ツール層: `act` / `find` の結果に point、`PointLocator` の記録と再生、dispatcher の解決。
7. README / 設計書の更新、全テスト、PR。

## 6. ビルダーに委ねる点

- 格子のラベル描画のフォント・色・線幅（読めれば良い）。
- 段階ごとの画像サイズの細部（長辺 640〜1280 の範囲）。
