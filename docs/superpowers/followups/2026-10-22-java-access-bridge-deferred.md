# Java Access Bridge 対応 申し送り

日付: 2026-10-22。ブランチ `feature/java-access-bridge`（設計 `docs/superpowers/specs/2026-10-22-java-access-bridge-design.md`）で意図的に後回しにした事項。

## 機能の未対応（設計書の非ゴール）

- `get_table` の Swing 対応。JAB の AccessibleTable API（`getAccessibleTableInfo` / `getAccessibleTableCellInfo` / `getAccessibleTableColumnHeader`）で `JTable` を 2 次元配列にできる。現状は Java の ref に対して `get_table is not supported for Java elements yet` を返す。
- JAB のイベントコールバック（フォーカス・プロパティ変化の通知）。ポーリング（再観測）で足りているので未使用。
- 重量級ポップアップ（画面外にはみ出すメニューやコンボのリスト）は別のトップレベル `SunAwtWindow` になる。タイトルが無いので `list_windows` に出ない。コンボの選択はウィンドウ内の `showing` なリストを探す方式なので、はみ出した場合は見つからず `Escape` + エラーになる。
- `TreeItem` の展開（`expanded` / `collapsed` 状態、`toggleexpand` アクション）は読んでいるだけで、展開操作のツールは無い。

## テスト

- `ToolDispatcherTests.GetElement_OnWindowRef_Succeeds_AndJournalsOnce` は前面ウィンドウを読むのに `Desktop` コレクションに入っておらず、Swing フィクスチャが並行して出入りすると稀に落ちる（単独では通る）。コレクションに入れるか、自分で起こしたウィンドウを使うようにする。
- `InputServiceTests.TypeText_IntoNotepad_AppearsInClipboardViaSelectAll` は本ブランチ以前から日本語 IME 環境で落ちる（`2026-10-19-phase1-deferred.md` 既出）。

## 実装メモ

- UIA は Java ウィンドウにもタイトルバーのボタン類を合成する。「JAB が使えない Java ウィンドウは根だけ」という設計書の前提は正確には「Swing の中身が無い」。
- `KeyChord` の record 等価性は `Modifiers` リストの参照比較（フェーズ 1 申し送り済み）。テストでは `Key` と `Modifiers` を個別に比較している。
