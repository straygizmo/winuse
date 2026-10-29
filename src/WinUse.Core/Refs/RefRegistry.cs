namespace WinUse.Core.Refs;

public sealed class RefRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ElementRef> _refs = new();
    private readonly Dictionary<RefKind, int> _counters = new();
    private int _snapshot;

    public int CurrentSnapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public ElementRef Register(RefKind kind, Locator locator, object? handle)
    {
        lock (_gate)
        {
            // A window keeps its ref for the life of the session: re-observing the same HWND (list_windows,
            // get_ui_tree, process_run ...) returns the ref already handed out, with the locator refreshed so a
            // changed title still resolves. Element refs are per observation and keep numbering up.
            if (kind == RefKind.Window && handle is not null)
            {
                foreach (var (id, existing) in _refs)
                {
                    if (existing.Kind != RefKind.Window || existing.Handle is null || !existing.Handle.Equals(handle)) continue;
                    var refreshed = existing with { Locator = locator };
                    _refs[id] = refreshed;
                    return refreshed;
                }
            }
            _counters.TryGetValue(kind, out var n);
            _counters[kind] = ++n;
            var prefix = kind switch
            {
                RefKind.Window => "win",
                RefKind.Uia => "u",
                RefKind.Dom => "w",
                RefKind.Point => "pt",
                RefKind.Java => "j",
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            var r = new ElementRef($"{prefix}{n}", kind, locator, handle);
            _refs[r.Id] = r;
            return r;
        }
    }

    public bool TryGet(string id, out ElementRef r)
    {
        lock (_gate)
        {
            return _refs.TryGetValue(id, out r!);
        }
    }

    /// <summary>Marks a new observation boundary. Existing refs stay resolvable.</summary>
    public void BeginSnapshot()
    {
        lock (_gate) _snapshot++;
    }
}
