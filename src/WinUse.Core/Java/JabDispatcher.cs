using System.Collections.Concurrent;
using WinUse.Core.Windows;

namespace WinUse.Core.Java;

/// <summary>
/// The thread every Java Access Bridge call runs on. <c>Windows_run</c> creates a hidden window on the calling thread and
/// the JVMs answer through that thread's message queue, so unlike <see cref="Threading.AutomationDispatcher"/> this loop
/// pumps Windows messages; queued work is woken with <c>WM_APP</c>.
/// </summary>
public sealed class JabDispatcher : IDisposable
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private int _pumped;
    private int _disposed;
    private Exception? _startError;

    /// <param name="onThreadStart">Runs on the new thread before any work (for <c>Windows_run</c>).</param>
    public JabDispatcher(Action onThreadStart)
    {
        ArgumentNullException.ThrowIfNull(onThreadStart);
        _thread = new Thread(() => Loop(onThreadStart)) { IsBackground = true, Name = "winuse-jab" };
        _thread.Start();
        _ready.Wait();
        if (_startError is not null) throw new InvalidOperationException("Java Access Bridge thread failed to start.", _startError);
    }

    public bool IsOnDispatcherThread => Thread.CurrentThread == _thread;

    /// <summary>Native thread id, for <c>PostThreadMessageW</c>.</summary>
    internal uint ThreadId => _threadId;

    /// <summary>Every window message the loop has dispatched so far.</summary>
    internal int PumpedMessages => Volatile.Read(ref _pumped);

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
        _queue.Enqueue(Execute);
        if (!NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_APP, 0, 0))
            tcs.TrySetException(new InvalidOperationException("Java Access Bridge thread is not accepting work."));
        return tcs.Task;
    }

    public Task RunAsync(Action work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync<object?>(() => { work(); return null; }, ct);
    }

    private void Loop(Action onThreadStart)
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        // Force the message queue into existence before anyone posts to it.
        NativeMethods.PeekMessageW(out _, 0, 0, 0, NativeMethods.PM_NOREMOVE);
        try { onThreadStart(); }
        catch (Exception ex) { _startError = ex; }
        _ready.Set();
        if (_startError is not null) return;

        while (NativeMethods.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            Interlocked.Increment(ref _pumped);
            if (msg.message == NativeMethods.WM_APP && msg.hwnd == 0)
            {
                while (_queue.TryDequeue(out var action)) action();
                continue;
            }
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }
        // Drain whatever was queued after WM_QUIT so no caller waits forever.
        while (_queue.TryDequeue(out var rest)) rest();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_startError is null) NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, 0, 0);
        if (!IsOnDispatcherThread) _thread.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
    }
}
