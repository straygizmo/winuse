using WinUse.Core.Refs;
using WinUse.Core.Uia;
using WinUse.Core.Windows;

namespace WinUse.Core.Java;

/// <summary>
/// Builds the compressed <see cref="UiNode"/> tree of a Swing window from Java Access Bridge, with the same shape rules
/// as the UIA tree builder except that unnamed panes are always collapsed (Swing nests six to ten of them above every
/// control) and <c>max_depth</c> counts the depth of the output. Must run on the bridge thread.
/// </summary>
/// <remarks>
/// Swing reports <c>showing</c> for every component in a displayed hierarchy, including the ones a scroll pane has scrolled
/// out of view or that lie beyond the window's edge after it shrank: their rectangle is simply outside the viewport or the
/// window. The builder therefore clips every rectangle to the window and to the <c>viewport</c> ancestors: a component with
/// no on-screen part is <c>offscreen</c> (left out unless <c>include_offscreen</c>, keeping its full rectangle so a caller
/// knows where to scroll), a partly visible one carries the visible part, so a click at its centre lands on it.
/// </remarks>
internal sealed class JavaTreeBuilder(IJabAccess jab, RefRegistry refs, WindowLocator wloc, TreeOptions opt, JavaTransform tx,
    Action<string, JabHandle> keep)
{
    /// <summary>Upper bound on bridge round trips per tree: every child costs an IPC call.</summary>
    public const int MaxNodes = 5000;

    /// <summary>The JAB role of a <c>JViewport</c>, the only container whose children are routinely scrolled out of view.</summary>
    public const string ViewportRole = "viewport";

    /// <summary>Upper bound on the ancestors <see cref="ClipOf"/> walks (Swing nests a dozen at most).</summary>
    private const int MaxAncestors = 64;

    private int _visited;

    /// <summary>True when the walk stopped at <see cref="MaxNodes"/>.</summary>
    public bool Truncated { get; private set; }

    /// <summary>True when an element at <c>max_depth</c> had children that were therefore not walked.</summary>
    public bool DepthLimitReached { get; private set; }

    /// <summary>Children skipped because the bridge handed out no handle or no context info for them.</summary>
    public int UnreadableNodes { get; private set; }

    /// <param name="keepRoot">false when the caller already owns <paramref name="root"/> (a scope ref).</param>
    /// <param name="clip">The on-screen area the root's ancestors leave (a scope inside a scroll pane); null: the root's own rectangle.</param>
    public UiNode BuildRoot(JabHandle root, IReadOnlyList<JavaPathSegment> basePath, bool keepRoot = true, Rect? clip = null)
    {
        var info = jab.GetInfo(root) ?? throw new InvalidOperationException("Access Bridge returned no information for the window root.");
        var ct = JavaRoleMap.ControlTypeOf(info.Role, null);
        var flags = JavaStates.Parse(info.States);
        var rect = flags.Showing ? tx.Apply(info.X, info.Y, info.Width, info.Height) : default;
        var r = refs.Register(RefKind.Java, new JavaLocator(wloc, basePath), null);
        if (keepRoot) keep(r.Id, root);
        var rootClip = Narrow(clip, rect.IsEmpty ? null : rect);
        var children = Children(root, info, ct, basePath, 0, rect, rootClip);
        return MakeNode(jab, root, info, r.Id, ct, NameOf(jab, root, info, ct), 0, children, rect, opt.ReadValues);
    }

    private List<UiNode> Children(JabHandle parent, JabContextInfo parentInfo, string parentCt, IReadOnlyList<JavaPathSegment> parentPath,
        int outDepth, Rect fallbackRect, Rect? clip)
    {
        var result = new List<UiNode>();
        if (outDepth >= opt.MaxDepth)
        {
            if (parentInfo.ChildrenCount > 0) DepthLimitReached = true;
            return result;
        }
        var counts = new Dictionary<(string, string?), int>();
        // JTable cells report neither "showing" nor a position through the bridge (their renderer component is never
        // displayed); they are visible whenever the table is and carry the table's rectangle.
        var cellsVisible = parentCt == "Table" && JavaStates.Parse(parentInfo.States).Showing;
        for (var i = 0; i < parentInfo.ChildrenCount; i++)
        {
            if (_visited >= MaxNodes) { Truncated = true; break; }
            if (jab.GetChild(parent, i) is not { } h) { UnreadableNodes++; continue; }
            _visited++;
            var info = jab.GetInfo(h);
            if (info is null) { UnreadableNodes++; jab.Release(h); continue; }

            var ct = JavaRoleMap.ControlTypeOf(info.Role, parentInfo.Role);
            var name = NameOf(jab, h, info, ct);
            // Never skip a sibling here: the resolver counts every child, so indexes must agree.
            var key = (info.Role, name);
            counts.TryGetValue(key, out var idx);
            counts[key] = idx + 1;
            var path = new List<JavaPathSegment>(parentPath) { new(info.Role, name, idx) };

            var flags = JavaStates.Parse(info.States);
            // Items of a closed menu and table cells have no position; they inherit the nearest showing ancestor's rectangle.
            var fullRect = flags.Showing ? tx.Apply(info.X, info.Y, info.Width, info.Height) : fallbackRect;
            var (rect, clippedOut) = flags.Showing ? Clip(fullRect, clip) : (fullRect, false);
            var visible = (flags.Showing && !clippedOut) || cellsVisible;
            if (!visible && !opt.IncludeOffscreen && !JavaRoleMap.IsMenuFamily(ct, parentCt))
            {
                jab.Release(h);
                continue;
            }
            var childClip = info.Role == ViewportRole && flags.Showing && !rect.IsEmpty ? Narrow(clip, rect) : clip;

            var keepNode = !opt.InteractiveOnly
                || InteractiveFilter.IsInteractive(ct)
                || ct == "Table"
                || (ct == "Text" && !string.IsNullOrWhiteSpace(name));
            if (keepNode)
            {
                var r = refs.Register(RefKind.Java, new JavaLocator(wloc, path), null);
                keep(r.Id, h);
                // A table's cells (one node per cell, read through IPC each) are left to get_table or a scoped tree.
                var sub = opt.InteractiveOnly && ct is "Text" or "Table"
                    ? []
                    : Children(h, info, ct, path, outDepth + 1, rect, childClip);
                result.Add(MakeNode(jab, h, info, r.Id, ct, name, outDepth + 1, sub, rect, opt.ReadValues, visible));
            }
            else if (ct == "Pane" && name is not null)
            {
                // A named pane (titled border, named panel) is kept only when its subtree yields kept children.
                var sub = Children(h, info, ct, path, outDepth + 1, rect, childClip);
                if (sub.Count > 0)
                {
                    var r = refs.Register(RefKind.Java, new JavaLocator(wloc, path), null);
                    keep(r.Id, h);
                    result.Add(MakeNode(jab, h, info, r.Id, ct, name, outDepth + 1, sub, rect, opt.ReadValues, visible));
                }
                else jab.Release(h);
            }
            else
            {
                // Collapse: hoist the kept descendants to this level.
                result.AddRange(Children(h, info, ct, path, outDepth, rect, childClip));
                jab.Release(h);
            }
        }
        return result;
    }

    /// <summary>
    /// A showing element's rectangle against the on-screen area its ancestors leave: the visible part when they overlap,
    /// else the full rectangle flagged as clipped out (nothing of the element is on screen).
    /// </summary>
    internal static (Rect Rect, bool ClippedOut) Clip(Rect rect, Rect? clip)
    {
        if (clip is not { } c || rect.IsEmpty) return (rect, false);
        var visible = rect.Intersect(c);
        return visible.IsEmpty ? (rect, true) : (visible, false);
    }

    private static Rect? Narrow(Rect? clip, Rect? by) => (clip, by) switch
    {
        (null, var b) => b,
        (var c, null) => c,
        (var c, var b) => c.Value.Intersect(b.Value),
    };

    /// <summary>
    /// The on-screen area the ancestors of <paramref name="h"/> leave: the top-level window's rectangle narrowed by every
    /// showing <c>viewport</c> on the way up; null when the bridge reports no parent chain. Walks up through
    /// <see cref="IJabAccess.GetParent"/> (one round trip per level) and releases every handle it creates.
    /// </summary>
    internal static Rect? ClipOf(IJabAccess jab, JabHandle h, JavaTransform tx)
    {
        Rect? clip = null;
        var cur = h;
        var ownsCur = false;
        try
        {
            for (var level = 0; level < MaxAncestors; level++)
            {
                var parent = jab.GetParent(cur);
                if (parent is not { } p)
                {
                    // cur is the top-level window: its frame bounds everything inside it.
                    if (cur != h && jab.GetInfo(cur) is { } top && JavaStates.Parse(top.States).Showing && top.Width > 0)
                        clip = Narrow(clip, tx.Apply(top.X, top.Y, top.Width, top.Height));
                    break;
                }
                if (jab.GetInfo(p) is { } info && info.Role == ViewportRole && JavaStates.Parse(info.States).Showing && info.Width > 0)
                    clip = Narrow(clip, tx.Apply(info.X, info.Y, info.Width, info.Height));
                if (ownsCur) jab.Release(cur);
                cur = p;
                ownsCur = true;
            }
        }
        finally
        {
            if (ownsCur) jab.Release(cur);
        }
        return clip;
    }

    /// <summary>
    /// Accessible name, else the description (tool tips of icon-only buttons), else the virtual name. The virtual name
    /// is a bridge round trip that walks the container, so panes (unnamed almost always) never pay for it. The locator
    /// resolver uses the same rule, so the (role, name) keys agree.
    /// </summary>
    internal static string? NameOf(IJabAccess jab, JabHandle h, JabContextInfo info, string controlType) =>
        info.Name ?? info.Description ?? (controlType == "Pane" ? null : jab.GetVirtualName(h));

    /// <param name="visible">Overrides the showing state (table cells are visible with their table, a clipped-out element is not); null: as the states say.</param>
    internal static UiNode MakeNode(IJabAccess jab, JabHandle h, JabContextInfo info, string refId, string ct, string? name, int depth,
        IReadOnlyList<UiNode> children, Rect rect, bool readValue = true, bool? visible = null)
    {
        var flags = JavaStates.Parse(info.States);
        return new UiNode(refId, ct, name, null, readValue ? ReadValue(jab, h, ct) : null,
            flags.Enabled, !(visible ?? flags.Showing), JavaStates.CheckedOf(flags, ct, info.Role), flags.Focused, rect, depth, children);
    }

    /// <summary>The value shown for editable / ranged controls: text, the selected combo item, or the numeric value.</summary>
    internal static string? ReadValue(IJabAccess jab, JabHandle h, string ct)
    {
        switch (ct)
        {
            case "Edit":
                return Truncate(jab.GetText(h, 500));
            case "ComboBox":
            {
                if (jab.GetSelection(h, 0) is not { } sel) return null;
                try { return jab.GetInfo(sel) is { } si ? NameOf(jab, sel, si, JavaRoleMap.ControlTypeOf(si.Role, "combo box")) : null; }
                finally { jab.Release(sel); }
            }
            case "Slider" or "Spinner" or "ProgressBar":
                return jab.GetValue(h);
            default:
                return null;
        }
    }

    private static string? Truncate(string? s) => s is { Length: > 500 } ? s[..500] : s;
}
