using WinUse.Core.Java;

namespace WinUse.Core.Tests.Java;

/// <summary>A node of a fake Swing accessibility tree. Coordinates are "Java screen" coordinates.</summary>
internal sealed class FakeNode(string role, string? name = null, string states = "enabled,visible,showing",
    int x = 0, int y = 0, int w = 10, int h = 10)
{
    public string Role = role;
    public string? Name = name;
    public string States = states;
    public int X = x, Y = y, W = w, H = h;
    public List<FakeNode> Children = [];
    public string[]? Actions;
    public string? Text;
    public string? Value;
    public string? Description;
    public string? VirtualName;
    public bool HasSelection;
    public List<int> Selected = [];
    public bool RejectSetText;
    /// <summary>A table: its cells are <see cref="Children"/> in row-major order, <see cref="Columns"/> per row.</summary>
    public int? Columns;
    /// <summary>Header cell names of a table (null: no column header).</summary>
    public string[]? Header;
    /// <summary>The bridge hands out no handle for this node (getAccessibleChildFromContext / getAccessibleTableCellInfo fail).</summary>
    public bool NoHandle;
    /// <summary>The bridge returns no context info for this node (getAccessibleContextInfo fails).</summary>
    public bool NoInfo;

    public FakeNode Add(params FakeNode[] kids)
    {
        Children.AddRange(kids);
        return this;
    }

    public FakeNode With(string[]? actions = null, string? text = null, string? value = null, string? description = null,
        string? virtualName = null, bool? hasSelection = null)
    {
        if (actions is not null) Actions = actions;
        if (text is not null) Text = text;
        if (value is not null) Value = value;
        if (description is not null) Description = description;
        if (virtualName is not null) VirtualName = virtualName;
        if (hasSelection is not null) HasSelection = hasSelection.Value;
        return this;
    }

    public bool Showing => States.Split(',', StringSplitOptions.TrimEntries).Contains("showing");

    /// <summary>Depth-first search by role and name.</summary>
    public FakeNode? Find(string role, string? name)
    {
        if (Role == role && Name == name) return this;
        foreach (var c in Children)
            if (c.Find(role, name) is { } f) return f;
        return null;
    }
}

/// <summary>
/// <see cref="IJabAccess"/> over a <see cref="FakeNode"/> tree. Every handle is a fresh id (like a JNI global ref);
/// using a released handle throws, so tests catch use-after-release.
/// </summary>
internal sealed class FakeJabAccess(FakeNode root, nint hwnd = 0x1234) : IJabAccess
{
    private readonly Dictionary<long, FakeNode> _live = new();
    private long _next = 1000;

    public FakeNode Root { get; } = root;
    public nint Hwnd { get; } = hwnd;
    public bool JavaWindow { get; set; } = true;
    public HashSet<long> Released { get; } = [];
    public List<(FakeNode Node, string Action)> Actions { get; } = [];
    public List<(FakeNode Node, int Index)> Selections { get; } = [];
    public List<FakeNode> Cleared { get; } = [];
    public List<FakeNode> Focused { get; } = [];
    /// <summary>Side effect of an action; return false to make the bridge report failure.</summary>
    public Func<FakeNode, string, bool>? OnAction { get; set; }
    public int LiveHandles => _live.Count;
    public int VirtualNameCalls { get; private set; }
    public int ColumnHeaderCalls { get; private set; }

    private JabHandle NewHandle(FakeNode n)
    {
        var id = _next++;
        _live[id] = n;
        return new JabHandle(1, id);
    }

    public FakeNode NodeOf(JabHandle h)
    {
        if (Released.Contains(h.Ac)) throw new InvalidOperationException($"use after release: handle {h.Ac}");
        return _live.TryGetValue(h.Ac, out var n) ? n : throw new InvalidOperationException($"unknown handle {h.Ac}");
    }

    public bool IsJavaWindow(nint hwnd) => JavaWindow && hwnd == Hwnd;

    public JabHandle? GetContextFromHwnd(nint hwnd) => JavaWindow && hwnd == Hwnd ? NewHandle(Root) : null;

    public JabContextInfo? GetInfo(JabHandle h)
    {
        var n = NodeOf(h);
        if (n.NoInfo) return null;
        var parent = ParentOf(n);
        var showing = n.Showing;
        return new JabContextInfo(n.Name, n.Description, n.Role, n.States,
            parent?.Children.IndexOf(n) ?? -1, n.Children.Count,
            showing ? n.X : -1, showing ? n.Y : -1, showing ? n.W : 0, showing ? n.H : 0,
            n.Actions is { Length: > 0 }, n.HasSelection, n.Text is not null, n.Value is not null);
    }

    private FakeNode? ParentOf(FakeNode target, FakeNode? from = null)
    {
        from ??= Root;
        foreach (var c in from.Children)
        {
            if (ReferenceEquals(c, target)) return from;
            if (ParentOf(target, c) is { } p) return p;
        }
        return null;
    }

    public JabHandle? GetChild(JabHandle h, int index)
    {
        var n = NodeOf(h);
        return index >= 0 && index < n.Children.Count && !n.Children[index].NoHandle ? NewHandle(n.Children[index]) : null;
    }

    public void Release(JabHandle h)
    {
        if (!_live.Remove(h.Ac)) throw new InvalidOperationException($"double release: handle {h.Ac}");
        Released.Add(h.Ac);
    }

    public IReadOnlyList<string> GetActions(JabHandle h) => NodeOf(h).Actions ?? [];

    public bool DoAction(JabHandle h, string action)
    {
        var n = NodeOf(h);
        Actions.Add((n, action));
        return OnAction?.Invoke(n, action) ?? true;
    }

    public string? GetText(JabHandle h, int maxChars)
    {
        var t = NodeOf(h).Text;
        return t is null ? null : t.Length > maxChars ? t[..maxChars] : t;
    }

    public string? GetValue(JabHandle h) => NodeOf(h).Value;

    public bool SetText(JabHandle h, string text)
    {
        var n = NodeOf(h);
        if (n.RejectSetText) return false;
        n.Text = text;
        return true;
    }

    public bool RequestFocus(JabHandle h)
    {
        Focused.Add(NodeOf(h));
        return true;
    }

    public void AddSelection(JabHandle h, int index)
    {
        var n = NodeOf(h);
        n.Selected.Add(index);
        Selections.Add((n, index));
    }

    public void ClearSelection(JabHandle h)
    {
        var n = NodeOf(h);
        n.Selected.Clear();
        Cleared.Add(n);
    }

    public JabHandle? GetSelection(JabHandle h, int index)
    {
        var n = NodeOf(h);
        return index < n.Selected.Count && n.Selected[index] < n.Children.Count ? NewHandle(n.Children[n.Selected[index]]) : null;
    }

    public int GetSelectionCount(JabHandle h) => NodeOf(h).Selected.Count;

    public string? GetVirtualName(JabHandle h)
    {
        VirtualNameCalls++;
        return NodeOf(h).VirtualName;
    }

    public JabWindowDiagnosis Diagnose(nint hwnd, int pid) =>
        new(JabStage.NoHello, $"fake: pid {pid} never said hello", pid, Hwnd, null, null, null, false, 4000, []);

    public JabTableInfo? GetTableInfo(JabHandle h)
    {
        var n = NodeOf(h);
        if (n.Columns is not { } cols || cols <= 0) return null;
        return new JabTableInfo(n.Children.Count / cols, cols, NewHandle(n));
    }

    /// <summary>The header as a one-row table whose cells are label nodes made on the fly.</summary>
    public JabTableInfo? GetTableColumnHeader(JabHandle h)
    {
        ColumnHeaderCalls++;
        var n = NodeOf(h);
        if (n.Columns is not { } cols || n.Header is null) return null;
        var header = new FakeNode("table") { Columns = cols };
        header.Children.AddRange(n.Header.Select(name => new FakeNode("label", name)));
        return new JabTableInfo(1, cols, NewHandle(header));
    }

    public JabHandle? GetTableCell(JabHandle table, int row, int column)
    {
        var n = NodeOf(table);
        if (n.Columns is not { } cols || row < 0 || column < 0 || column >= cols) return null;
        var i = row * cols + column;
        return i < n.Children.Count && !n.Children[i].NoHandle ? NewHandle(n.Children[i]) : null;
    }

    public JabVersionInfo? GetVersion(int vmId) => new("fake 1.8.0", "fake", "fake");
    public JabVersionInfo? GetVersionForWindow(nint hwnd) => IsJavaWindow(hwnd) ? GetVersion(0) : null;
    public JabLayout? LayoutForWindow(nint hwnd) => IsJavaWindow(hwnd) ? JabLayout.Wide : null;
}

/// <summary>Runs bridge work inline; <paramref name="available"/> false mimics a client whose window could not be created.</summary>
internal sealed class FakeJabBridge(FakeJabAccess access, bool available = true, string? reason = null) : IJabBridge
{
    public FakeJabAccess Access { get; } = access;
    public bool IsAvailable => available;
    public string? UnavailableReason => available ? null : reason ?? "not found (searched: fake)";
    public string? Status => available ? "fake client" : null;
    public bool Disposed { get; private set; }

    public Task<T> RunAsync<T>(Func<IJabAccess, T> work)
    {
        if (!available) throw new InvalidOperationException($"Java Access Bridge is not available: {UnavailableReason}");
        return Task.FromResult(work(Access));
    }

    public void Dispose() => Disposed = true;
}

/// <summary>The fixture-shaped tree the unit tests share (see SwingFixture.java).</summary>
internal static class FakeTrees
{
    /// <summary>A two-row table like the connection list of a launcher: name, port and an "active" check box per row.</summary>
    public static FakeNode Table(string? name = "Connections") =>
        new FakeNode("table", name, x: 100, y: 300, w: 300, h: 60) { Columns = 3, Header = ["Name", "Port", "Active"] }.Add(
            new FakeNode("label", "alpha"), new FakeNode("label", "8080"), new FakeNode("check box", states: "enabled,visible,showing,checked"),
            new FakeNode("label", "beta"), new FakeNode("label", "9090"), new FakeNode("check box"));

    public static FakeNode Fixture()
    {
        var save = new FakeNode("menu item", "Save", "enabled,visible").With(actions: ["click"]);
        var file = new FakeNode("menu", "File", x: 100, y: 130, w: 40, h: 20).With(actions: ["click"])
            .Add(new FakeNode("menu item", "New", "enabled,visible").With(actions: ["click"]), save);
        var menuBar = new FakeNode("menu bar", x: 100, y: 130, w: 640, h: 20).Add(file);
        var increment = new FakeNode("push button", "Increment", x: 120, y: 160, w: 80, h: 24).With(actions: ["click"]);
        FakeNode deepWrap = new FakeNode("panel", x: 300, y: 300, w: 60, h: 24)
            .Add(new FakeNode("push button", "Deep", x: 300, y: 300, w: 60, h: 24).With(actions: ["click"]));
        for (var i = 0; i < 7; i++) deepWrap = new FakeNode("panel", x: 300, y: 300, w: 60, h: 24).Add(deepWrap);
        var list = new FakeNode("list", x: 100, y: 200, w: 100, h: 60).With(hasSelection: true).Add(
            new FakeNode("label", "Alpha", x: 100, y: 200, w: 100, h: 20),
            new FakeNode("label", "Beta", x: 100, y: 220, w: 100, h: 20),
            new FakeNode("label", "Gamma", x: 100, y: 240, w: 100, h: 20));
        var tabs = new FakeNode("page tab list", x: 100, y: 190, w: 640, h: 200).With(hasSelection: true).Add(
            new FakeNode("page tab", "First", x: 100, y: 190, w: 50, h: 20),
            new FakeNode("page tab", "Second", x: 150, y: 190, w: 50, h: 20));
        tabs.Selected.Add(0);
        var text = new FakeNode("text", "Name", x: 200, y: 160, w: 100, h: 24).With(text: "hello");
        var status = new FakeNode("label", "status: ready", x: 100, y: 500, w: 200, h: 20);
        var content = new FakeNode("panel", x: 100, y: 130, w: 640, h: 450).Add(
            menuBar,
            new FakeNode("panel", x: 100, y: 150, w: 640, h: 40).Add(increment),
            deepWrap, list, tabs, text, status);
        return new FakeNode("frame", "WinUse Swing Fixture", x: 100, y: 100, w: 640, h: 480).Add(
            new FakeNode("root pane", x: 100, y: 100, w: 640, h: 480).Add(
                new FakeNode("layered pane", x: 100, y: 100, w: 640, h: 480).Add(content)));
    }
}
