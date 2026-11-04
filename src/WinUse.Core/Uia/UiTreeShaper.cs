using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WinUse.Core.Uia;

/// <summary>Post-processing of an element tree for the caller: filtering, truncation and a compact JSON form.</summary>
public static class UiTreeShaper
{
    /// <summary>
    /// Keeps the nodes whose control type is in <paramref name="controlTypes"/> (case-insensitive) and whose name matches
    /// <paramref name="name"/>; a node that does not match is replaced by its matching descendants, so the result is as
    /// flat as the filter allows. The root is always kept. Null filters match everything.
    /// </summary>
    public static UiNode Filter(UiNode root, IReadOnlyCollection<string>? controlTypes, Regex? name)
    {
        if ((controlTypes is null || controlTypes.Count == 0) && name is null) return root;
        var types = controlTypes is { Count: > 0 } ? new HashSet<string>(controlTypes, StringComparer.OrdinalIgnoreCase) : null;
        bool Matches(UiNode n) =>
            (types is null || types.Contains(n.ControlType))
            && (name is null || (n.Name is not null && name.IsMatch(n.Name)));
        IEnumerable<UiNode> Shape(UiNode n, int depth)
        {
            if (Matches(n)) return [n with { Depth = depth, Children = n.Children.SelectMany(c => Shape(c, depth + 1)).ToList() }];
            return n.Children.SelectMany(c => Shape(c, depth));
        }
        return root with { Children = root.Children.SelectMany(c => Shape(c, 1)).ToList() };
    }

    /// <summary>
    /// The element the tree should be scoped to: the node whose control type is <paramref name="controlType"/> (when given,
    /// case-insensitive) and whose name matches <paramref name="name"/> (when given). When several match, the one that has
    /// the keyboard focus or contains it wins (a browser's active tab page over its side panes), else the first in
    /// document order. Null when nothing matches; <paramref name="matches"/> then lists what was considered, for the error.
    /// </summary>
    public static UiNode? FindScope(UiNode root, string? controlType, Regex? name, out IReadOnlyList<UiNode> matches)
    {
        bool Matches(UiNode n) =>
            (controlType is null || string.Equals(n.ControlType, controlType, StringComparison.OrdinalIgnoreCase))
            && (name is null || (n.Name is not null && name.IsMatch(n.Name)));
        var all = new List<UiNode>();
        void Collect(UiNode n)
        {
            if (Matches(n)) all.Add(n);
            foreach (var c in n.Children) Collect(c);
        }
        foreach (var c in root.Children) Collect(c);
        matches = all;
        if (all.Count == 0) return null;
        return all.FirstOrDefault(n => n.Focused) ?? all.FirstOrDefault(ContainsFocus) ?? all[0];
    }

    /// <summary>Whether the node or one of its descendants has the keyboard focus.</summary>
    public static bool ContainsFocus(UiNode n) => n.Focused || n.Children.Any(ContainsFocus);

    /// <summary>The subtree with depths counted from <paramref name="n"/> (0) again, for a node taken out of a bigger tree.</summary>
    public static UiNode Rebase(UiNode n, int depth = 0) =>
        n with { Depth = depth, Children = n.Children.Select(c => Rebase(c, depth + 1)).ToList() };

    /// <summary>Parses a comma-separated control type list ("Edit, Button") into its entries; null or blank gives null.</summary>
    public static IReadOnlyList<string>? ParseControlTypes(string? list) =>
        string.IsNullOrWhiteSpace(list)
            ? null
            : list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static int Count(UiNode n) => 1 + n.Children.Sum(Count);

    /// <summary>
    /// Keeps the first <paramref name="maxNodes"/> nodes in document (pre-order) order and drops the rest; returns the
    /// trimmed tree and how many nodes were dropped. The root always counts as the first node.
    /// </summary>
    public static (UiNode Tree, int Omitted) Truncate(UiNode root, int maxNodes)
    {
        if (maxNodes < 1) throw new ArgumentOutOfRangeException(nameof(maxNodes), "max_nodes must be at least 1.");
        var total = Count(root);
        if (total <= maxNodes) return (root, 0);
        var budget = maxNodes - 1; // the root
        UiNode Trim(UiNode n)
        {
            var kept = new List<UiNode>();
            foreach (var c in n.Children)
            {
                if (budget <= 0) break;
                budget--;
                kept.Add(Trim(c));
            }
            return n with { Children = kept };
        }
        return (Trim(root), total - maxNodes);
    }

    /// <summary>
    /// The tree without default-valued fields: no <c>null</c> name / automationId / value / checked, no
    /// <c>enabled: true</c>, <c>offscreen: false</c>, <c>focused: false</c> or <c>depth</c>; <c>rect</c> as "x,y,w,h";
    /// <c>children</c> only when there are any.
    /// </summary>
    public static JsonObject Compact(UiNode n)
    {
        var o = new JsonObject { ["ref"] = n.Ref, ["controlType"] = n.ControlType };
        if (!string.IsNullOrEmpty(n.Name)) o["name"] = n.Name;
        if (!string.IsNullOrEmpty(n.AutomationId)) o["automationId"] = n.AutomationId;
        if (n.Value is not null) o["value"] = n.Value;
        if (!n.Enabled) o["enabled"] = false;
        if (n.Offscreen) o["offscreen"] = true;
        if (n.Checked is { } c) o["checked"] = c;
        if (n.Focused) o["focused"] = true;
        o["rect"] = $"{n.Rect.X},{n.Rect.Y},{n.Rect.Width},{n.Rect.Height}";
        if (n.Children.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var child in n.Children) arr.Add(Compact(child));
            o["children"] = arr;
        }
        return o;
    }
}
