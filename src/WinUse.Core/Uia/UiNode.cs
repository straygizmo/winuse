using WinUse.Core.Windows;

namespace WinUse.Core.Uia;

public sealed record UiNode(
    string Ref, string ControlType, string? Name, string? AutomationId, string? Value,
    bool Enabled, bool Offscreen, bool? Checked, bool Focused, Rect Rect, int Depth,
    IReadOnlyList<UiNode> Children);

/// <param name="ReadValues">False to leave <see cref="UiNode.Value"/> null everywhere: the text of edit fields and the like is never read (nor logged).</param>
public sealed record TreeOptions(string? ScopeRef = null, int MaxDepth = 12, bool InteractiveOnly = true, bool IncludeOffscreen = false, bool ReadValues = true);

/// <summary>
/// A table read: the rows (header first when read), the dimensions of the whole table, how many data rows were left out
/// and how many cells could not be read (those cells are null; an empty cell is "").
/// </summary>
public sealed record TableReadResult(string?[][] Rows, int RowCount, int ColumnCount, int OmittedRows, int UnreadableCells);
