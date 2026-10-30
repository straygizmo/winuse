using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinUse.Core.Config;
using WinUse.Core.Windows;

namespace WinUse.Core.Java;

/// <summary>Lazily started Java Access Bridge client: availability, the reason it is not available, and a way to run work on its thread.</summary>
public interface IJabBridge : IDisposable
{
    /// <summary>Starts the client thread and its hidden window on first use.</summary>
    bool IsAvailable { get; }
    /// <summary>Why the bridge cannot be used ("WINUSE_JAB=off", "client window could not be created: ..."); null when available.</summary>
    string? UnavailableReason { get; }
    /// <summary>One line for doctor: the client window and the JVMs connected with their wire formats; null when unavailable.</summary>
    string? Status { get; }
    Task<T> RunAsync<T>(Func<IJabAccess, T> work);
}

/// <summary>
/// The built-in Java Access Bridge client: a <see cref="JabDispatcher"/> thread that owns a <see cref="JabWindow"/> and a
/// <see cref="JabClient"/>. No Windows-side DLL is loaded; winuse speaks the wire format itself, so JVMs with the legacy
/// bridge are read like any other.
/// </summary>
public sealed class JabBridge(WinUseConfig cfg, ILogger? log = null) : IJabBridge
{
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly object _gate = new();
    private bool _initialized;
    private JabDispatcher? _dispatcher;
    private JabClient? _client;
    private JabWindow? _window;
    private string? _reason;

    public bool IsAvailable
    {
        get { Initialize(); return _client is not null; }
    }

    public string? UnavailableReason
    {
        get { Initialize(); return _reason; }
    }

    public string? Status
    {
        get
        {
            if (!IsAvailable) return null;
            var client = _client!;
            var describe = _dispatcher!.RunAsync(client.Describe).GetAwaiter().GetResult();
            return $"built-in client (window 0x{_window!.Hwnd:X}; {describe})";
        }
    }

    public Task<T> RunAsync<T>(Func<IJabAccess, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!IsAvailable) throw new InvalidOperationException($"Java Access Bridge is not available: {_reason}");
        var access = _client!;
        return _dispatcher!.RunAsync(() => work(access));
    }

    private void Initialize()
    {
        lock (_gate)
        {
            if (_initialized) return;
            _initialized = true;
            if (cfg.Jab == JabMode.Off)
            {
                _reason = "WINUSE_JAB=off";
                return;
            }
            var services = new JabClientServices(
                OpenChannel: javaHwnd =>
                {
                    if (_window is null) return new JabChannelOpen(null, "the client window does not exist yet");
                    var channel = JabMemoryChannel.Open(_window.Hwnd, javaHwnd, out var failure, _log);
                    return new JabChannelOpen(channel, failure);
                },
                Inspect: pid => { var i = JavaProcessInfo.Inspect(pid); return new JabJvmProbe(i.Layout, i.Is32Bit, i.LoadedBridgeModule, i.Error); },
                PidOf: hwnd => { NativeMethods.GetWindowThreadProcessId(hwnd, out var pid); return (int)pid; },
                IsWindow: NativeMethods.IsWindow,
                ClientHwnd: () => _window?.Hwnd ?? 0);
            var client = new JabClient(services, _log);
            try
            {
                _dispatcher = new JabDispatcher(() => _window = JabWindow.Create(client, _log));
                _client = client;
                _log.LogInformation("Java Access Bridge client window 0x{Hwnd:X} created", _window!.Hwnd);
            }
            catch (Exception ex)
            {
                var cause = ex.InnerException ?? ex;
                _reason = $"client window could not be created: {cause.Message}";
                _log.LogWarning(ex, "Java Access Bridge {Reason}", _reason);
                _dispatcher?.Dispose();
                _dispatcher = null;
                client.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_dispatcher is not null)
            {
                try
                {
                    _dispatcher.RunAsync(() =>
                    {
                        _window?.Dispose();
                        _client?.Dispose();
                    }).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Java Access Bridge shutdown threw");
                }
                _dispatcher.Dispose();
                _dispatcher = null;
            }
            _client = null;
            _window = null;
        }
    }
}
