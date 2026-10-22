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
