using WinUse.Core.Windows;

namespace WinUse.Core.Uia;

public sealed record UiNode(
    string Ref, string ControlType, string? Name, string? AutomationId, string? Value,
    bool Enabled, bool Offscreen, bool? Checked, bool Focused, Rect Rect, int Depth,
    IReadOnlyList<UiNode> Children);

/// <param name="ReadValues">False to leave <see cref="UiNode.Value"/> null everywhere: the text of edit fields and the like is never read (nor logged).</param>
public sealed record TreeOptions(string? ScopeRef = null, int MaxDepth = 12, bool InteractiveOnly = true, bool IncludeOffscreen = false, bool ReadValues = true);

/// <summary>The rows read from a table (header first when there is one), the total number of data rows and how many of them were left out.</summary>
public sealed record TableReadResult(string[][] Rows, int RowCount, int OmittedRows);
