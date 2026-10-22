using WinUse.Core.Refs;
using WinUse.Core.Uia;
using WinUse.Core.Windows;

namespace WinUse.Core.Java;

/// <summary>
/// Builds the compressed <see cref="UiNode"/> tree of a Swing window from Java Access Bridge, with the same shape rules
/// as the UIA tree builder except that unnamed panes are always collapsed (Swing nests six to ten of them above every
/// control) and <c>max_depth</c> counts the depth of the output. Must run on the bridge thread.
/// </summary>
internal sealed class JavaTreeBuilder(IJabAccess jab, RefRegistry refs, WindowLocator wloc, TreeOptions opt, JavaTransform tx,
    Action<string, JabHandle> keep)
{
    /// <summary>Upper bound on bridge round trips per tree: every child costs an IPC call.</summary>
    public const int MaxNodes = 5000;

    private int _visited;

    /// <summary>True when the walk stopped at <see cref="MaxNodes"/>.</summary>
    public bool Truncated { get; private set; }

    /// <param name="keepRoot">false when the caller already owns <paramref name="root"/> (a scope ref).</param>
    public UiNode BuildRoot(JabHandle root, IReadOnlyList<JavaPathSegment> basePath, bool keepRoot = true)
    {
        var info = jab.GetInfo(root) ?? throw new InvalidOperationException("Access Bridge returned no information for the window root.");
        var ct = JavaRoleMap.ControlTypeOf(info.Role, null);
        var flags = JavaStates.Parse(info.States);
        var rect = flags.Showing ? tx.Apply(info.X, info.Y, info.Width, info.Height) : default;
        var r = refs.Register(RefKind.Java, new JavaLocator(wloc, basePath), null);
        if (keepRoot) keep(r.Id, root);
        var children = Children(root, info, ct, basePath, 0, rect);
        return MakeNode(jab, root, info, r.Id, ct, NameOf(jab, root, info, ct), 0, children, rect);
    }

    private List<UiNode> Children(JabHandle parent, JabContextInfo parentInfo, string parentCt, IReadOnlyList<JavaPathSegment> parentPath,
        int outDepth, Rect fallbackRect)
    {
        var result = new List<UiNode>();
        if (outDepth >= opt.MaxDepth) return result;
        var counts = new Dictionary<(string, string?), int>();
        for (var i = 0; i < parentInfo.ChildrenCount; i++)
        {
            if (_visited >= MaxNodes) { Truncated = true; break; }
            if (jab.GetChild(parent, i) is not { } h) continue;
            _visited++;
            var info = jab.GetInfo(h);
            if (info is null) { jab.Release(h); continue; }

            var ct = JavaRoleMap.ControlTypeOf(info.Role, parentInfo.Role);
            var name = NameOf(jab, h, info, ct);
            // Never skip a sibling here: the resolver counts every child, so indexes must agree.
            var key = (info.Role, name);
            counts.TryGetValue(key, out var idx);
            counts[key] = idx + 1;
            var path = new List<JavaPathSegment>(parentPath) { new(info.Role, name, idx) };

            var flags = JavaStates.Parse(info.States);
            if (!flags.Showing && !opt.IncludeOffscreen && !JavaRoleMap.IsMenuFamily(ct, parentCt))
            {
                jab.Release(h);
                continue;
            }
            // Items of a closed menu have no position; they inherit the nearest showing ancestor's rectangle.
            var rect = flags.Showing ? tx.Apply(info.X, info.Y, info.Width, info.Height) : fallbackRect;

            var keepNode = !opt.InteractiveOnly
                || InteractiveFilter.IsInteractive(ct)
                || (ct == "Text" && !string.IsNullOrWhiteSpace(name));
            if (keepNode)
            {
                var r = refs.Register(RefKind.Java, new JavaLocator(wloc, path), null);
                keep(r.Id, h);
                var sub = opt.InteractiveOnly && ct == "Text"
                    ? []
                    : Children(h, info, ct, path, outDepth + 1, rect);
                result.Add(MakeNode(jab, h, info, r.Id, ct, name, outDepth + 1, sub, rect));
            }
            else if (ct == "Pane" && name is not null)
            {
                // A named pane (titled border, named panel) is kept only when its subtree yields kept children.
                var sub = Children(h, info, ct, path, outDepth + 1, rect);
                if (sub.Count > 0)
                {
                    var r = refs.Register(RefKind.Java, new JavaLocator(wloc, path), null);
                    keep(r.Id, h);
                    result.Add(MakeNode(jab, h, info, r.Id, ct, name, outDepth + 1, sub, rect));
                }
                else jab.Release(h);
            }
            else
            {
                // Collapse: hoist the kept descendants to this level.
                result.AddRange(Children(h, info, ct, path, outDepth, rect));
                jab.Release(h);
            }
        }
        return result;
    }

    /// <summary>
    /// Accessible name, else the description (tool tips of icon-only buttons), else the virtual name. The virtual name
    /// is a bridge round trip that walks the container, so panes (unnamed almost always) never pay for it. The locator
    /// resolver uses the same rule, so the (role, name) keys agree.
    /// </summary>
    internal static string? NameOf(IJabAccess jab, JabHandle h, JabContextInfo info, string controlType) =>
        info.Name ?? info.Description ?? (controlType == "Pane" ? null : jab.GetVirtualName(h));

    internal static UiNode MakeNode(IJabAccess jab, JabHandle h, JabContextInfo info, string refId, string ct, string? name, int depth,
        IReadOnlyList<UiNode> children, Rect rect)
    {
        var flags = JavaStates.Parse(info.States);
        return new UiNode(refId, ct, name, null, ReadValue(jab, h, ct),
            flags.Enabled, !flags.Showing, JavaStates.CheckedOf(flags, ct, info.Role), flags.Focused, rect, depth, children);
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
