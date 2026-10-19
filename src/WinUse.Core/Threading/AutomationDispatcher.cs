using System.Collections.Concurrent;

namespace WinUse.Core.Threading;

/// <summary>Runs all UI Automation / input / COM work on one dedicated STA thread.</summary>
public sealed class AutomationDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private int _disposed;

    public AutomationDispatcher()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "winuse-automation" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public bool IsOnDispatcherThread => Thread.CurrentThread == _thread;

    public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Execute()
        {
            if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
            try { tcs.TrySetResult(work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }

        if (IsOnDispatcherThread)
        {
            Execute();
            return tcs.Task;
        }

        try { _queue.Add(Execute); }
        catch (InvalidOperationException) { throw new ObjectDisposedException(nameof(AutomationDispatcher)); }
        return tcs.Task;
    }

    public Task RunAsync(Action work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync<object?>(() => { work(); return null; }, ct);
    }

    private void Loop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
            action();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.CompleteAdding();
        if (!IsOnDispatcherThread) _thread.Join(TimeSpan.FromSeconds(5));
    }
}
