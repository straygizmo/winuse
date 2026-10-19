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
        var typed = kids.Where(k => TypeOf(k) == seg.ControlType).ToList();

        // 1. Full key (the same key the index was computed over).
        var full = typed.Where(k => (seg.AutomationId is null || Id(k) == seg.AutomationId)
                                    && (seg.Name is null || NameOf(k) == seg.Name)).ToList();
        if (full.Count > 0) return full[Math.Min(seg.Index, full.Count - 1)];

        // 2. AutomationId alone.
        if (seg.AutomationId is not null)
        {
            var byId = kids.FirstOrDefault(k => Id(k) == seg.AutomationId);
            if (byId is not null) return byId;
        }
        // 3. ControlType + Name.
        if (seg.Name is not null)
        {
            var byName = typed.FirstOrDefault(k => NameOf(k) == seg.Name);
            if (byName is not null) return byName;
        }
        // 4. ControlType + Index.
        return seg.Index < typed.Count ? typed[seg.Index] : null;
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
