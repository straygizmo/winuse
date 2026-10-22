# winuse フェーズ 1 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** AI が Windows を観察・操作できる MCP サーバー兼回帰再生 CLI `winuse` を .NET 10 で作り、MDium のサンプルシナリオが対話でも `winuse run` でも通る状態にする。

**Architecture:** 単一 exe が `--mcp`（stdio MCP サーバー）と `run`（シナリオ再生）を兼ねる。Core 層のサービス（Win32 ウィンドウ、SendInput、FlaUI の UIA、Playwright over CDP、画面キャプチャ、プロセス、ファイル、Jev クライアント、Grounding、シナリオ）を、MCP ツール層と CLI の両方が共通の `ToolDispatcher` 経由で呼ぶ。UIA / 入力 / COM は 1 本の STA 自動化スレッドで直列実行する。

**Tech Stack:** .NET 10 (`net10.0-windows`), C# 14, ModelContextProtocol 2.2.0, Microsoft.Extensions.Hosting 10.x, FlaUI.UIA3 5.0.0, Microsoft.Playwright 1.63.0, System.Drawing.Common, xUnit。

**Spec:** `docs/superpowers/specs/2026-10-19-winuse-mcp-design.md`

## Global Constraints

- TFM は `net10.0-windows`、`Nullable` と `ImplicitUsings` は enable。SDK は 10.0.102。
- パッケージ: `ModelContextProtocol` 2.2.0、`FlaUI.UIA3` 5.0.0、`Microsoft.Playwright` 1.63.0。これ以外の自動化系ライブラリは追加しない。
- stdio モードでは `Console.Out` に書かない。ログは stderr（`LogToStandardErrorThreshold = Trace`）と `%LOCALAPPDATA%/winuse/logs/`。
- ツール名は英語 snake_case、ツール説明文は英語。
- ref の形式: UIA は `u{n}`、DOM は `w{n}`、ウィンドウは `win{n}`。
- ファイル操作は `allowed_roots`（既定: cwd、`%TEMP%`）配下のみ。`process_kill` は winuse が起動したプロセスか `killable_processes` のみ。
- Jev: `POST {JEV_BASE_URL}/v1/systemone`、`Authorization: Bearer {JEV_API_KEY}`、既定 `JEV_BASE_URL=https://api.typesafe.ai`、`JEV_MODEL=jev-latest`。閾値既定: probability 0.55 / confidence 0.35、満足確認 noul 0.7。choice は最大 255 択。
- 再生の終了コード: 0 PASS、1 FAIL、2 実行エラー。非対話セッションは 2。Jev 未設定・障害は WARN(skipped)。
- `.env`（`JEV_API_KEY` を含む）は `.gitignore` に入れる。
- 実行環境は Windows ネイティブの対話型デスクトップのみ。
- コミットメッセージ末尾に `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` を付ける。

## Review Focus

1. 同名ウィンドウが複数ある / タイトルに正規表現の特殊文字が含まれる → `list_windows` は全件返し、タイトル指定の操作は前面に近い 1 件を選ぶ。エラーで落ちない。（Task 4 にテスト）
2. 高 DPI 環境でスクリーンショットの座標とクリック座標がずれる → プロセスを Per-Monitor V2 DPI aware にし、キャプチャは物理ピクセルで行う。（Task 6 にテスト）
3. `press_keys` に不正なキー名（`ctrl+`、`foo+a`）→ 例外ではなく `ArgumentException` を明示的に投げ、ツール層で `isError` にする。（Task 5 にテスト）
4. 候補が 255 件を超える画面 → 二段階 choice に分割し、Jev の制限を超えない。（Task 12 にテスト）
5. 回帰再生中に Jev が未設定・429・タイムアウト → `check` ステップは WARN(skipped)、全体は PASS のまま。（Task 13 にテスト）

---

## ファイル構成

```
winuse.sln
.gitignore                      (.env, bin/, obj/, .vs/, TestResults/)
.env                            (既存。JEV_API_KEY)
src/WinUse.Core/                クラスライブラリ (net10.0-windows)
  Config/WinUseConfig.cs        設定モデルと .env / 環境変数の読み込み
  Threading/AutomationDispatcher.cs  STA 自動化スレッド
  Refs/ElementRef.cs            ref と Locator のモデル
  Refs/RefRegistry.cs           ref の採番と逆引き
  Windows/WindowService.cs      Win32 ウィンドウ操作
  Windows/NativeMethods.cs      user32 P/Invoke
  Input/InputService.cs         SendInput
  Input/KeyChord.cs             "ctrl+shift+s" の解析
  Screen/ScreenService.cs       キャプチャ
  Processes/ProcessService.cs   起動・終了・CDP ポート
  Files/FileService.cs          許可ルート付きファイル操作
  Uia/UiaService.cs             FlaUI ラッパ
  Uia/UiaLocatorResolver.cs     UiaLocator → AutomationElement
  Web/WebService.cs             Playwright over CDP
  Web/DomCollector.js           DOM からインタラクティブ要素を集める JS（埋め込みリソース）
  Jev/JevClient.cs              HTTP クライアント
  Jev/JevModels.cs              要求・応答モデル
  Grounding/Candidate.cs        圧縮候補
  Grounding/CandidateBuilder.cs UIA + DOM の統合と圧縮
  Grounding/Grounder.cs         find / act / check
  Scenarios/Journal.cs          step_id 付きジャーナル
  Scenarios/Scenario.cs         JSON モデル
  Scenarios/ScenarioRunner.cs   再生
  Scenarios/IToolDispatcher.cs  ツール名 → 実行の抽象
src/WinUse/                     実行ファイル (net10.0-windows, Exe)
  Program.cs                    --mcp / run / doctor
  app.manifest                  PerMonitorV2 DPI aware
  Tools/ObserveTools.cs         screenshot, list_windows, get_ui_tree, get_dom, get_element, get_text, get_table, get_list_items
  Tools/IntentTools.cs          act, find, check
  Tools/InputTools.cs           click, hover, drag, scroll, type_text, press_keys
  Tools/ElementTools.cs         set_text, select_item, set_checked, focus, invoke
  Tools/WindowTools.cs          window_*
  Tools/WaitTools.cs            wait_for
  Tools/ProcessTools.cs         process_*
  Tools/FileTools.cs            file_* 等
  Tools/AssertTools.cs          assert_*
  Tools/ScenarioTools.cs        scenario_*
  Tools/ToolDispatcher.cs       IToolDispatcher 実装（名前 → 上記メソッド）
  Tools/ToolResult.cs           共通の結果 JSON（step_id を含む）
tests/WinUse.Core.Tests/        xUnit。Integration は Trait("Category","Integration")
scenarios/mdium-smoke.json      サンプルシナリオ
docs/superpowers/spikes/        Office 接続スパイクの記録
```

単体テストは `dotnet test --filter "Category!=Integration"`、統合テストは `dotnet test --filter "Category=Integration"`（ログオン済みデスクトップで実行）。

---

### Task 1: ソリューション骨格と設定読み込み

**Files:**

- Create: `winuse.sln`, `.gitignore`, `src/WinUse.Core/WinUse.Core.csproj`, `src/WinUse/WinUse.csproj`, `src/WinUse/Program.cs`, `src/WinUse/app.manifest`, `tests/WinUse.Core.Tests/WinUse.Core.Tests.csproj`
- Create: `src/WinUse.Core/Config/WinUseConfig.cs`
- Test: `tests/WinUse.Core.Tests/Config/WinUseConfigTests.cs`

**Interfaces:**

- Produces: `sealed record WinUseConfig(string? JevApiKey, string JevBaseUrl, string JevModel, double MinProbability, double MinConfidence, double SatisfiedThreshold, IReadOnlyList<string> AllowedRoots, IReadOnlyList<string> KillableProcesses, string LogDirectory)`
- Produces: `static WinUseConfig WinUseConfig.Load(string currentDirectory, string exeDirectory, IDictionary<string,string?> environment)` — `.env` は cwd → exe dir の順に読み、環境変数が `.env` より優先。
- 既定値: `JevBaseUrl="https://api.typesafe.ai"`, `JevModel="jev-latest"`, `MinProbability=0.55`, `MinConfidence=0.35`, `SatisfiedThreshold=0.7`, `AllowedRoots=[cwd, %TEMP%]`, `LogDirectory=%LOCALAPPDATA%/winuse/logs`。環境変数名: `JEV_API_KEY`, `JEV_BASE_URL`, `JEV_MODEL`, `WINUSE_ALLOWED_ROOTS`（`;` 区切り）, `WINUSE_KILLABLE_PROCESSES`（`;` 区切り）。

- [ ] **Step 1: `git init` と骨格作成**

```powershell
git init
dotnet new sln -n winuse
dotnet new classlib -n WinUse.Core -o src/WinUse.Core -f net10.0
dotnet new console  -n WinUse      -o src/WinUse      -f net10.0
dotnet new xunit    -n WinUse.Core.Tests -o tests/WinUse.Core.Tests -f net10.0
dotnet sln add src/WinUse.Core src/WinUse tests/WinUse.Core.Tests
dotnet add src/WinUse reference src/WinUse.Core
dotnet add tests/WinUse.Core.Tests reference src/WinUse.Core
dotnet add tests/WinUse.Core.Tests reference src/WinUse
```

3 つの csproj の `TargetFramework` を `net10.0-windows` に変え、`UseWindowsForms` は付けない。`src/WinUse.Core/WinUse.Core.csproj` に `<InternalsVisibleTo Include="WinUse.Core.Tests" />` を入れる。`src/WinUse/WinUse.csproj` に `<ApplicationManifest>app.manifest</ApplicationManifest>` と `<AssemblyName>winuse</AssemblyName>` を追加。`app.manifest` に `<dpiAwareness>PerMonitorV2</dpiAwareness>` を書く。`.gitignore` に `.env`, `bin/`, `obj/`, `.vs/`, `TestResults/`。

- [ ] **Step 2: 失敗するテストを書く**

```csharp
[Fact]
public void Load_ReadsDotEnvAndEnvironmentOverrides()
{
    var dir = Directory.CreateTempSubdirectory();
    File.WriteAllText(Path.Combine(dir.FullName, ".env"), "JEV_API_KEY=from-dotenv\nJEV_MODEL=jev-1.13.0\n");
    var env = new Dictionary<string, string?> { ["JEV_MODEL"] = "jev-latest" };
    var cfg = WinUseConfig.Load(dir.FullName, dir.FullName, env);
    Assert.Equal("from-dotenv", cfg.JevApiKey);
    Assert.Equal("jev-latest", cfg.JevModel);          // 環境変数が優先
    Assert.Equal("https://api.typesafe.ai", cfg.JevBaseUrl);
    Assert.Equal(0.55, cfg.MinProbability);
    Assert.Contains(dir.FullName, cfg.AllowedRoots);
}

[Fact]
public void Load_WithoutDotEnv_HasNullApiKey()
{
    var dir = Directory.CreateTempSubdirectory();
    var cfg = WinUseConfig.Load(dir.FullName, dir.FullName, new Dictionary<string, string?>());
    Assert.Null(cfg.JevApiKey);
}
```

- [ ] **Step 3: テストが失敗することを確認**

Run: `dotnet test tests/WinUse.Core.Tests --filter "FullyQualifiedName~WinUseConfigTests"`
Expected: ビルドエラー（`WinUseConfig` 未定義）

- [ ] **Step 4: `WinUseConfig` と `Load` を実装**

`.env` は `KEY=VALUE` 行のみ、`#` 始まりは無視、値の前後の `"` は剥がす。

- [ ] **Step 5: テストが通ることを確認**

Run: `dotnet test tests/WinUse.Core.Tests --filter "FullyQualifiedName~WinUseConfigTests"`
Expected: PASS 2 件

- [ ] **Step 6: コミット**

```bash
git add -A
git commit -m "chore: scaffold winuse solution and config loading"
```

---

### Task 2: STA 自動化スレッド

**Files:**

- Create: `src/WinUse.Core/Threading/AutomationDispatcher.cs`
- Test: `tests/WinUse.Core.Tests/Threading/AutomationDispatcherTests.cs`

**Interfaces:**

- Produces: `sealed class AutomationDispatcher : IDisposable` with `Task<T> RunAsync<T>(Func<T> work, CancellationToken ct = default)`, `Task RunAsync(Action work, CancellationToken ct = default)`, `bool IsOnDispatcherThread { get; }`。内部は `ApartmentState.STA` のスレッド 1 本 + `BlockingCollection<Action>`。例外は `Task` に伝播。

- [ ] **Step 1: 失敗するテストを書く**

```csharp
[Fact]
public async Task RunAsync_ExecutesOnStaThreadAndSerializes()
{
    using var d = new AutomationDispatcher();
    var state = await d.RunAsync(() => Thread.CurrentThread.GetApartmentState());
    Assert.Equal(ApartmentState.STA, state);

    var order = new List<int>();
    var t1 = d.RunAsync(() => { Thread.Sleep(50); order.Add(1); });
    var t2 = d.RunAsync(() => order.Add(2));
    await Task.WhenAll(t1, t2);
    Assert.Equal(new[] { 1, 2 }, order);
}

[Fact]
public async Task RunAsync_PropagatesException()
{
    using var d = new AutomationDispatcher();
    await Assert.ThrowsAsync<InvalidOperationException>(() => d.RunAsync(() => throw new InvalidOperationException("boom")));
}
```

- [ ] **Step 2: 失敗を確認** — Run: `dotnet test --filter "FullyQualifiedName~AutomationDispatcherTests"` Expected: ビルドエラー
- [ ] **Step 3: 実装**
- [ ] **Step 4: 成功を確認** — Expected: PASS 2 件
- [ ] **Step 5: コミット** — `git commit -m "feat(core): add STA automation dispatcher"`

---

### Task 3: ref とロケータのモデル

**Files:**

- Create: `src/WinUse.Core/Refs/ElementRef.cs`, `src/WinUse.Core/Refs/RefRegistry.cs`
- Test: `tests/WinUse.Core.Tests/Refs/RefRegistryTests.cs`

**Interfaces:**

- Produces: `enum RefKind { Window, Uia, Dom }`
- Produces: `abstract record Locator`; `sealed record WindowLocator(string TitleRegex, string? ProcessName, string? ClassName) : Locator`; `sealed record UiaLocator(WindowLocator Window, IReadOnlyList<UiaPathSegment> Path) : Locator`; `sealed record UiaPathSegment(string ControlType, string? AutomationId, string? Name, int Index)`; `sealed record DomLocator(WindowLocator Window, string Selector) : Locator`
- Produces: `sealed record ElementRef(string Id, RefKind Kind, Locator Locator, object? Handle)` — `Handle` は UIA の `AutomationElement`、DOM の `IElementHandle` か selector、ウィンドウの `nint`。
- Produces: `sealed class RefRegistry` with `ElementRef Register(RefKind kind, Locator locator, object? handle)`（Id は kind ごとの連番 `u1`, `w1`, `win1`）、`bool TryGet(string id, out ElementRef r)`、`void BeginSnapshot()`（旧 ref は残すが新しい連番は続きから）、`Locator`/`ElementRef` は `System.Text.Json` で多相シリアライズ可能（`[JsonPolymorphic]`, `[JsonDerivedType]`）。

- [ ] **Step 1: 失敗するテストを書く**

```csharp
[Fact]
public void Register_AssignsKindPrefixedSequentialIds()
{
    var reg = new RefRegistry();
    var w = new WindowLocator("^Untitled", null, null);
    Assert.Equal("win1", reg.Register(RefKind.Window, w, (nint)1).Id);
    Assert.Equal("u1", reg.Register(RefKind.Uia, new UiaLocator(w, []), null).Id);
    Assert.Equal("u2", reg.Register(RefKind.Uia, new UiaLocator(w, []), null).Id);
    Assert.Equal("w1", reg.Register(RefKind.Dom, new DomLocator(w, "#x"), null).Id);
    Assert.True(reg.TryGet("u2", out var r) && r.Kind == RefKind.Uia);
    Assert.False(reg.TryGet("u9", out _));
}

[Fact]
public void Locator_RoundTripsThroughJson()
{
    Locator l = new DomLocator(new WindowLocator("MDium", "mdium", null), "[data-testid=\"editor\"]");
    var json = JsonSerializer.Serialize(l);
    var back = JsonSerializer.Deserialize<Locator>(json);
    Assert.Equal(l, back);
}
```

- [ ] **Step 2: 失敗を確認** → **Step 3: 実装** → **Step 4: PASS を確認**
- [ ] **Step 5: コミット** — `git commit -m "feat(core): add element refs and locator model"`

---

### Task 4: Win32 ウィンドウサービス

**Files:**

- Create: `src/WinUse.Core/Windows/NativeMethods.cs`, `src/WinUse.Core/Windows/WindowService.cs`
- Test: `tests/WinUse.Core.Tests/Windows/WindowServiceTests.cs`（Integration）, `tests/WinUse.Core.Tests/Windows/WindowFilterTests.cs`（Unit）

**Interfaces:**

- Produces: `sealed record WindowInfo(nint Hwnd, string Title, string ClassName, string ProcessName, int Pid, Rect Rect, WindowState State)`; `enum WindowState { Normal, Minimized, Maximized }`; `readonly record struct Rect(int X, int Y, int Width, int Height)`
- Produces: `sealed class WindowService(AutomationDispatcher d)` with
  - `Task<IReadOnlyList<WindowInfo>> ListAsync(string? titleRegex = null, string? processName = null)` — 可視トップレベルのみ、Z 順（前面が先）。`titleRegex` が不正な正規表現なら `Regex.Escape` してリテラル一致にフォールバック。
  - `Task<WindowInfo?> FindAsync(WindowLocator loc)` — Z 順で最初の一致。
  - `Task<bool> ActivateAsync(nint hwnd)`, `Task<bool> CloseAsync(nint hwnd)`, `Task<bool> SetStateAsync(nint hwnd, WindowState s)`, `Task<bool> MoveAsync(nint hwnd, Rect r)`, `Task<bool> ExistsAsync(WindowLocator loc)`
  - `Task<WindowInfo?> WaitForAsync(WindowLocator loc, TimeSpan timeout)`, `Task<bool> WaitClosedAsync(WindowLocator loc, TimeSpan timeout)`（200ms ポーリング）
  - `static bool Matches(WindowInfo w, WindowLocator loc)`（純粋関数、Unit テスト用）

- [ ] **Step 1: 失敗する Unit テスト（Review Focus 1）**

```csharp
[Theory]
[InlineData("Untitled - Notepad", "^Untitled", true)]
[InlineData("C++ (1)", "C++ (1)", true)]       // 不正な正規表現 → リテラル一致
[InlineData("Other", "^Untitled", false)]
public void Matches_HandlesRegexAndInvalidPatterns(string title, string pattern, bool expected)
{
    var w = new WindowInfo(1, title, "Notepad", "notepad", 1, new Rect(0,0,1,1), WindowState.Normal);
    Assert.Equal(expected, WindowService.Matches(w, new WindowLocator(pattern, null, null)));
}
```

- [ ] **Step 2: 失敗する Integration テスト**

```csharp
[Fact, Trait("Category", "Integration")]
public async Task Notepad_CanBeFoundActivatedAndClosed()
{
    using var d = new AutomationDispatcher();
    var svc = new WindowService(d);
    using var p = Process.Start("notepad.exe");
    var loc = new WindowLocator("Notepad", "notepad", null);
    var w = await svc.WaitForAsync(loc, TimeSpan.FromSeconds(10));
    Assert.NotNull(w);
    Assert.True(await svc.ActivateAsync(w.Hwnd));
    Assert.True(await svc.SetStateAsync(w.Hwnd, WindowState.Maximized));
    p.Kill();
    Assert.True(await svc.WaitClosedAsync(loc, TimeSpan.FromSeconds(10)));
}
```

- [ ] **Step 3: 失敗を確認** → **Step 4: 実装**（`EnumWindows`, `GetWindowTextW`, `GetClassNameW`, `GetWindowThreadProcessId`, `IsWindowVisible`, `GetWindowRect`, `IsIconic`, `IsZoomed`, `SetForegroundWindow`（失敗時は `AttachThreadInput` で再試行）, `ShowWindow`, `MoveWindow`, `PostMessage(WM_CLOSE)`）
- [ ] **Step 5: PASS を確認** — Run: `dotnet test --filter "FullyQualifiedName~WindowService|FullyQualifiedName~WindowFilter"`
- [ ] **Step 6: コミット** — `git commit -m "feat(core): add Win32 window service"`

---

### Task 5: 入力サービス（SendInput）

**Files:**

- Create: `src/WinUse.Core/Input/KeyChord.cs`, `src/WinUse.Core/Input/InputService.cs`
- Test: `tests/WinUse.Core.Tests/Input/KeyChordTests.cs`（Unit）, `tests/WinUse.Core.Tests/Input/InputServiceTests.cs`（Integration）

**Interfaces:**

- Produces: `enum MouseButton { Left, Right, Middle }`
- Produces: `sealed record KeyChord(IReadOnlyList<ushort> Modifiers, ushort Key)` with `static KeyChord Parse(string text)` — 区切りは `+`、大小無視。修飾: `ctrl`, `alt`, `shift`, `win`。キー: 英数 1 文字、`f1`〜`f24`, `enter`, `tab`, `esc`, `escape`, `space`, `backspace`, `delete`, `home`, `end`, `pageup`, `pagedown`, `up`, `down`, `left`, `right`, `insert`。不明なら `ArgumentException("Unknown key: ...")`。
- Produces: `sealed class InputService(AutomationDispatcher d)` with `Task ClickAsync(int x, int y, MouseButton b = Left, int count = 1)`, `Task MoveToAsync(int x, int y)`, `Task DragAsync(int x1, int y1, int x2, int y2)`, `Task ScrollAsync(int x, int y, int deltaNotches)`（正で上）, `Task TypeTextAsync(string text, int perCharDelayMs = 0)`（`KEYEVENTF_UNICODE`）, `Task PressAsync(KeyChord chord)`。座標は物理ピクセル（スクリーン座標）。

- [ ] **Step 1: 失敗する Unit テスト（Review Focus 3）**

```csharp
[Theory]
[InlineData("ctrl+s", 1, 0x53)]
[InlineData("Ctrl+Shift+S", 2, 0x53)]
[InlineData("enter", 0, 0x0D)]
[InlineData("f5", 0, 0x74)]
public void Parse_ValidChords(string text, int modCount, int vk)
{
    var c = KeyChord.Parse(text);
    Assert.Equal(modCount, c.Modifiers.Count);
    Assert.Equal((ushort)vk, c.Key);
}

[Theory]
[InlineData("ctrl+")]
[InlineData("foo+a")]
[InlineData("")]
public void Parse_InvalidChords_Throw(string text)
    => Assert.Throws<ArgumentException>(() => KeyChord.Parse(text));
```

- [ ] **Step 2: 失敗する Integration テスト**

```csharp
[Fact, Trait("Category", "Integration")]
public async Task TypeText_IntoNotepad_AppearsInClipboardViaSelectAll()
{
    // notepad を起動 → WindowService で待つ → TypeTextAsync("winuse テスト") → ctrl+a, ctrl+c
    // → Clipboard の文字列に "winuse テスト" が含まれる（Clipboard 読み取りは STA スレッド上で user32 の OpenClipboard/GetClipboardData を P/Invoke）
}
```

- [ ] **Step 3: 失敗を確認** → **Step 4: 実装** → **Step 5: PASS を確認**
- [ ] **Step 6: コミット** — `git commit -m "feat(core): add SendInput-based input service and key chord parser"`

---

### Task 6: 画面キャプチャ

**Files:**

- Create: `src/WinUse.Core/Screen/ScreenService.cs`
- Modify: `src/WinUse.Core/WinUse.Core.csproj`（`System.Drawing.Common` パッケージ追加）
- Test: `tests/WinUse.Core.Tests/Screen/ScreenServiceTests.cs`（Integration）

**Interfaces:**

- Produces: `sealed record Capture(byte[] Png, int Width, int Height, double Scale)`
- Produces: `sealed class ScreenService(AutomationDispatcher d)` with `Task<Capture> CaptureScreenAsync(double scale = 1.0)`（仮想スクリーン全体、物理ピクセル）, `Task<Capture> CaptureWindowAsync(nint hwnd, double scale = 1.0)`（`PrintWindow` を試し、失敗時は `GetWindowRect` 範囲の画面コピー）, `Task<Capture> CaptureRectAsync(Rect r, double scale = 1.0)`。`scale` は 0.25〜1.0 にクランプ。

- [ ] **Step 1: 失敗するテスト（Review Focus 2）**

```csharp
[Fact, Trait("Category", "Integration")]
public async Task CaptureScreen_ReturnsPngOfVirtualScreenInPhysicalPixels()
{
    using var d = new AutomationDispatcher();
    var cap = await new ScreenService(d).CaptureScreenAsync();
    Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, cap.Png[..4]);
    var vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
    Assert.Equal(vw, cap.Width);   // PerMonitorV2 なので物理ピクセル
}

[Fact, Trait("Category", "Integration")]
public async Task CaptureScreen_WithHalfScale_HalvesDimensions()
{
    // scale 0.5 → Width が元の半分（±1）
}
```

テストプロセスも DPI aware である必要がある。テストプロジェクトの `app.manifest` にも `PerMonitorV2` を設定する（`dotnet test` の testhost はマニフェストを無視するため、テストの先頭で `SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)` を呼ぶ `[ModuleInitializer]` を `tests/WinUse.Core.Tests/DpiInit.cs` に置く）。

- [ ] **Step 2: 失敗を確認** → **Step 3: 実装** → **Step 4: PASS を確認**
- [ ] **Step 5: コミット** — `git commit -m "feat(core): add screen capture service"`

---

### Task 7: プロセスサービス（WebView2 デバッグポート対応）

**Files:**

- Create: `src/WinUse.Core/Processes/ProcessService.cs`
- Test: `tests/WinUse.Core.Tests/Processes/ProcessServiceTests.cs`

**Interfaces:**

- Produces: `sealed record RunSpec(string Path, string? Args, string? Cwd, IReadOnlyDictionary<string,string>? Env, bool WebViewDebug, bool RequireFresh)`
- Produces: `sealed record RunResult(int Pid, int? CdpPort, string ProcessName)`
- Produces: `sealed class ProcessService(WinUseConfig cfg)` with
  - `RunResult Run(RunSpec spec)` — `WebViewDebug` なら空きポートを取り（`TcpListener` で 0 を bind して解放）、環境変数 `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` に `--remote-debugging-port={port} --remote-allow-origins=*` を設定（既存値があれば末尾に追記）。`RequireFresh` かつ同名プロセスが存在すれば `InvalidOperationException("Process already running: ...")`。起動した PID を `Started` に記録。
  - `bool Exists(string processName)`, `bool Kill(string nameOrPid)` — `Started` に含まれる PID、または `cfg.KillableProcesses` に名前が含まれる場合のみ。該当しなければ `UnauthorizedAccessException`。
  - `IReadOnlySet<int> Started { get; }`

- [ ] **Step 1: 失敗するテスト**

```csharp
[Fact]
public void Run_WithWebViewDebug_SetsEnvAndReturnsPort()
{
    var svc = new ProcessService(TestConfig.Default);
    var r = svc.Run(new RunSpec("cmd.exe", "/c echo %WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS% > \"" + outFile + "\"", null, null, WebViewDebug: true, RequireFresh: false));
    Assert.NotNull(r.CdpPort);
    // プロセス終了を待って outFile に "--remote-debugging-port={r.CdpPort} --remote-allow-origins=*" が含まれる
}

[Fact]
public void Kill_RefusesProcessNotStartedByUs()
{
    var svc = new ProcessService(TestConfig.Default);
    Assert.Throws<UnauthorizedAccessException>(() => svc.Kill("explorer"));
}

[Fact]
public void Kill_AllowsProcessStartedByUs()
{
    var svc = new ProcessService(TestConfig.Default);
    var r = svc.Run(new RunSpec("cmd.exe", "/c timeout /t 30", null, null, false, false));
    Assert.True(svc.Kill(r.Pid.ToString()));
}
```

`tests/WinUse.Core.Tests/TestConfig.cs` に `static class TestConfig` を作る: `Default`（Task 1 の既定値、`JevApiKey=null`、`KillableProcesses=[]`）、`WithRoots(params string[] roots)`、`WithJev(string apiKey)`。以降のタスクはこれを使う。

- [ ] **Step 2: 失敗を確認** → **Step 3: 実装** → **Step 4: PASS を確認**
- [ ] **Step 5: コミット** — `git commit -m "feat(core): add process service with WebView2 debug port"`

---

### Task 8: 許可ルート付きファイルサービス

**Files:**

- Create: `src/WinUse.Core/Files/FileService.cs`
- Test: `tests/WinUse.Core.Tests/Files/FileServiceTests.cs`

**Interfaces:**

- Produces: `sealed class FileService(WinUseConfig cfg)` with `bool Exists(string path)`, `string ReadText(string path)`, `void WriteText(string path, string text)`, `void AppendText(string path, string text)`, `void Copy(string src, string dst, bool overwrite)`, `void Move(string src, string dst)`, `void Delete(string path)`（ファイルもディレクトリも）, `IReadOnlyList<string> ListDir(string path)`, `void Zip(string dir, string zipPath)`, `void Unzip(string zipPath, string dir)`。すべて `Path.GetFullPath` 後に `cfg.AllowedRoots` のいずれかの配下でなければ `UnauthorizedAccessException("Path outside allowed roots: ...")`。

- [ ] **Step 1: 失敗するテスト**

```csharp
[Fact]
public void WriteAndRead_InsideAllowedRoot_Works()
{
    var root = Directory.CreateTempSubdirectory().FullName;
    var svc = new FileService(TestConfig.WithRoots(root));
    var f = Path.Combine(root, "a.txt");
    svc.WriteText(f, "hi"); svc.AppendText(f, "!");
    Assert.Equal("hi!", svc.ReadText(f));
}

[Fact]
public void Delete_OutsideAllowedRoot_Throws()
{
    var svc = new FileService(TestConfig.WithRoots(Directory.CreateTempSubdirectory().FullName));
    Assert.Throws<UnauthorizedAccessException>(() => svc.Delete(@"C:\Windows\notepad.exe"));
}

[Fact]
public void TraversalOutsideRoot_Throws()
{
    var root = Directory.CreateTempSubdirectory().FullName;
    var svc = new FileService(TestConfig.WithRoots(root));
    Assert.Throws<UnauthorizedAccessException>(() => svc.Exists(Path.Combine(root, "..", "x.txt")));
}
```

- [ ] **Step 2: 失敗を確認** → **Step 3: 実装** → **Step 4: PASS を確認**
- [ ] **Step 5: コミット** — `git commit -m "feat(core): add allow-listed file service"`

---

### Task 9: UIA サービス（FlaUI）

**Files:**

- Create: `src/WinUse.Core/Uia/UiaService.cs`, `src/WinUse.Core/Uia/UiaLocatorResolver.cs`, `src/WinUse.Core/Uia/UiNode.cs`
- Modify: `src/WinUse.Core/WinUse.Core.csproj`（`FlaUI.UIA3` 5.0.0）
- Test: `tests/WinUse.Core.Tests/Uia/UiaServiceTests.cs`（Integration）, `tests/WinUse.Core.Tests/Uia/InteractiveFilterTests.cs`（Unit）

**Interfaces:**

- Produces: `sealed record UiNode(string Ref, string ControlType, string? Name, string? AutomationId, string? Value, bool Enabled, bool Offscreen, bool? Checked, bool Focused, Rect Rect, int Depth, IReadOnlyList<UiNode> Children)`
- Produces: `sealed record TreeOptions(string? ScopeRef = null, int MaxDepth = 12, bool InteractiveOnly = true, bool IncludeOffscreen = false)`
- Produces: `static class InteractiveFilter` with `static bool IsInteractive(string controlType)` — true: Button, Edit, ComboBox, CheckBox, RadioButton, MenuItem, TabItem, ListItem, TreeItem, Hyperlink, Document, SplitButton, Slider, Spinner, Tab, List, Tree, Menu, MenuBar, ToolBar, Pane(子を持つ場合のみ保持), Window。Text は保持するが `IsInteractive=false`。
- Produces: `sealed class UiaService(AutomationDispatcher d, RefRegistry refs, WindowService windows) : IDisposable` with
  - `Task<UiNode> GetTreeAsync(nint hwnd, TreeOptions opt)` — 各ノードを `refs.Register(RefKind.Uia, UiaLocator, element)` で登録。`InteractiveOnly` ならインタラクティブでないコンテナは畳む（子だけ持ち上げる）。
  - `Task<UiNode> GetElementAsync(string refId)`（全プロパティ版: `Children` 空）
  - `Task<string> GetTextAsync(string refId)`（Value → Text → Name の順）
  - `Task SetTextAsync(string refId, string text)`（Value パターン、無ければ focus + ctrl+a + TypeText）
  - `Task SelectItemAsync(string refId, string item)`（ComboBox: Expand → 子 ListItem を Name 一致で Select）
  - `Task SetCheckedAsync(string refId, bool on)`, `Task InvokeAsync(string refId)`（Invoke → Toggle → Select → 中心クリックの順で試す）, `Task FocusAsync(string refId)`
  - `Task<string[][]> GetTableAsync(string refId)`, `Task<string[]> GetListItemsAsync(string refId)`
  - `Task<AutomationElement?> ResolveAsync(UiaLocator loc)` — `UiaLocatorResolver` を使い、ウィンドウを `windows.FindAsync` で見つけてから Path を順に辿る（AutomationId 優先、無ければ ControlType + Name、同名複数は Index）。
  - `Task<Rect> GetRectAsync(string refId)`

- [ ] **Step 1: 失敗する Unit テスト**

```csharp
[Theory]
[InlineData("Button", true)] [InlineData("Edit", true)] [InlineData("Text", false)] [InlineData("Image", false)]
public void IsInteractive(string type, bool expected) => Assert.Equal(expected, InteractiveFilter.IsInteractive(type));
```

- [ ] **Step 2: 失敗する Integration テスト**

```csharp
[Fact, Trait("Category", "Integration")]
public async Task Notepad_TreeContainsDocument_AndSetTextRoundTrips()
{
    // notepad 起動 → hwnd 取得 → GetTreeAsync → ControlType=="Document" か "Edit" のノードを探す
    // → SetTextAsync(ref, "hello uia") → GetTextAsync(ref) == "hello uia"
    // → ResolveAsync(そのノードの UiaLocator) が null でない
}

[Fact, Trait("Category", "Integration")]
public async Task Calculator_InvokeButtons_ComputesSum()
{
    // calc.exe 起動 → "1"(AutomationId num1Button), "+"(plusButton), "2"(num2Button), "="(equalButton) を Invoke
    // → AutomationId "CalculatorResults" の Name に "3" が含まれる
}
```

- [ ] **Step 3: 失敗を確認** → **Step 4: 実装**（`UIA3Automation` は dispatcher スレッド上で 1 回だけ生成。`CacheRequest` は使わず、プロパティ取得は `element.Properties.X.ValueOrDefault`。）
- [ ] **Step 5: PASS を確認** — Run: `dotnet test --filter "FullyQualifiedName~Uia"`
- [ ] **Step 6: コミット** — `git commit -m "feat(core): add FlaUI-based UIA service with locators"`

---

### Task 10: WebView2 サービス（Playwright over CDP）

**Files:**

- Create: `src/WinUse.Core/Web/WebService.cs`, `src/WinUse.Core/Web/DomNode.cs`, `src/WinUse.Core/Web/DomCollector.js`（`EmbeddedResource`）
- Modify: `src/WinUse.Core/WinUse.Core.csproj`（`Microsoft.Playwright` 1.63.0、`DomCollector.js` を EmbeddedResource）
- Test: `tests/WinUse.Core.Tests/Web/WebServiceTests.cs`（Integration。CDP 対象は `msedge.exe --remote-debugging-port=<port> --user-data-dir=<temp> --no-first-run about:blank` を使う。WebView2 固有の環境変数は Task 16 の MDium シナリオで検証する）, `tests/WinUse.Core.Tests/Web/DomCollectorTests.cs`（Integration、同じ Edge に `page.SetContentAsync` でテスト HTML を流す）

**Interfaces:**

- Produces: `sealed record DomNode(string Ref, string Role, string? Name, string Tag, string? Value, bool Enabled, bool Focused, bool? Checked, Rect Rect, string Selector)` — `Rect` はページ座標ではなく**スクリーン物理座標**（`window.screenX/Y` + `devicePixelRatio` + `getBoundingClientRect` から算出。ウィンドウ枠のオフセットは `WindowService` の矩形と WebView2 ホストの UIA 矩形から補正する。ズレが出る場合は Task 12 の DOM 優先ルールでクリックは Playwright 経由にするため致命的ではない）
- Produces: `sealed record PageInfo(string Id, string Url, string Title)`
- Produces: `sealed class WebService(RefRegistry refs) : IAsyncDisposable` with
  - `Task ConnectAsync(int cdpPort, TimeSpan timeout)` — `http://127.0.0.1:{port}/json/version` を 250ms ポーリングしてから `Chromium.ConnectOverCDPAsync`。
  - `bool IsConnected { get; }`, `Task<IReadOnlyList<PageInfo>> ListPagesAsync()`, `Task SelectPageAsync(string idOrUrlSubstring)`（既定は最後にアクティブになったページ。`page.BringToFront` はしない）
  - `Task<IReadOnlyList<DomNode>> GetDomAsync(bool interactiveOnly = true)` — `DomCollector.js` を `page.EvaluateAsync` して配列を受け取り、各要素を `refs.Register(RefKind.Dom, DomLocator, selector)`。
  - `Task ClickAsync(string refId)`, `Task FillAsync(string refId, string text)`, `Task SelectOptionAsync(string refId, string label)`, `Task<string> GetTextAsync(string refId)`, `Task<bool> ExistsAsync(string selector)`, `Task<string> GetTextBySelectorAsync(string selector)`
  - `Task<string?> ResolveAsync(DomLocator loc)` — selector が 1 件に一致すれば selector を返す。
- `DomCollector.js` の契約: `(interactiveOnly) => Array<{role, name, tag, value, enabled, focused, checked, rect:{x,y,w,h}, selector}>`。selector の優先順: `[data-testid="…"]` → `#id`（id が英数のみ）→ `role=…[name="…"]`（Playwright の role セレクタ、name は aria-label / label / textContent の順）→ CSS の nth-child パス。interactiveOnly では `a, button, input, select, textarea, [role=button|link|textbox|checkbox|radio|combobox|menuitem|tab|option|switch], [contenteditable=true]` を可視（`getClientRects().length>0`、`visibility!=hidden`）かつ `display!=none` のものに絞る。

- [ ] **Step 1: 失敗する Integration テスト**

```csharp
[Fact, Trait("Category", "Integration")]
public async Task GetDom_ListsInteractiveElementsWithStableSelectors()
{
    // Edge を CDP 付きで起動 → ConnectAsync → page.SetContentAsync("<button data-testid='save'>Save</button><input id='q'><p>text</p>")
    var nodes = await web.GetDomAsync();
    Assert.Equal(2, nodes.Count);
    Assert.Contains(nodes, n => n.Selector == "[data-testid=\"save\"]" && n.Role == "button" && n.Name == "Save");
    Assert.Contains(nodes, n => n.Selector == "#q" && n.Role == "textbox");
}

[Fact, Trait("Category", "Integration")]
public async Task Fill_ThenGetText_RoundTrips()
{
    // 同上の HTML → FillAsync(inputRef, "abc") → GetTextAsync(inputRef) == "abc"
}
```

- [ ] **Step 2: 失敗を確認** → **Step 3: 実装** → **Step 4: PASS を確認** — Run: `dotnet test --filter "FullyQualifiedName~Web"`
- [ ] **Step 5: コミット** — `git commit -m "feat(core): add Playwright-over-CDP web service and DOM collector"`

---

### Task 11: Jev クライアント

**Files:**

- Create: `src/WinUse.Core/Jev/JevModels.cs`, `src/WinUse.Core/Jev/JevClient.cs`
- Test: `tests/WinUse.Core.Tests/Jev/JevClientTests.cs`

**Interfaces:**

- Produces: `abstract record JevQuestion(string Instructions)`; `sealed record ChoiceQuestion(string Instructions, IReadOnlyDictionary<string,string?> Criteria) : JevQuestion`; `sealed record NoulQuestion(string Instructions) : JevQuestion`
- Produces: `sealed record ChoiceAnswer(string Choice, IReadOnlyDictionary<string,double> Probabilities, double Confidence)`; `sealed record NoulAnswer(double Probability)`
- Produces: `sealed record JevResponse(string Model, IReadOnlyDictionary<string, object> Answers, int InputTokens)` — 値は `ChoiceAnswer` か `NoulAnswer`。
- Produces: `interface IJevClient { bool IsConfigured { get; } Task<JevResponse> AskAsync(object state, IReadOnlyDictionary<string, JevQuestion> questions, CancellationToken ct = default); }`
- Produces: `sealed class JevClient(HttpClient http, WinUseConfig cfg, ILogger<JevClient> log) : IJevClient` — 要求 JSON: `{ "model": cfg.JevModel, "state": state, "questions": { id: { "type": "choice"|"noul", "instructions": ..., "criteria": {...} } } }`。応答 `answers[id]` の `type` で分岐。`IsConfigured = cfg.JevApiKey != null`。未設定で `AskAsync` → `InvalidOperationException("Jev is not configured")`。429/529 は 0.5s, 1s, 2s のバックオフで最大 3 回再試行、それでもだめなら `JevUnavailableException`。タイムアウト 10 秒 → `JevUnavailableException`。
- Produces: `sealed class JevUnavailableException(string message, Exception? inner) : Exception`

- [ ] **Step 1: 失敗するテスト（`HttpMessageHandler` のフェイクを `tests/.../FakeHttpHandler.cs` に作る）**

```csharp
[Fact]
public async Task AskAsync_SendsExpectedBodyAndParsesChoiceAndNoul()
{
    var handler = new FakeHttpHandler(req => {
        Assert.Equal("https://api.typesafe.ai/v1/systemone", req.RequestUri!.ToString());
        Assert.Equal("Bearer k", req.Headers.Authorization!.ToString());
        var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result).RootElement;
        Assert.Equal("jev-latest", body.GetProperty("model").GetString());
        Assert.Equal("choice", body.GetProperty("questions").GetProperty("target").GetProperty("type").GetString());
        return Json(200, """{"model":"jev-1.13.0","answers":{"target":{"type":"choice","choice":"u3","probabilities":{"u3":0.9,"none":0.1},"confidence":0.8},"done":{"type":"noul","noul":0.2}},"usage":{"input_tokens":120,"output_tokens":0}}""");
    });
    var client = new JevClient(new HttpClient(handler), TestConfig.WithJev("k"), NullLogger<JevClient>.Instance);
    var r = await client.AskAsync(new { goal = "x" }, new Dictionary<string, JevQuestion> {
        ["target"] = new ChoiceQuestion("Which element?", new Dictionary<string,string?> { ["u3"] = "Save button", ["none"] = null }),
        ["done"] = new NoulQuestion("Is it done?") });
    var c = Assert.IsType<ChoiceAnswer>(r.Answers["target"]);
    Assert.Equal("u3", c.Choice); Assert.Equal(0.8, c.Confidence);
    Assert.Equal(0.2, Assert.IsType<NoulAnswer>(r.Answers["done"]).Probability);
}

[Fact]
public async Task AskAsync_RetriesOn429_ThenThrowsJevUnavailable()
{
    var handler = new FakeHttpHandler(_ => new HttpResponseMessage((HttpStatusCode)429));
    var client = new JevClient(new HttpClient(handler), TestConfig.WithJev("k"), NullLogger<JevClient>.Instance);
    await Assert.ThrowsAsync<JevUnavailableException>(() => client.AskAsync(new { }, new Dictionary<string, JevQuestion> { ["q"] = new NoulQuestion("?") }));
    Assert.Equal(4, handler.CallCount);   // 初回 + 3 回再試行
}

[Fact]
public void IsConfigured_FalseWithoutKey()
    => Assert.False(new JevClient(new HttpClient(), TestConfig.Default, NullLogger<JevClient>.Instance).IsConfigured);
```

再試行テストのバックオフは `JevClient` のコンストラクタ省略引数 `TimeSpan[]? backoff = null` でテストから `[0,0,0]` に差し替える。

- [ ] **Step 2: 失敗を確認** → **Step 3: 実装** → **Step 4: PASS を確認**
- [ ] **Step 5: コミット** — `git commit -m "feat(core): add Jev typed-decision client"`

---

### Task 12: Grounding（候補生成と act / find / check）

**Files:**

- Create: `src/WinUse.Core/Grounding/Candidate.cs`, `src/WinUse.Core/Grounding/CandidateBuilder.cs`, `src/WinUse.Core/Grounding/Grounder.cs`
- Test: `tests/WinUse.Core.Tests/Grounding/CandidateBuilderTests.cs`, `tests/WinUse.Core.Tests/Grounding/GrounderTests.cs`

**Interfaces:**

- Produces: `sealed record Candidate(string Id, string Role, string Label, string? Value, bool Enabled, bool Focused, bool Editable, bool? Checked, string Region, string? Container)` — `Region` は `"top-left"`…`"bottom-right"` の 9 分割（ウィンドウ矩形基準）。`Container` は直近の祖先コンテナのラベル（二段階用）。
- Produces: `static class CandidateBuilder` with
  - `static IReadOnlyList<Candidate> Build(UiNode uiaTree, IReadOnlyList<DomNode> dom, Rect windowRect, Rect? webViewRect)` — UIA ノードのうち `webViewRect` に含まれるものは落とし（DOM 優先）、DOM ノードを追加。`Label` は Name → Value → AutomationId → Selector の順。
  - `static IReadOnlyList<IReadOnlyList<Candidate>> SplitForChoice(IReadOnlyList<Candidate> all, int max = 254)` — 254 以下ならそのまま 1 グループ。超えるときは `Container` ごとにまとめ、各グループが 254 以下になるまで分割。（`none` の分を 1 つ空ける）
- Produces: `sealed record FindResult(IReadOnlyList<(Candidate Candidate, double Probability)> Ranked, double Confidence, bool Grounded)`; `sealed record ActResult(bool Executed, Candidate? Target, double Probability, double Confidence, bool? Verified, IReadOnlyList<(Candidate, double)> Alternatives, string? Reason)`; `sealed record CheckResult(double Probability, bool Available)`
- Produces: `enum ActKind { Auto, Click, Type, Select, Toggle }`
- Produces: `interface IObservationSource { Task<Observation> ObserveAsync(string? window); }` と `sealed record Observation(WindowInfo Window, IReadOnlyList<Candidate> Candidates, IReadOnlyList<string> RecentActions)` — 実装は Task 14 の `ToolDispatcher` 側（UIA + Web + Window を束ねる）。Grounder はこのインターフェースだけに依存する。
- Produces: `interface IActionExecutor { Task ExecuteAsync(Candidate target, ActKind kind, string? text); }` — 実装は Task 14。
- Produces: `sealed class Grounder(IJevClient jev, IObservationSource obs, IActionExecutor exec, WinUseConfig cfg)` with
  - `Task<FindResult> FindAsync(string intent, string? window, int topK = 5)` — `jev.IsConfigured` が false なら `Grounded=false` で候補をラベル順に返す。グループが複数なら第一段で `Container` の choice、第二段で要素の choice。
  - `Task<ActResult> ActAsync(string intent, string? window, ActKind kind, string? text, double? minConfidence)` — Find → 閾値判定（probability ≥ cfg.MinProbability かつ confidence ≥ (minConfidence ?? cfg.MinConfidence)）→ `exec.ExecuteAsync` → 再観察して `satisfied` noul → `Verified = p ≥ cfg.SatisfiedThreshold`。閾値未満は `Executed=false`, `Reason="low confidence"`, `Alternatives` 上位 5 件。`kind=Auto` は `kind` 質問を同時に投げる。
  - `Task<CheckResult> CheckAsync(string question, string? window)` — 未設定 / `JevUnavailableException` なら `Available=false`。
- state の形: `{ goal, window: { title, process }, elements: Candidate[]（Id, Role, Label, Value, Enabled, Focused, Editable, Checked, Region のみ）, recent_actions: string[] }`。質問 `target` の instructions は `"Which element should be acted on to accomplish `goal`? Choose none if no element fits."`、criteria は `Id → "{Role} '{Label}' ({Region})"` + `none → null`。

- [ ] **Step 1: 失敗する Unit テスト（Review Focus 4 を含む）**

```csharp
[Fact]
public void Build_DropsUiaNodesInsideWebViewRect_AndPrefersDom()
{
    var uia = Tree(Button("Save", rect: new Rect(10,10,50,20)), Button("WebBtn", rect: new Rect(300,300,50,20)));
    var dom = new[] { Dom("button", "WebBtn", "[data-testid=\"web\"]", new Rect(300,300,50,20)) };
    var c = CandidateBuilder.Build(uia, dom, new Rect(0,0,900,900), new Rect(200,200,600,600));
    Assert.Equal(2, c.Count);
    Assert.Contains(c, x => x.Id.StartsWith("w") && x.Label == "WebBtn");
    Assert.Equal("top-left", c.Single(x => x.Label == "Save").Region);
}

[Fact]
public void SplitForChoice_KeepsGroupsUnder255()
{
    var many = Enumerable.Range(0, 600).Select(i => Cand($"u{i}", container: $"Group{i / 100}")).ToList();
    var groups = CandidateBuilder.SplitForChoice(many);
    Assert.True(groups.Count >= 3);
    Assert.All(groups, g => Assert.True(g.Count <= 254));
}

[Fact]
public async Task ActAsync_ExecutesWhenAboveThreshold_AndVerifies()
{
    var jev = new FakeJev(choice: ("u2", 0.9, 0.8), noul: 0.95);
    var exec = new FakeExecutor();
    var g = new Grounder(jev, new FakeObservation(Cand("u1","Cancel"), Cand("u2","Save")), exec, TestConfig.WithJev("k"));
    var r = await g.ActAsync("click save", null, ActKind.Click, null, null);
    Assert.True(r.Executed); Assert.Equal("u2", exec.Last!.Id); Assert.True(r.Verified);
}

[Fact]
public async Task ActAsync_ReturnsAlternativesWhenBelowThreshold()
{
    var jev = new FakeJev(choice: ("u2", 0.4, 0.3), noul: 0.5);
    var g = new Grounder(jev, new FakeObservation(Cand("u1","Cancel"), Cand("u2","Save")), new FakeExecutor(), TestConfig.WithJev("k"));
    var r = await g.ActAsync("click save", null, ActKind.Click, null, null);
    Assert.False(r.Executed); Assert.Equal("low confidence", r.Reason); Assert.NotEmpty(r.Alternatives);
}

[Fact]
public async Task FindAsync_WithoutJev_ReturnsUngroundedCandidates()
{
    var g = new Grounder(new FakeJev(configured: false), new FakeObservation(Cand("u1","Cancel")), new FakeExecutor(), TestConfig.Default);
    var r = await g.FindAsync("anything", null);
    Assert.False(r.Grounded); Assert.Single(r.Ranked);
}

[Fact]
public async Task CheckAsync_WhenJevUnavailable_ReportsNotAvailable()
{
    var g = new Grounder(new FakeJev(throwUnavailable: true), new FakeObservation(), new FakeExecutor(), TestConfig.WithJev("k"));
    Assert.False((await g.CheckAsync("done?", null)).Available);
}
```

フェイク（`FakeJev`, `FakeObservation`, `FakeExecutor`）とヘルパー（`Tree`, `Button`, `Dom`, `Cand`）は `tests/WinUse.Core.Tests/Grounding/Fakes.cs` に置く。

- [ ] **Step 2: 失敗を確認** → **Step 3: 実装** → **Step 4: PASS を確認** — Run: `dotnet test --filter "FullyQualifiedName~Grounding"`
- [ ] **Step 5: コミット** — `git commit -m "feat(core): add candidate builder and Jev-backed grounder"`

---

### Task 13: ジャーナルとシナリオ（モデル・保存・再生）

**Files:**

- Create: `src/WinUse.Core/Scenarios/Journal.cs`, `src/WinUse.Core/Scenarios/Scenario.cs`, `src/WinUse.Core/Scenarios/IToolDispatcher.cs`, `src/WinUse.Core/Scenarios/ScenarioRunner.cs`
- Test: `tests/WinUse.Core.Tests/Scenarios/JournalTests.cs`, `tests/WinUse.Core.Tests/Scenarios/ScenarioRunnerTests.cs`

**Interfaces:**

- Produces: `sealed record JournalEntry(string StepId, string Tool, JsonObject Args, Locator? Resolved, bool? Passed, TimeSpan Elapsed, DateTimeOffset At)`; `sealed class Journal` with `string NextId()`（`s1`, `s2`…）, `void Record(JournalEntry e)`, `IReadOnlyList<JournalEntry> Entries`, `bool TryGet(string stepId, out JournalEntry e)`
- Produces: `sealed record ScenarioStep(string Tool, JsonObject Args, Locator? Resolved = null, bool Soft = false, double? MinProbability = null)`; `sealed record Scenario(string Name, int Version, IReadOnlyDictionary<string,string> Env, IReadOnlyList<ScenarioStep> Steps, string? Description = null)` with `static Scenario FromJournal(string name, Journal j, IEnumerable<string> stepIds, string? description)`（`check` ツールのステップは `Soft=true`, `MinProbability=0.8`）, `string ToJson()`, `static Scenario Parse(string json)`, `void Save(string path)`, `static Scenario Load(string path)`
- Produces: `sealed record ToolInvocation(string Tool, JsonObject Args, Locator? Resolved)`; `sealed record ToolOutcome(bool Ok, bool? Passed, Locator? Resolved, JsonObject Result, string? Error, bool JevUnavailable = false)`
- Produces: `interface IToolDispatcher { Task<ToolOutcome> InvokeAsync(ToolInvocation inv, CancellationToken ct); }`
- Produces: `enum StepStatus { Pass, Fail, Warn, Error }`; `sealed record StepReport(int Index, string Tool, StepStatus Status, string? Message, TimeSpan Elapsed, string? ScreenshotBefore, string? ScreenshotAfter)`; `sealed record RunReport(string Scenario, IReadOnlyList<StepReport> Steps, int ExitCode)`
- Produces: `sealed class ScenarioRunner(IToolDispatcher dispatcher, Func<Task<byte[]>>? screenshot, string? reportDir)` with `Task<RunReport> RunAsync(Scenario s, bool heal, CancellationToken ct)`:
  1. `${VAR}` を `s.Env` → 環境変数の順で展開（未定義は Error、ExitCode 2）。
  2. 各ステップを `dispatcher.InvokeAsync` に渡す。`Resolved` があれば渡す（ディスパッチャはロケータ優先で解決し、`heal=false` で解決失敗なら `Ok=false`）。
  3. `Soft` ステップ: `Ok=false` または `JevUnavailable` または `Passed=false` → `Warn`。
  4. 非 Soft: `Ok=false` → `Error`（以降中断、ExitCode 2）、`Passed=false` → `Fail`（以降中断、ExitCode 1）。
  5. `screenshot` があれば各ステップ前後に撮って `reportDir/{index:D3}-{before|after}.png` に保存。
  6. 全ステップ Pass/Warn なら ExitCode 0。`heal=true` でディスパッチャが新しい `Resolved` を返したら `s` を `{path}.healed.json` に保存（`RunAsync` の呼び出し側 = CLI が担当。Runner は `RunReport` に `HealedScenario` を含める）。

- [ ] **Step 1: 失敗するテスト（Review Focus 5 を含む）**

```csharp
[Fact]
public void FromJournal_PicksOnlyGivenStepsInOrder_AndMarksCheckSoft()
{
    var j = new Journal();
    j.Record(Entry(j.NextId(), "click", Args(("ref","u1"))));          // s1 (除外)
    j.Record(Entry(j.NextId(), "act", Args(("intent","save")), new DomLocator(W, "#save")));  // s2
    j.Record(Entry(j.NextId(), "check", Args(("question","saved?"))));  // s3
    var s = Scenario.FromJournal("t", j, ["s2", "s3"], null);
    Assert.Equal(["act", "check"], s.Steps.Select(x => x.Tool));
    Assert.True(s.Steps[1].Soft); Assert.Equal(0.8, s.Steps[1].MinProbability);
    Assert.Equal(s, Scenario.Parse(s.ToJson()));
}

[Fact]
public async Task RunAsync_SoftStepWithJevUnavailable_IsWarnAndExitZero()
{
    var d = new FakeDispatcher(("check", new ToolOutcome(false, null, null, new(), "jev", JevUnavailable: true)));
    var s = new Scenario("t", 1, new Dictionary<string,string>(), [new("check", Args(("question","?")), Soft: true)]);
    var r = await new ScenarioRunner(d, null, null).RunAsync(s, false, default);
    Assert.Equal(StepStatus.Warn, r.Steps[0].Status); Assert.Equal(0, r.ExitCode);
}

[Fact]
public async Task RunAsync_FailedAssert_StopsWithExitOne()
{
    var d = new FakeDispatcher(("assert_text", new ToolOutcome(true, false, null, new(), null)), ("click", new ToolOutcome(true, null, null, new(), null)));
    var s = new Scenario("t", 1, new Dictionary<string,string>(), [new("assert_text", Args()), new("click", Args())]);
    var r = await new ScenarioRunner(d, null, null).RunAsync(s, false, default);
    Assert.Equal(1, r.ExitCode); Assert.Single(r.Steps);
}

[Fact]
public async Task RunAsync_ExpandsEnvVariables_AndErrorsOnUndefined()
{
    var d = new FakeDispatcher(("process_run", new ToolOutcome(true, null, null, new(), null)));
    var ok = new Scenario("t", 1, new Dictionary<string,string> { ["EXE"] = "x.exe" }, [new("process_run", Args(("path","${EXE}")))]);
    await new ScenarioRunner(d, null, null).RunAsync(ok, false, default);
    Assert.Equal("x.exe", d.Last!.Args["path"]!.GetValue<string>());
    var bad = new Scenario("t", 1, new Dictionary<string,string>(), [new("process_run", Args(("path","${NOPE}")))]);
    Assert.Equal(2, (await new ScenarioRunner(d, null, null).RunAsync(bad, false, default)).ExitCode);
}
```

- [ ] **Step 2: 失敗を確認** → **Step 3: 実装** → **Step 4: PASS を確認** — Run: `dotnet test --filter "FullyQualifiedName~Scenarios"`
- [ ] **Step 5: コミット** — `git commit -m "feat(core): add journal, scenario model and runner"`

---

### Task 14: MCP ツール層とディスパッチャ

**Files:**

- Create: `src/WinUse/Tools/ToolResult.cs`, `src/WinUse/Tools/ToolContext.cs`, `src/WinUse/Tools/ObserveTools.cs`, `src/WinUse/Tools/IntentTools.cs`, `src/WinUse/Tools/InputTools.cs`, `src/WinUse/Tools/ElementTools.cs`, `src/WinUse/Tools/WindowTools.cs`, `src/WinUse/Tools/WaitTools.cs`, `src/WinUse/Tools/ProcessTools.cs`, `src/WinUse/Tools/FileTools.cs`, `src/WinUse/Tools/AssertTools.cs`, `src/WinUse/Tools/ScenarioTools.cs`, `src/WinUse/Tools/ToolDispatcher.cs`
- Modify: `src/WinUse/WinUse.csproj`（`ModelContextProtocol` 2.2.0, `Microsoft.Extensions.Hosting`）, `src/WinUse/Program.cs`
- Test: `tests/WinUse.Core.Tests/Tools/ToolRegistryTests.cs`（Unit）, `tests/WinUse.Core.Tests/Tools/ToolDispatcherTests.cs`（Unit、ファイル系ツールで往復）

**Interfaces:**

- Produces: `sealed class ToolContext`（DI シングルトン、`static ToolContext Create(WinUseConfig cfg)` で組み立て）: `WinUseConfig Config`, `AutomationDispatcher Dispatcher`, `RefRegistry Refs`, `WindowService Windows`, `InputService Input`, `ScreenService Screen`, `ProcessService Processes`, `FileService Files`, `UiaService Uia`, `WebService Web`, `IJevClient Jev`, `Grounder Grounder`, `Journal Journal`, `nint? LastWindow`。`ToolContext` は `IObservationSource` と `IActionExecutor` を実装する（Observe: 対象ウィンドウ → `Uia.GetTreeAsync` + `Web.IsConnected` なら `Web.GetDomAsync` → `CandidateBuilder.Build`。Execute: Candidate の Id が `w` 始まりなら `Web.ClickAsync/FillAsync/SelectOptionAsync`、`u` 始まりなら `Uia.InvokeAsync/SetTextAsync/SelectItemAsync/SetCheckedAsync`）。
- Produces: `static class ToolResult` with `static string Ok(object payload, string stepId)`（`{"step_id":..., ...payload}` の JSON）, `static CallToolResult Error(string message, string stepId)`（`IsError=true`）。すべてのツールは `ToolContext.Journal` に記録し `step_id` を返す。
- Produces: 各 `[McpServerToolType]` クラスの `[McpServerTool(Name="…")]` メソッド。ツール名と引数は設計書 §6 のとおり。`click` / `hover` / `drag` / `scroll` に ref が渡されたとき、`w` 始まりは `Web.ClickAsync` 等を使い、`u` 始まりは `Uia.GetRectAsync` の中心座標で `Input` を使う。`wait_for` は 200ms ポーリングで `window` → `Windows.WaitForAsync`、`window_closed` → `Windows.WaitClosedAsync`、`element(selector)` → `Web.ExistsAsync` または `Uia.ResolveAsync`、`text(ref, expected)` → `GetText` の部分一致、`enabled(ref)` → `GetElement().Enabled` を使う。`ToolContext` のサービスは副作用なく生成できる（UIA3Automation と Playwright は初回使用時に生成）。`screenshot` は `IEnumerable<ContentBlock>`（`ImageContentBlock.FromBytes(png, "image/png")` + 寸法の `TextContentBlock`）を返す。それ以外は `string`（JSON）または `CallToolResult`。
- Produces: `sealed class ToolDispatcher(ToolContext ctx) : IToolDispatcher` — ツール名からメソッドへのマップ（リフレクションで `[McpServerTool]` の `Name` を集める）。`inv.Resolved` があれば先にロケータを解決して `ref` 引数を差し替える（UIA: `Uia.ResolveAsync` → `Refs.Register`、DOM: `Web.ResolveAsync`）。解決失敗で `heal=false` のときは `Ok=false, Error="locator not found"`。`act`/`find`/`check` の結果は `ActResult` 等を JSON にしたもの。`check` で `Available=false` のときは `JevUnavailable=true`。
- Produces: `Program.cs` — `--mcp`: `Host.CreateApplicationBuilder` + `AddMcpServer().WithStdioServerTransport().WithTools<ObserveTools>()...`、`builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)`、ファイルログは `cfg.LogDirectory/winuse-{yyyyMMdd}.log` に簡易な `ILoggerProvider` で追記。`run` / `doctor` は Task 15。

- [ ] **Step 1: 失敗する Unit テスト**

```csharp
[Fact]
public void AllSpecifiedToolsAreRegistered()
{
    var names = ToolDispatcher.DiscoverToolNames();   // static、リフレクション
    string[] expected = ["screenshot","list_windows","get_ui_tree","get_dom","get_element","get_text","get_table","get_list_items",
        "act","find","check","click","hover","drag","scroll","type_text","press_keys",
        "set_text","select_item","set_checked","focus","invoke",
        "window_activate","window_close","window_set_state","window_move","window_exists","wait_for",
        "process_run","process_kill","process_exists",
        "file_exists","read_text","write_text","append_text","copy","move","delete","list_dir","zip","unzip",
        "assert_text","assert_exists","assert_window","assert_file",
        "scenario_save","scenario_list","scenario_run"];
    Assert.Empty(expected.Except(names));
}

[Fact]
public async Task Dispatcher_WriteThenReadText_RoundTripsAndJournals()
{
    var ctx = ToolContext.Create(TestConfig.WithRoots(tmp));   // 本物の ToolContext。UIA / Playwright は未使用なら初期化されない
    var d = new ToolDispatcher(ctx);
    var w = await d.InvokeAsync(new("write_text", Args(("path", f), ("text", "hi")), null), default);
    Assert.True(w.Ok);
    var r = await d.InvokeAsync(new("read_text", Args(("path", f)), null), default);
    Assert.Equal("hi", r.Result["text"]!.GetValue<string>());
    Assert.Equal(2, ctx.Journal.Entries.Count);
}
```

- [ ] **Step 2: 失敗を確認** → **Step 3: 実装** → **Step 4: PASS を確認** — Run: `dotnet test --filter "FullyQualifiedName~Tools"`
- [ ] **Step 5: 手動スモーク** — `dotnet run --project src/WinUse -- --mcp` を起動し、stdin に `{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}` を流して stdout に `result` が返り、stderr 以外にログが出ないことを確認。
- [ ] **Step 6: コミット** — `git commit -m "feat: add MCP tool layer and dispatcher"`

---

### Task 15: CLI（run / doctor）と Claude Code 登録

**Files:**

- Modify: `src/WinUse/Program.cs`
- Create: `src/WinUse/Cli/RunCommand.cs`, `src/WinUse/Cli/DoctorCommand.cs`, `README.md`
- Test: `tests/WinUse.Core.Tests/Cli/RunCommandTests.cs`

**Interfaces:**

- Produces: `static class RunCommand` with `static Task<int> ExecuteAsync(string scenarioPath, bool heal, string? reportDir, ToolContext ctx)` — 非対話セッション（`Environment.UserInteractive == false` または `GetSystemMetrics(SM_REMOTESESSION)` 相当でデスクトップが無い）なら stderr にメッセージを出して 2。`Scenario.Load` → `ScenarioRunner.RunAsync` → 結果を stderr に 1 行/ステップ（`[PASS] 1 process_run (812ms)`）→ `reportDir/report.json` に `RunReport` → 終了コード。`heal` で `HealedScenario` があれば `{path}.healed.json` に保存。
- Produces: `static class DoctorCommand` with `static Task<int> ExecuteAsync(ToolContext ctx)` — 出力: `.env` の検出パス、Jev 設定有無と疎通（`AskAsync` で `noul("Is 1+1=2?")` を投げて確率を表示）、FlaUI でデスクトップのトップレベル数、WebView2 ランタイムのバージョン（`HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}` の `pv`）。
- Produces: `Program.cs` の引数規約: `winuse --mcp` / `winuse run <path> [--heal] [--report <dir>]` / `winuse doctor` / それ以外は usage を stderr に出して 2。

- [ ] **Step 1: 失敗するテスト**

```csharp
[Fact]
public async Task Run_WritesReportJsonAndReturnsExitCode()
{
    // FakeDispatcher で pass するシナリオを一時ファイルに保存 → RunCommand.ExecuteAsync(path, false, reportDir, ctx)
    // → 戻り 0、reportDir/report.json が存在し "ExitCode":0 を含む
}
```

`RunCommand` が `ToolContext` ではなく `IToolDispatcher` を受け取るオーバーロードを持ち、テストはそれを使う。

- [ ] **Step 2: 失敗を確認** → **Step 3: 実装** → **Step 4: PASS を確認**
- [ ] **Step 5: README.md** — ビルド、`.env`、Claude Code 登録コマンドを書く:

```powershell
dotnet publish src/WinUse -c Release -o out
claude mcp add winuse -- C:\Users\mtmar\source\repos\winuse\out\winuse.exe --mcp
```

- [ ] **Step 6: 手動確認** — `dotnet run --project src/WinUse -- doctor` が Jev の確率と UIA のウィンドウ数を表示する。
- [ ] **Step 7: コミット** — `git commit -m "feat: add run and doctor CLI commands"`

---

### Task 16: MDium サンプルシナリオ（受け入れ）

**Files:**

- Create: `scenarios/mdium-smoke.json`, `docs/superpowers/spikes/2026-10-19-mdium-smoke.md`

**Interfaces:**

- Consumes: 全ツール。MDium の exe は `C:\Users\mtmar\source\repos\mdium\src-tauri\target\release\mdium.exe`（存在しなければ `cd ../mdium && npm run tauri build`）。

- [ ] **Step 1: 対話で手順を確立する** — Claude Code に winuse を登録し、次の流れを `act` / `get_dom` / `screenshot` で実施する: `process_run(path=MDIUM_EXE, webview_debug=true, wait_window="MDium")` → `get_dom` で CDP 接続とページ一覧を確認 → 新規ドキュメントを作る操作を `act` で → `type_text("winuse smoke")` → 保存操作を `act` で → `assert_text` でエディタの内容を確認 → `check("Is the document saved?")` → `window_close`。各ステップの `step_id` を控える。
- [ ] **Step 2: `scenario_save(name="mdium-smoke", step_ids=[...], path="scenarios/mdium-smoke.json")`** を呼ぶ。
- [ ] **Step 3: 再生** — Run: `dotnet run --project src/WinUse -- run scenarios/mdium-smoke.json --report out/report` Expected: 終了コード 0、全ステップ PASS（`check` は PASS か WARN）。
- [ ] **Step 4: 記録** — `docs/superpowers/spikes/2026-10-19-mdium-smoke.md` に、CDP 接続までの所要時間、`get_dom` の要素数、`act` の probability / confidence、詰まった点を残す。
- [ ] **Step 5: コミット** — `git commit -m "test: add MDium smoke scenario"`

---

### Task 17: Office 接続スパイク（合否に含めない）

**Files:**

- Create: `docs/superpowers/spikes/2026-10-19-office-cdp-spike.md`

- [ ] **Step 1:** Word を終了した状態で `process_run(path="C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", webview_debug=true, require_fresh=true, wait_window="Word")` を呼ぶ。
- [ ] **Step 2:** リボンの「Agent」トグルを `get_ui_tree` + `invoke`（または `act`）で押して作業ウィンドウを開く。
- [ ] **Step 3:** `get_dom` を呼び、ページ一覧（URL / タイトル）に officecli-agent の作業ウィンドウが現れるか、他の WebView2（サインイン等）が先にポートを掴んでいないかを記録する。
- [ ] **Step 4:** 結果を `docs/superpowers/spikes/2026-10-19-office-cdp-spike.md` に書く（接続可否、ページの URL パターン、必要なフォールバック）。フェーズ 1.5 の設計入力にする。
- [ ] **Step 5: コミット** — `git commit -m "docs: record Office CDP spike results"`
