using System.Text.Json.Nodes;
using WinUse.Core.Refs;

namespace WinUse.Core.Scenarios;

public sealed record JournalEntry(string StepId, string Tool, JsonObject Args, Locator? Resolved, bool? Passed, TimeSpan Elapsed, DateTimeOffset At);

public sealed class Journal
{
    private readonly object _lock = new();
    private readonly List<JournalEntry> _entries = [];
    private int _next;

    public string NextId()
    {
        lock (_lock) return "s" + (++_next);
    }

    public void Record(JournalEntry e)
    {
        lock (_lock) _entries.Add(e);
    }

    public IReadOnlyList<JournalEntry> Entries
    {
        get { lock (_lock) return _entries.ToArray(); }
    }

    public bool TryGet(string stepId, out JournalEntry e)
    {
        lock (_lock)
        {
            var found = _entries.FirstOrDefault(x => x.StepId == stepId);
            e = found!;
            return found is not null;
        }
    }
}
