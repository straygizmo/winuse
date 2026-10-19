using WinUse.Core.Uia;
using WinUse.Core.Web;
using WinUse.Core.Windows;

namespace WinUse.Core.Grounding;

public static class CandidateBuilder
{
    static readonly string[,] Regions =
    {
        { "top-left", "top-center", "top-right" },
        { "middle-left", "middle-center", "middle-right" },
        { "bottom-left", "bottom-center", "bottom-right" },
    };

    public static IReadOnlyList<Candidate> Build(UiNode uiaTree, IReadOnlyList<DomNode> dom, Rect windowRect, Rect? webViewRect)
    {
        var result = new List<Candidate>();
        Walk(uiaTree, null, windowRect, webViewRect, result);
        foreach (var d in dom)
        {
            var label = First(d.Name, d.Value, d.Selector) ?? d.Role;
            var editable = d.Role is "textbox" or "contenteditable" or "searchbox"
                || d.Tag is "textarea" or "contenteditable"
                || (d.Role == "combobox" && !string.IsNullOrEmpty(d.Value));
            result.Add(new Candidate(d.Ref, d.Role, label, d.Value, d.Enabled, d.Focused, editable, d.Checked,
                RegionOf(d.Rect, windowRect), "web"));
        }
        return result;
    }

    static void Walk(UiNode node, string? container, Rect win, Rect? web, List<Candidate> output)
    {
        var isContainer = node.ControlType is "Window" or "Pane";
        if (!isContainer && InteractiveFilter.IsInteractive(node.ControlType) && !InsideWeb(node.Rect, web))
        {
            var label = First(node.Name, node.Value, node.AutomationId) ?? node.ControlType;
            var editable = node.ControlType is "Edit" or "Document"
                || (node.ControlType == "ComboBox" && !string.IsNullOrEmpty(node.Value));
            output.Add(new Candidate(node.Ref, node.ControlType, label, node.Value, node.Enabled,
                node.Focused, editable, node.Checked, RegionOf(node.Rect, win), container));
        }
        var childContainer = isContainer && !string.IsNullOrEmpty(node.Name) ? node.Name : container;
        foreach (var c in node.Children) Walk(c, childContainer, win, web, output);
    }

    static bool InsideWeb(Rect r, Rect? web)
    {
        if (web is not { } w) return false;
        var cx = r.X + r.Width / 2;
        var cy = r.Y + r.Height / 2;
        return cx >= w.X && cx < w.X + w.Width && cy >= w.Y && cy < w.Y + w.Height;
    }

    static string? First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v));

    static string RegionOf(Rect r, Rect win)
    {
        var cx = r.X + r.Width / 2.0;
        var cy = r.Y + r.Height / 2.0;
        return Regions[Bucket(cy, win.Y, win.Height), Bucket(cx, win.X, win.Width)];
    }

    static int Bucket(double v, int origin, int size)
    {
        if (size <= 0) return 1;
        var i = (int)Math.Floor((v - origin) * 3.0 / size);
        return Math.Clamp(i, 0, 2);
    }

    public static IReadOnlyList<IReadOnlyList<Candidate>> SplitForChoice(IReadOnlyList<Candidate> all, int max = 254)
    {
        if (all.Count <= max) return [all];
        var groups = new List<IReadOnlyList<Candidate>>();
        foreach (var g in all.GroupBy(c => c.Container ?? "window"))
        {
            var list = g.ToList();
            for (var i = 0; i < list.Count; i += max)
                groups.Add(list.GetRange(i, Math.Min(max, list.Count - i)));
        }
        return groups;
    }
}
