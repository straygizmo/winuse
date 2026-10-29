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
