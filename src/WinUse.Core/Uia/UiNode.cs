using WinUse.Core.Windows;

namespace WinUse.Core.Uia;

public sealed record UiNode(
    string Ref, string ControlType, string? Name, string? AutomationId, string? Value,
    bool Enabled, bool Offscreen, bool? Checked, bool Focused, Rect Rect, int Depth,
    IReadOnlyList<UiNode> Children);

public sealed record TreeOptions(string? ScopeRef = null, int MaxDepth = 12, bool InteractiveOnly = true, bool IncludeOffscreen = false);
