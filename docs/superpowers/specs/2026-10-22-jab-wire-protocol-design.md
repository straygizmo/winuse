# Java Access Bridge ワイヤープロトコルの自前実装 — 旧形式 Bridge の JVM も読む

日付: 2026-10-22
状態: 実装済み（ブランチ `feature/jab-wire-protocol`、計画 `docs/superpowers/plans/2026-10-22-jab-wire-protocol.md`）。本文中の「私の判断」はビルダー（Claude）の判断で、ユーザーが決めた事項と区別する。
前提: PR #4 はマージ済み。`origin/main` から新ブランチを切る。PR #4 の `Jvm.Jre8x86Legacy` テストフィクスチャと `JavaProcessInfo.LoadedBridgeModule` を使う。

## 1. 目的と背景

Java Access Bridge（JAB）は Java 側 DLL（JVM 内）と Windows 側 DLL（支援技術内）が Windows メッセージとメモリマップドファイルで対話する仕組みで、winuse は現在 OpenJDK の `WindowsAccessBridge-64.dll` を同梱・ロードして Swing を読んでいる。

ユーザーの対象環境（32 ビットの Oracle JRE 7u45）では、JVM が読み込む Java 側 DLL が接尾辞なしの `JavaAccessBridge.dll`（OpenJDK のビルド構成 `ACCESSBRIDGE_ARCH_LEGACY`）だった。この旧形式はオブジェクトハンドル `JOBJECT64` が 32 ビット（`jobject`）で、`WindowsAccessBridge-64.dll` が送る 64 ビットハンドル（`jlong`）のパケットとサイズが合わない。Java 側（`JavaAccessBridge.cpp` の `processPackage`）はサイズ不一致のパケットを捨てるため、ハンドルを含まない `isJavaWindow` だけ応答し、`getAccessibleContextFromHWND` 以降は無反応になる。手元でも再現した（32 ビット Temurin 8 に `-Djava.ext.dirs` で `access-bridge.jar` + `jaccess.jar` だけを見せると旧形式 DLL が読み込まれ、`get_ui_tree` が「no accessible context」になる。`-32` だけなら読める）。

Windows 側 DLL を旧形式に差し替える手は使えない。旧形式の Windows 側 DLL（`WindowsAccessBridge.dll`）は 32 ビットで、64 ビットの winuse にはロードできない。32 ビットの補助プロセスは対象マシンに 32 ビットのランタイムを要求する。対象の JRE に `-32` 系ファイルを足す案は、Java 8 用クラスファイルを Java 7 が読めず、顧客環境の JRE を触ることにもなるので除外した。

そこで、Windows 側の役割を winuse 自身が C# で実装する。プロトコルはパケット構造体の受け渡しだけで、レイアウト（ハンドル幅）を JVM ごとに切り替えれば旧形式と現行形式の両方と話せる。副産物として `WindowsAccessBridge-64.dll` の同梱が不要になる。

**ユーザーが決めたこと**
- 自前実装に全面的に置き換える。DLL 経由の経路は残さない（設定での切り替えも不要）。
- 別 PR にする。

## 2. ゴール / 非ゴール / アンチゴール

**ゴール**
- 旧形式の Java 側 Bridge（`JavaAccessBridge.dll`）を読み込んだ 32 ビット JVM の Swing ウィンドウで、`get_ui_tree` 以下の全ツールが現行形式の JVM と同じように動く。
- 現行形式（`JavaAccessBridge-32.dll` / `-64.dll`、Java 8 以降と Java 9+ のモジュール版）は従来どおり動く。既存の Swing 統合テストが 3 つの JVM（Temurin 21 x64、Temurin 8 x86、Temurin 8 x86 旧形式）すべてで通る。
- 複数の JVM（形式が混在していてもよい）に同時に接続できる。
- `WindowsAccessBridge-64.dll`、その同梱（`third_party/jab`）、`WINUSE_JAB_DLL` 設定、DLL の探索ロジックを削除する。
- `winuse doctor` が、接続中の JVM ごとにパケット形式（legacy / wide）を表示する。

**非ゴール**
- JAB のイベント通知（フォーカス・プロパティ変化）。受信はするが捨てる（ポーリングで足りている）。
- `get_table`、JavaFX、SWT。これまでどおり対象外。
- `IJabAccess` の API を広げること。今ある 17 メソッドを同じ意味で提供する。
- 32 ビット Windows 上での動作（winuse は 64 ビット）。

**アンチゴール**
- 現行形式の JVM で性能や挙動が変わること。1 回の API 呼び出しは今と同じく 1 往復の `SendMessage` にとどめる。
- 64 ビット DLL と C# 実装の二重保守。DLL 経路は消す。
- `JavaService` / `JavaTreeBuilder` 以上の層に手が入ること。差し替えは `IJabAccess` の実装と `JabBridge` の初期化に閉じる。

## 3. プロトコル（調査結果。OpenJDK jdk21u `libwindowsaccessbridge` / `common` と jdk8u `sun/bridge` のソースで確認）

### 3.1 握手

- 両者とも非表示のトップレベルウィンドウを持つ。Windows 側は `CreateDialog`、Java 側も同様。登録メッセージは 2 つ: `RegisterWindowMessage("AccessBridge-FromWindows-Hello")` と `RegisterWindowMessage("AccessBridge-FromJava-Hello")`。
- Windows 側は起動時に `PostMessage(HWND_BROADCAST, FromWindowsHello, (WPARAM)自ウィンドウ, 0)`。Java 側はこれを受けると `PostMessage(送信元, FromJavaHello, (WPARAM)Java側ウィンドウ, (LPARAM)Java側ウィンドウ)` を返す。Java 側も起動時に同じ FromJavaHello を `HWND_BROADCAST` に投げるので、後から起動した JVM も見える。**ブロードキャストはトップレベルウィンドウにしか届かない**。メッセージ専用ウィンドウ（`HWND_MESSAGE`）では受信できない。
- `vmID` は Java 側ウィンドウの HWND を `long`（32 ビット）に落とした値。FromJavaHello の `lParam` がそれで、以後の全パケットの `vmID` フィールドに入れる。
- Windows 側は FromJavaHello を受けると JVM インスタンスを作り、`initiateIPC`:
  1. `CreateFileMapping(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, 最大パケット長 + 8, 名前)` で名前付きメモリマップドファイルを作る。名前は ANSI 254 文字以内（OpenJDK は `"AccessBridge-%p-%p.mmf"`。任意でよいが、自ウィンドウと Java 側ウィンドウの組で一意にする）。
  2. ビューの先頭に `"OK?"`（NUL 終端）を書く。
  3. `MemoryMappedFileCreatedPackage { ABHWND64 bridgeWindow; char filename[255]; }`（`bridgeWindow` は自ウィンドウの HWND を 32 ビットに落としたもの）を型 `0x11000` で `WM_COPYDATA` 送信する: `SendMessageTimeout(Java側hwnd, WM_COPYDATA, (WPARAM)自hwnd, &COPYDATASTRUCT{dwData=0, cbData=4+260, lpData=buf}, SMTO_BLOCK|SMTO_NOTIMEOUTIFNOTHUNG, 4000)`。
  4. Java 側（`AccessBridgeATInstance::initiateIPC`）は名前でマッピングを開き、`"OK?"` を確認して `"OK!"` に書き換える。戻ってきたらビューが `"OK!"` で始まることを確認する。違えばその JVM は捨てる。

### 3.2 要求と応答

- 要求は `sendMemoryPackage`: バッファ（先頭 4 バイトが `PackageType`、続いて構造体）をビューに `memcpy`、`view[bufsize] = 0`、`SendMessageTimeout(Java側hwnd, AB_MESSAGE_WAITING, (WPARAM)自hwnd, (LPARAM)bufsize, SMTO_BLOCK|SMTO_NOTIMEOUTIFNOTHUNG, 4000)`。Java 側はビュー上の構造体を処理して結果フィールドを書き込み、`view[bufsize] = 1` を立てる。戻ったら `view[bufsize] == 1` を確認し、ビューからバッファへコピーして結果を読む。
- 定数（`AccessBridgeMessages.h`）: `AB_MEMORY_MAPPED_FILE_SETUP = WM_USER+0x1000`、`AB_MESSAGE_WAITING = WM_USER+0x1001`、`AB_MESSAGE_QUEUED = WM_USER+0x1002`、`AB_DLL_GOING_AWAY = WM_USER+0x1003`（`WM_USER = 0x0400`）。`AB_MEMORY_MAPPED_FILE_OK_QUERY = "OK?"`、`AB_MEMORY_MAPPED_FILE_OK_ANSWER = "OK!"`。
- Java 側は `AB_MESSAGE_WAITING` の送信元（`wParam`）を握手済みの AT ウィンドウ一覧から探し、見つからなければ無視する。パケットは `bufsize == sizeof(PackageType) + sizeof(その構造体)` のときだけ処理する。**ここが旧形式との非互換点**: 構造体サイズがレイアウトで変わる。
- 終了: Windows 側は消える前に各 JVM へ `SendMessage(Java側hwnd, AB_DLL_GOING_AWAY, (WPARAM)自hwnd, 0)`。Java 側も JVM 終了時に `AB_DLL_GOING_AWAY`（`wParam` = Java 側 hwnd）を AT に送り、さらに `WM_COPYDATA` でイベントパケット（`cJavaShutdownPackage` 等）を送ることがある。Windows 側は自ウィンドウ宛の `WM_COPYDATA`（イベント類）を受け取る口を持たなければならないが、捨ててよい。

### 3.3 レイアウト

`AccessBridgePackages.h`（JDK の `include/win32/bridge/`）の構造体を C のアライメント規則で並べる。型の幅: `PackageType` / `jint` / `long` / `BOOL` / `int` = 4、`jboolean` = 1、`wchar_t` = 2、`char` = 1、`ABHWND64` = 4（両形式とも。旧形式は `HWND`、現行は `long`）、`JOBJECT64` = **現行（wide）8 バイト・8 アライン / 旧形式（legacy）4 バイト・4 アライン**。構造体の末尾は最大アライメントの倍数までパディング。バッファ上では `PackageType` の直後（オフセット 4）に構造体が置かれ、そこにアライメント調整は無い（C 側が `buffer + sizeof(PackageType)` にキャストしているだけ）。

使うパケットと型 ID、両形式の構造体サイズ（`PackageType` の 4 バイトを含まない。実装のテストで検算すること）:

| パケット | 型 ID | フィールド | wide | legacy |
|---|---|---|---|---|
| IsJavaWindow | 0x11 | `jint window; jboolean rResult` | 8 | 8 |
| GetAccessibleContextFromHWND | 0x10 | `jint window; long rVMID; JOBJECT64 rAccessibleContext` | 16 | 12 |
| GetAccessibleContextInfo | 0x102 | `long vmID; JOBJECT64 ac; AccessibleContextInfo r` | 6208 | 6196 |
| GetAccessibleChildFromContext | 0x103 | `long vmID; JOBJECT64 ac; jint childIndex; JOBJECT64 r` | 32 | 16 |
| GetAccessibleParentFromContext | 0x104 | `long vmID; JOBJECT64 ac; JOBJECT64 r` | 24 | 12 |
| ReleaseJavaObject | 0x1 | `long vmID; JOBJECT64 object` | 16 | 8 |
| GetAccessBridgeVersion | 0x2 | `long vmID; AccessBridgeVersionInfo r`（wchar 256×4） | 2052 | 2052 |
| GetAccessibleActions | 0x1502 | `long vmID; JOBJECT64 ac; AccessibleActions r`（`jint` + 256×`wchar[256]`） | 131096 | 131084 |
| DoAccessibleActions | 0x1503 | `long vmID; JOBJECT64 ac; AccessibleActionsToDo todo; BOOL rResult; jint failure`（todo = `jint` + 32×`wchar[256]`） | 16416 | 16404 |
| GetAccessibleTextInfo | 0x200 | `long vmID; JOBJECT64 ac; jint x; jint y; AccessibleTextInfo r`（`jint`×3） | 40 | 28 |
| GetAccessibleTextRange | 0x206 | `long vmID; JOBJECT64 ac; jint start; jint end; wchar_t rText[10240]` | 20504 | 20496 |
| GetCurrentAccessibleValueFromContext | 0x300 | `long vmID; JOBJECT64 ac; wchar_t rValue[256]` | 528 | 520 |
| AddAccessibleSelectionFromContext | 0x400 | `long vmID; JOBJECT64 ac; jint index` | 24 | 12 |
| ClearAccessibleSelectionFromContext | 0x401 | `long vmID; JOBJECT64 ac` | 16 | 8 |
| GetAccessibleSelectionFromContext | 0x402 | `long vmID; JOBJECT64 ac; jint index; JOBJECT64 r` | 32 | 16 |
| GetAccessibleSelectionCountFromContext | 0x403 | `long vmID; JOBJECT64 ac; jint rCount` | 24 | 12 |
| SetTextContents | 0x1600 | `long vmID; JOBJECT64 ac; wchar_t text[1024]; BOOL rResult` | 2072 | 2060 |
| GetVirtualAccessibleName | 0x1700 | `long vmID; JOBJECT64 ac; wchar_t rName[1024]; int len` | 2072 | 2060 |
| RequestFocus | 0x1701 | `long vmID; JOBJECT64 ac` | 16 | 8 |
| MemoryMappedFileCreated | 0x11000 | `ABHWND64 bridgeWindow; char filename[255]` | 260 | 260 |

型 ID はヘッダの `enum PackageType` から写した（`cJavaShutdownPackage` = 0x1010）。ビルダーチェックで表の 20 行を C 規則で再計算し、両形式とも一致した。`AccessibleContextInfo` は 6188 バイト（wchar 1024×2 + 256×4、`jint`×11）で両形式共通。既存の `JabNative.AccessibleContextInfo` と同じ。

旧形式の `JOBJECT64` は 32 ビットのポインタで、上位ビットが立つことがある。読むときは **符号なし** 32 ビットとして `long` に拡張し、書くときは下位 32 ビットを書く（`JabHandle.Ac` は `long` のまま）。

## 4. 設計

### 4.1 構成（`src/WinUse.Core/Java/`）

- **`JabWindow`**（新規）: ディスパッチャスレッド上に隠しトップレベルウィンドウを作る（`RegisterClassExW` + `CreateWindowExW`、`WS_EX_TOOLWINDOW`、非表示、`UnmanagedCallersOnly` の WndProc）。`RegisterWindowMessageW` で 2 つの Hello メッセージ ID を取得し、起動時に `HWND_BROADCAST` へ FromWindowsHello を投げる。WndProc は FromJavaHello（→ 接続）、`AB_DLL_GOING_AWAY`（→ 切断）、`WM_COPYDATA`（→ `cJavaShutdownPackage` なら切断、他は捨てる）、`WM_DESTROY` を扱い、残りは `DefWindowProc`。Dispose で各 JVM に `AB_DLL_GOING_AWAY` を送ってウィンドウを壊す。
- **`JabPacket`**（新規）: C レイアウト計算機。フィールドを型（`I32`, `Bool8`, `Obj`, `WChars(n)`, `Chars(n)`, `Struct(...)`）で宣言すると、与えた `JabLayout`（`Wide` / `Legacy`）でオフセットとサイズが決まる。バッファは `byte[]`（または `NativeMemory`）で、`PackageType` を先頭に書き、構造体フィールドをオフセットで読み書きする。各パケットの定義は静的に 1 回ずつ。サイズは §3.3 の表と単体テストで突き合わせる。
- **`JabJvm`**（新規）: 接続済み JVM 1 つ。`JavaHwnd`、`VmId`、`Layout`、`Pid`、メモリマップドファイルとビュー。`Connect()`（§3.1 の握手）、`Send(ReadOnlySpan<byte> request, Span<byte> response)`（§3.2）。`Send` は `SendMessageTimeout` の失敗・タイムアウト・完了フラグ未設定で `false` を返す。`IsAlive` は `IsWindow(JavaHwnd)`。
- **`JabClient : IJabAccess`**（新規。`JabAccess` を置き換える）: JVM 一覧を持ち、17 メソッドを各パケットで実装する。
  - `IsJavaWindow(hwnd)`: 死んだ JVM を外した上で、各 JVM に IsJavaWindow を送り、最初の `rResult != 0` で true。
  - `GetContextFromHwnd(hwnd)`: 各 JVM に送り、`rAccessibleContext != 0` かつ `rVMID == jvm.VmId` のものを返す。
  - 以降のメソッドは `h.VmId` の JVM を探し、無ければ null / false / 空。
  - `GetVersion`: `GetAccessBridgeVersion`。`WindowsDllVersion` には winuse のアセンブリバージョンを入れる（DLL は無いので）。
  - 旧 `JabAccess` の各メソッドの戻り値規約（null の意味、`GetValue` の 256 文字制限、`GetText` の 10240 文字上限、`DoAction` の `failure`）を維持する。
- **`JabBridge`**（変更）: DLL 探索を捨て、`JabDispatcher` のスレッド開始時に `JabWindow` を作る。`IsAvailable` はウィンドウ作成成功。`UnavailableReason` は `WINUSE_JAB=off` か `client window could not be created: <Win32 エラー>`。`DllPath` を `Status`（例: `built-in client (window 0x1A2B, 2 JVMs: pid 4120 64-bit wide, pid 7788 32-bit legacy)`）に置き換え、`JavaService.BridgeDllPath` → `BridgeStatus`。
- **`JabDispatcher`**（変更なし）: メッセージポンプはそのまま。`onThreadStart` で `JabWindow` を作る。

### 4.2 レイアウト判定

Java 側 DLL の名前は三通りある。Java 8 以前の 32 ビット JRE: 旧形式 `JavaAccessBridge.dll` と現行 `JavaAccessBridge-32.dll`。Java 8 以前の 64 ビット: `JavaAccessBridge-64.dll`。**Java 9 以降（モジュール `jdk.accessibility`）は 32/64 ビットとも接尾辞なしの `javaaccessbridge.dll` で、これは現行形式（wide）**。名前だけ（大文字小文字無視）で旧形式と判定すると Java 9+ をすべて誤判定する。PR #4 の `JavaProcessInfo.IsLegacyBridge` はこのバグを持っており、Temurin 21 を doctor が「legacy」と表示している（テストは `Contains("bridge responding")` だけなので検出できなかった）。本件の最初のコミットで直す。

規則: `Legacy` ⇔ 読み込まれたモジュール名が `JavaAccessBridge.dll`（大文字小文字無視）**かつ** その JRE が Java 8 以前（`<jre>\lib\modules` が無い）。それ以外（`-32` / `-64` / Java 9+ の `javaaccessbridge.dll`）は `Wide`。`JavaProcessInfo` に `Layout`（`Wide` / `Legacy` / `Unknown`）を持たせ、doctor と `JabClient` の両方がそれを使う。

判定のタイミング: FromJavaHello を受けた時点で `GetWindowThreadProcessId(javaHwnd)` → pid → `JavaProcessInfo.Inspect(pid)`。`EnumProcessModulesEx` は起動直後に `ERROR_PARTIAL_COPY` で失敗することがあるので、失敗したら `Unknown` のまま接続し、最初のパケット送信前に再判定する（最大 3 回、100 ms 間隔）。それでも取れない（別ユーザー・昇格プロセスなど）場合は `Wide` にして警告ログ。そのような JVM へはそもそもメッセージが届かないので実害は無い（私の判断）。判定結果は JVM インスタンスに保持し、doctor に出す。

`Doctor_ReportsFixtureAsResponding` は JVM ごとに期待する形式名（`wide` / `legacy`）を厳密に確認するように直し、同じ取りこぼしを防ぐ。

### 4.3 スレッドと再入

全 JAB 呼び出しは今までどおりディスパッチャスレッドで行う。`SendMessageTimeout` の待ち中、Java 側から自ウィンドウへ送られる `WM_COPYDATA`（イベント、終了通知）が WndProc に再入する。WndProc は JVM 一覧を変更せず、「切断予定」のフラグを立てるだけにし、実際の削除は `JabClient` の各メソッド入口（呼び出しの外）で行う（OpenJDK の `isVMInstanceChainInUse` と同じ発想）。接続（握手）も WndProc 内で `SendMessageTimeout` を呼ぶが、OpenJDK も同じ構造で問題ない。

### 4.4 削除するもの

- `JabNative.cs`、`JabAccess.cs`、`JabDllLocator.cs`（とテスト）、`third_party/jab/`、`WinUse.csproj` のコピー項目、`WinUseConfig.JabDll` と `WINUSE_JAB_DLL`、`scripts/fetch-jab.ps1` の DLL 部分（`-Dev` 部分は残し、`scripts/fetch-dev-java.ps1` に改名。私の判断）。
- README の「クライアント DLL」節、CHANGELOG の DLL 同梱の記述、旧形式の既知の制限。`JavaProcessInfo.Describe` と doctor の旧形式の説明文は「legacy wire format」と形式名を示すだけにする。

### 4.5 受け入れ基準と実機確認

手元に Oracle JRE 7u45 は無い（Oracle のアーカイブはログインが必要）。**受け入れ基準は `tools/` の 3 本の JVM で全統合テストが通ること**とし、32 ビット Temurin 8 を `-Djava.ext.dirs` で旧形式にしたものを 7u45 の代理とする（JAB 2.0.3 世代の旧形式 DLL と、使う 17 パケットの構造体は同じヘッダ由来で、レイアウトが変わった履歴は無い）。7u45 実機での確認は PR マージ後にユーザーが行う。実機で食い違いが出たときに原因を絞れるよう、`JabClient` は応答が空だった呼び出しをパケット型と JVM の形式付きで Debug ログに出し、`doctor` は接続中の各 JVM に `GetAccessBridgeVersion` を送って `VMversion` / `bridgeJavaDLLVersion` を表示する（この 2 つはレイアウトに依存しないので、7u45 で最低限ここまで通ることが分かる）。

### 4.6 doctor

```
Java Access Bridge: built-in client (window 0x1A2B)
Java accessibility (user): ...
Java windows: 2
  "App" (pid 7788, 32-bit, SunAwtFrame): bridge responding (legacy wire format, Java 1.7.0_45, bridge 2.0.3)
      jvm: C:\...\jre\bin\client\jvm.dll (7.0.450), JavaAccessBridge.dll loaded (legacy), ...
```

## 5. テスト

- **単体（Java 不要）**: `JabPacket` のサイズ・オフセットが §3.3 の表と一致（両形式）。`JabHandle` の legacy 変換（上位ビットが立つ 32 ビット値の往復）。`JabClient` を偽の送信層（`IJabTransport` 相当: 要求バッファを受けて応答バッファを書く）で動かし、`GetContextFromHwnd` が `rVMID` 一致の JVM を選ぶこと、死んだ JVM を外すこと、`Send` 失敗時の戻り値規約（null / false / 空）を確認する。
- **統合（`tools/` の JVM）**: `JabAccessIntegrationTests` と `JavaIntegrationTests` の全ケースを `Jdk21x64`、`Jre8x86`、`Jre8x86Legacy` の 3 本で回す。現在の「旧形式は読めない」テスト 2 本は「旧形式でも読める」に書き換える（doctor は `legacy wire format`、`get_ui_tree` はボタンを返す）。追加: JVM 2 つ（21 と 8 旧形式）を同時に起こして両方の木が読めること。フィクスチャ終了後に `IsJavaWindow` が false になり、次の呼び出しがハングしないこと。
- **全体**: 既知の IME テスト以外が通ること。`dotnet publish` の出力に `WindowsAccessBridge-64.dll` が無いこと。
- **判定の回帰**: `JavaProcessInfo` の単体テストに Java 9+ の `javaaccessbridge.dll`（wide）、Java 8 の `JavaAccessBridge.dll`（legacy）、`JavaAccessBridge-32.dll`（wide）の 3 例。

## 6. リスクと対処

- **構造体サイズのずれ**: 1 バイトでも違うと Java 側が無言で捨てる。単体テストの表と、3 本の統合テストで検出する。表の値はこの設計で C 規則から手計算したもので、実装時に再計算して食い違えば実装側で `sizeof` を優先し、表を直す。
- **ブロードキャストが届かない**: メッセージ専用ウィンドウにしないこと。UIPI（昇格した JVM）は従来どおり対象外。
- **JVM のハング**: `SendMessageTimeout` 4 秒。失敗した呼び出しは null / false を返し、`JavaService` の既存のエラー経路に乗る。
- **プロセス終了時**: `AB_DLL_GOING_AWAY` を送らずに消えても Java 側は次回の送信失敗で片付けるので致命的ではない。

## 7. ビルダーに委ねること

- `JabPacket` の実装方式（オフセット計算機か、`long`/`int` で二重定義した `StructLayout` 構造体か）。表のサイズが一致すればよい。
- メモリマップドファイルの命名、`System.IO.MemoryMappedFiles` と Win32 直叩きのどちらを使うか。
- WndProc の P/Invoke 構成（`NativeMethods` への追加の仕方）。
- 単体テスト用の偽送信層の切り方。
