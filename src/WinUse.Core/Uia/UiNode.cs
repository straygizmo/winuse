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

/// <summary>A UIA tree read plus whether an element at the depth limit had children that were not walked.</summary>
public sealed record UiaTreeResult(UiNode Root, bool DepthLimitReached);

/// <summary>A scrollable ancestor that clips an element: its control type, name and on-screen rectangle (the viewport).</summary>
public sealed record UiaClip(string ControlType, string? Name, Rect Rect);

/// <summary>
/// Where a UIA element is on screen: its bounding rectangle, the part left after clipping it to every scrollable ancestor
/// (scroll viewport) and whether UIA reports it offscreen. Only the visible part may be clicked.
/// </summary>
public sealed record UiaVisibility(Rect Rect, Rect VisibleRect, bool Offscreen, UiaClip? ClippedBy)
{
    /// <summary>
    /// The element clipped to the viewports of its scrollable ancestors (innermost first). <see cref="ClippedBy"/> is the
    /// first viewport that leaves nothing of it, else the last one that cut it.
    /// </summary>
    public static UiaVisibility Compute(Rect rect, bool offscreen, IEnumerable<UiaClip> scrollAncestors)
    {
        var visible = rect;
        UiaClip? by = null;
        foreach (var a in scrollAncestors)
        {
            if (a.Rect.IsEmpty || visible.IsEmpty) continue;
            var next = visible.Intersect(a.Rect);
            if (next != visible) by = a;
            visible = next;
            if (visible.IsEmpty) break;
        }
        return new UiaVisibility(rect, visible, offscreen, by);
    }

    /// <summary>
    /// Pixels of an element that must remain inside its viewport, in each direction, for it to be clicked (or all of it
    /// when it is smaller): a strip of a pixel or two at the viewport's edge is no safe target.
    /// </summary>
    public const int MinVisiblePixels = 5;

    /// <summary>Why the element cannot be clicked, for an error message; null when it can.</summary>
    public string? NotClickableReason(string refId)
    {
        if (Rect.IsEmpty) return $"Ref '{refId}' has no visible area; nothing was clicked.";
        if (ClippedBy is { } c && (VisibleRect.IsEmpty
                                    || VisibleRect.Width < Math.Min(MinVisiblePixels, Rect.Width)
                                    || VisibleRect.Height < Math.Min(MinVisiblePixels, Rect.Height)))
            return $"Ref '{refId}' at {Rect} lies {(VisibleRect.IsEmpty ? "outside" : $"almost entirely outside (only {VisibleRect} shows)")} the viewport {c.Rect} "
                   + $"of its scroll container ({c.ControlType}{(string.IsNullOrEmpty(c.Name) ? "" : $" '{c.Name}'")}): it is scrolled out of view, so its centre is on "
                   + "another element. Nothing was clicked. Scroll it into view (scroll over the container), observe again and click the fresh ref.";
        if (Offscreen)
            return $"Ref '{refId}' at {Rect} is reported offscreen by UI Automation (scrolled out of view, clipped, or its menu / tab is closed); nothing was clicked. "
                   + "Scroll it into view or open its container, observe again and click the fresh ref.";
        return null;
    }
}
