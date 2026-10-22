using WinUse.Core.Refs;

namespace WinUse.Core.Java;

/// <summary>Walks a <see cref="JavaLocator"/> path from a window root. Must run on the bridge thread.</summary>
internal static class JavaLocatorResolver
{
    /// <summary>
    /// Resolves <paramref name="path"/> under <paramref name="root"/>. Every handle created on the way is released
    /// except the result; <paramref name="root"/> itself is never released (the caller owns it).
    /// </summary>
    public static JabHandle? Resolve(IJabAccess jab, JabHandle root, IReadOnlyList<JavaPathSegment> path)
    {
        var cur = root;
        foreach (var seg in path)
        {
            var info = jab.GetInfo(cur);
            if (info is null) { ReleaseUnlessRoot(jab, root, cur); return null; }
            var kids = new List<(JabHandle Handle, JabContextInfo Info, string? Name)>();
            for (var i = 0; i < info.ChildrenCount; i++)
            {
                if (jab.GetChild(cur, i) is not { } h) continue;
                var ki = jab.GetInfo(h);
                if (ki is null) { jab.Release(h); continue; }
                kids.Add((h, ki, JavaTreeBuilder.NameOf(jab, h, ki)));
            }
            var next = Pick(kids, seg);
            foreach (var k in kids)
                if (next is null || k.Handle != next.Value) jab.Release(k.Handle);
            ReleaseUnlessRoot(jab, root, cur);
            if (next is null) return null;
            cur = next.Value;
        }
        return cur;
    }

    private static void ReleaseUnlessRoot(IJabAccess jab, JabHandle root, JabHandle h)
    {
        if (h != root) jab.Release(h);
    }

    private static JabHandle? Pick(List<(JabHandle Handle, JabContextInfo Info, string? Name)> kids, JavaPathSegment seg)
    {
        // Exact key, the same one the builder computed Index over.
        var group = kids.Where(k => k.Info.Role == seg.Role && k.Name == seg.Name).ToList();
        if (seg.Index < group.Count) return group[seg.Index].Handle;
        // Siblings were removed since the recording: a named element that is now unique still resolves.
        return seg.Name is not null && group.Count == 1 ? group[0].Handle : null;
    }
}
