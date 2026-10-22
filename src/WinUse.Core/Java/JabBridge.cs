using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinUse.Core.Config;

namespace WinUse.Core.Java;

/// <summary>Lazily loaded Java Access Bridge: availability, the reason it is not available, and a way to run work on its thread.</summary>
public interface IJabBridge : IDisposable
{
    /// <summary>Loads the DLL and starts the bridge thread on first use.</summary>
    bool IsAvailable { get; }
    /// <summary>Why the bridge cannot be used ("WINUSE_JAB=off", "not found (searched: ...)", "load failed: ..."); null when available.</summary>
    string? UnavailableReason { get; }
    string? DllPath { get; }
    Task<T> RunAsync<T>(Func<IJabAccess, T> work);
}

public sealed class JabBridge(WinUseConfig cfg, string exeDirectory, ILogger? log = null) : IJabBridge
{
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly object _gate = new();
    private bool _initialized;
    private JabDispatcher? _dispatcher;
    private JabAccess? _access;
    private string? _reason;
    private string? _dllPath;

    public bool IsAvailable
    {
        get { Initialize(); return _access is not null; }
    }

    public string? UnavailableReason
    {
        get { Initialize(); return _reason; }
    }

    public string? DllPath
    {
        get { Initialize(); return _dllPath; }
    }

    public Task<T> RunAsync<T>(Func<IJabAccess, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!IsAvailable) throw new InvalidOperationException($"Java Access Bridge is not available: {_reason}");
        var access = _access!;
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
            var path = JabDllLocator.Locate(cfg.JabDll, exeDirectory, Environment.GetEnvironmentVariable, File.Exists,
                JabDllLocator.RegistryJavaHomes, out var searched, out var thirtyTwoBit);
            if (path is null && NativeLibrary.TryLoad(JabDllLocator.FileName, out var viaPath))
            {
                NativeLibrary.Free(viaPath);
                path = JabDllLocator.FileName;
            }
            if (path is null)
            {
                _reason = JabDllLocator.NotFoundReason(searched, thirtyTwoBit);
                _log.LogWarning("Java Access Bridge {Reason}", _reason);
                return;
            }
            try
            {
                var fns = JabNative.Load(path);
                _dispatcher = new JabDispatcher(() => { unsafe { fns.Windows_run(); } });
                _access = new JabAccess(fns);
                _dllPath = path;
                _log.LogInformation("Java Access Bridge loaded from {Path}", path);
            }
            catch (Exception ex)
            {
                _reason = $"load failed: {ex.Message} ({path})";
                _log.LogWarning(ex, "Java Access Bridge load failed from {Path}", path);
                _dispatcher?.Dispose();
                _dispatcher = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _dispatcher?.Dispose();
            _dispatcher = null;
            _access = null;
        }
    }
}
