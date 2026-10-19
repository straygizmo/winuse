using FlaUI.Core.AutomationElements;
using WinUse.Core.Refs;

namespace WinUse.Core.Uia;

/// <summary>Walks a locator path from a window root. Must be called on the automation thread.</summary>
internal static class UiaLocatorResolver
{
    public static AutomationElement? Resolve(AutomationElement root, IReadOnlyList<UiaPathSegment> path)
    {
        var cur = root;
        foreach (var seg in path)
        {
            AutomationElement[] kids;
            try { kids = cur.FindAllChildren(); }
            catch { return null; }
            var next = Pick(kids, seg);
            if (next is null) return null;
            cur = next;
        }
        return cur;
    }

    private static AutomationElement? Pick(AutomationElement[] kids, UiaPathSegment seg)
    {
        // Exact key, the same one the builder computed Index over (nulls included).
        var group = kids.Where(k => TypeOf(k) == seg.ControlType && Id(k) == seg.AutomationId && NameOf(k) == seg.Name).ToList();
        if (group.Count > 0) return seg.Index < group.Count ? group[seg.Index] : null;

        // Looser fallbacks (element renamed, etc.) only when unambiguous.
        if (seg.AutomationId is not null)
        {
            var byId = kids.Where(k => TypeOf(k) == seg.ControlType && Id(k) == seg.AutomationId).ToList();
            if (byId.Count == 1) return byId[0];
        }
        if (seg.Name is not null)
        {
            var byName = kids.Where(k => TypeOf(k) == seg.ControlType && NameOf(k) == seg.Name).ToList();
            if (byName.Count == 1) return byName[0];
        }
        return null;
    }

    internal static string TypeOf(AutomationElement e)
    {
        try { return e.Properties.ControlType.ValueOrDefault.ToString(); } catch { return ""; }
    }

    internal static string? Id(AutomationElement e)
    {
        try { var s = e.Properties.AutomationId.ValueOrDefault; return string.IsNullOrEmpty(s) ? null : s; } catch { return null; }
    }

    internal static string? NameOf(AutomationElement e)
    {
        try { var s = e.Properties.Name.ValueOrDefault; return string.IsNullOrEmpty(s) ? null : s; } catch { return null; }
    }
}
