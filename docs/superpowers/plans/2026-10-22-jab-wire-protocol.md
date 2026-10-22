# JAB ワイヤープロトコル自前実装 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `WindowsAccessBridge-64.dll` を使わず、winuse 自身が Java Access Bridge のワイヤープロトコルを話して、旧形式（legacy）と現行形式（wide）の両方の JVM から Swing を読む。

**Architecture:** `JabDispatcher` のスレッド上に隠しトップレベルウィンドウ（`JabWindow`）を作り、登録メッセージで JVM と握手し、JVM ごとに名前付きメモリマップドファイル（`JabMemoryChannel`）で要求・応答を交換する。パケットは C のアライメント規則を再現する `JabPacket` が組み立て、`JabLayout`（Wide / Legacy）でハンドル幅を切り替える。`JabClient` が `IJabAccess` を実装し、`JavaService` 以上の層は変更しない。

**Tech Stack:** .NET 10 / C# 13、`System.IO.MemoryMappedFiles`、`LibraryImport` による user32 P/Invoke、xunit。

**Spec:** `docs/superpowers/specs/2026-10-22-jab-wire-protocol-design.md`

## Global Constraints

- ブランチ `feature/jab-wire-protocol`（`origin/main` = dc92bda から分岐、設計書と cherry-pick 済みの旧形式診断コミット d481a03 を含む）。
- 全 JAB 呼び出しは `JabDispatcher` のスレッドで行う。`JabWindow` もそのスレッドで作る（`HWND_BROADCAST` を受けるためトップレベルウィンドウ。`HWND_MESSAGE` は不可）。
- `SendMessageTimeout` のフラグは `SMTO_BLOCK | SMTO_NOTIMEOUTIFNOTHUNG`（0x0001 | 0x0008）、タイムアウト 4000 ms。
- 定数: `WM_USER = 0x0400`、`AB_MESSAGE_WAITING = WM_USER + 0x1001`、`AB_DLL_GOING_AWAY = WM_USER + 0x1003`、`WM_COPYDATA = 0x004A`、`HWND_BROADCAST = 0xFFFF`。登録メッセージ名 `"AccessBridge-FromWindows-Hello"` / `"AccessBridge-FromJava-Hello"`。握手文字列 `"OK?"` → `"OK!"`。
- パケット: 先頭 4 バイトが型 ID、オフセット 4 から構造体（アライメント調整なし）。型幅: `jint`/`long`/`BOOL`/`int` = 4、`jboolean` = 1、`wchar_t` = 2、`char` = 1、`ABHWND64` = 4、`JOBJECT64` = Wide 8（アライン 8）/ Legacy 4（アライン 4）。構造体末尾は最大アライメントの倍数までパディング。
- 旧形式の `JOBJECT64` は符号なし 32 ビットとして読み、下位 32 ビットを書く。`JabHandle.Ac` は `long` のまま。
- `IJabAccess` の 17 メソッドの戻り値規約は現行 `JabAccess`（`src/WinUse.Core/Java/JabAccess.cs`）と同じ: 失敗は null / false / 空、`GetValue` は 256 文字、`GetText` は 10240 文字上限、`GetVirtualName` は 1024 文字。
- 削除対象: `JabNative.cs`、`JabAccess.cs`、`JabDllLocator.cs`（+テスト）、`third_party/jab/`、`WinUse.csproj` のコピー項目、`WinUseConfig.JabDll` / `WINUSE_JAB_DLL`、`scripts/fetch-jab.ps1` の DLL 部分。
- ユーザーの対象環境（Oracle JRE 7u45）をドキュメントに書かない。「Java 7 x86」「旧形式」とだけ書く。
- コミットメッセージ末尾: `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`。既存の `.cs` に BOM は無い。

## Review Focus

- 旧形式 JVM のオブジェクトハンドルに上位ビットが立つ（32 ビットポインタ ≥ 0x80000000）: 符号拡張すると別の値になり Java 側が別オブジェクトを参照する。→ Task 3 の `ReadObj_Legacy_IsUnsigned` と `WriteObj_Legacy_WritesLow32Bits`。
- Java 21 の `javaaccessbridge.dll`（小文字・接尾辞なし）が旧形式と誤判定される。→ Task 1 の `Layout_Java9Plus_LowercaseModule_IsWide`。
- JVM が応答しない（ハング・終了直後）: `SendMessageTimeout` 失敗や完了フラグ未設定で呼び出し側がハングせず null/false を得る。→ Task 4 の `Exchange_Fails_ReturnsNullOrFalse`、Task 6 の `Jvm_Exit_IsJavaWindowFalse_NoHang`。
- 2 つの JVM が同時に居て形式が違う: 各ハンドルが自分の JVM のレイアウトで送られる。→ Task 4 の `TwoJvms_DifferentLayouts_RouteByVmId`、Task 6 の `TwoJvms_BothReadable`。
- 名前やテキストが固定長を超える / NUL 終端が無い: 書くときは切り詰めて NUL 終端、読むときは NUL か末尾で止まる。→ Task 3 の `WriteWChars_TruncatesAndTerminates`、`ReadWChars_StopsAtNulOrEnd`。

---

### Task 1: `JavaProcessInfo` の形式判定を直す（Java 9+ は wide）

**Files:**
- Modify: `src/WinUse.Core/Java/JavaProcessInfo.cs`
- Create: `src/WinUse.Core/Java/JabLayout.cs`
- Modify: `src/WinUse/Cli/DoctorCommand.cs:92-95`
- Test: `tests/WinUse.Core.Tests/Java/JavaProcessInfoTests.cs`、`tests/WinUse.Core.Tests/Java/JavaIntegrationTests.cs`（`Doctor_ReportsFixtureAsResponding`）

**Interfaces:**
- Produces: `public enum JabLayout { Wide, Legacy }`（`JabLayout.cs`）。`JavaProcessInfo` に位置引数 `bool Modular = false`（`<jre>\lib\modules` が存在 = Java 9+）を `LoadedBridgeModule` の後に追加。`public bool IsLegacyBridge => LoadedBridgeModule が "JavaAccessBridge.dll"（OrdinalIgnoreCase）&& !Modular`。`public JabLayout? Layout => LoadedBridgeModule is null ? null : IsLegacyBridge ? JabLayout.Legacy : JabLayout.Wide`。`Describe()` は `"{module} loaded (legacy wire format)"` / `"{module} loaded (wide wire format)"`。旧形式の長い説明文（"it answers isJavaWindow but the 64-bit winuse cannot read its tree ..."）は削る。

- [ ] **Step 1: 失敗するテストを書く**（`JavaProcessInfoTests.cs` に追加、既存の `Describe_ExplainsTheLegacyBridge` は書き換え）

```csharp
[Fact]
public void Layout_Java9Plus_LowercaseModule_IsWide()
{
    var info = new JavaProcessInfo(false, @"C:\jdk\bin\server\jvm.dll", "21", true, true, null, "javaaccessbridge.dll", Modular: true);
    Assert.False(info.IsLegacyBridge);
    Assert.Equal(JabLayout.Wide, info.Layout);
    Assert.Contains("javaaccessbridge.dll loaded (wide wire format)", info.Describe());
}

[Fact]
public void Layout_Java8_LegacyModule_IsLegacy()
{
    var info = new JavaProcessInfo(true, @"C:\jre\bin\client\jvm.dll", "1.8.0_472", true, true, null, "JavaAccessBridge.dll", Modular: false);
    Assert.True(info.IsLegacyBridge);
    Assert.Equal(JabLayout.Legacy, info.Layout);
    Assert.Contains("JavaAccessBridge.dll loaded (legacy wire format)", info.Describe());
}

[Fact]
public void Layout_Java8_Suffix32Module_IsWide()
{
    var info = new JavaProcessInfo(true, @"C:\jre\bin\client\jvm.dll", "1.8.0_472", true, true, null, "JavaAccessBridge-32.dll");
    Assert.Equal(JabLayout.Wide, info.Layout);
}

[Fact]
public void Layout_NoBridgeLoaded_IsNull()
{
    Assert.Null(new JavaProcessInfo(true, @"C:\jre\bin\client\jvm.dll", "1.8.0_472", true, true, null).Layout);
}
```

- [ ] **Step 2: 失敗を確認**

Run: `dotnet build winuse.sln 2>&1 | grep -E "error" ; dotnet test tests/WinUse.Core.Tests --no-build --filter "FullyQualifiedName~JavaProcessInfoTests"`
Expected: `Modular` / `JabLayout` が無いコンパイルエラー。

- [ ] **Step 3: 実装**

`JabLayout.cs` に enum。`JavaProcessInfo.Inspect` で `Modular = File.Exists(Path.Combine(jre, "lib", "modules"))` を計算して渡す（既に `jar` の判定で同じ式を使っている）。`Describe()` の形式表示を上記の文言にする。`DoctorCommand.cs` の responding 時の state を `info.Layout` で `"bridge responding (legacy wire format)"` / `"bridge responding (wide wire format)"` / 形式不明なら `"bridge responding"` にする（この時点では旧形式はまだ読めないが、文言は最終形に合わせておく）。

- [ ] **Step 4: `Doctor_ReportsFixtureAsResponding` を厳密化**（`JavaIntegrationTests.cs:277-289`）

`Assert.Contains("bridge responding", text)` を `Assert.Contains("bridge responding (wide wire format)", text)` にし、`Assert.Contains("JavaAccessBridge-", text)` を `Assert.Contains(jvm == Jvm.Jdk21x64 ? "javaaccessbridge.dll loaded (wide wire format)" : "JavaAccessBridge-32.dll loaded (wide wire format)", text)` にする。`Doctor_ExplainsTheLegacyBridge_AndGetUiTreeReportsIt` の doctor 文言の期待を `"bridge responding (legacy wire format)"` と `"JavaAccessBridge.dll loaded (legacy wire format)"` にする（`get_ui_tree` が失敗する期待は Task 6 で反転するまで残す）。

- [ ] **Step 5: テスト実行**

Run: `dotnet build winuse.sln 2>&1 | grep -E "error"; dotnet test tests/WinUse.Core.Tests --no-build --filter "FullyQualifiedName~JavaProcessInfoTests|FullyQualifiedName~Doctor_"`
Expected: 全件 PASS（Temurin 21 と 8 で doctor が `wide wire format`、旧形式フィクスチャで `legacy wire format`）。

- [ ] **Step 6: Commit**

```bash
git add src/WinUse.Core/Java/JabLayout.cs src/WinUse.Core/Java/JavaProcessInfo.cs src/WinUse/Cli/DoctorCommand.cs tests/WinUse.Core.Tests/Java/JavaProcessInfoTests.cs tests/WinUse.Core.Tests/Java/JavaIntegrationTests.cs
git commit -m "fix(java): Java 9+ javaaccessbridge.dll is the wide wire format, not legacy"
```

---

### Task 2: `JabPacket` — C レイアウト計算とパケット定義

**Files:**
- Create: `src/WinUse.Core/Java/JabPacket.cs`
- Create: `src/WinUse.Core/Java/JabPackets.cs`
- Test: `tests/WinUse.Core.Tests/Java/JabPacketTests.cs`

**Interfaces:**
- Produces（すべて `internal`、名前空間 `WinUse.Core.Java`）:
  - `enum JabFieldKind { I32, Bool8, Obj, WChars, Chars, Blob }`
  - `readonly record struct JabField(JabFieldKind Kind, int Count = 1, int Align = 0)` — `WChars`/`Chars`/`Blob` の `Count` は要素数（Blob はバイト数、`Align` 必須）。
  - `sealed class JabPacketLayout { int TypeId; JabLayout Layout; int StructSize; int BufferSize => 4 + StructSize; int Offset(int field); int Size(int field); JabField Field(int field); static JabPacketLayout Build(int typeId, JabLayout layout, params JabField[] fields) }`
  - `sealed class JabPacket(JabPacketLayout layout)`: `byte[] Buffer`（長さ `layout.BufferSize`、型 ID 書き込み済み）、`int Size`、`void WriteI32(int field, int v)`、`void WriteObj(int field, long v)`、`void WriteWChars(int field, string s)`（`Count-1` 文字に切り詰め、NUL 終端）、`void WriteChars(int field, string ascii)`、`int ReadI32(int field)`、`bool ReadBool8(int field)`（≠0）、`long ReadObj(int field)`、`string ReadWChars(int field)`（最初の NUL まで）、`ReadOnlySpan<byte> ReadBlob(int field)`、`static string ReadWCharsAt(ReadOnlySpan<byte> bytes, int offset, int count)`、`static int ReadI32At(ReadOnlySpan<byte> bytes, int offset)`。
  - `static class JabPackets`: `enum JabPacketKind { IsJavaWindow, GetContextFromHwnd, GetContextInfo, GetChild, GetParent, ReleaseObject, GetVersion, GetActions, DoActions, GetTextInfo, GetTextRange, GetValue, AddSelection, ClearSelection, GetSelection, GetSelectionCount, SetText, GetVirtualName, RequestFocus, MemoryMappedFileCreated }`、`static JabPacketLayout For(JabPacketKind kind, JabLayout layout)`（キャッシュ）、`static int MaxBufferSize`（両形式・全種の最大 `BufferSize`）、`static JabPacket New(JabPacketKind kind, JabLayout layout)`。各パケットのフィールド番号は入れ子 static class の定数で公開する（例 `JabPackets.GetChild.VmId = 0, Ac = 1, ChildIndex = 2, Result = 3`）。
  - 定数: `JabPackets.MaxStringSize = 1024`、`ShortStringSize = 256`、`MaxBufferSizeChars = 10240`、`MaxActionInfo = 256`、`MaxActionsToDo = 32`、`MemoryMappedNameSize = 255`、`AccessibleContextInfoSize = 6188`、`JavaShutdownPackageType = 0x1010`。

  パケットのスキーマ（型 ID と、フィールド順。`Obj` = JOBJECT64、`I32` = jint/long/BOOL/int、`Hwnd` は `I32`）:

  | Kind | TypeId | Fields |
  |---|---|---|
  | IsJavaWindow | 0x11 | `Window:I32, Result:Bool8` |
  | GetContextFromHwnd | 0x10 | `Window:I32, VmId:I32, Result:Obj` |
  | GetContextInfo | 0x102 | `VmId:I32, Ac:Obj, Info:Blob(6188, align 4)` |
  | GetChild | 0x103 | `VmId:I32, Ac:Obj, ChildIndex:I32, Result:Obj` |
  | GetParent | 0x104 | `VmId:I32, Ac:Obj, Result:Obj` |
  | ReleaseObject | 0x1 | `VmId:I32, Object:Obj` |
  | GetVersion | 0x2 | `VmId:I32, VmVersion:WChars(256), JavaClassVersion:WChars(256), JavaDllVersion:WChars(256), WinDllVersion:WChars(256)` |
  | GetActions | 0x1502 | `VmId:I32, Ac:Obj, Actions:Blob(4 + 256*512, align 4)` |
  | DoActions | 0x1503 | `VmId:I32, Ac:Obj, ToDo:Blob(4 + 32*512, align 4), Result:I32, Failure:I32` |
  | GetTextInfo | 0x200 | `VmId:I32, Ac:Obj, X:I32, Y:I32, CharCount:I32, CaretIndex:I32, IndexAtPoint:I32` |
  | GetTextRange | 0x206 | `VmId:I32, Ac:Obj, Start:I32, End:I32, Text:WChars(10240)` |
  | GetValue | 0x300 | `VmId:I32, Ac:Obj, Value:WChars(256)` |
  | AddSelection | 0x400 | `VmId:I32, Ac:Obj, Index:I32` |
  | ClearSelection | 0x401 | `VmId:I32, Ac:Obj` |
  | GetSelection | 0x402 | `VmId:I32, Ac:Obj, Index:I32, Result:Obj` |
  | GetSelectionCount | 0x403 | `VmId:I32, Ac:Obj, Count:I32` |
  | SetText | 0x1600 | `VmId:I32, Ac:Obj, Text:WChars(1024), Result:I32` |
  | GetVirtualName | 0x1700 | `VmId:I32, Ac:Obj, Name:WChars(1024), Len:I32` |
  | RequestFocus | 0x1701 | `VmId:I32, Ac:Obj` |
  | MemoryMappedFileCreated | 0x11000 | `BridgeWindow:I32, Filename:Chars(255)` |

  `AccessibleContextInfo` ブロブ内のオフセット（両形式共通、`JabPackets.ContextInfo` 定数）: `Name 0 (wchar 1024)`, `Description 2048 (1024)`, `Role 4096 (256)`, `RoleEn 4608 (256)`, `States 5120 (256)`, `StatesEn 5632 (256)`, `IndexInParent 6144`, `ChildrenCount 6148`, `X 6152`, `Y 6156`, `Width 6160`, `Height 6164`, `AccessibleComponent 6168`, `AccessibleAction 6172`, `AccessibleSelection 6176`, `AccessibleText 6180`, `AccessibleInterfaces 6184`。`AccessibleActions` ブロブ: `Count 0`、名前 i は `4 + i*512 (wchar 256)`。`AccessibleActionsToDo` ブロブ: 同じ配置（最大 32）。

- [ ] **Step 1: 失敗するテストを書く**（`JabPacketTests.cs`）

```csharp
public class JabPacketTests
{
    public static IEnumerable<object[]> Sizes() =>
    [
        [JabPacketKind.IsJavaWindow, 8, 8], [JabPacketKind.GetContextFromHwnd, 16, 12], [JabPacketKind.GetContextInfo, 6208, 6196],
        [JabPacketKind.GetChild, 32, 16], [JabPacketKind.GetParent, 24, 12], [JabPacketKind.ReleaseObject, 16, 8],
        [JabPacketKind.GetVersion, 2052, 2052], [JabPacketKind.GetActions, 131096, 131084], [JabPacketKind.DoActions, 16416, 16404],
        [JabPacketKind.GetTextInfo, 40, 28], [JabPacketKind.GetTextRange, 20504, 20496], [JabPacketKind.GetValue, 528, 520],
        [JabPacketKind.AddSelection, 24, 12], [JabPacketKind.ClearSelection, 16, 8], [JabPacketKind.GetSelection, 32, 16],
        [JabPacketKind.GetSelectionCount, 24, 12], [JabPacketKind.SetText, 2072, 2060], [JabPacketKind.GetVirtualName, 2072, 2060],
        [JabPacketKind.RequestFocus, 16, 8], [JabPacketKind.MemoryMappedFileCreated, 260, 260],
    ];

    [Theory, MemberData(nameof(Sizes))]
    public void StructSize_MatchesTheCHeader(JabPacketKind kind, int wide, int legacy)
    {
        Assert.Equal(wide, JabPackets.For(kind, JabLayout.Wide).StructSize);
        Assert.Equal(legacy, JabPackets.For(kind, JabLayout.Legacy).StructSize);
        Assert.Equal(4 + wide, JabPackets.For(kind, JabLayout.Wide).BufferSize);
    }

    [Fact]
    public void Offsets_FollowCAlignment()
    {
        var wide = JabPackets.For(JabPacketKind.GetChild, JabLayout.Wide);
        Assert.Equal(0, wide.Offset(JabPackets.GetChild.VmId));
        Assert.Equal(8, wide.Offset(JabPackets.GetChild.Ac));
        Assert.Equal(16, wide.Offset(JabPackets.GetChild.ChildIndex));
        Assert.Equal(24, wide.Offset(JabPackets.GetChild.Result));
        var legacy = JabPackets.For(JabPacketKind.GetChild, JabLayout.Legacy);
        Assert.Equal(4, legacy.Offset(JabPackets.GetChild.Ac));
        Assert.Equal(12, legacy.Offset(JabPackets.GetChild.Result));
        Assert.Equal(4, JabPackets.For(JabPacketKind.IsJavaWindow, JabLayout.Wide).Offset(JabPackets.IsJavaWindow.Result));
    }

    [Fact]
    public void TypeId_IsWrittenFirst()
    {
        var p = JabPackets.New(JabPacketKind.GetActions, JabLayout.Legacy);
        Assert.Equal(0x1502, BitConverter.ToInt32(p.Buffer, 0));
        Assert.Equal(p.Buffer.Length, p.Size);
    }

    [Fact]
    public void ReadObj_Legacy_IsUnsigned()
    {
        var p = JabPackets.New(JabPacketKind.GetChild, JabLayout.Legacy);
        BitConverter.GetBytes(unchecked((int)0x8000_0001)).CopyTo(p.Buffer, 4 + 12);
        Assert.Equal(0x8000_0001L, p.ReadObj(JabPackets.GetChild.Result));
    }

    [Fact]
    public void WriteObj_Legacy_WritesLow32Bits_AndWide_Writes64()
    {
        var legacy = JabPackets.New(JabPacketKind.ReleaseObject, JabLayout.Legacy);
        legacy.WriteObj(JabPackets.ReleaseObject.Object, 0x8000_0001L);
        Assert.Equal(unchecked((int)0x8000_0001), BitConverter.ToInt32(legacy.Buffer, 4 + 4));
        var wide = JabPackets.New(JabPacketKind.ReleaseObject, JabLayout.Wide);
        wide.WriteObj(JabPackets.ReleaseObject.Object, 0x1_2345_6789L);
        Assert.Equal(0x1_2345_6789L, BitConverter.ToInt64(wide.Buffer, 4 + 8));
    }

    [Fact]
    public void WriteWChars_TruncatesAndTerminates()
    {
        var p = JabPackets.New(JabPacketKind.GetValue, JabLayout.Wide);
        p.WriteWChars(JabPackets.GetValue.Value, new string('x', 300));
        Assert.Equal(new string('x', 255), p.ReadWChars(JabPackets.GetValue.Value));
    }

    [Fact]
    public void ReadWChars_StopsAtNulOrEnd()
    {
        var p = JabPackets.New(JabPacketKind.GetValue, JabLayout.Wide);
        p.WriteWChars(JabPackets.GetValue.Value, "ab");
        Assert.Equal("ab", p.ReadWChars(JabPackets.GetValue.Value));
        var off = 4 + p.Layout.Offset(JabPackets.GetValue.Value);
        for (var i = 0; i < 256; i++) BitConverter.GetBytes('z').CopyTo(p.Buffer, off + i * 2); // no NUL at all
        Assert.Equal(new string('z', 256), p.ReadWChars(JabPackets.GetValue.Value));
    }

    [Fact]
    public void WriteChars_IsAsciiNulTerminated()
    {
        var p = JabPackets.New(JabPacketKind.MemoryMappedFileCreated, JabLayout.Wide);
        p.WriteChars(JabPackets.MemoryMappedFileCreated.Filename, "winuse-jab-1-2");
        var off = 4 + p.Layout.Offset(JabPackets.MemoryMappedFileCreated.Filename);
        Assert.Equal((byte)'w', p.Buffer[off]);
        Assert.Equal(0, p.Buffer[off + 14]);
    }

    [Fact]
    public void MaxBufferSize_IsTheWideGetActionsPacket() => Assert.Equal(4 + 131096, JabPackets.MaxBufferSize);
}
```

`JabPacket` は `public JabPacketLayout Layout { get; }` も持つ（テストが使う）。

- [ ] **Step 2: 失敗を確認**

Run: `dotnet build winuse.sln 2>&1 | grep -E "error" | head -3`
Expected: `JabPackets` 未定義のコンパイルエラー。

- [ ] **Step 3: 実装**

`JabPacketLayout.Build`: 各フィールドの（サイズ, アライン）を Kind と layout から決め、`offset = align up(current, align)`、構造体サイズは `align up(end, maxAlign)`。`JabPackets.For` は `ConcurrentDictionary<(JabPacketKind, JabLayout), JabPacketLayout>`。`JabPacket` のバッファは `new byte[layout.BufferSize]` で、コンストラクタが型 ID を書く。

- [ ] **Step 4: テスト実行**

Run: `dotnet test tests/WinUse.Core.Tests --filter "FullyQualifiedName~JabPacketTests"`
Expected: 全件 PASS。失敗した行があれば設計書 §3.3 の表を C 規則で再計算し、実装側（アライメント規則）を直す。表の値自体が間違っていた場合のみ設計書を直す。

- [ ] **Step 5: Commit**

```bash
git add src/WinUse.Core/Java/JabPacket.cs src/WinUse.Core/Java/JabPackets.cs tests/WinUse.Core.Tests/Java/JabPacketTests.cs
git commit -m "feat(java): JAB packet layouts for the wide and legacy wire formats"
```

---

### Task 3: `JabClient` — パケットで `IJabAccess` を実装（偽チャネルで単体テスト）

**Files:**
- Create: `src/WinUse.Core/Java/IJabChannel.cs`
- Create: `src/WinUse.Core/Java/JabClient.cs`
- Create: `tests/WinUse.Core.Tests/Java/FakeJabChannel.cs`
- Test: `tests/WinUse.Core.Tests/Java/JabClientTests.cs`

**Interfaces:**
- Consumes: Task 2 の `JabPackets` / `JabPacket`、Task 1 の `JabLayout`、既存 `IJabAccess` / `JabHandle` / `JabContextInfo` / `JabVersionInfo`。
- Produces:
  - `internal interface IJabChannel : IDisposable { bool Exchange(byte[] buffer, int size); }` — `buffer[0..size)` を送り、応答を同じバッファに書き戻す。失敗（タイムアウト、相手が消えた、完了フラグ未設定）で false。
  - `internal sealed record JabClientServices(Func<nint, IJabChannel?> OpenChannel, Func<int, JabLayout?> DetectLayout, Func<nint, int> PidOf, Func<nint, bool> IsWindow)` — 本番は Task 5 が組む。`OpenChannel(javaHwnd)` は握手済みチャネルか、失敗なら null。
  - `internal sealed class JabJvm { nint JavaHwnd; int VmId; int Pid; JabLayout? Layout; IJabChannel Channel; bool Dead; }`（`JabClient.cs` 内、プロパティは `JabClient` だけが書く）。
  - `internal sealed class JabClient(JabClientServices services, ILogger? log = null) : IJabAccess, IDisposable`:
    - `void Connect(nint javaHwnd, int vmId)` — 既知で生きていれば無視。`PidOf` → `DetectLayout(pid)`（null なら最初の送信前に最大 3 回、100 ms 間隔で再試行し、それでも null なら `Wide` + `LogWarning`）。`OpenChannel` が null を返したら追加しない（`LogWarning`）。
    - `void MarkDead(nint javaHwnd)` — フラグだけ立てる（WndProc から呼ばれる。一覧は変更しない）。
    - `IReadOnlyList<JabJvm> Jvms` — 生きているもの。
    - `string Describe()` — `"{n} JVMs: pid 4120 wide, pid 7788 legacy"`（0 件なら `"no JVMs connected"`）。
    - `IJabAccess` 17 メソッド。各メソッド入口で `Prune()`（`Dead` または `!IsWindow(JavaHwnd)` の JVM を一覧から外して `Channel.Dispose()`）。送信失敗（`Exchange` false）は `LogDebug("JAB {Kind} to pid {Pid} ({Layout}) got no answer")` して失敗値を返す。
    - `JabHandle` の `VmId` で JVM を探す。無ければ失敗値。
    - `GetVersion(vmId)` の `WindowsDllVersion` は `"winuse " + typeof(JabClient).Assembly.GetName().Version`。
    - `IsJavaWindow(hwnd)`: 各 JVM に送り最初の `Result` true で true。`GetContextFromHwnd(hwnd)`: 各 JVM に送り `Result != 0 && VmId == jvm.VmId` のものを返す。
    - `GetInfo`: `Info` ブロブを `JabPackets.ContextInfo` のオフセットで読み、`JabContextInfo(Name(空→null), Description(空→null), RoleEn, StatesEn, IndexInParent, ChildrenCount, X, Y, Width, Height, AccessibleAction != 0, AccessibleSelection != 0, AccessibleText != 0, (AccessibleInterfaces & 1) != 0)`。
    - `GetActions`: `Count` を `[0, 256]` に clamp して名前を読む。`DoAction(h, action)`: `ToDo` ブロブに `Count = 1`、名前 0 に `action`（255 文字に切り詰め、NUL 終端）、`Result != 0` を返す。
    - `GetText(h, maxChars)`: `GetTextInfo(x=0,y=0)` → `n = clamp(min(CharCount, maxChars), 0, 10239)`、0 なら ""、`GetTextRange(Start=0, End=n-1)` → `Text` の先頭 `n` 文字（NUL で切る）。
    - `GetValue`: `Value`（空→null）。`SetText`: `Text` に書き `Result != 0`。`RequestFocus`: 送信成功を返す。`AddSelection`/`ClearSelection`: 送るだけ。`GetSelection`: `Result != 0 ? handle : null`。`GetSelectionCount`: `Count`（失敗で 0）。`GetVirtualName`: `Name`（空→null）。`Release`: 送る。`GetChild`: `Result != 0 ? new JabHandle(h.VmId, Result) : null`。
  - テスト用 `FakeJabChannel : IJabChannel`（`tests/.../FakeJabChannel.cs`）: コンストラクタ `(JabLayout layout, int vmId, Func<int typeId, JabPacket request, bool> handler)`。`Exchange` は型 ID を読み、`JabPackets` の逆引き（型 ID → Kind）で `JabPacket` を包んで handler に渡し、handler の戻り値を返す。`List<int> SentTypeIds` を記録。`Disposed` フラグ。

- [ ] **Step 1: 失敗するテストを書く**（`JabClientTests.cs`。ヘルパ `Services(params (nint hwnd, JabLayout layout, int vmId, Func<int, JabPacket, bool> handler)[] jvms)` が `JabClientServices` を組む: `OpenChannel` は hwnd に対応する `FakeJabChannel`、`DetectLayout` は pid→layout の辞書、`PidOf` は hwnd→pid（hwnd × 10）、`IsWindow` は `alive` セット）

```csharp
[Fact]
public void TwoJvms_DifferentLayouts_RouteByVmId()
{
    // JVM A (wide, vmId 100) owns hwnd 0x10; JVM B (legacy, vmId 200) owns hwnd 0x20.
    // handler: IsJavaWindow -> Result = (Window == my hwnd); GetContextFromHwnd -> VmId = my vmId, Result = 0x8000_0001 when Window == my hwnd
    // GetContextInfo -> Name "root", RoleEn "frame", ChildrenCount 1
    var client = new JabClient(Services(a, b));
    client.Connect(0x10, 100); client.Connect(0x20, 200);
    Assert.True(client.IsJavaWindow(0x20));
    var h = client.GetContextFromHwnd(0x20)!.Value;
    Assert.Equal(200, h.VmId);
    Assert.Equal(0x8000_0001L, h.Ac);                      // legacy handle read unsigned
    Assert.Equal("frame", client.GetInfo(h)!.Role);
    Assert.Equal([0x11, 0x10, 0x102], channelB.SentTypeIds.Distinct()); // only JVM B saw the info request
    Assert.DoesNotContain(0x102, channelA.SentTypeIds);
}

[Fact]
public void GetContextFromHwnd_IgnoresAnswersWithForeignVmId()
{
    // JVM answers Result != 0 but VmId = 999 -> null
}

[Fact]
public void Exchange_Fails_ReturnsNullOrFalse()
{
    // handler returns false for everything
    Assert.False(client.IsJavaWindow(0x10));
    Assert.Null(client.GetContextFromHwnd(0x10));
    Assert.Null(client.GetInfo(new JabHandle(100, 1)));
    Assert.Empty(client.GetActions(new JabHandle(100, 1)));
    Assert.False(client.DoAction(new JabHandle(100, 1), "click"));
    Assert.Null(client.GetText(new JabHandle(100, 1), 100));
    Assert.Equal(0, client.GetSelectionCount(new JabHandle(100, 1)));
    Assert.Null(client.GetVersion(100));
}

[Fact]
public void UnknownVmId_ReturnsFailureValues()
{
    Assert.Null(client.GetInfo(new JabHandle(555, 1)));
    Assert.False(client.SetText(new JabHandle(555, 1), "x"));
}

[Fact]
public void DeadJvm_IsPrunedAndItsChannelDisposed()
{
    client.Connect(0x10, 100);
    alive.Remove(0x10);
    Assert.False(client.IsJavaWindow(0x10));
    Assert.Empty(client.Jvms);
    Assert.True(channelA.Disposed);
}

[Fact]
public void MarkDead_TakesEffectAtNextCall()
{
    client.Connect(0x10, 100);
    client.MarkDead(0x10);
    Assert.Single(client.Jvms);           // list untouched inside the callback
    client.IsJavaWindow(0x10);
    Assert.Empty(client.Jvms);
}

[Fact]
public void Connect_Twice_KeepsOneJvm_AndFailedHandshake_AddsNone()
{
    client.Connect(0x10, 100); client.Connect(0x10, 100);
    Assert.Single(client.Jvms);
    client.Connect(0x30, 300);            // OpenChannel returns null for 0x30
    Assert.Single(client.Jvms);
}

[Fact]
public void UnknownLayout_IsRetriedThenDefaultsToWide()
{
    // DetectLayout returns null for pid of 0x10 -> after Connect, Layout null; first call sends with Wide; Describe says "wide"
    client.Connect(0x10, 100);
    Assert.Null(client.Jvms[0].Layout);
    client.IsJavaWindow(0x10);
    Assert.Equal(JabLayout.Wide, client.Jvms[0].Layout);
    Assert.Equal(JabLayout.Wide, channelA.LastLayoutSeen);   // FakeJabChannel records the layout of the request it received (by struct size)
}

[Fact]
public void GetText_ClampsToCharCountAndMax()
{
    // GetTextInfo -> CharCount 5; GetTextRange -> Text "hello"
    Assert.Equal("hel", client.GetText(h, 3));
    Assert.Equal("hello", client.GetText(h, 100));
    // the GetTextRange request carried Start 0 / End 2 for the first call
}

[Fact]
public void DoAction_SendsOneActionName_AndReadsResult()
{
    // handler asserts ToDo blob: Count == 1, name 0 == "click"; writes Result 1
    Assert.True(client.DoAction(h, "click"));
}

[Fact]
public void GetVersion_FillsWindowsDllVersionWithWinuse()
{
    // handler writes VmVersion "1.8.0_472", JavaDllVersion "2.0.3"
    var v = client.GetVersion(100)!;
    Assert.Equal("1.8.0_472", v.VmVersion);
    Assert.StartsWith("winuse ", v.WindowsDllVersion);
}

[Fact]
public void Describe_ListsPidsAndLayouts()
{
    Assert.Equal("2 JVMs: pid 160 wide, pid 320 legacy", client.Describe());
}
```

`FakeJabChannel` は受けたバッファ長から `JabPacketKind`×`JabLayout` を逆引きして `LastLayoutSeen` を記録する（型 ID と `size - 4 == StructSize` の両方が一致する layout）。

- [ ] **Step 2: 失敗を確認**

Run: `dotnet build winuse.sln 2>&1 | grep -E "error" | head -3`
Expected: `JabClient` 未定義。

- [ ] **Step 3: 実装**

`IJabChannel.cs`、`JabClient.cs`（`JabJvm` と `JabClientServices` も同ファイル）。`FakeJabChannel.cs`。

- [ ] **Step 4: テスト実行**

Run: `dotnet test tests/WinUse.Core.Tests --filter "FullyQualifiedName~JabClientTests|FullyQualifiedName~JabPacketTests"`
Expected: 全件 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/WinUse.Core/Java/IJabChannel.cs src/WinUse.Core/Java/JabClient.cs tests/WinUse.Core.Tests/Java/FakeJabChannel.cs tests/WinUse.Core.Tests/Java/JabClientTests.cs
git commit -m "feat(java): JabClient speaks the JAB wire format over a channel per JVM"
```

---

### Task 4: `JabMemoryChannel` と `JabWindow` — 握手と要求応答の Win32 層

**Files:**
- Modify: `src/WinUse.Core/Windows/NativeMethods.cs`
- Create: `src/WinUse.Core/Java/JabMemoryChannel.cs`
- Create: `src/WinUse.Core/Java/JabWindow.cs`
- Test: `tests/WinUse.Core.Tests/Java/JabWindowTests.cs`

**Interfaces:**
- Consumes: Task 3 の `JabClient`（`Connect`、`MarkDead`、`Jvms`）、`IJabChannel`、Task 2 の `JabPackets.MemoryMappedFileCreated` / `MaxBufferSize` / `JavaShutdownPackageType`、既存 `JabDispatcher`。
- Produces:
  - `NativeMethods` に追加: `RegisterClassExW(ref WNDCLASSEXW)`、`CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint hInstance, nint param)`、`DestroyWindow`、`DefWindowProcW`、`RegisterWindowMessageW(string)`、`IsWindow`、`GetParent`、`GetModuleHandleW(string? null)`、`struct WNDCLASSEXW`（`cbSize, style, lpfnWndProc(nint), cbClsExtra, cbWndExtra, hInstance, hIcon, hCursor, hbrBackground, lpszMenuName(nint), lpszClassName(nint), hIconSm`）、`struct COPYDATASTRUCT { nuint dwData; uint cbData; nint lpData; }`、定数 `WM_COPYDATA = 0x004A`, `WM_DESTROY = 0x0002`, `WM_USER = 0x0400`, `AB_MESSAGE_WAITING = WM_USER + 0x1001`, `AB_DLL_GOING_AWAY = WM_USER + 0x1003`, `HWND_BROADCAST = 0xFFFF`, `SMTO_BLOCK = 0x1`, `SMTO_NOTIMEOUTIFNOTHUNG = 0x8`, `WS_POPUP = 0x80000000`。既存 `SendMessageTimeoutW` と `PostMessageW` と `GetWindowThreadProcessId` を使う。
  - `internal sealed class JabMemoryChannel : IJabChannel` — `static JabMemoryChannel? Open(nint ourHwnd, nint javaHwnd, ILogger? log)`: 名前 `$"winuse-jab-{ourHwnd:X}-{javaHwnd:X}"`、`MemoryMappedFile.CreateNew(name, JabPackets.MaxBufferSize + 8)`、`CreateViewAccessor()`、先頭に `"OK?\0"` を書き、`MemoryMappedFileCreated` パケット（`BridgeWindow = (int)ourHwnd`、`Filename = name`）を `COPYDATASTRUCT` で `SendMessageTimeoutW(javaHwnd, WM_COPYDATA, ourHwnd, &cds, SMTO_BLOCK|SMTO_NOTIMEOUTIFNOTHUNG, 4000)`、戻ったらビュー先頭 3 バイトが `"OK!"` なら成功、それ以外は破棄して null。`Exchange(buffer, size)`: `WriteArray(0, buffer, 0, size)`、`Write(size, (byte)0)`、`SendMessageTimeoutW(javaHwnd, AB_MESSAGE_WAITING, ourHwnd, size, flags, 4000, out _) != 0` かつ `ReadByte(size) == 1` なら `ReadArray(0, buffer, 0, size)` して true。
  - `internal sealed unsafe class JabWindow : IDisposable` — `static JabWindow Create(JabClient client, ILogger? log)`（ディスパッチャスレッドで呼ぶ。失敗は `Win32Exception`）: クラス `"WinUseJabClient"` を一度だけ登録（`[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] static nint WndProc(nint, uint, nint, nint)`）、`CreateWindowExW(WS_EX_TOOLWINDOW, cls, "winuse Java Access Bridge client", WS_POPUP, 0,0,0,0, 0, 0, GetModuleHandleW(null), 0)`、`static ConcurrentDictionary<nint, JabWindow>` で hwnd → インスタンス、`FromWindowsHello` / `FromJavaHello` を `RegisterWindowMessageW`、`PostMessageW(HWND_BROADCAST, FromWindowsHello, hwnd, 0)`。`nint Hwnd`。WndProc: `msg == FromJavaHello` → `client.Connect(wParam, (int)lParam)`; `AB_DLL_GOING_AWAY` → `client.MarkDead(wParam)`; `WM_COPYDATA` → `lpData` 先頭 4 バイトが `JavaShutdownPackageType` なら `vmID`（オフセット 4）の JVM を `MarkDead`、他は無視して 0 を返す; それ以外 `DefWindowProcW`。例外は捕まえてログ（WndProc から投げない）。`Dispose()`（ディスパッチャスレッド）: 生きている JVM ごとに `SendMessageTimeoutW(javaHwnd, AB_DLL_GOING_AWAY, hwnd, 0, flags, 1000)`、`DestroyWindow`、辞書から外す。
  - `JabClient` の本番 `JabClientServices`: `OpenChannel = javaHwnd => JabMemoryChannel.Open(window.Hwnd, javaHwnd, log)`、`DetectLayout = pid => JavaProcessInfo.Inspect(pid).Layout`、`PidOf = hwnd => { GetWindowThreadProcessId(hwnd, out var pid); return (int)pid; }`、`IsWindow = NativeMethods.IsWindow`。`JabWindow.Create` は `client` を受け取るので、`JabBridge`（Task 5）が先に `JabClient` を作り、`OpenChannel` は作成後に `window.Hwnd` を参照するクロージャにする（`JabWindow` を保持する変数をキャプチャ）。

- [ ] **Step 1: 失敗するテストを書く**（`JabWindowTests.cs`。本物のウィンドウだが JVM 不要）

```csharp
public class JabWindowTests
{
    [Fact]
    public async Task Create_MakesATopLevelWindow_ThatAnswersHello_AndDisposeDestroysIt()
    {
        var connects = new List<(nint, int)>();
        var client = new JabClient(new JabClientServices(_ => null, _ => null, _ => 0, _ => true));
        client.Connecting += (hwnd, vmId) => connects.Add((hwnd, vmId));
        JabWindow? w = null;
        using var d = new JabDispatcher(() => w = JabWindow.Create(client, null));
        Assert.NotNull(w);
        Assert.True(NativeMethods.IsWindow(w!.Hwnd));
        Assert.Equal(0, NativeMethods.GetParent(w.Hwnd));          // top-level (add GetParent to NativeMethods)
        // Pretend to be a JVM: post FromJavaHello to the window and let the loop pump it.
        var hello = NativeMethods.RegisterWindowMessageW("AccessBridge-FromJava-Hello");
        NativeMethods.PostMessageW(w.Hwnd, hello, 0x1234, 0x1234);
        await d.RunAsync(() => { });                                 // queued after the posted message
        // OpenChannel returned null, so nothing connected, but Connect was reached: assert via a services spy
        Assert.Contains((0x1234, 0x1234), connects);                 // spy's OpenChannel records (hwnd, vmId) via client.Connect
        await d.RunAsync(() => w.Dispose());
        Assert.False(NativeMethods.IsWindow(w.Hwnd));
    }
}
```

スパイ: `OpenChannel = hwnd => { connects.Add((hwnd, lastVmId)); return null; }` では vmId が取れないので、`JabClientServices` の `OpenChannel` の代わりに `JabClient` に `internal event Action<nint, int>? Connecting` を足し、テストはそれを購読する。

- [ ] **Step 2: 失敗を確認**

Run: `dotnet build winuse.sln 2>&1 | grep -E "error" | head -3`
Expected: `JabWindow` 未定義。

- [ ] **Step 3: 実装**

`NativeMethods` 追加、`JabMemoryChannel.cs`、`JabWindow.cs`、`JabClient.Connecting` イベント。

- [ ] **Step 4: テスト実行**

Run: `dotnet test tests/WinUse.Core.Tests --filter "FullyQualifiedName~JabWindowTests"`
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/WinUse.Core/Windows/NativeMethods.cs src/WinUse.Core/Java/JabMemoryChannel.cs src/WinUse.Core/Java/JabWindow.cs src/WinUse.Core/Java/JabClient.cs tests/WinUse.Core.Tests/Java/JabWindowTests.cs
git commit -m "feat(java): hidden JAB client window, hello handshake and memory-mapped request channel"
```

---

### Task 5: `JabBridge` を自前クライアントに切り替え、3 本の JVM で統合テスト

**Files:**
- Modify: `src/WinUse.Core/Java/JabBridge.cs`（全面書き換え）
- Modify: `src/WinUse.Core/Java/JavaService.cs:53`（`BridgeDllPath` → `BridgeStatus`）、`JavaService.NoContextMessage`
- Modify: `src/WinUse/Tools/ToolContext.cs:72`
- Modify: `src/WinUse/Cli/DoctorCommand.cs:76-78`
- Delete: `src/WinUse.Core/Java/JabNative.cs`、`src/WinUse.Core/Java/JabAccess.cs`
- Modify: `tests/WinUse.Core.Tests/Java/FakeJabAccess.cs:189-204`（`FakeJabBridge.DllPath` → `Status`）
- Modify: `tests/WinUse.Core.Tests/Java/JabAccessIntegrationTests.cs`、`tests/WinUse.Core.Tests/Java/JavaIntegrationTests.cs`、`tests/WinUse.Core.Tests/Cli/DoctorCommandTests.cs`

**Interfaces:**
- Consumes: Task 3 `JabClient`、Task 4 `JabWindow` / `JabMemoryChannel`。
- Produces:
  - `IJabBridge`: `string? DllPath` を `string? Status` に置き換える（利用可能時 `"built-in client (window 0x1A2B; 2 JVMs: pid 4120 wide, pid 7788 legacy)"`、不可時 null）。`IReadOnlyList<JabJvm>`は公開しない（doctor は `Status` と `JavaProcessInfo.Layout` で足りる）。
  - `public sealed class JabBridge(WinUseConfig cfg, ILogger? log = null) : IJabBridge` — `exeDirectory` 引数を削除。`Initialize()`: `cfg.Jab == Off` → `_reason = "WINUSE_JAB=off"`。それ以外: `JabClient` を作り、`new JabDispatcher(() => window = JabWindow.Create(client, log))`。失敗（`InvalidOperationException` / `Win32Exception`）→ `_reason = $"client window could not be created: {ex.Message}"`。`Dispose()`: `dispatcher.RunAsync(() => window.Dispose())` を待ってから `dispatcher.Dispose()`、`client.Dispose()`。
  - `JavaService.BridgeStatus => bridge.Status`。`NoContextMessage(pid)`: 旧形式の長文を削り、`$"Java Access Bridge returned no accessible context for the window (JVM pid {pid}, {module} loaded, {layout} wire format)."` にする。
  - doctor 1 行目: `java.BridgeAvailable ? $"Java Access Bridge: {java.BridgeStatus}" : $"Java Access Bridge: {java.BridgeUnavailableReason}"`。

- [ ] **Step 1: テストを先に書き換える**

  1. `JabAccessIntegrationTests`: `BridgeDll` と `Ready` の `File.Exists(BridgeDll)` を消す（`Ready(jvm) => Jvms.Available(jvm)`）。`cfg` は `TestConfig.Default`。`new JabBridge(cfg, AppContext.BaseDirectory)` → `new JabBridge(cfg)`。`Bridge_SeesFixtureWindow_AndReadsRootInfo` に `[InlineData(Jvm.Jre8x86Legacy)]` を追加し、`version!.WindowsDllVersion` が `"winuse "` で始まることを assert。`LegacyJavaBridge_AnswersIsJavaWindow_ButNoContext_AndTheReasonSaysSo` を削除し、代わりに:

```csharp
[Fact]
public async Task TwoJvms_BothReadable()
{
    if (!Ready(Jvm.Jdk21x64) || !Ready(Jvm.Jre8x86Legacy)) return;
    using var ctx = ToolContext.Create(TestConfig.Default, null, null);
    using var a = await SwingFixture.LaunchAsync(ctx.Windows, Jvm.Jdk21x64);
    using var b = await SwingFixture.LaunchAsync(ctx.Windows, Jvm.Jre8x86Legacy);
    foreach (var f in new[] { a, b })
    {
        var tree = await ctx.Java.GetTreeAsync(f.Window.Hwnd, new TreeOptions());
        Assert.Contains(Flatten(tree), n => n.ControlType == "Button" && n.Name == "Increment");
    }
    Assert.Contains("2 JVMs", ctx.Java.BridgeStatus);
    Assert.Contains("legacy", ctx.Java.BridgeStatus);
}

[Fact]
public async Task Jvm_Exit_IsJavaWindowFalse_NoHang()
{
    if (!Ready(Jvm.Jre8x86)) return;
    using var ctx = ToolContext.Create(TestConfig.Default, null, null);
    var f = await SwingFixture.LaunchAsync(ctx.Windows, Jvm.Jre8x86);
    Assert.True(await WaitJava(ctx, f.Window));
    var hwnd = f.Window.Hwnd;
    f.Dispose();                                   // kills the JVM
    var sw = Stopwatch.StartNew();
    Assert.False(await ctx.Java.IsJavaWindowAsync(f.Window));
    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6));
    Assert.DoesNotContain("1 JVMs", ctx.Java.BridgeStatus);
}
```

  （`Flatten(UiNode)` と `WaitJava(ctx, window)`（最大 2 秒 `IsJavaWindowAsync` を待つ）はテストファイル内のヘルパ。`SwingFixture` に 2 つ同時起動で問題が無いか: `LaunchAsync` は自分の pid の窓を待つので可。）

  2. `JavaIntegrationTests`: 全 `[Theory]` に `[InlineData(Jvm.Jre8x86Legacy)]` を追加（`Doctor_ReportsFixtureAsResponding` は `jvm == Jvm.Jre8x86Legacy ? "bridge responding (legacy wire format)" : "bridge responding (wide wire format)"` と `"JavaAccessBridge.dll loaded (legacy wire format)"` を期待）。`Doctor_ExplainsTheLegacyBridge_AndGetUiTreeReportsIt` を削除（Theory 化で代替）。`Cfg` から `JabDll` を外す。`Doctor_ReportsFixtureAsResponding` の `"Java Access Bridge: loaded from"` を `"Java Access Bridge: built-in client"` に。
  3. `DoctorCommandTests`: `Doctor_ReportsJavaAccessBridge_LoadedFromExeDirectory_AndUserEnablement` → `Doctor_ReportsJavaAccessBridge_BuiltInClient_AndUserEnablement`（`TestConfig.Default`、`Contains("Java Access Bridge: built-in client (window 0x")`）。`Doctor_ReportsJavaAccessBridge_NotFound_WithTheLocatorReason` → `Doctor_ReportsJavaAccessBridge_UnavailableReason`（`FakeJabBridge(..., available: false, reason: "client window could not be created: boom")` → `Contains("Java Access Bridge: client window could not be created: boom")`）。`JabDllLocator` への参照を消す。
  4. `FakeJabBridge`: `public string? Status => available ? "fake client" : null;`。

- [ ] **Step 2: 失敗を確認**

Run: `dotnet build winuse.sln 2>&1 | grep -E "error" | head -5`
Expected: `Status` / `JabBridge(cfg)` が無いエラー。

- [ ] **Step 3: 実装**

`JabBridge.cs` 書き換え、`IJabBridge.Status`、`JavaService.BridgeStatus` と `NoContextMessage`、`ToolContext` の `new JabBridge(cfg, lf.CreateLogger<JabBridge>())`、doctor 1 行目。`JabNative.cs` と `JabAccess.cs` を `git rm`。この時点で `JabDllLocator` は未参照になるが削除は Task 7。

- [ ] **Step 4: テスト実行**

Run: `dotnet build winuse.sln 2>&1 | grep -E "error"; dotnet test tests/WinUse.Core.Tests --no-build --filter "FullyQualifiedName~Java|FullyQualifiedName~Jab|FullyQualifiedName~Doctor" --logger "console;verbosity=normal" 2>&1 | grep -E "成功 |失敗|Passed|Failed"`
Expected: `JavaIntegrationTests` の全 Theory が 3 本の JVM で PASS、`TwoJvms_BothReadable` / `Jvm_Exit_IsJavaWindowFalse_NoHang` PASS。旧形式で木が読めなければ Task 2 のレイアウト（特に `GetContextFromHwnd` 12 バイト、`GetContextInfo` 6196 バイト）を疑い、`JabClient` の Debug ログ（どのパケットが無応答か）で絞る。

- [ ] **Step 5: Commit**

```bash
git add -A src/WinUse.Core/Java src/WinUse/Tools/ToolContext.cs src/WinUse/Cli/DoctorCommand.cs tests/WinUse.Core.Tests/Java tests/WinUse.Core.Tests/Cli/DoctorCommandTests.cs
git commit -m "feat(java): read Swing through the built-in JAB client; legacy and wide JVMs, no WindowsAccessBridge-64.dll"
```

---

### Task 6: doctor に JVM ごとの Java 側バージョンを出す

**Files:**
- Modify: `src/WinUse.Core/Java/JavaService.cs`（メソッド追加）
- Modify: `src/WinUse/Cli/DoctorCommand.cs:88-100`
- Test: `tests/WinUse.Core.Tests/Java/JavaServiceReadTests.cs`（偽ブリッジ）、`tests/WinUse.Core.Tests/Java/JavaIntegrationTests.cs`（`Doctor_ReportsFixtureAsResponding`）

**Interfaces:**
- Produces: `public Task<JabVersionInfo?> GetVersionAsync(WindowInfo w)` in `JavaService` — `bridge.RunAsync(j => { var h = j.GetContextFromHwnd(w.Hwnd); if (h is null) return null; try { return j.GetVersion(h.Value.VmId); } finally { j.Release(h.Value); } })`。bridge 不可なら null。
- doctor の responding 行: `bridge responding (legacy wire format, Java 1.8.0_472, bridge 2.0.3)` — 形式は `info.Layout`、`Java` は `version.VmVersion`、`bridge` は `version.JavaDllVersion`（version が null なら形式だけ）。

- [ ] **Step 1: 失敗するテストを書く**

`JavaServiceReadTests` に: `FakeJabAccess.GetVersion` は `("fake 1.8.0", "fake", "fake")` を返すので、

```csharp
[Fact]
public async Task GetVersionAsync_ReturnsTheJvmVersion_AndReleasesTheRoot()
{
    var v = await Service.GetVersionAsync(Window);
    Assert.Equal("fake 1.8.0", v!.VmVersion);
    Assert.Equal(0, Jab.LiveHandles);     // FakeJabAccess already tracks outstanding handles; use its existing counter/property name
}
```

`JavaIntegrationTests.Doctor_ReportsFixtureAsResponding` に `Assert.Contains(jvm == Jvm.Jdk21x64 ? ", Java 21" : ", Java 1.8.0", text)` と `Assert.Contains(", bridge ", text)` を追加。

- [ ] **Step 2: 失敗を確認** — Run: `dotnet build winuse.sln 2>&1 | grep error | head -3` → `GetVersionAsync` 未定義。

- [ ] **Step 3: 実装** — `JavaService.GetVersionAsync`、doctor の行組み立て。

- [ ] **Step 4: テスト実行** — Run: `dotnet test tests/WinUse.Core.Tests --filter "FullyQualifiedName~JavaServiceReadTests|FullyQualifiedName~Doctor_ReportsFixtureAsResponding"` → PASS。

- [ ] **Step 5: Commit**

```bash
git add src/WinUse.Core/Java/JavaService.cs src/WinUse/Cli/DoctorCommand.cs tests/WinUse.Core.Tests/Java/JavaServiceReadTests.cs tests/WinUse.Core.Tests/Java/JavaIntegrationTests.cs
git commit -m "feat(java): doctor shows each JVM's wire format and Java-side bridge version"
```

---

### Task 7: DLL 関連の削除と設定・スクリプト・ドキュメントの整理

**Files:**
- Delete: `src/WinUse.Core/Java/JabDllLocator.cs`、`tests/WinUse.Core.Tests/Java/JabDllLocatorTests.cs`、`third_party/jab/`（全部）
- Modify: `src/WinUse/WinUse.csproj`（コピー項目 2 行とコメントを削除）
- Modify: `src/WinUse.Core/Config/WinUseConfig.cs:61,116`（`JabDll` と `WINUSE_JAB_DLL` を削除）、`tests/WinUse.Core.Tests/Config/WinUseConfigTests.cs:148,158,162`
- Rename: `scripts/fetch-jab.ps1` → `scripts/fetch-dev-java.ps1`（DLL 取得部分と `-Dest` を削除、`-Dev` の処理を無条件にする。SYNOPSIS: "Fetches the Java runtimes the integration tests use (Temurin JDK x64 to tools\jdk, Temurin 8 JRE x86 to tools\jre8-x86)."）
- Modify: `README.md` Java Swing 節、`CHANGELOG.md` Unreleased、`docs/superpowers/followups/2026-10-22-java-access-bridge-deferred.md`、設計書の状態行

- [ ] **Step 1: 削除と改名**

```bash
git rm -q src/WinUse.Core/Java/JabDllLocator.cs tests/WinUse.Core.Tests/Java/JabDllLocatorTests.cs
git rm -rq third_party
git mv scripts/fetch-jab.ps1 scripts/fetch-dev-java.ps1
```

`WinUse.csproj` のコピー項目、`WinUseConfig.JabDll`、`WinUseConfigTests` の該当 assert を消す。

- [ ] **Step 2: ビルドとテスト**

Run: `dotnet build winuse.sln 2>&1 | grep -E "error|警告"; dotnet test tests/WinUse.Core.Tests --no-build --filter "FullyQualifiedName~WinUseConfigTests|FullyQualifiedName~Doctor"`
Expected: 0 エラー、PASS。`ls src/WinUse/bin/Debug/net10.0-windows | grep -i bridge` が空。

- [ ] **Step 3: ドキュメント**

  - README「Java Swing (Java Access Bridge)」節: 「Two things must be in place」を「One thing must be in place」にし、項目 1（クライアント DLL）を削除、`fetch-jab.ps1` の例を `pwsh -File scripts/fetch-dev-java.ps1`（統合テスト用）に。冒頭の説明に「winuse implements the JAB wire protocol itself (no `WindowsAccessBridge-64.dll`), speaking both the wide format (`JavaAccessBridge-32/64.dll`, Java 9+ `javaaccessbridge.dll`) and the legacy format of 32-bit Java 7/8 JREs that ship only `JavaAccessBridge.dll`」を足す。Settings 行から `WINUSE_JAB_DLL` を削除。Limits の旧形式の段落を削除。doctor の説明に「each Java window's wire format and Java-side bridge version」を足す。
  - CHANGELOG Unreleased: 「`WindowsAccessBridge-64.dll` ... bundled」の項目を「winuse talks the Java Access Bridge wire protocol itself; no client DLL is loaded or shipped. 32-bit JREs whose only Java-side bridge is the legacy `JavaAccessBridge.dll` (Java 7/8 x86) are readable like any other.」に置き換え、`WINUSE_JAB_DLL` を設定一覧から削除、Known issues の旧形式の項目を削除、「`scripts/fetch-dev-java.ps1` fetches the test JDK/JRE」に。
  - 設計書の状態行を「実装済み」に。followups に「JAB イベント通知は受信して捨てている（`JabWindow`）」を追記。

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "chore(java): drop WindowsAccessBridge-64.dll, WINUSE_JAB_DLL and the DLL locator; docs for the built-in client"
```

---

### Task 8: 全体検証

- [ ] **Step 1: 全テスト**

Run: `dotnet build winuse.sln 2>&1 | grep -E "error|警告"; dotnet test tests/WinUse.Core.Tests --no-build 2>&1 | grep -E "失敗|合格|Passed|Failed"`
Expected: 失敗は `InputServiceTests.TypeText_IntoNotepad_AppearsInClipboardViaSelectAll`（既知の IME）のみ。`ToolDispatcherTests.GetElement_OnWindowRef_Succeeds_AndJournalsOnce` は既知の稀な競合（単独再実行で通れば可）。

- [ ] **Step 2: publish に DLL が無いこと**

Run: `dotnet publish src/WinUse -c Release -o <scratchpad>/publish2 2>&1 | tail -1; ls <scratchpad>/publish2 | grep -i "bridge\|jab-legal"`
Expected: 何も表示されない（`out` への publish はこのセッションの MCP サーバーがロックしているので別フォルダ）。

- [ ] **Step 3: 手動確認（3 JVM）**

`scripts/mcp_batch.py` で `<scratchpad>/publish2/winuse.exe` に対し、`tools/jre8-x86` を `-Djava.ext.dirs=<ext-legacy>` で起こした `SwingFixture` に `get_ui_tree` → ボタン `Increment` が `j{n}` で返る。

- [ ] **Step 4: PR**

`superpowers:finishing-a-development-branch` に従い push して PR を作る（base `main`）。PR 本文: Why（旧形式の非互換と DLL 同梱の廃止）、What、Tests（3 JVM、2 JVM 同時、終了時）、Not verified（Oracle 7u45 実機、ユーザーが確認）。
