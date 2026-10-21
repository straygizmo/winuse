# Java Access Bridge 対応 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Swing アプリのウィンドウを Java Access Bridge（JAB）経由で読み、UIA と同じ要素モデル（`j{n}` ref、`UiNode`、候補、ロケータ）に流し込んで、既存ツール（`get_ui_tree` / `act` / `find` / `invoke` / `click` …）と `winuse run` 再生がそのまま動くようにする。

**Architecture:** `WinUse.Core/Java/` に、`WindowsAccessBridge-64.dll` を実行時パスから読む P/Invoke 層（`JabNative` → `IJabAccess`）、メッセージポンプ付き専用スレッド（`JabDispatcher`）、その上の `JavaService`（`UiaService` と同じ公開面）を置く。`JavaService` のツリー構築・ロケータ解決・操作は `IJabAccess` 抽象の上に書き、偽の Swing ツリーで単体テストする。ツール層は `RefKind.Java` の分岐を各所に足すだけで、Java ウィンドウ（クラス名 `SunAwt*`）の観測だけが JAB に向く。実機確認は Temurin 21（64bit）と Temurin 8（32bit）で起動した Swing フィクスチャに対する統合テスト。

**Tech Stack:** .NET 10 (`net10.0-windows`), C# 14, `NativeLibrary` + `delegate* unmanaged` 関数ポインタ、xUnit 2.9。追加 NuGet なし。開発用 Java は `scripts/fetch-jab.ps1 -Dev` が `tools\` に展開する Temurin。

**Spec:** `docs/superpowers/specs/2026-10-22-java-access-bridge-design.md`

## Global Constraints

- ブランチ `feature/java-access-bridge`。TFM `net10.0-windows`、`Nullable` / `ImplicitUsings` enable、`PlatformTarget=x64`。追加 NuGet は入れない。
- 旧 RPA ツール由来の名称・外部 exe をコードにもコメントにも持ち込まない。
- DLL（`WindowsAccessBridge-64.dll`）や JDK をリポジトリにコミットしない。`tools/` を `.gitignore` に足す。
- ref の形式: Java は `j{n}`、ロケータの `$type` は `"java"`。`JavaPathSegment.Role` は JAB の `role_en_US` をそのまま持つ。
- ツール名・説明文は英語。stdio モードで `Console.Out` に書かない。
- JAB の呼び出しはすべて `JabDispatcher` のスレッド上で行う。解放済みの `JOBJECT64` を二度と使わない（JVM がクラッシュしうる）。
- 既存の UIA / DOM / 視覚グラウンディングの挙動を変えない（`CandidateBuilder` のメニューコンテナ継承を除く。これは閉じた UIA メニューには子が無いので影響しない）。
- 環境変数: `WINUSE_JAB`（`auto` / `off`、既定 `auto`）、`WINUSE_JAB_DLL`（パス）、`WINUSE_JAVA_ACCESSIBILITY`（`on` / `off`、既定 `on`）。
- Java 側の有効化フラグ: `-Djavax.accessibility.assistive_technologies=com.sun.java.accessibility.AccessBridge`。ユーザープロファイルのファイルは `%USERPROFILE%\.accessibility.properties`（`assistive_technologies=com.sun.java.accessibility.AccessBridge`、`screen_magnifier_present=true`）。
- 統合テストは `tools\jdk\bin\java.exe` が無ければ早期 return で skip する（既存テストの流儀）。ユーザーの `.accessibility.properties` には触らない。
- コミットメッセージ末尾に `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` を付ける。

## ヘッダから確定した JAB の事実（`AccessBridgeCalls.h` / `AccessBridgePackages.h`、OpenJDK master）

- `vmID` は C の `long` = **32bit**（C# `int`）。`JOBJECT64` = `jlong` = **64bit**（C# `long`）。`jint` = `int`、`BOOL` = `int`。
- エクスポート名は小文字始まり: `Windows_run`, `isJavaWindow`, `getAccessibleContextFromHWND`, `getHWNDFromAccessibleContext`, `getAccessibleContextInfo`, `getAccessibleChildFromContext`, `getAccessibleParentFromContext`, `releaseJavaObject`, `getVersionInfo`, `getAccessibleActions`, `doAccessibleActions`, `getAccessibleTextInfo`, `getAccessibleTextRange`, `getCurrentAccessibleValueFromContext`, `setTextContents`, `requestFocus`, `getVirtualAccessibleName`, `addAccessibleSelectionFromContext`, `clearAccessibleSelectionFromContext`, `getAccessibleSelectionFromContext`, `getAccessibleSelectionCountFromContext`。
- シグネチャ（x64 では呼び出し規約の区別なし）:
  - `void Windows_run()`
  - `BOOL isJavaWindow(HWND)`
  - `BOOL getAccessibleContextFromHWND(HWND, long* vmID, JOBJECT64* ac)`
  - `HWND getHWNDFromAccessibleContext(long vmID, JOBJECT64 ac)`
  - `BOOL getAccessibleContextInfo(long vmID, JOBJECT64 ac, AccessibleContextInfo*)`
  - `JOBJECT64 getAccessibleChildFromContext(long vmID, JOBJECT64 ac, jint i)`（0 = 失敗）
  - `JOBJECT64 getAccessibleParentFromContext(long vmID, JOBJECT64 ac)`
  - `void releaseJavaObject(long vmID, JOBJECT64 obj)`
  - `BOOL getVersionInfo(long vmID, AccessBridgeVersionInfo*)`
  - `BOOL getAccessibleActions(long vmID, JOBJECT64 ac, AccessibleActions*)`
  - `BOOL doAccessibleActions(long vmID, JOBJECT64 ac, AccessibleActionsToDo*, jint* failure)`
  - `BOOL getAccessibleTextInfo(long vmID, JOBJECT64 at, AccessibleTextInfo*, jint x, jint y)`
  - `BOOL getAccessibleTextRange(long vmID, JOBJECT64 at, jint start, jint end, wchar_t* text, short len)`
  - `BOOL getCurrentAccessibleValueFromContext(long vmID, JOBJECT64 av, wchar_t* value, short len)`
  - `BOOL setTextContents(long vmID, JOBJECT64 ac, const wchar_t* text)`
  - `BOOL requestFocus(long vmID, JOBJECT64 ac)`
  - `BOOL getVirtualAccessibleName(long vmID, JOBJECT64 ac, wchar_t* name, int len)`
  - `void addAccessibleSelectionFromContext(long vmID, JOBJECT64 as, int i)` / `void clearAccessibleSelectionFromContext(long vmID, JOBJECT64 as)`
  - `JOBJECT64 getAccessibleSelectionFromContext(long vmID, JOBJECT64 as, int i)` / `int getAccessibleSelectionCountFromContext(long vmID, JOBJECT64 as)`
- 構造体（`Sequential`、`wchar_t` = `char`、`MAX_STRING_SIZE=1024`、`SHORT_STRING_SIZE=256`）:
  - `AccessibleContextInfo { char name[1024]; char description[1024]; char role[256]; char role_en_US[256]; char states[256]; char states_en_US[256]; int indexInParent, childrenCount, x, y, width, height; int accessibleComponent, accessibleAction, accessibleSelection, accessibleText, accessibleInterfaces; }`（6,188 バイト）
  - `AccessibleTextInfo { int charCount, caretIndex, indexAtPoint; }`
  - `AccessibleActionInfo { char name[256]; }`; `AccessibleActions { int actionsCount; AccessibleActionInfo actionInfo[256]; }`（131,076 バイト。ヒープに置く）; `AccessibleActionsToDo { int actionsCount; AccessibleActionInfo actions[32]; }`
  - `AccessBridgeVersionInfo { char VMversion[256]; char bridgeJavaClassVersion[256]; char bridgeJavaDLLVersion[256]; char bridgeWinDLLVersion[256]; }`
  - `accessibleInterfaces` のビット: `cAccessibleValueInterface = 1`（ビルダーはヘッダの `#define cAccessibleValueInterface` で確認する）。
- `Windows_run` は呼び出しスレッドに隠しウィンドウを作り、JVM の応答はそのスレッドのメッセージキューに届く。ポンプを回さないと `isJavaWindow` は常に偽。

## Review Focus

1. Java ウィンドウなのに Access Bridge が応答しない（アプリ起動時に有効化されていなかった）→ `get_ui_tree` は例外を出さず根だけを返し、`java_access_bridge` に理由を入れる。1 秒程度で返る。（Task 5 の `IsJavaWindow_FalseWithReason_WhenBridgeNotResponding`、Task 10 の `GetUiTree_WithJabOff_ReturnsRootAndReason` にテスト）
2. 閉じたメニューの項目 → ツリーに `Offscreen=true` で現れ、`Rect` は親メニューの矩形、`invoke` はメニューを開かずに `click` アクションで実行される。（Task 5 / Task 6 / Task 10 にテスト）
3. 観測を 3 回繰り返した後に古い `j{n}` ref で操作 → 解放済みハンドルは使わず、ロケータで再解決してから操作する。（Task 6 にテスト）
4. 高 DPI（150%）で DPI 非対応の Java 8 → JAB の座標は仮想化されているので、ウィンドウ矩形で較正した座標がクリック位置になる。（Task 3 にテスト）
5. 無名 `JPanel` が 8 段ネストした中のボタン → 既定の `max_depth=12` で落ちずに現れる。（Task 5 / Task 10 にテスト）

---

## ファイル構成

```
scripts/fetch-jab.ps1                                  Temurin から DLL（配布用）と JDK/JRE（開発用）を取得
src/WinUse.Core/Config/WinUseConfig.cs                 WINUSE_JAB / WINUSE_JAB_DLL / WINUSE_JAVA_ACCESSIBILITY
src/WinUse.Core/Refs/ElementRef.cs                     RefKind.Java, JavaLocator, JavaPathSegment
src/WinUse.Core/Refs/RefRegistry.cs                    接頭辞 "j"
src/WinUse.Core/Java/JabNative.cs                      構造体と関数ポインタ（NativeLibrary）
src/WinUse.Core/Java/IJabAccess.cs                     JabHandle, JabContextInfo, IJabAccess
src/WinUse.Core/Java/JabAccess.cs                      IJabAccess の JabNative 実装
src/WinUse.Core/Java/JabDllLocator.cs                  DLL の探索順
src/WinUse.Core/Java/JabDispatcher.cs                  メッセージポンプ付き JAB スレッド
src/WinUse.Core/Java/JabBridge.cs                      IJabBridge（遅延ロード + 可用性 + RunAsync）と実装
src/WinUse.Core/Java/JavaRoleMap.cs                    role_en_US → ControlType
src/WinUse.Core/Java/JavaStates.cs                     states_en_US → フラグ
src/WinUse.Core/Java/JavaGeometry.cs                   座標較正
src/WinUse.Core/Java/JavaTreeBuilder.cs                UiNode ツリー構築
src/WinUse.Core/Java/JavaLocatorResolver.cs            JavaLocator のパス解決
src/WinUse.Core/Java/JavaService.cs                    公開面（UiaService 相当）
src/WinUse.Core/Java/JabEnablement.cs                  .accessibility.properties と JAVA_TOOL_OPTIONS
src/WinUse.Core/Java/JavaProcessInfo.cs                doctor 用: プロセスのビット数と jvm.dll
src/WinUse.Core/Processes/ProcessService.cs            JAVA_TOOL_OPTIONS 注入
src/WinUse.Core/Grounding/CandidateBuilder.cs          メニューのコンテナ名継承
src/WinUse/Tools/ToolContext.cs                        Java, IsJavaRef, 観測の分岐, ExecuteAsync / CenterOfAsync
src/WinUse/Tools/{Observe,Element,Wait,Process}Tools.cs  RefKind.Java 分岐と説明文
src/WinUse/Tools/ToolDispatcher.cs                     case JavaLocator
src/WinUse/Cli/JabCommand.cs                           winuse jab enable|disable|status
src/WinUse/Cli/DoctorCommand.cs                        Java 行
src/WinUse/Program.cs                                  "jab" サブコマンド
tests/WinUse.Core.Tests/Java/FakeJabAccess.cs          偽の Swing ツリー
tests/WinUse.Core.Tests/Java/*Tests.cs                 単体
tests/WinUse.Core.Tests/Java/SwingFixture.java         統合テスト用 Swing アプリ
tests/WinUse.Core.Tests/Java/SwingFixture.cs           コンパイル・起動・後始末
tests/WinUse.Core.Tests/Java/JavaIntegrationTests.cs   実 JVM に対するテスト
```

---

### Task 1: 開発環境と Swing フィクスチャ（`fetch-jab.ps1`、`tools/`、`SwingFixture.java`）

**Files:**
- Create: `scripts/fetch-jab.ps1`
- Create: `tests/WinUse.Core.Tests/Java/SwingFixture.java`
- Create: `tests/WinUse.Core.Tests/Java/SwingFixture.cs`
- Create: `tests/WinUse.Core.Tests/Java/SwingFixtureTests.cs`
- Modify: `.gitignore`（`tools/` を追加）
- Modify: `tests/WinUse.Core.Tests/WinUse.Core.Tests.csproj`（`SwingFixture.java` を `None` + `CopyToOutputDirectory=PreserveNewest`）

**Interfaces:**
- Produces: `internal enum Jvm { Jdk21x64, Jre8x86 }`; `internal static class Jvms { static string RepoRoot; static string? JavaExe(Jvm jvm); static string? Javac; static bool Available(Jvm jvm); }`; `internal sealed class SwingFixture : IDisposable { static Task<SwingFixture> LaunchAsync(WindowService windows, Jvm jvm); WindowInfo Window; int Pid; }`。フィクスチャのウィンドウタイトルは `WinUse Swing Fixture`、クラス名 `SunAwtFrame`。

- [ ] **Step 1: `scripts/fetch-jab.ps1` を書く**

パラメータ: `-Dest` (既定 `out`)、`-Version` (既定 `21`)、`-Dev` (switch)。
1. `https://api.adoptium.net/v3/binary/latest/$Version/ga/windows/x64/jre/hotspot/normal/eclipse` を一時 zip に取得し展開。`bin\WindowsAccessBridge-64.dll` を `$Dest\WindowsAccessBridge-64.dll` に、`legal\jdk.accessibility\*` を `$Dest\jab-legal\` にコピー。
2. `-Dev` のとき: `.../$Version/ga/windows/x64/jdk/hotspot/normal/eclipse` を `tools\jdk\`（zip の最上位フォルダを剥がす）に、`.../8/ga/windows/x86/jre/hotspot/normal/eclipse` を `tools\jre8-x86\` に展開。`tools\jre8-x86\bin\JavaAccessBridge-32.dll` と `tools\jre8-x86\lib\ext\access-bridge-32.jar` が無ければ Warning を出す。
3. 最後に取得した各ファイルのパスを出力する。`$ErrorActionPreference = 'Stop'`。

- [ ] **Step 2: スクリプトを実行し、成果物を確認する**

Run: `pwsh -File scripts/fetch-jab.ps1 -Dev`
Expected: `out\WindowsAccessBridge-64.dll`、`tools\jdk\bin\javac.exe`、`tools\jre8-x86\bin\java.exe` が存在し、`tools\jre8-x86\bin\JavaAccessBridge-32.dll` が存在する（無ければ Warning が出て、32bit の統合テストは skip になる旨を計画の進捗台帳に記す）。

- [ ] **Step 3: `tests/WinUse.Core.Tests/Java/SwingFixture.java` を書く（Java 8 文法、`public class SwingFixture`）**

`JFrame("WinUse Swing Fixture")`、`setBounds(100, 100, 640, 480)`、`EXIT_ON_CLOSE`。
- メニューバー: `JMenu "File"` に `JMenuItem "New"`, `"Save"`; `JMenu "Edit"` に `JCheckBoxMenuItem "Word Wrap"`。
- NORTH: `JButton "Increment"`（押下回数 n でステータスを `status: increment n` に）、`JTextField` 初期値 `hello`、アクセシブル名 `Name`（`getAccessibleContext().setAccessibleName("Name")`）、`JCheckBox "Enabled"`、`JComboBox {"One","Two","Three"}`。
- CENTER: `JTabbedPane` タブ `"First"`（`JList {"Alpha","Beta","Gamma"}`）と `"Second"`（無名 `JPanel` を 8 段ネストした中に `JButton "Deep"`）。
- SOUTH: `JLabel` ステータス、初期 `status: ready`。各操作で更新: `status: new`, `status: save`, `status: wordwrap on|off`, `status: increment n`, `status: check on|off`, `status: combo Two`, `status: list Beta`, `status: tab Second`, `status: deep`。

- [ ] **Step 4: `tests/WinUse.Core.Tests/Java/SwingFixture.cs` を書く**

- `Jvms.RepoRoot`: `AppContext.BaseDirectory` から上に `winuse.sln` を探す。`JavaExe(Jdk21x64) = tools\jdk\bin\java.exe`、`JavaExe(Jre8x86) = tools\jre8-x86\bin\java.exe`、`Javac = tools\jdk\bin\javac.exe`。
- `SwingFixture.LaunchAsync(windows, jvm)`: `%TEMP%\winuse-swing-fixture\` に `javac --release 8 -d <dir> SwingFixture.java` で一度だけコンパイル（`SwingFixture.class` が .java より新しければ省略）。`java -Djavax.accessibility.assistive_technologies=com.sun.java.accessibility.AccessBridge -cp <dir> SwingFixture` を起動し、`NotepadFixture` と同じ要領で新しい `WinUse Swing Fixture` ウィンドウを 15 秒待つ。`Dispose` は自分の pid だけ `Kill`。
- `[Collection("Desktop")]` を使う側に要求する（コメントで明記）。

- [ ] **Step 5: 起動テストを書く**

```csharp
[Collection("Desktop")]
public class SwingFixtureTests
{
    [Theory] [InlineData(Jvm.Jdk21x64)] [InlineData(Jvm.Jre8x86)]
    public async Task Fixture_Launches_AsSunAwtFrame(Jvm jvm)
    {
        if (!Jvms.Available(jvm)) return; // no JVM in tools/: skip
        using var d = new AutomationDispatcher();
        var windows = new WindowService(d);
        using var f = await SwingFixture.LaunchAsync(windows, jvm);
        Assert.Equal("WinUse Swing Fixture", f.Window.Title);
        Assert.Equal("SunAwtFrame", f.Window.ClassName);
    }
}
```

- [ ] **Step 6: テストを実行する**

Run: `dotnet test tests/WinUse.Core.Tests --filter "FullyQualifiedName~SwingFixtureTests"`
Expected: PASS（2 ケース）。

- [ ] **Step 7: Commit**

```bash
git add scripts/fetch-jab.ps1 .gitignore tests/WinUse.Core.Tests/Java tests/WinUse.Core.Tests/WinUse.Core.Tests.csproj
git commit -m "test(java): Swing fixture and fetch-jab.ps1 for Access Bridge development"
```

---

### Task 2: 基盤 — `RefKind.Java`、`JavaLocator`、設定値

**Files:**
- Modify: `src/WinUse.Core/Refs/ElementRef.cs`
- Modify: `src/WinUse.Core/Refs/RefRegistry.cs:19-28`
- Modify: `src/WinUse.Core/Config/WinUseConfig.cs`
- Test: `tests/WinUse.Core.Tests/Refs/RefRegistryTests.cs`, `tests/WinUse.Core.Tests/Config/WinUseConfigTests.cs`

**Interfaces:**
- Produces: `RefKind.Java`（接頭辞 `j`）; `public sealed record JavaPathSegment(string Role, string? Name, int Index);` `public sealed record JavaLocator(WindowLocator Window, IReadOnlyList<JavaPathSegment> Path) : Locator;`（`[JsonDerivedType(typeof(JavaLocator), "java")]`）; `public enum JabMode { Auto, Off }`; `WinUseConfig` に `JabMode Jab = JabMode.Auto`, `string? JabDll = null`, `bool JavaAccessibility = true`。

- [ ] **Step 1: テストを書く**

`RefRegistryTests` に:
```csharp
[Fact] public void Register_JavaRef_UsesJPrefix() { ... Assert.Equal("j1", reg.Register(RefKind.Java, new JavaLocator(w, []), null).Id); }
[Fact] public void JavaLocator_RoundTripsWithDiscriminatorAndRole()
{
    Locator l = new JavaLocator(new WindowLocator("x", "javaw", "SunAwtFrame"), [new JavaPathSegment("push button", "Save", 1)]);
    var json = JsonSerializer.Serialize(l);
    Assert.Contains("\"$type\":\"java\"", json);
    var back = Assert.IsType<JavaLocator>(JsonSerializer.Deserialize<Locator>(json));
    Assert.Equal(new JavaPathSegment("push button", "Save", 1), Assert.Single(back.Path));
}
```
`WinUseConfigTests` に: `WINUSE_JAB=off` → `Jab == JabMode.Off`; 未設定 → `Auto`; `WINUSE_JAB=maybe` → `InvalidOperationException`; `WINUSE_JAB_DLL=C:\x\WindowsAccessBridge-64.dll` → `JabDll`; `WINUSE_JAVA_ACCESSIBILITY=off` → `JavaAccessibility == false`; `on` / 未設定 → true; `WINUSE_JAVA_ACCESSIBILITY=yes` → `InvalidOperationException`。

- [ ] **Step 2: 失敗を確認する**

Run: `dotnet test tests/WinUse.Core.Tests --filter "FullyQualifiedName~RefRegistryTests|FullyQualifiedName~WinUseConfigTests"`
Expected: コンパイルエラー（`RefKind.Java` 未定義）。

- [ ] **Step 3: 実装する**

`ElementRef.cs` に enum 値と record を追加。`RefRegistry` の switch に `RefKind.Java => "j"`。`WinUseConfig` に 3 プロパティと `Load` の解析（`ParseEnum<JabMode>("WINUSE_JAB", ...)`、`ParseOnOff("WINUSE_JAVA_ACCESSIBILITY", raw)`: `on`/`true` → true、`off`/`false` → false、他は `InvalidOperationException`）。

- [ ] **Step 4: テストを通す**

Run: 同上。Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/WinUse.Core/Refs src/WinUse.Core/Config tests/WinUse.Core.Tests/Refs tests/WinUse.Core.Tests/Config
git commit -m "feat(java): RefKind.Java, JavaLocator and WINUSE_JAB settings"
```

---

### Task 3: 純粋関数 — `JavaRoleMap`、`JavaStates`、`JavaGeometry`

**Files:**
- Create: `src/WinUse.Core/Java/JavaRoleMap.cs`, `src/WinUse.Core/Java/JavaStates.cs`, `src/WinUse.Core/Java/JavaGeometry.cs`
- Test: `tests/WinUse.Core.Tests/Java/JavaRoleMapTests.cs`, `JavaStatesTests.cs`, `JavaGeometryTests.cs`

**Interfaces:**
- Produces:
  - `public static class JavaRoleMap { public static string ControlTypeOf(string role, string? parentRole); public static bool IsMenuFamily(string controlType, string? parentControlType); }`
  - `public readonly record struct JavaStateFlags(bool Enabled, bool Showing, bool Focused, bool Checked);` `public static class JavaStates { public static JavaStateFlags Parse(string statesEnUs); public static bool? CheckedOf(JavaStateFlags s, string controlType, string role); }`
  - `public readonly record struct JavaTransform(double Scale, double OffsetX, double OffsetY) { public Rect Apply(int x, int y, int w, int h); }` `public static class JavaGeometry { public static JavaTransform Calibrate(Rect window, (int X, int Y, int W, int H)? root, double dpiScale); }`

- [ ] **Step 1: テストを書く**

`JavaRoleMapTests`: 仕様 4.4 の表を `[Theory]` で全行（`("push button", null, "Button")`, `("toggle button", null, "Button")`, `("menu bar", null, "MenuBar")`, `("menu", null, "MenuItem")`, `("menu item", null, "MenuItem")`, `("popup menu", null, "Menu")`, `("check box", null, "CheckBox")`, `("radio button", null, "RadioButton")`, `("combo box", null, "ComboBox")`, `("text", null, "Edit")`, `("password text", null, "Edit")`, `("label", null, "Text")`, `("label", "list", "ListItem")`, `("label", "page tab list", "TabItem")`, `("list", null, "List")`, `("tree", null, "Tree")`, `("unknown", "tree", "TreeItem")`, `("unknown", null, "Custom")`, `("table", null, "Table")`, `("page tab list", null, "Tab")`, `("page tab", null, "TabItem")`, `("tool bar", null, "ToolBar")`, `("slider", null, "Slider")`, `("spinbox", null, "Spinner")`, `("progress bar", null, "ProgressBar")`, `("scroll bar", null, "ScrollBar")`, `("hyperlink", null, "Hyperlink")`, `("frame", null, "Window")`, `("dialog", null, "Window")`, `("internal frame", null, "Window")`, `("panel", null, "Pane")`, `("root pane", null, "Pane")`, `("viewport", null, "Pane")`, `("filler", null, "Pane")`, `("something new", null, "Custom")`）。`IsMenuFamily`: `("MenuBar", null) → true`, `("MenuItem", "MenuBar") → true`, `("Menu", null) → true`, `("CheckBox", "MenuItem") → true`, `("RadioButton", "Menu") → true`, `("CheckBox", "Pane") → false`, `("Button", "MenuItem") → false`。

`JavaStatesTests`: `Parse("enabled,focusable,visible,showing,checked")` → `Enabled, Showing, Checked` true、`Focused` false; `Parse("")` → 全 false; 空白付き `"enabled, showing"` も可。`CheckedOf`: CheckBox+checked → true、CheckBox 無し → false、RadioButton 無し → false、`("Button", "toggle button")` checked → true、`("Button", "push button")` → null、`Edit` → null。

`JavaGeometryTests`:
- 仮想化（150%）: `Calibrate(window: (300, 150, 960, 720), root: (200, 100, 640, 480), dpiScale: 1.5)` → `Scale 1.5`, `Apply(210, 110, 100, 20)` == `Rect(315, 165, 150, 30)`。
- 等倍: `Calibrate((100, 100, 640, 480), (100, 100, 640, 480), 1.0).Apply(110, 120, 50, 20)` == `Rect(110, 120, 50, 20)`。
- 根なし → `Scale == dpiScale`, offset 0。
- 範囲外（root 幅 1 → scale 640）→ `Scale == dpiScale`, offset 0。
- root 幅 0・高さ 480・window 高さ 720 → scale 1.5（高さで計算）。

- [ ] **Step 2: 失敗を確認する**

Run: `dotnet test tests/WinUse.Core.Tests --filter "FullyQualifiedName~WinUse.Core.Tests.Java"`
Expected: コンパイルエラー。

- [ ] **Step 3: 実装する**

`JavaRoleMap`: `Dictionary<string,string>`（ordinal）+ 親依存の 3 規則。`IsMenuFamily(ct, parentCt)` = `ct is "MenuBar" or "Menu" or "MenuItem"` または（`ct is "CheckBox" or "RadioButton"` かつ `parentCt is "MenuItem" or "Menu"`）。`JavaStates.Parse`: `Split(',')` + `Trim`、ordinal 比較。`JavaGeometry.Calibrate`: 仕様 4.6 の式。範囲 `0.5 <= scale <= 4.0` 以外は後退。

- [ ] **Step 4: テストを通す**

Run: 同上。Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/WinUse.Core/Java tests/WinUse.Core.Tests/Java
git commit -m "feat(java): role map, state flags and coordinate calibration"
```

---

### Task 4: ネイティブ層 — `JabNative`、`IJabAccess` / `JabAccess`、`JabDllLocator`、`JabDispatcher`、`JabBridge`

**Files:**
- Create: `src/WinUse.Core/Java/JabNative.cs`, `IJabAccess.cs`, `JabAccess.cs`, `JabDllLocator.cs`, `JabDispatcher.cs`, `JabBridge.cs`
- Test: `tests/WinUse.Core.Tests/Java/JabDllLocatorTests.cs`, `JabDispatcherTests.cs`, `JabAccessIntegrationTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public readonly record struct JabHandle(int VmId, long Ac);
  public sealed record JabContextInfo(string? Name, string? Description, string Role, string States, int IndexInParent, int ChildrenCount,
      int X, int Y, int Width, int Height, bool HasAction, bool HasSelection, bool HasText, bool HasValue);
  public sealed record JabVersionInfo(string VmVersion, string JavaDllVersion, string WindowsDllVersion);
  public interface IJabAccess
  {
      bool IsJavaWindow(nint hwnd);
      JabHandle? GetContextFromHwnd(nint hwnd);
      JabContextInfo? GetInfo(JabHandle h);
      JabHandle? GetChild(JabHandle h, int index);
      void Release(JabHandle h);
      IReadOnlyList<string> GetActions(JabHandle h);
      bool DoAction(JabHandle h, string action);          // false when the bridge reports failure
      string? GetText(JabHandle h, int maxChars);          // null when no AccessibleText
      string? GetValue(JabHandle h);                       // getCurrentAccessibleValueFromContext
      bool SetText(JabHandle h, string text);
      bool RequestFocus(JabHandle h);
      void AddSelection(JabHandle h, int index);
      void ClearSelection(JabHandle h);
      JabHandle? GetSelection(JabHandle h, int index);
      int GetSelectionCount(JabHandle h);
      string? GetVirtualName(JabHandle h);
      JabVersionInfo? GetVersion(int vmId);
  }
  public static class JabDllLocator
  {
      public const string FileName = "WindowsAccessBridge-64.dll";
      /// <returns>path or null; <paramref name="searched"/> lists the places tried, for doctor</returns>
      public static string? Locate(string? configuredPath, string exeDirectory, Func<string, string?> env,
          Func<string, bool> fileExists, Func<IEnumerable<string>> registryJavaHomes, out IReadOnlyList<string> searched);
  }
  public sealed class JabDispatcher : IDisposable
  {
      public JabDispatcher(Action onThreadStart);   // runs Windows_run on the new thread before serving work
      public bool IsOnDispatcherThread { get; }
      internal uint ThreadId { get; }               // native thread id, for PostThreadMessageW
      internal int PumpedMessages { get; }          // every message the loop dispatched (tests)
      public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct = default);
      public Task RunAsync(Action work, CancellationToken ct = default);
  }
  public interface IJabBridge : IDisposable
  {
      bool IsAvailable { get; }            // triggers the lazy load on first use
      string? UnavailableReason { get; }   // "WINUSE_JAB=off" | "not found (searched: ...)" | "load failed: ..." 
      string? DllPath { get; }
      Task<T> RunAsync<T>(Func<IJabAccess, T> work);
  }
  public sealed class JabBridge(WinUseConfig cfg, string exeDirectory, ILogger? log = null) : IJabBridge
  ```
- `JabBridge.IsAvailable` は初回に `JabDllLocator.Locate` → `NativeLibrary.Load` → `JabDispatcher(Windows_run)` を行い、失敗理由を `UnavailableReason` に残す。`Jab == JabMode.Off` なら探索せず `"WINUSE_JAB=off"`。

- [ ] **Step 1: `JabDllLocatorTests` を書く**

```csharp
[Fact] public void Locate_PrefersConfiguredPath()       // configured exists → returned first
[Fact] public void Locate_ThenExeDir_ThenJavaHome_ThenRegistry()  // 順序を fileExists の述語で切り替えて検証
[Fact] public void Locate_ReturnsNullAndListsSearched_WhenMissing()  // searched に 5 項目: configured(or "WINUSE_JAB_DLL not set"), exe dir, JAVA_HOME, registry, PATH
```
PATH 段階は `Locate` では「`FileName` そのまま（LoadLibrary 探索に任せる）」を最後の候補として返すのではなく、**存在確認できないので `searched` に `PATH` と記し、他が無ければ null** を返す。`JabBridge` は null のとき最後に `NativeLibrary.TryLoad(FileName, out _)` を試す。

- [ ] **Step 2: `JabDispatcherTests` を書く**

```csharp
[Fact] public async Task RunAsync_ExecutesOnDispatcherThread_AfterStartHook()
{
    int startThread = 0;
    using var d = new JabDispatcher(() => startThread = Environment.CurrentManagedThreadId);
    var t = await d.RunAsync(() => Environment.CurrentManagedThreadId);
    Assert.Equal(startThread, t);
    Assert.NotEqual(Environment.CurrentManagedThreadId, t);
}
[Fact] public async Task RunAsync_PumpsWindowMessages()   // PostThreadMessageW(d.ThreadId, WM_APP + 1, 0, 0) → await d.RunAsync(() => 0) → Assert.True(d.PumpedMessages >= 1)
[Fact] public async Task Dispose_StopsThread_AndRejectsNewWork()  // ObjectDisposedException
[Fact] public async Task RunAsync_PropagatesExceptions()
```

- [ ] **Step 3: 失敗を確認する**

Run: `dotnet test tests/WinUse.Core.Tests --filter "FullyQualifiedName~JabDllLocatorTests|FullyQualifiedName~JabDispatcherTests"`
Expected: コンパイルエラー。

- [ ] **Step 4: `JabNative` を実装する**

`unsafe static class JabNative`: 上記「ヘッダから確定した事実」の構造体（`fixed char` バッファ、`StructLayout(LayoutKind.Sequential)`）と、`Load(string path)` で `NativeLibrary.Load` + `NativeLibrary.GetExport` して `delegate* unmanaged<...>` フィールドに入れる `JabFunctions` クラス。`AccessibleActions` は `NativeMemory.AllocZeroed` で確保して `finally` で解放。文字列化は `new string(ptr)` を最初の `'\0'` まで。

- [ ] **Step 5: `JabAccess : IJabAccess` を実装する**

- `GetInfo`: `getAccessibleContextInfo` が偽なら null。`HasValue = (accessibleInterfaces & 1) != 0`。空文字の `name` / `description` は null。
- `GetText(h, max)`: `getAccessibleTextInfo(..., x: 0, y: 0)` → `charCount`; `n = min(charCount, max)`; バッファ `n + 1`; `getAccessibleTextRange(0, n - 1, buf, (short)(n + 1))`。`charCount == 0` → `""`。
- `GetActions`: `actionsCount` 件の名前。`DoAction`: `AccessibleActionsToDo` に 1 件、戻り値 `BOOL`。
- `GetValue` / `GetVirtualName`: 1024 文字バッファ。
- `GetChild` / `GetSelection`: 0 が返れば null。

- [ ] **Step 6: `JabDllLocator`、`JabDispatcher`、`JabBridge` を実装する**

- `JabDllLocator.Locate`: 仕様 4.2 の順。レジストリは `registryJavaHomes()`（本番では `HKLM\SOFTWARE\JavaSoft\{JDK,Java Runtime Environment,JRE}` 配下の `JavaHome` 値を列挙する `JabDllLocator.RegistryJavaHomes()`）。
- `JabDispatcher`: 専用スレッド（`IsBackground`、名前 `winuse-jab`）。開始時 `PeekMessage` でキューを作り、`onThreadStart()` を呼び、`ManualResetEventSlim` で準備完了を通知。ループは `GetMessage`; `WM_APP` でキュー（`ConcurrentQueue<Action>`）を全部実行、`WM_QUIT` で終了。投入は `Enqueue` + `PostThreadMessage(threadId, WM_APP, 0, 0)`。`Dispose` は `PostThreadMessage(WM_QUIT)` と `Join(5 s)`。`NativeMethods` に `PeekMessageW` / `GetMessageW` / `TranslateMessage` / `DispatchMessageW` / `PostThreadMessageW` を追加。
- `JabBridge`: `Lazy<...>` で一度だけ初期化。`Windows_run` を `onThreadStart` に渡す。`RunAsync(work)` は `IsAvailable` 偽なら `InvalidOperationException(UnavailableReason)`。

- [ ] **Step 7: 単体テストを通す**

Run: Step 3 と同じ。Expected: PASS。

- [ ] **Step 8: `JabAccessIntegrationTests` を書く（実 JVM）**

```csharp
[Collection("Desktop")]
public class JabAccessIntegrationTests
{
    [Theory] [InlineData(Jvm.Jdk21x64)] [InlineData(Jvm.Jre8x86)]
    public async Task Bridge_SeesFixtureWindow_AndReadsRootInfo(Jvm jvm)
    {
        if (!Jvms.Available(jvm) || !File.Exists(Path.Combine(Jvms.RepoRoot, "out", JabDllLocator.FileName))) return;
        var cfg = TestConfig.Default with { JabDll = Path.Combine(Jvms.RepoRoot, "out", JabDllLocator.FileName) };
        using var d = new AutomationDispatcher(); var windows = new WindowService(d);
        using var f = await SwingFixture.LaunchAsync(windows, jvm);
        using var bridge = new JabBridge(cfg, AppContext.BaseDirectory);
        Assert.True(bridge.IsAvailable, bridge.UnavailableReason);
        var isJava = false;
        for (var i = 0; i < 20 && !isJava; i++) { isJava = await bridge.RunAsync(j => j.IsJavaWindow(f.Window.Hwnd)); if (!isJava) await Task.Delay(100); }
        Assert.True(isJava, "isJavaWindow stayed false (accessibility not enabled in the fixture JVM?)");
        var (info, version) = await bridge.RunAsync(j =>
        {
            var root = j.GetContextFromHwnd(f.Window.Hwnd)!.Value;
            try { return (j.GetInfo(root), j.GetVersion(root.VmId)); } finally { j.Release(root); }
        });
        Assert.Equal("frame", info!.Role);
        Assert.Equal("WinUse Swing Fixture", info.Name);
        Assert.True(info.ChildrenCount > 0);
        Assert.NotNull(version);
    }
}
```

- [ ] **Step 9: 統合テストを通す**

Run: `dotnet test tests/WinUse.Core.Tests --filter "FullyQualifiedName~JabAccessIntegrationTests"`
Expected: PASS（2 ケース）。失敗したら構造体レイアウト（`sizeof(AccessibleContextInfo) == 6188`）とエクスポート名を疑う。

- [ ] **Step 10: Commit**

```bash
git add src/WinUse.Core/Java src/WinUse.Core/Windows/NativeMethods.cs tests/WinUse.Core.Tests/Java
git commit -m "feat(java): WindowsAccessBridge-64 interop, pumped dispatcher and lazy bridge"
```

---

### Task 5: `JavaTreeBuilder`、`JavaLocatorResolver`、`JavaService` の読み取り側

**Files:**
- Create: `src/WinUse.Core/Java/JavaTreeBuilder.cs`, `JavaLocatorResolver.cs`, `JavaService.cs`
- Create: `tests/WinUse.Core.Tests/Java/FakeJabAccess.cs`, `FakeJabBridge.cs`
- Test: `tests/WinUse.Core.Tests/Java/JavaTreeBuilderTests.cs`, `JavaLocatorResolverTests.cs`, `JavaServiceReadTests.cs`

**Interfaces:**
- Consumes: Task 3 の 3 クラス、Task 4 の `IJabAccess` / `IJabBridge`、`RefRegistry`、`WindowService`、`InputService`、`UiNode` / `TreeOptions`。
- Produces:
  ```csharp
  internal sealed class JavaTreeBuilder(IJabAccess jab, RefRegistry refs, WindowLocator wloc, TreeOptions opt, JavaTransform tx, Action<string, JabHandle> keep)
  {
      public const int MaxNodes = 5000;
      public bool Truncated { get; }
      public UiNode BuildRoot(JabHandle root, IReadOnlyList<JavaPathSegment> basePath);   // registers RefKind.Java refs (Handle = null), calls keep(refId, handle) for each kept node
  }
  internal static class JavaLocatorResolver
  {
      public static JabHandle? Resolve(IJabAccess jab, JabHandle root, IReadOnlyList<JavaPathSegment> path); // releases every intermediate handle; the caller owns the result
  }
  public sealed class JavaService(IJabBridge bridge, RefRegistry refs, WindowService windows, InputService input, ILogger? log = null) : IDisposable
  {
      // test seams (defaults use windows / input)
      internal Func<nint, Task<WindowInfo?>> WindowLookup { get; set; }
      internal Func<int, int, Task> ClickAt { get; set; }
      internal Func<KeyChord, Task> Press { get; set; }
      public static bool LooksLikeJavaWindow(WindowInfo w);           // ClassName starts with "SunAwt"
      public Task<bool> IsJavaWindowAsync(WindowInfo w);              // false when not SunAwt*, bridge unavailable, or isJavaWindow stays false (retries up to 1 s only until the first success ever)
      public string? UnavailableReason(WindowInfo w);                 // null when IsJavaWindowAsync was true; else bridge reason or "bridge not responding (accessibility not enabled when the app started? run: winuse doctor)"
      public Task<UiNode> GetTreeAsync(nint hwnd, TreeOptions opt);
      public Task<UiNode> GetElementAsync(string refId);
      public Task<Rect> GetRectAsync(string refId);
      public Task<string> GetTextAsync(string refId);
      public Task<JabHandle?> ResolveAsync(JavaLocator loc);          // caller must not keep it beyond the next call; used by ToolDispatcher through RegisterResolvedAsync
      public Task<string?> RegisterResolvedAsync(JavaLocator loc, bool lazy);  // registers a j{n} ref; null when not found and !lazy
  }
  ```
- `FakeJabAccess`: `FakeNode(string Role, string? Name, string States = "enabled,visible,showing", int X = 0, int Y = 0, int W = 10, int H = 10, List<FakeNode>? Children, string[]? Actions, string? Text, string? Value, string? Description)` の木を持ち、ハンドルは連番。`Released: HashSet<long>`; 解放済みハンドルへのアクセスは `InvalidOperationException("use after release")`。`DoAction` は `(handle, action)` を `Actions` リストに記録し、`OnAction: Func<FakeNode,string,bool>?` で副作用を注入。`FakeJabBridge(FakeJabAccess, bool available = true, string? reason = null)` は `RunAsync` を同期実行。

- [ ] **Step 1: `JavaTreeBuilderTests` を書く**

偽ツリー: `frame "WinUse Swing Fixture"` → `root pane` → `layered pane` → `panel` → [`menu bar` → `menu "File"` → (`menu item "New"`, `menu item "Save"`; どちらも states `enabled,visible`、X=Y=-1) ; `panel` → `push button "Increment"`(X 120,Y 40,W 80,H 24); 8 段の無名 `panel` → `push button "Deep"`; `list` → 3 つの `label`; `page tab list` → 2 つの `page tab`; `text` (Text "hello", Name "Name"); `label "status: ready"`]。`JavaTransform(1, 0, 0)`。
```csharp
[Fact] public void Build_CollapsesUnnamedPanes_AndKeepsInteractive()  // 根の直下に MenuBar, Button "Increment", Button "Deep", List, Tab, Edit, Text "status: ready" が並ぶ（Pane が一つも無い）
[Fact] public void Build_MenuItemsInClosedMenu_AreKeptOffscreen_WithParentRect()  // MenuBar → MenuItem "File" → MenuItem "Save": Offscreen true, Rect == "File" の Rect
[Fact] public void Build_DeepButton_WithinDefaultMaxDepth()  // Depth of "Deep" <= 2
[Fact] public void Build_IndexCountsEverySibling()  // list の 3 label: ListItem, path 末尾 Index 0,1,2; Role "label"
[Fact] public void Build_RegistersJavaRefs_AndKeepsHandles()  // ref id は "j" 始まり; keep が全 kept ノード分呼ばれる; 非 kept の中間ハンドルは Released に入る
[Fact] public void Build_StopsAtMaxNodes()  // 6000 子の list → Truncated true
[Fact] public void Build_NamedPane_IsKept_WhenItHasKeptChildren()  // panel "Options" → check box → Pane "Options" が残る
[Fact] public void Build_NameFallsBackToDescription_ThenVirtualName()
```

- [ ] **Step 2: `JavaLocatorResolverTests` を書く**

```csharp
[Fact] public void Resolve_FollowsRoleNameIndex()          // ["frame"/…] path to "Save" (Index 1 among (menu item, null)? → ここは Name が違うので Index 0) と 2 つ目の無名 panel (Index 1)
[Fact] public void Resolve_FallsBackToUniqueName_WhenIndexMissing()   // 兄弟が減って Index 1 が無いが Name 一致が 1 件
[Fact] public void Resolve_ReturnsNull_WhenAmbiguousOrMissing()
[Fact] public void Resolve_ReleasesIntermediateHandles()   // 返ったハンドル以外が Released
```

- [ ] **Step 3: `JavaServiceReadTests` を書く**

`FakeJabBridge` + 本物の `RefRegistry` / `AutomationDispatcher` / `WindowService` / `InputService`。`GetTreeAsync` には hwnd が要るので `JavaService.WindowLookup` を差し替えて偽ウィンドウ `(hwnd 0x1234, "WinUse Swing Fixture", "SunAwtFrame", "java", pid 1, Rect(100,100,640,480))` を返す。
```csharp
[Fact] public async Task GetTree_UsesCalibratedCoordinates()   // root info (100,100,640,480) と window Rect 一致 → Button rect そのまま; window Rect を 2 倍にすると rect も 2 倍
[Fact] public async Task GetElement_ReadsValueForEdit()        // Value "hello"
[Fact] public async Task GetText_PrefersAccessibleText_ThenValue_ThenName()
[Fact] public async Task IsJavaWindow_FalseForNonSunAwt_WithoutTouchingBridge()
[Fact] public async Task IsJavaWindow_FalseWithReason_WhenBridgeUnavailable()   // UnavailableReason(w) == "not found (…)"
[Fact] public async Task IsJavaWindow_FalseWithReason_WhenBridgeNotResponding() // fake IsJavaWindow=false → reason に "winuse doctor"
[Fact] public async Task GetTree_ReleasesHandles_TwoSnapshotsLater()   // 3 回 GetTreeAsync → 1 回目の kept ハンドルが Released に入る、2 回目はまだ
[Fact] public async Task RegisterResolved_LazyRegistersEvenWhenMissing()
```

- [ ] **Step 4: 失敗を確認する**

Run: `dotnet test tests/WinUse.Core.Tests --filter "FullyQualifiedName~JavaTreeBuilderTests|FullyQualifiedName~JavaLocatorResolverTests|FullyQualifiedName~JavaServiceReadTests"`
Expected: コンパイルエラー。

- [ ] **Step 5: `JavaTreeBuilder` を実装する**

`UiaService.TreeBuilder` を手本に。各子で `GetInfo` → `(role, name)` のカウンタで `Index`。`controlType = JavaRoleMap.ControlTypeOf(role, parentRole)`、`name = info.Name ?? info.Description ?? jab.GetVirtualName(h)`。`flags = JavaStates.Parse`。保持判定:
- `keep = !opt.InteractiveOnly || InteractiveFilter.IsInteractive(ct) || (ct == "Text" && name != null)`
- 非表示（`!flags.Showing`）かつ `!opt.IncludeOffscreen` かつ `!JavaRoleMap.IsMenuFamily(ct, parentCt)` → 子孫ごと飛ばす（ハンドル解放）
- `ct == "Pane"`: `name == null` → 潰す（子孫を同じ出力深さに持ち上げる）; `name != null` → 子を集めて 1 件以上なら残す
- その他非 keep → 潰す
- 出力深さが `opt.MaxDepth` に達したら子を読まない。訪問数が `MaxNodes` に達したら `Truncated = true` で止める。
- `Rect`: `flags.Showing` なら `tx.Apply(x,y,w,h)`、そうでなければ引数で渡される「最も近い showing な祖先の Rect」。
- 残すノードは `refs.Register(RefKind.Java, new JavaLocator(wloc, path), null)` + `keep(refId, handle)`。残さないノードのハンドルは `jab.Release`。`Value` は根と `Edit` / `ComboBox` / `Slider` / `Spinner` / `ProgressBar` のみ（`Edit`: `GetText(h, 500)`、`ComboBox`: `GetSelection(h,0)` の名前、他: `GetValue`）。`Checked = JavaStates.CheckedOf(flags, ct, role)`。

- [ ] **Step 6: `JavaLocatorResolver` と `JavaService` の読み取り側を実装する**

- `Resolve`: `UiaLocatorResolver.Pick` と同じ 2 段（完全一致グループの `Index`、無ければ `Name` 一致が 1 件）。子を全部 `GetChild` + `GetInfo` し、選ばなかった子と前段のハンドルを `Release`。
- `JavaService`: ハンドル台帳 `Dictionary<string refId, (int Snapshot, JabHandle)>` と `HashSet<string> _released`。`GetTreeAsync`: `refs.BeginSnapshot()` → `snapshot = refs.CurrentSnapshot` → 台帳の `Snapshot <= snapshot - 2` を `Release` して `_released` に移す → root `GetContextFromHwnd` → 根 info から `JavaGeometry.Calibrate(window.Rect, root, GetDpiForWindow(hwnd)/96.0)` → `JavaTreeBuilder`。`ScopeRef` 指定時は `LiveAsync(scope)` を根に、`basePath` はそのロケータの `Path`。
- `LiveAsync(refId)`: 台帳にあり `_released` に無く `GetInfo` が成功 → そのハンドル。それ以外 → `ResolveAsync(locator)` で再解決し台帳を更新（古いハンドルは解放）。
- `IsJavaWindowAsync`: `LooksLikeJavaWindow` → `bridge.IsAvailable` → `IsJavaWindow(hwnd)`; 一度も真になったことがなければ 100 ms × 10 回まで再試行。結果と理由をウィンドウ hwnd ごとに記録（`UnavailableReason(w)` 用）。
- `Dispose`: 台帳の全ハンドルを `Release`（`bridge.IsAvailable` のときだけ）、`bridge.Dispose()`。

- [ ] **Step 7: テストを通す**

Run: Step 4 と同じ。Expected: PASS。

- [ ] **Step 8: Commit**

```bash
git add src/WinUse.Core/Java tests/WinUse.Core.Tests/Java
git commit -m "feat(java): JavaService tree, locator resolution and handle lifetime"
```

---

### Task 6: `JavaService` の操作側

**Files:**
- Modify: `src/WinUse.Core/Java/JavaService.cs`
- Test: `tests/WinUse.Core.Tests/Java/JavaServiceActionTests.cs`

**Interfaces:**
- Produces: `Task InvokeAsync(string refId)`, `Task SetTextAsync(string refId, string text)`, `Task SetCheckedAsync(string refId, bool on)`, `Task FocusAsync(string refId)`, `Task SelectItemAsync(string refId, string item)`, `Task<string[]> GetListItemsAsync(string refId)`, `Task<string[][]> GetTableAsync(string refId)`（常に `NotSupportedException("get_table is not supported for Java elements yet")`）。`InputService` は座標クリックと `ctrl+a` / 入力 / `Escape` に使う。

- [ ] **Step 1: テストを書く（`FakeJabAccess` の `Actions` 記録と `OnAction` 副作用で検証）**

```csharp
[Fact] public async Task Invoke_UsesClickAction()                      // Actions に (SaveHandle, "click")
[Fact] public async Task Invoke_OnClosedMenuItem_DoesNotOpenMenu()     // "File" には何も送られず "Save" に click のみ
[Fact] public async Task Invoke_FallsBackToParentSelection_ForTab()    // page tab (no actions) → 親 page tab list に AddSelection(1)
[Fact] public async Task Invoke_FallsBackToCenterClick_WhenNothingElse()   // actions 無し・親 selection 無し・showing → ClickAt に中心座標が渡る
[Fact] public async Task Invoke_Throws_WhenActionFails()               // OnAction false → InvalidOperationException
[Fact] public async Task Invoke_AfterThreeSnapshots_ReresolvesInsteadOfUsingReleasedHandle()   // Review Focus 3: Released に入ったハンドルで DoAction が呼ばれない（Fake は use-after-release で throw する）
[Fact] public async Task SetText_UsesSetTextContents()
[Fact] public async Task SetChecked_ClicksUntilStateMatches_MaxThree()  // OnAction で states を toggle; 一致後は呼ばない; 変わらない場合 3 回で例外
[Fact] public async Task SelectItem_List_ClearsThenAddsSelectionByName()   // "Beta" → ClearSelection(list), AddSelection(list, 1); 部分一致 "bet" も可
[Fact] public async Task SelectItem_Combo_OpensPopup_ThenClicksShowingItem()  // togglePopup → OnAction で popup menu/list を showing にして子を増やす → clickAt(中心)
[Fact] public async Task SelectItem_Combo_PressesEscape_WhenItemMissing()   // 例外 + Press に Escape が渡る
[Fact] public async Task GetListItems_ReturnsChildNames()
[Fact] public async Task GetTable_Throws_NotSupported()
```

- [ ] **Step 2: 失敗を確認する**

Run: `dotnet test tests/WinUse.Core.Tests --filter "FullyQualifiedName~JavaServiceActionTests"`
Expected: コンパイルエラー。

- [ ] **Step 3: 実装する（仕様 4.7 の表どおり）**

- `InvokeAsync`: `LiveAsync` → `GetActions`: `click` があればそれ、無ければ先頭。アクションが無ければ親（ロケータの一つ上を `Resolve`、または `GetInfo(parent)` が `HasSelection`）に `AddSelection(indexInParent)`。それも無ければ `showing` を確認し中心を `clickAt`。
- `SetTextAsync`: `SetText` が偽 → ウィンドウをアクティブ化 → `RequestFocus` → 100 ms → `ctrl+a` → 空なら `delete`、else `TypeTextAsync`。
- `SetCheckedAsync`: `GetInfo` → `CheckedOf` と比較、違えば `DoAction("click")`、最大 3 回。
- `SelectItemAsync`: `ControlType` を `JavaRoleMap` で判定。`ComboBox` 分岐は `DoAction("togglePopup")` → 100 ms → 同じウィンドウの根から `showing` な `list` の `label` 子で名前一致を探す（`JavaTreeBuilder` を `InteractiveOnly=false, IncludeOffscreen=false` で再構築して検索）→ `clickAt(中心)`; 見つからなければ `press(Escape)` → 例外。
- `GetListItemsAsync`: `List` / `Tab` / `Tree` は子の名前; `ComboBox` は上と同じ手順で名前を集めて `Escape`。

- [ ] **Step 4: テストを通す**

Run: 同上。Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/WinUse.Core/Java tests/WinUse.Core.Tests/Java
git commit -m "feat(java): invoke, set_text, set_checked, select_item and list items over Access Bridge"
```

---

### Task 7: ツール層の統合 — 観測の分岐、`RefKind.Java` の分岐、`ToolDispatcher`、`CandidateBuilder`

**Files:**
- Modify: `src/WinUse/Tools/ToolContext.cs`（`Java` プロパティ、`IsJavaRef`、`AnyRefPattern`、`GetTreeAsync(WindowInfo, TreeOptions)` ヘルパ、`ObserveAsync`、`CenterOfAsync`、`ActivateOwnerAsync`、`FindWindowAsync`、`ExecuteAsync`、`Dispose`）
- Modify: `src/WinUse/Tools/ObserveTools.cs`（`get_ui_tree` の分岐と `java_access_bridge`、`screenshot(element)`、`get_element`、`ReadTextAsync`、`RequireUia` → `RequireElement`）
- Modify: `src/WinUse/Tools/ElementTools.cs`（`OnRef` に `RefKind.Java`）
- Modify: `src/WinUse/Tools/WaitTools.cs:86-99`（`ElementExistsAsync`）
- Modify: `src/WinUse/Tools/ToolDispatcher.cs:120-155`（`case JavaLocator`）
- Modify: `src/WinUse.Core/Grounding/CandidateBuilder.cs:34-48`
- Modify: `src/WinUse.Core/Grounding/Grounder.cs`（候補 0 件の理由に `UnavailableReason` を添える: `IObservationSource.ObserveAsync` の `Observation` に `string? NoElementsReason` を追加）
- Test: `tests/WinUse.Core.Tests/Grounding/CandidateBuilderTests.cs`, `tests/WinUse.Core.Tests/Tools/ToolHelpersTests.cs`, `tests/WinUse.Core.Tests/Tools/ToolDispatcherTests.cs`, `tests/WinUse.Core.Tests/Tools/JavaToolContextTests.cs`

**Interfaces:**
- Consumes: Task 5 / 6 の `JavaService`。
- Produces: `ToolContext.Java : JavaService`; `ToolContext.Create(..., IJabBridge? jab = null)` オーバーロード（テストで `FakeJabBridge` を注入）; `public static bool IsJavaRef(string id)`; `Observation(WindowInfo Window, IReadOnlyList<Candidate> Candidates, IReadOnlyList<string> RecentActions, string? NoElementsReason = null)`; `get_ui_tree` 結果に `java_access_bridge`（Java ウィンドウで JAB に落ちられなかったときのみ非 null）。

- [ ] **Step 1: テストを書く**

`CandidateBuilderTests`:
```csharp
[Fact] public void Build_MenuItemWithChildren_IsContainerForDescendants()
// MenuBar → MenuItem "File" → MenuItem "Save": "Save" の Candidate.Container == "File"; "File" 自身も候補（Container は外側）
```
`ToolHelpersTests`: `ToolContext.IsJavaRef("j3") == true`, `LooksLikeRef("j3") == true`, `IsUiaRef("j3") == false`。

`JavaToolContextTests`（`FakeJabBridge` + `FakeJabAccess`、ウィンドウ列挙は本物なので Java ウィンドウは実在しない → 観測経路は `IsJavaWindowAsync` が偽になる方を検証）:
```csharp
[Fact] public async Task ExecuteAsync_JavaRef_Click_CallsInvoke()      // Refs に j ref を手で登録 → ExecuteAsync(Candidate(j1), Click) → Fake の Actions に click
[Fact] public async Task ExecuteAsync_JavaRef_Type_CallsSetText()
[Fact] public async Task CenterOfAsync_JavaRef_UsesJavaRect()
```
`ToolDispatcherTests`:
```csharp
[Fact] public async Task Dispatcher_JavaLocator_WhenBridgeUnavailable_ReturnsLocatorNotFound()   // Resolved = JavaLocator(...), tool invoke → !Ok, Error に "locator"
[Fact] public async Task Dispatcher_JavaLocator_LazyTools_GetARef()   // assert_exists with JavaLocator → Ok, Passed false
```

- [ ] **Step 2: 失敗を確認する**

Run: `dotnet test tests/WinUse.Core.Tests --filter "FullyQualifiedName~CandidateBuilderTests|FullyQualifiedName~ToolHelpersTests|FullyQualifiedName~JavaToolContextTests|FullyQualifiedName~ToolDispatcherTests"`
Expected: コンパイルエラー / FAIL。

- [ ] **Step 3: 実装する**

- `ToolContext`: コンストラクタで `Java = new JavaService(jab ?? new JabBridge(cfg, AppContext.BaseDirectory, lf.CreateLogger<JabBridge>()), Refs, Windows, Input, Log)`。`GetTreeAsync(WindowInfo w, TreeOptions opt)`: `await Java.IsJavaWindowAsync(w) ? Java.GetTreeAsync(w.Hwnd, opt) : Uia.GetTreeAsync(w.Hwnd, opt)`（`RetryIfChildlessAsync` は呼び出し側で従来どおり）。`ObserveAsync` はこのヘルパを使い、Java 窓で候補 0 件なら `NoElementsReason = Java.UnavailableReason(w)`。`CenterOfAsync` / `FindWindowAsync` / `ActivateOwnerAsync` に `RefKind.Java` / `JavaLocator` を UIA と同様に追加。`ExecuteAsync` に `IsJavaRef` 分岐（UIA と同じ `kind` 割り当てで `Java.*`）。`Dispose` で `Java.Dispose()`。
- `ObserveTools.GetUiTree`: `scope` が `j` ref なら `Java.GetTreeAsync`; 結果に `java_access_bridge = JavaService.LooksLikeJavaWindow(w) && !isJava ? ctx.Java.UnavailableReason(w) : null`。`get_element` / `get_text` / `screenshot(element)` / `get_list_items` に `RefKind.Java`。`get_table` は `Java.GetTableAsync` に委ねて例外メッセージを返す。説明文の `u{n}` を `u{n} / j{n}` に、`get_ui_tree` に「Java Swing windows are read through Java Access Bridge and return j{n} refs; java_access_bridge explains why a Java window could not be read.」を足す。
- `ElementTools.OnRef`: `case RefKind.Java: await java(r)` — 3 本目の `Func<ElementRef, Task>` 引数を足す。
- `WaitTools.ElementExistsAsync`: `case RefKind.Java: try { await ctx.Java.GetElementAsync(r.Id); return true; } catch { return false; }`。
- `ToolDispatcher.ResolveAsync`: `case JavaLocator j: return await ctx.Java.RegisterResolvedAsync(j, lazy)`。
- `CandidateBuilder.Walk`: `var isMenuContainer = node.ControlType is "MenuItem" or "Menu" or "MenuBar" && node.Children.Count > 0;` → `childContainer = (isContainer || isMenuContainer) && name != null ? name : container`。
- `Grounder`: `Fail([], "no elements")` の文言を `o.NoElementsReason is null ? "no elements" : $"no elements ({o.NoElementsReason})"` に（`screenshots off` の分岐も同様に理由を後ろに付ける）。

- [ ] **Step 4: テストを通す（既存テストも全部）**

Run: `dotnet test tests/WinUse.Core.Tests`
Expected: 全 PASS（統合テストは環境により skip）。

- [ ] **Step 5: Commit**

```bash
git add src/WinUse src/WinUse.Core/Grounding tests/WinUse.Core.Tests
git commit -m "feat(java): route Java windows through JavaService in observe, act, element and replay paths"
```

---

### Task 8: 有効化 — `JabEnablement`、`winuse jab`、`process_run` の `JAVA_TOOL_OPTIONS`

**Files:**
- Create: `src/WinUse.Core/Java/JabEnablement.cs`, `src/WinUse/Cli/JabCommand.cs`
- Modify: `src/WinUse.Core/Processes/ProcessService.cs:20-36`（`Run` で注入）, `src/WinUse/Program.cs`（`case "jab"`、usage 行）, `src/WinUse/Tools/ProcessTools.cs:20`（説明文に 1 文）
- Test: `tests/WinUse.Core.Tests/Java/JabEnablementTests.cs`, `tests/WinUse.Core.Tests/Cli/JabCommandTests.cs`, `tests/WinUse.Core.Tests/Processes/ProcessServiceTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static class JabEnablement
  {
      public const string AssistiveTechnology = "com.sun.java.accessibility.AccessBridge";
      public const string ToolOptionsVar = "JAVA_TOOL_OPTIONS";
      public const string ToolOption = "-Djavax.accessibility.assistive_technologies=" + AssistiveTechnology;
      public static string DefaultPropertiesPath { get; }   // %USERPROFILE%\.accessibility.properties
      public static string ComposeToolOptions(string? existing);           // existing null/blank → ToolOption; contains "-Djavax.accessibility.assistive_technologies=" → existing unchanged; else existing + " " + ToolOption
      public static bool IsEnabled(string propertiesPath);                 // file exists and assistive_technologies contains AssistiveTechnology
      public static void Enable(string propertiesPath);                    // adds to assistive_technologies (comma list, keeps others), sets screen_magnifier_present=true, keeps other lines
      public static void Disable(string propertiesPath);                   // removes both; deletes the file when nothing else remains
  }
  public static class JabCommand { public static int Execute(string[] args, TextWriter output, string? propertiesPath = null); }  // args: ["jab","enable"|"disable"|"status"]; exit 0, usage → 2
  ```

- [ ] **Step 1: テストを書く**

`JabEnablementTests`（一時ファイル）: `ComposeToolOptions(null) == ToolOption`; `ComposeToolOptions("-Xmx1g") == "-Xmx1g " + ToolOption`; 既に含む → 変更なし; `Enable` on missing file → 2 行のファイル、`IsEnabled` true; 既存 `assistive_technologies=foo.Bar` → `foo.Bar,com.sun.java.accessibility.AccessBridge`、他の行 `# comment` と `x=1` が保持; `Enable` 二回で重複しない; `Disable` → 他の行だけ残る; 他の行が無ければファイル削除。
`JabCommandTests`: `status` 未有効 → 出力に `not enabled` と `winuse jab enable`; `enable` → `enabled` と `restart`（再起動を促す文）; `disable` → `disabled`; 不明サブコマンド → 2。
`ProcessServiceTests`（既存ファイルに追加）: `cmd.exe /c echo %JAVA_TOOL_OPTIONS%` をリダイレクトで起動… `ProcessService.Run` は出力を捨てるので、代わりに `internal static void ApplyJavaAccessibility(IDictionary<string,string?> env, bool enabled)` を抽出して単体テスト: enabled かつ未設定 → `ToolOption`; 既存値あり → 連結; `enabled=false` → 触らない。

- [ ] **Step 2: 失敗を確認する**

Run: `dotnet test tests/WinUse.Core.Tests --filter "FullyQualifiedName~JabEnablementTests|FullyQualifiedName~JabCommandTests|FullyQualifiedName~ProcessServiceTests"`
Expected: コンパイルエラー。

- [ ] **Step 3: 実装する**

`JabEnablement` は `.properties` を行単位で扱う（`key=value`、`#` コメント保持、UTF-8 BOM 無し）。`ProcessService.Run` は `psi.Environment` に対して `ApplyJavaAccessibility(psi.Environment, cfg.JavaAccessibility)` を `WebViewDebug` 処理の直後に呼ぶ。`Program.Main` に `case "jab": return JabCommand.Execute(args, Console.Out);`、usage に `winuse jab enable|disable|status`。`process_run` の説明に `Java processes are started with accessibility enabled (JAVA_TOOL_OPTIONS) unless WINUSE_JAVA_ACCESSIBILITY=off.` を足す。

- [ ] **Step 4: テストを通す**

Run: 同上。Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/WinUse.Core/Java src/WinUse.Core/Processes src/WinUse/Cli src/WinUse/Program.cs src/WinUse/Tools/ProcessTools.cs tests/WinUse.Core.Tests
git commit -m "feat(java): winuse jab enable/disable/status and JAVA_TOOL_OPTIONS on process_run"
```

---

### Task 9: `winuse doctor` の Java 行

**Files:**
- Create: `src/WinUse.Core/Java/JavaProcessInfo.cs`
- Modify: `src/WinUse/Cli/DoctorCommand.cs`（UIA 行の後に Java セクション）, `src/WinUse.Core/Windows/NativeMethods.cs`（`IsWow64Process`, `OpenProcess`, `EnumProcessModulesEx`, `GetModuleFileNameExW`）
- Test: `tests/WinUse.Core.Tests/Cli/DoctorCommandTests.cs`, `tests/WinUse.Core.Tests/Java/JavaProcessInfoTests.cs`

**Interfaces:**
- Produces: `public sealed record JavaProcessInfo(bool? Is32Bit, string? JvmDllPath, string? JvmVersion, bool? JavaBridgeDllPresent, bool? AccessBridgeJarPresent, string? Error) { public static JavaProcessInfo Inspect(int pid); }`。`JvmDllPath` はモジュール一覧から `jvm.dll`; `JavaBridgeDllPresent` は同じ JRE の `bin\JavaAccessBridge-32.dll` / `-64.dll`（ビット数に応じて）; `AccessBridgeJarPresent` は Java 8 系（`lib\ext` がある）なら `lib\ext\access-bridge-32.jar`（64bit は `access-bridge-64.jar`）の有無、9 以降（`lib\modules` がある）は `true`。`OpenProcess` 失敗 → `Error = "not accessible (running elevated or as another user?)"`。

- [ ] **Step 1: テストを書く**

`DoctorCommandTests` に: 既定設定 → 出力に `Java Access Bridge:` 行と `Java accessibility (user):` 行; `Jab = JabMode.Off` → `Java Access Bridge: WINUSE_JAB=off`; `JabDll = <存在しないパス>` → `not found` と `fetch-jab.ps1`。`JavaProcessInfoTests`: 自プロセス（`Environment.ProcessId`）→ `Is32Bit == false`, `JvmDllPath == null`, `Error == null`; pid 4（System）→ `Error` 非 null。

- [ ] **Step 2: 失敗を確認する**

Run: `dotnet test tests/WinUse.Core.Tests --filter "FullyQualifiedName~DoctorCommandTests|FullyQualifiedName~JavaProcessInfoTests"`
Expected: FAIL。

- [ ] **Step 3: 実装する**

`DoctorCommand` に仕様 4.11 の書式で出力する。Java ウィンドウは `ctx.Windows.ListAsync()` の `JavaService.LooksLikeJavaWindow` に該当するもの。各ウィンドウで `ctx.Java.IsJavaWindowAsync(w)` → `bridge responding` / `bridge NOT responding (accessibility not enabled when the app started?)`、続けて `JavaProcessInfo.Inspect(w.Pid)` の行。`Java accessibility (user):` は `JabEnablement.IsEnabled(DefaultPropertiesPath)`。終了コードは変えない。

- [ ] **Step 4: テストを通す**

Run: 同上。Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/WinUse.Core/Java src/WinUse.Core/Windows src/WinUse/Cli tests/WinUse.Core.Tests
git commit -m "feat(java): doctor reports Access Bridge DLL, user enablement and Java windows"
```

---

### Task 10: 実 JVM に対する統合テスト

**Files:**
- Create: `tests/WinUse.Core.Tests/Java/JavaIntegrationTests.cs`

**Interfaces:**
- Consumes: Task 1 の `SwingFixture` / `Jvms`、`ToolContext`、`ToolDispatcher`、`Scenario`。

- [ ] **Step 1: テストを書く（`[Collection("Desktop")]`、各テストは `[Theory] [InlineData(Jvm.Jdk21x64)] [InlineData(Jvm.Jre8x86)]`、JVM か `out\WindowsAccessBridge-64.dll` が無ければ return）**

共通: `cfg = TestConfig.Default with { JabDll = <out\...dll>, AllowedRoots = [tmp] }`、`ctx = ToolContext.Create(cfg)`、`d = new ToolDispatcher(ctx)`、`InvokeAsync(new("get_ui_tree", Args(("window", "^WinUse Swing Fixture$")), null))` でツリー取得。ヘルパ `Find(tree, controlType, name)` と `Status()`（`Text` ノードで名前が `status: ` 始まり）。
```csharp
Tree_ListsButtonsMenusAndDeepButton       // Button "Increment", MenuItem "Save" (Offscreen true), Button "Deep", Edit (Value "hello"), List, Tab, java_access_bridge == null
Invoke_Button_UpdatesStatus               // invoke j(Increment) → 再観測で "status: increment 1"
Invoke_ClosedMenuItem_RunsWithoutOpening  // invoke j(Save) → "status: save"; メニューは開いていない（再観測で MenuItem "Save" が Offscreen true のまま）
SetChecked_MenuCheckItem                  // set_checked j(Word Wrap) true → "status: wordwrap on"
SetText_Edit                              // set_text "world" → get_text == "world"
SelectItem_List_Tab_Combo                 // list "Beta" → "status: list Beta"; tab "Second" → "status: tab Second"; combo "Two" → "status: combo Two"
Click_ByRef_UsesCalibratedCenter          // click ref=j(Increment) → "status: increment 1"（座標経路）
Act_WithFakeJev_PicksButton               // ToolContext.Create(cfg, null, new FakeJev(choice: (<Increment の ref>, 0.9, 0.9))) → act "Increment" → executed true
StaleRef_AfterThreeObservations_StillInvokes   // get_ui_tree ×3 の後に最初の ref で invoke → status 更新
Scenario_SaveAndReplay_InvokesByLocator   // invoke を含むステップを scenario_save → 新しい ToolContext で scenario_run → exit_code 0 かつ status 更新
GetUiTree_WithJabOff_ReturnsRootAndReason // cfg with Jab = Off → tree.Children 空, java_access_bridge == "WINUSE_JAB=off"
Doctor_ReportsFixtureAsResponding         // DoctorCommand 出力に "WinUse Swing Fixture" と "bridge responding"
```

- [ ] **Step 2: 実行する**

Run: `dotnet test tests/WinUse.Core.Tests --filter "FullyQualifiedName~JavaIntegrationTests"`
Expected: 全 PASS × 2 JVM。`Jre8x86` が JAB 未同梱で skip になる場合は、その事実を CHANGELOG の Known issues 候補として台帳に記す。

- [ ] **Step 3: 失敗したものを直し、再実行する**

典型: `getAccessibleTextRange` の終端が排他的なら `GetText` の `end` を `n` に; 座標ずれは `JavaGeometry` の較正元（根の矩形が装飾込みか）を確認; メニュー項目の `click` が効かないときは `doClick` 相当のアクション名を `GetActions` の実値で確認。

- [ ] **Step 4: Commit**

```bash
git add tests/WinUse.Core.Tests/Java
git commit -m "test(java): end-to-end Access Bridge tests on Temurin 21 x64 and Temurin 8 x86"
```

---

### Task 11: ドキュメントとリリースノート

**Files:**
- Modify: `README.md`（「Java Swing (Java Access Bridge)」節: 仕組み 3 行、`scripts/fetch-jab.ps1`、`winuse jab enable`、`process_run` の `JAVA_TOOL_OPTIONS`、環境変数 3 つ、`doctor` の見方、`get_table` 非対応、トリミング JRE / 昇格プロセスの制限。CLI 一覧に `winuse jab`。ツール一覧の ref 形式に `j{n}`）
- Modify: `CHANGELOG.md`（`## Unreleased` に Added / Changed（`process_run` の環境変数）/ Known issues（`get_table` 非対応、JAB 未同梱 JRE、昇格プロセス））
- Modify: `docs/superpowers/followups/`（新規 `2026-10-22-java-access-bridge-deferred.md`: `get_table` の AccessibleTable 対応、JAB イベントコールバック、重量級ポップアップのウィンドウ列挙）

- [ ] **Step 1: 書く**
- [ ] **Step 2: `dotnet build winuse.sln` と `dotnet test tests/WinUse.Core.Tests` が通ることを確認する**

Run: `dotnet build winuse.sln -warnaserror:false && dotnet test tests/WinUse.Core.Tests`
Expected: ビルド成功、全 PASS。

- [ ] **Step 3: Commit**

```bash
git add README.md CHANGELOG.md docs/superpowers/followups
git commit -m "docs(java): README section, changelog and follow-ups for Java Access Bridge support"
```
