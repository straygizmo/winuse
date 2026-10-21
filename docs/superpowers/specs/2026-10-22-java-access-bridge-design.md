# Java Access Bridge 対応設計 — Swing アプリの要素を認識して操作する

日付: 2026-10-22
状態: 設計（ビルダーチェック済み、ユーザー承認待ち）。本文中の「私の判断」はビルダー（Claude）の判断で、ユーザーが決めた事項と区別する。
前提: 0.1.0（`main`）の上に載せる。

## 1. 目的

Swing（Java AWT）アプリのウィンドウは UI Automation からは中身のない一枚のウィンドウ（クラス名 `SunAwtFrame` / `SunAwtDialog`）に見える。`get_ui_tree` は根だけを返し、`act` / `find` / `check` は候補 0 件としてスクリーンショットによる視覚グラウンディングに落ちる。

Java Access Bridge（JAB）を使って Swing のアクセシビリティツリーを読み、UIA アプリと同じ要素モデル（ref、`UiNode`、候補、ロケータ）に流し込む。これにより既存ツールが Swing ウィンドウでもそのまま動く。

**前提（決まっていること）**
- 32bit / 64bit どちらの JVM で動く Swing アプリにも対応する。主な用途はボタンとメニューの操作。
- 対象環境に 64bit Java が無く、アプリ同梱の JRE しか無い（JRE の設定変更も難しい）ケースを前提に設計する。
- winuse 側は 64bit プロセス（`PlatformTarget=x64`）。

## 2. ゴール / 非ゴール / アンチゴール

**ゴール**
- Swing ウィンドウで `get_ui_tree` がボタン、メニュー、メニュー項目、テキスト欄、チェックボックス、コンボボックス、リスト、タブ、ツリーなどを返し、各要素に ref が付く。
- `act` / `find` / `check` が Swing の要素を候補として使う（視覚グラウンディングに落ちない）。
- `click` / `hover` / `invoke` / `focus` / `set_text` / `set_checked` / `select_item` / `get_element` / `get_text` / `get_list_items` / `assert_exists` / `assert_text` / `wait_for` / `screenshot(target=element)` が Swing の ref を受け付ける。
- メニュー項目は、メニューを開いていなくてもツリーに現れ、`invoke`（および `act`）で直接実行できる。
- 保存したシナリオが AI なしで再生でき、`--heal` も動く。
- 64bit Java が無い環境で動く: 必要な DLL を winuse の隣に置くだけで済む。
- Java 側の有効化を JRE 本体に触らずに行える手段を提供し、`winuse doctor` が状態を診断する。

**非ゴール**
- `get_table` の Swing 対応（`JTable` の AccessibleTable API）。本件では Swing の ref に対して明確なエラーを返すだけにし、申し送りに残す。
- JavaFX、SWT、AWT 単体（Swing を使わない）アプリ。SWT は UIA / MSAA で見えるので対象外。
- JAB のイベントコールバック（フォーカス変化、プロパティ変化の通知）。ポーリング（再観測）で足りる。
- Java 7 以前など JAB が同梱されない JRE への対応。`doctor` で「JAB 同梱なし」と報告するにとどめる。
- Swing と UIA の要素が同じウィンドウに混在するケース（ネイティブ埋め込み）。Java ウィンドウは JAB のみで読む。

**アンチゴール**
- 既存の UIA / DOM / 視覚グラウンディングの挙動が変わること。JAB が使えないときは今日と同じ経路（UIA の根のみ → 視覚グラウンディング）に落ちる。
- ユーザーの JRE やシステム設定を黙って書き換えること。有効化は明示的なコマンドか、winuse が起動するプロセスの環境変数に限る。
- 再生がモデルの確率に依存すること。Swing の操作もロケータで決定的に再生する。
- バイナリ（DLL）をリポジトリにコミットすること。

## 3. Java Access Bridge の仕組み（調査結果）

- JAB は Java 側（JVM にロードされる `JavaAccessBridge-32.dll` / `-64.dll` と `com.sun.java.accessibility.AccessBridge`）と Windows 側（支援技術が読み込む `WindowsAccessBridge-32.dll` / `-64.dll`）から成り、両者は Windows メッセージ（`WM_COPYDATA` と登録メッセージによるブロードキャスト）で通信する。**ビット数は一致しなくてよい**: 64bit の支援技術が 32bit JVM と話せる。Windows 側 DLL のビット数は読み込むプロセス（winuse = 64bit）に合わせる。
- `WindowsAccessBridge-64.dll` は 64bit JDK / JRE の `bin` に同梱される（Java 8 以降、OpenJDK ビルドも含む。モジュール `jdk.accessibility`）。ライセンスは GPLv2 + Classpath Exception。単体ファイルを winuse の隣に置いて配布することは許される（法的通知を同梱する）。
- 初期化は `Windows_run()`。この関数は呼び出しスレッド上に隠しウィンドウを作り、既存の JVM へブロードキャストする。JVM からの応答は **そのスレッドのメッセージキュー** に届くので、呼び出しスレッドは `GetMessage` / `DispatchMessage` でメッセージを回し続けなければならない。回さないと `isJavaWindow` は永遠に偽を返す（既知の落とし穴）。後から起動した JVM も自分からブロードキャストするので、ポンプを回していれば自動的に見える。
- オブジェクト参照は `JOBJECT64`（64bit DLL では `jlong`、すなわち `long`）。取得した `AccessibleContext` は `releaseJavaObject(vmID, obj)` で解放しないと JVM 側にリークする。
- 主要 API（`AccessBridgeCalls.h`、JDK の `include/win32/bridge/` に同梱）:
  - 発見: `isJavaWindow(HWND)`, `getAccessibleContextFromHWND(HWND, long* vmID, AccessibleContext*)`, `getHWNDFromAccessibleContext`, `getTopLevelObject`, `isSameObject`
  - 構造: `getAccessibleContextInfo(vmID, ac, AccessibleContextInfo*)`（名前、説明、ロール、状態、親内インデックス、子の数、画面座標 x/y/幅/高さ、対応インターフェースのフラグ）、`getAccessibleChildFromContext(vmID, ac, index)`, `getAccessibleParentFromContext`
  - 操作: `getAccessibleActions` / `doAccessibleActions`（Swing の `AccessibleAction`。ボタン・メニュー項目は `click`、コンボボックスは `togglePopup`、ツリーノードは `toggleexpand`）、`requestFocus`, `setTextContents`, `addAccessibleSelectionFromContext` / `clearAccessibleSelectionFromContext` / `getAccessibleSelectionCountFromContext` / `getAccessibleSelectionFromContext`
  - 読み取り: `getAccessibleTextInfo` / `getAccessibleTextRange`, `getCurrentAccessibleValueFromContext`, `getVirtualAccessibleName`
  - `AccessibleContextInfo` の文字列は `wchar_t` 固定長（名前・説明 1024、ロール・状態 256）。`role_en_US` / `states_en_US` はロケール非依存の英語（例: `push button`, `menu item`; 状態は `enabled,focused,visible,showing,checked,selected,editable,expanded,collapsed,...` のカンマ区切り）。
- **Java 側の有効化**: JVM 起動時に `javax.accessibility.assistive_technologies` に `com.sun.java.accessibility.AccessBridge` が含まれる必要がある。手段は三つで、いずれも JRE 本体を変更しない。
  1. `%USERPROFILE%\.accessibility.properties` に `assistive_technologies=com.sun.java.accessibility.AccessBridge` と `screen_magnifier_present=true` を書く（`jabswitch -enable` がしていることと同じ。Java 8 以降が読む。管理者権限不要）。
  2. 環境変数 `JAVA_TOOL_OPTIONS=-Djavax.accessibility.assistive_technologies=com.sun.java.accessibility.AccessBridge` を付けて JVM を起動する（JVM は stderr に `Picked up JAVA_TOOL_OPTIONS: ...` と 1 行出す）。
  3. Windows の「簡単操作」でスクリーンリーダーを有効にする（対象外）。
  いずれも **アプリの起動前** に効いていなければならず、起動中のアプリには効かない（再起動が必要）。
- **座標**: `AccessibleContextInfo.x/y` は Java の画面座標。32bit の Java 8 は DPI 非対応なので、高 DPI 環境では Windows が仮想化した論理座標（96 DPI 相当）が返る。winuse は Per-Monitor V2 で物理ピクセルを使うため、変換が必要。Java 9 以降は DPI 対応で、JVM のスケールを掛けた座標になる。表示されていない要素（閉じたメニューの項目など）は x/y が負または 0 幅で、状態に `showing` が含まれない。

## 4. 設計

### 4.1 構成要素（`WinUse.Core/Java/`）

| 型 | 役割 |
|---|---|
| `JabNative` | `WindowsAccessBridge-64.dll` の P/Invoke（`DllImport` ではなく `NativeLibrary.Load` + 関数ポインタ。DLL のパスを実行時に決めるため）。構造体は `AccessBridgeCalls.h` に合わせて `StructLayout(Sequential, CharSet.Unicode)` で定義。 |
| `JabDispatcher` | JAB 専用スレッド。起動時に `Windows_run()` を呼び、`GetMessage` ループを回す。作業は `PostThreadMessage(WM_APP)` で起こされるキューから実行する。`AutomationDispatcher` とは別スレッド（UIA スレッドはメッセージポンプを持たないため）。全 JAB 呼び出しはこのスレッド上で行う。 |
| `JavaService` | `UiaService` と同じ公開面（`GetTreeAsync`, `GetElementAsync`, `GetRectAsync`, `GetTextAsync`, `InvokeAsync`, `SetTextAsync`, `SelectItemAsync`, `SetCheckedAsync`, `FocusAsync`, `GetListItemsAsync`, `ResolveAsync`）。`IsJavaWindowAsync(hwnd)`、`IsAvailable`（DLL が読めて `Windows_run` が成功した）、`UnavailableReason` を持つ。 |
| `JavaRoleMap` | JAB の `role_en_US` → UIA の ControlType 名（4.4）。 |
| `JavaStates` | `states_en_US` の解析（4.5）。 |
| `JavaLocatorResolver` | `JavaLocator` のパスを根から辿る（`UiaLocatorResolver` と同じアルゴリズム）。 |
| `JabEnablement` | `.accessibility.properties` の読み書きと状態判定、`JAVA_TOOL_OPTIONS` の組み立て。 |
| `JabDllLocator` | DLL の探索（4.2）。 |

`Refs/ElementRef.cs` に `RefKind.Java`（接頭辞 `j`、例 `j12`）と `JavaLocator(WindowLocator Window, IReadOnlyList<JavaPathSegment> Path)`、`JavaPathSegment(string Role, string? Name, int Index)` を追加する（`$type: "java"`）。`Role` は JAB の `role_en_US` をそのまま持つ（ControlType への写像後ではなく。写像を変えてもシナリオが壊れないように）。

### 4.2 DLL の探索と読み込み

探索順（最初に見つかったもの）:
1. `WINUSE_JAB_DLL`（ファイルの完全パス）
2. `winuse.exe` と同じディレクトリの `WindowsAccessBridge-64.dll`
3. `%JAVA_HOME%\bin\WindowsAccessBridge-64.dll`
4. レジストリ `HKLM\SOFTWARE\JavaSoft\{JDK,Java Runtime Environment,JRE}\<version>\JavaHome` の `bin`
5. `LoadLibrary("WindowsAccessBridge-64.dll")`（PATH）

読み込みは遅延する: 最初に Java ウィンドウらしきもの（4.3 の事前判定）に出会ったとき、または `doctor` が呼んだとき。失敗（見つからない、`Windows_run` が失敗）は `JavaService.UnavailableReason` に残し、警告ログを **1 回だけ** 出す。以降その観測は従来経路（UIA の根のみ）に進む。`WINUSE_JAB=off` で機能全体を無効にできる。

### 4.3 Java ウィンドウの判定とツリーの取得

- 事前判定: ウィンドウのクラス名が `SunAwt` で始まる（`SunAwtFrame`, `SunAwtDialog`, `SunAwtWindow`）。これに該当しないウィンドウでは JAB を呼ばない（コストと誤検知を避ける）。
- 本判定: `isJavaWindow(hwnd)`。`Windows_run` 直後は JVM の応答待ちで偽になりうるので、初回ロード後は最大 1 秒（100 ms 刻み）だけ再試行する。偽のままなら「Java ウィンドウだが Access Bridge が応答しない」として警告ログ（`doctor` を促す文言）を出し、従来経路に進む。
- `ToolContext.ObserveAsync` と `get_ui_tree` は、ウィンドウが Java ウィンドウなら `Java.GetTreeAsync` を使い、そうでなければ従来どおり `Uia.GetTreeAsync`。`RetryIfChildlessAsync` は Java 経路にも同じく適用する（起動直後は子が揃っていないことがある）。
- ツリー構築（`JavaService.TreeBuilder`）は `UiaService.TreeBuilder` と同じ規則に従う:
  - 根は `getAccessibleContextFromHWND`。各ノードで `getAccessibleContextInfo` を取り、子は `getAccessibleChildFromContext(i)` を `childrenCount` 回。
  - `Index` は同じ親の中で `(Role, Name)` が同じ兄弟の中の順番。**兄弟は一つも飛ばさずに数える**（リゾルバと一致させるため）。
  - `interactive_only` のとき `InteractiveFilter.IsInteractive(controlType)` または名前付き `Text` を残す。**`Pane` は UIA と違い、名前が無ければ常に潰して子孫を持ち上げる**（Swing は frame → root pane → layered pane → content panel → scroll pane → viewport → panel … と、ボタン 1 つの手前に無名のコンテナが 6〜10 段入るのが普通で、UIA の規則だと無名 `Pane` のネストだらけになるため。私の判断）。名前付き `Pane`（タイトル付き枠など）は UIA と同じく、残す子孫があるときだけ残す。それ以外のロールは潰して子孫を持ち上げる。
  - `max_depth` は **潰した後の深さ**（出力に残るノードの深さ）で数える。UIA は実際の深さで数えるが、Swing の深いネストでは既定の 12 でタブ内やスクロール内のボタンが落ちてしまうため（私の判断）。巨大ツリーの歯止めは次項のノード数上限が担う。
  - `include_offscreen=false` のとき `showing` でないノードは飛ばす。**例外: メニュー系**（ControlType が `MenuBar` / `Menu` / `MenuItem`、および `MenuItem` の子である `CheckBox` / `RadioButton`）は `showing` でなくても残す。閉じたメニューの項目を `invoke` できるようにするため。これらの `Offscreen` は真のまま報告し、`Rect` には最も近い `showing` な祖先の矩形（通常はメニューバー上の `File` など）を入れる。候補の `region` が妥当になり、座標クリックに落ちたときも破壊的でない。
  - 1 ウィンドウあたりのノード訪問数は 5,000 で打ち切る（JAB は 1 呼び出しごとに IPC なので巨大なツリーで固まらないため）。打ち切ったら警告ログ。
  - 訪問した `AccessibleContext` は、ref の `Handle` として保持するもの以外は `releaseJavaObject` で即時解放する。ref の `Handle` は `(vmID, ac)` を持つ `JavaHandle`。次の `BeginSnapshot` から 2 スナップショット古い ref の handle は解放する（`RefRegistry` に解放フックを追加する。私の判断）。
- `UiNode` への写像: `ControlType` = 4.4、`Name` = `name`（空なら `getVirtualAccessibleName`、それも空なら null）、`AutomationId` = null（Swing に相当物がない）、`Value` = 4.7 の読み取り規則（根と `Edit` / `ComboBox` / `Slider` / `Spinner` / `ProgressBar` のみ）、`Enabled` / `Offscreen` / `Checked` / `Focused` = 4.5、`Rect` = 4.6、`Depth` = 潰した後の深さ。

### 4.4 ロールの写像（`role_en_US` → ControlType）

| JAB ロール | ControlType | 備考 |
|---|---|---|
| `push button`, `toggle button` | `Button` | |
| `menu bar` | `MenuBar` | |
| `menu` | `MenuItem` | `File` のような開閉するメニュー。子を持つ。UIA の Swing 以外のアプリでもトップメニューは `MenuItem` |
| `menu item` | `MenuItem` | |
| `popup menu` | `Menu` | |
| `check box` | `CheckBox` | `JCheckBoxMenuItem` も同じロール |
| `radio button` | `RadioButton` | `JRadioButtonMenuItem` も同じロール |
| `combo box` | `ComboBox` | |
| `text`, `password text` | `Edit` | |
| `label` | `Text` | 親が `list` なら `ListItem`、親が `page tab list` なら `TabItem` |
| `list` | `List` | |
| `tree` | `Tree` | |
| `unknown`（親が `tree`） | `TreeItem` | `JTree` のノードはこのロールで返る |
| `table` | `Table` | 子（セル）は読まない（非ゴール） |
| `page tab list` | `Tab` | |
| `page tab` | `TabItem` | |
| `tool bar` | `ToolBar` | |
| `slider` | `Slider` | |
| `spinbox` | `Spinner` | |
| `progress bar` | `ProgressBar` | |
| `scroll bar` | `ScrollBar` | |
| `hyperlink` | `Hyperlink` | |
| `frame`, `dialog`, `window`, `internal frame` | `Window` | |
| `panel`, `root pane`, `layered pane`, `glass pane`, `scroll pane`, `viewport`, `split pane`, `desktop pane`, `filler`, `tool tip`, `separator`, `canvas`, `icon` | `Pane` | 名前が無ければ潰される（4.3） |
| その他 | `Custom` | `interactive_only` では潰される |

`InteractiveFilter` は変更しない（上の写像先はすべて既存の判定に乗る）。

### 4.5 状態の写像（`states_en_US`）

- `Enabled` = `enabled` を含む
- `Offscreen` = `showing` を含まない
- `Focused` = `focused` を含む
- `Checked` = ControlType が `CheckBox` / `RadioButton`、またはロールが `toggle button` のとき、`checked` を含めば真、含まなければ偽。それ以外は null
- `expanded` / `collapsed` は本件では使わない（将来 `TreeItem` の展開に使える）

### 4.6 座標の変換（DPI）

JAB の座標は Java の画面座標、winuse は物理ピクセル。ウィンドウごとに **自己較正** する（私の判断）:
- 根（frame / dialog）の `AccessibleContextInfo` の矩形 `J` と、同じ HWND の `GetWindowRect` の矩形 `W` を取り、`scale = W.Width / J.Width`（幅 0 なら高さ、どちらも 0 なら 1）、`offset = (W.X - J.X * scale, W.Y - J.Y * scale)` とする。各要素の矩形は `screen = J_elem * scale + offset` で変換し、整数に丸める。
- 根が得られない、または `scale` が 0.5〜4.0 の範囲外なら、`GetDpiForWindow(hwnd) / 96` を `scale`、`offset = 0` とする。
- `showing` でない要素の矩形は変換せず空（4.3 のメニュー例外で祖先の矩形を使う）。

これで DPI 非対応の 32bit Java 8（仮想化座標）と DPI 対応の Java 9+ の両方を同じ式で扱える。

### 4.7 操作と読み取り（`JavaService`）

すべて JAB スレッド上で実行し、`doAccessibleActions` の失敗（戻り値偽、または `failure` インデックス）は例外にする。

| 操作 | 方法 |
|---|---|
| `InvokeAsync` | `getAccessibleActions` に `click` があれば `doAccessibleActions(click)`。無ければ最初のアクション。アクションが無く親が選択（`accessibleSelection`）を持つなら親の `addAccessibleSelectionFromContext(indexInParent)`（タブ、リスト項目、ツリーノード）。それも無ければ `showing` を確認して中心を `InputService.ClickAsync`（`UiaService.InvokeAsync` と同じフォールバック）。メニュー項目はメニューが閉じていても `click` で実行できる（Swing の `doClick`）。 |
| `SetTextAsync` | `setTextContents`。戻りが偽なら `requestFocus` → `ctrl+a` → 入力（UIA 版と同じフォールバック）。 |
| `SetCheckedAsync` | 現在の `Checked` と違えば `click` アクション（最大 3 回）。一致しなければ例外。 |
| `FocusAsync` | `requestFocus`。 |
| `SelectItemAsync(container, item)` | `List` / `Tab` / `Tree`: 子を列挙して名前が一致（完全一致 → 部分一致）する子の `indexInParent` を `clearAccessibleSelectionFromContext` の後 `addAccessibleSelectionFromContext`。`ComboBox`: `togglePopup` アクションで開く → 100 ms 待つ → 同じウィンドウのツリーを再読みして `showing` な `List` の子から名前が一致する項目を探し、その中心をクリック（Swing のコンボのポップアップは選択 API では確定しないため）。見つからなければ `Escape` を送って閉じ、例外。 |
| `GetTextAsync` | `accessibleText` なら `getAccessibleTextInfo.charCount` 分を `getAccessibleTextRange`（上限 4,000 文字）。無ければ `Value`、無ければ名前。 |
| `Value` の読み取り | `Edit`: 上と同じテキスト（500 文字で切る）。`ComboBox`: `getAccessibleSelectionFromContext(0)` の名前。`Slider` / `Spinner` / `ProgressBar`: `getCurrentAccessibleValueFromContext`。 |
| `GetListItemsAsync` | `List` / `Tab` / `Tree`: 子の名前。`ComboBox`: ポップアップを開いて `List` の子の名前を集め、`Escape` で閉じる。 |
| `GetTableAsync` | `NotSupportedException("get_table is not supported for Java elements yet")`。 |

`ToolContext.ExecuteAsync`（`act` の実行）は `IsJavaRef` の分岐を足し、UIA と同じ `kind` の割り当てで `JavaService` を呼ぶ。`CenterOfAsync`（`click` / `hover` / `drag` / `scroll` の ref 指定）と `ActivateOwnerAsync` と `screenshot(target=element)` は `RefKind.Java` を UIA と同様に扱う。

### 4.8 ref、ロケータ、再生、heal

- `JavaLocatorResolver.Resolve(root, path)`: 各セグメントで親の子を全部列挙し、`(Role, Name)` が一致するグループの `Index` 番目を取る。無ければ `Name` だけ一致が 1 件のときそれを取る（UIA 版の緩い後退と同じ。`AutomationId` は無い）。
- `JavaService.ResolveAsync(JavaLocator)`: `WindowService.FindWithFallbackAsync` でウィンドウを見つけ、`getAccessibleContextFromHWND` から辿る。
- `ToolDispatcher.ResolveAsync` に `case JavaLocator` を足す（`UiaLocator` と対称。`lazy` の扱いも同じ）。`WaitTools.ElementExistsAsync` に `RefKind.Java` を足す。
- `ScenarioStep.LocatorEquals` と JSON の多相シリアライズは `$type: "java"` の追加だけで動くはず（ビルダーが確認する）。
- 古い handle の再解決: `LiveAsync(refId)` は handle の `(vmID, ac)` に対して `getAccessibleContextInfo` が成功すれば生きているとみなし、失敗ならロケータで再解決する（UIA の `IsAlive` と同じ）。

### 4.9 ツール層の変更

- `ElementTools.OnRef`: `case RefKind.Java` を追加し `ctx.Java.*` を呼ぶ。`ObserveTools.GetElement` / `ReadTextAsync` / `RequireUia`（`get_list_items` は Java を許可、`get_table` は 4.7 のエラー）も同様。
- ツール説明文の `u{n}` を `u{n} / j{n}` に広げる。`get_ui_tree` の説明に「Java Swing windows are read through Java Access Bridge and return j{n} refs」を足す。
- `ToolContext` に `Java`（`JavaService`）と `IsJavaRef`、ref 正規表現 `^(u|w|win|pt|j)\d+$`。
- `CandidateBuilder.Walk`: 子を持つ `MenuItem` / `Menu` / `MenuBar` も「コンテナ」として扱い、子孫の `Container` にその名前を入れる（`Save` の候補に `container: "File"` が付く）。候補自体にはならない点は変えない（`MenuItem` は候補にもなる。コンテナ名の継承だけ追加）。UIA のメニューは閉じていると子が無いので既存の挙動には影響しない。

### 4.10 Java 側の有効化

- CLI に `winuse jab enable | disable | status` を追加する。
  - `enable`: `%USERPROFILE%\.accessibility.properties` を読み（無ければ作り）、`assistive_technologies` に `com.sun.java.accessibility.AccessBridge` を追加（既存の値は保つ）、`screen_magnifier_present=true` を設定。他の行は保存する。終了後に「対象の Java アプリを再起動してください」と出す。
  - `disable`: その二つの設定を外す（ファイルが空になれば削除）。
  - `status`: ファイルの有無と有効化状態。
- `process_run` は起動する子プロセスの環境に `JAVA_TOOL_OPTIONS` を足す: 既存値があれば末尾に追加、`-Djavax.accessibility.assistive_technologies=` が既にあれば触らない。`WINUSE_JAVA_ACCESSIBILITY=off` で止められる（既定は `on`。ユーザー承認済み: winuse から起動する経路を「何も設定せずに動く」最短経路にするため）。Java 以外のプロセスには無害（読まれない）。副作用として Java プロセスが stderr に `Picked up JAVA_TOOL_OPTIONS: ...` と出す。README に書く。
- MCP の `process_run` の説明文にこの挙動を 1 文足す。

### 4.11 `winuse doctor`

既存の出力に続けて:
```
Java Access Bridge: WindowsAccessBridge-64.dll loaded from <path> (Windows_run ok)
  or: not found (searched: WINUSE_JAB_DLL, <exe dir>, JAVA_HOME, registry, PATH). Run scripts/fetch-jab.ps1.
  or: WINUSE_JAB=off
Java accessibility (user): enabled (%USERPROFILE%\.accessibility.properties)  |  not enabled (run: winuse jab enable)
Java windows: 2
  "MyApp" (pid 1234, 32-bit, SunAwtFrame): bridge responding
      jvm: C:\MyApp\jre\bin\client\jvm.dll (1.8.0_392), JavaAccessBridge-32.dll present
  "Other" (pid 2345, 64-bit, SunAwtFrame): bridge NOT responding (accessibility not enabled when the app started?)
      jvm: C:\...\jvm.dll (17.0.9), JavaAccessBridge-64.dll present
```
- Java ウィンドウ = クラス名 `SunAwt*` のトップレベル。`isJavaWindow` で応答を判定。
- JRE の場所は対象プロセスのモジュール一覧（`EnumProcessModulesEx(LIST_MODULES_ALL)`、64bit から 32bit プロセスも列挙できる）から `jvm.dll` を探して求め、同じ JRE の `bin` に `JavaAccessBridge-32.dll` / `-64.dll` があるか、`jvm.dll` のファイルバージョンを報告する。アクセス権で読めなければ `jvm: (not accessible)`。
- 終了コードは変えない（UIA が動けば 0）。

### 4.12 配布と開発環境: `scripts/fetch-jab.ps1`

- Adoptium API（`https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jre/hotspot/normal/eclipse`）から Temurin 21 JRE の zip を一時ディレクトリに取得し、`bin\WindowsAccessBridge-64.dll` と `legal\jdk.accessibility\` の通知を `-Dest`（既定 `out\`）に展開する（DLL は `out\WindowsAccessBridge-64.dll`、通知は `out\jab-legal\`）。
- `-Dev` を付けると開発用に二つ展開する（どちらも `tools\` 配下、`.gitignore` に追加）: 64bit の JDK 21 を `tools\jdk\` に（統合テスト用の Swing フィクスチャを `javac` でコンパイルし、64bit JVM で起動するため）、**32bit（x86）の Temurin 8 JRE** を `tools\jre8-x86\` に（同じフィクスチャを 32bit の Java 8 で起動し、「21 世代の `WindowsAccessBridge-64.dll` ↔ Java 8 世代の `JavaAccessBridge-32.dll`」という版ずれ・ビット数ずれの組み合わせを開発機で検証するため。JAB のメッセージプロトコルは Java 8 以降変わっておらず、スクリーンリーダーは実際にこの組み合わせで動いているが、本件の主用途なので実測する）。
- `winuse doctor` と README がこのスクリプトを案内する。Temurin のバージョンは `-Version` で変えられる。
- ターゲット環境へは `out\` をまるごとコピーすればよい（DLL が exe の隣にある）。

### 4.13 設定（環境変数）

| 変数 | 値 | 既定 | 意味 |
|---|---|---|---|
| `WINUSE_JAB` | `auto` / `off` | `auto` | `off` で Java Access Bridge を使わない（Java ウィンドウは従来どおり視覚グラウンディング） |
| `WINUSE_JAB_DLL` | ファイルパス | 未設定 | `WindowsAccessBridge-64.dll` の場所を明示 |
| `WINUSE_JAVA_ACCESSIBILITY` | `on` / `off` | `on` | `process_run` が `JAVA_TOOL_OPTIONS` で有効化フラグを渡すかどうか |

### 4.14 ログと失敗の見え方

- JAB が使えない理由はすべて `JavaService.UnavailableReason` に集約し、初回だけ警告。`get_ui_tree` の結果には `java_access_bridge: "<reason>"` を根の隣に入れる（Java ウィンドウで JAB に落ちられなかったときのみ）。AI がすぐ原因を知れるようにするため。
- `act` が Java ウィンドウで候補 0 件になった場合も同じ理由を `reason` に添える（`"no elements (java access bridge: not found)"`）。

## 5. テスト

**単体（Java 不要）**
- `JavaRoleMap`: 表の全行と親依存（`label` in `list` → `ListItem`、`unknown` in `tree` → `TreeItem`）。
- `JavaStates`: 各フラグ、`Checked` の三値。
- `JavaLocatorResolver`: 偽のツリー（`IJabTree` 風の抽象化、またはビルダー/リゾルバを `Func<node, children>` で駆動）で、完全一致・インデックス・名前のみ後退・不在。
- 座標変換: 仮想化座標（scale 1.5）、等倍、根なし後退。
- `JabEnablement`: 空ファイル、既存行の保持、既に有効、`disable` で空になれば削除、`JAVA_TOOL_OPTIONS` の合成（未設定・既存値・既に含む）。
- `JabDllLocator`: 探索順（環境と FS を注入）。
- `CandidateBuilder`: メニュー項目のコンテナ名継承。
- `JavaLocator` の JSON 往復と `LocatorEquals`。
- `ToolDispatcher.ResolveAsync` の `JavaLocator` 分岐（既存のディスパッチャテストの流儀で）。

**統合（`tools\jdk` があるときだけ。無ければ skip。`tools\jre8-x86` もあれば同じテストを 32bit Java 8 でも流す）**
- フィクスチャ `tests/WinUse.Core.Tests/Java/SwingFixture.java`: メニューバー（`File` > `New` / `Save`、`Edit` > チェック付き項目 `Word Wrap`）、ボタン `Increment` と押下回数を表示するラベル、テキスト欄、チェックボックス、コンボボックス（3 項目）、リスト（3 項目）、タブ 2 枚。各操作の結果はラベルに反映する（検証に使う）。テスト起動時に `javac` でコンパイルし、`java -Djavax.accessibility.assistive_technologies=com.sun.java.accessibility.AccessBridge` で起動（ユーザーの `.accessibility.properties` は触らない）。`NotepadFixture` と同じく自分が起動したプロセスだけ殺す。`Desktop` コレクション。
- テスト: ツリーに `MenuItem "Save"`（`File` の子、`Offscreen=true`）と `Button "Increment"` がある / `invoke` でラベルが変わる / 閉じたメニューの `Save` を `invoke` するとラベルが変わる / `set_text` / `set_checked` / `select_item`（リスト、タブ、コンボ）/ ref の再解決（再観測後に古い ref で `invoke`）/ `scenario_save` → `scenario_run` で `invoke` が再生される / `doctor` の Java 行。
- フィクスチャは 64bit Java 21 と 32bit Java 8 の両方で起動して同じテストを通す（`Theory` で JVM を切り替える）。これで 32bit・旧世代 JRE の経路も開発機で検証できる。フィクスチャの Java ソースは Java 8 でコンパイルできる文法に限る（`javac --release 8`）。

## 6. ビルダーに任せること

- `AccessBridgeCalls.h` / `AccessBridgePackages.h`（`tools\jdk\include\win32\bridge\`）を読んで構造体のレイアウトと関数シグネチャを確定する。設計書の API 名は記憶に基づくので、ヘッダが正。
- `JabDispatcher` のキュー起床方式（`PostThreadMessage` か `MsgWaitForMultipleObjectsEx`）。
- `RefRegistry` の解放フックの形。
- 統合テストの待ち時間や settle の調整。
- `doctor` の 32bit プロセスのモジュール列挙が権限で失敗したときの文言。

## 7. リスクと未解決事項

- Java 側で JAB が無効のままだと、ウィンドウは見えても応答しない。`doctor` と `get_ui_tree` の `java_access_bridge` 理由で気づけるようにするが、有効化そのものにはアプリの再起動が要る。
- 一部のランチャ（exe4j、launch4j など）は `JAVA_TOOL_OPTIONS` を子 JVM に渡さないことがある。その場合は `winuse jab enable` を使う。
- Java 7 以前、または `jdk.accessibility` を外して `jlink` した JRE、トリミングされた同梱 JRE（Java 8 なら `bin\JavaAccessBridge-32.dll` と `lib\ext\access-bridge-32.jar` / `jaccess.jar` が必要）では JAB が無い。`doctor` でこれらのファイルの有無を報告する。その場合、JRE に触らずに Swing を認識する手段は無い。
- 対象アプリが別ユーザーや管理者昇格で動いていると、`%USERPROFILE%\.accessibility.properties` は別プロファイルに書かれて効かず、`WM_COPYDATA` も UIPI で遮断される。`doctor` は Java ウィンドウのプロセスの整合性レベルが winuse より高いときにその旨を出す。
- 重量級ポップアップ（画面外にはみ出すメニューやコンボのリスト）は別のトップレベル HWND（`SunAwtWindow`）になる。ツリーは親ウィンドウから辿れるので操作は可能だが、`list_windows` にはタイトル無しのため出ない。
- JAB の呼び出しは 1 件ごとに IPC で、数百要素のウィンドウで数百 ms かかる見込み。5,000 ノードの上限と `max_depth` で抑える。
- 名前の無いボタン（アイコンのみ）は `Name` が null になり、候補ラベルが `Button` だけになる。JAB の `description`（ツールチップ由来）を `Name` の次の後退として使う（私の判断: `name` → `description` → `getVirtualAccessibleName`）。

## 8. 進め方

1. 基盤: `JabNative`、`JabDispatcher`、`JabDllLocator`、`JavaService.GetTreeAsync`、ロール/状態/座標、`RefKind.Java`、`get_ui_tree` と `ObserveAsync` の分岐。ここで Swing のツリーが見える。
2. 操作: `InvokeAsync` 以下の操作、`ExecuteAsync` / `CenterOfAsync` / ツール層の分岐。ここで `act` でボタンとメニューが動く。
3. 再生と運用: ロケータ解決、`ToolDispatcher`、`winuse jab enable`、`process_run` の環境注入、`doctor`、`fetch-jab.ps1`、README / CHANGELOG、統合テスト。

承認後は `superpowers:writing-plans` で実装計画を書く。
