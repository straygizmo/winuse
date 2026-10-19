using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WinUse.Core.Config;

namespace WinUse.Core.Processes;

public sealed class ProcessService(WinUseConfig cfg)
{
    private const string WebViewArgsVar = "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS";
    private readonly HashSet<int> _started = [];
    private readonly object _lock = new();

    public IReadOnlySet<int> Started
    {
        get { lock (_lock) return new HashSet<int>(_started); }
    }

    public RunResult Run(RunSpec spec)
    {
        var name = Path.GetFileNameWithoutExtension(spec.Path);
        if (spec.RequireFresh && Exists(name))
            throw new InvalidOperationException($"Process already running: {name}");

        var psi = new ProcessStartInfo(spec.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        if (spec.Args is not null) psi.Arguments = spec.Args;
        if (spec.Cwd is not null) psi.WorkingDirectory = spec.Cwd;
        if (spec.Env is not null)
            foreach (var (k, v) in spec.Env) psi.Environment[k] = v;

        int? port = null;
        if (spec.WebViewDebug)
        {
            port = FreePort();
            var flags = $"--remote-debugging-port={port} --remote-allow-origins=*";
            psi.Environment.TryGetValue(WebViewArgsVar, out var existing);
            psi.Environment[WebViewArgsVar] = string.IsNullOrWhiteSpace(existing) ? flags : existing + " " + flags;
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start: {spec.Path}");
        lock (_lock) _started.Add(proc.Id);
        return new RunResult(proc.Id, port, name);
    }

    public bool Exists(string processName)
    {
        var procs = Process.GetProcessesByName(StripExe(processName));
        try { return procs.Length > 0; }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    public bool Kill(string nameOrPid)
    {
        if (int.TryParse(nameOrPid, out var pid))
        {
            bool ours;
            lock (_lock) ours = _started.Contains(pid);
            if (!ours) throw new UnauthorizedAccessException($"Not allowed to kill: {nameOrPid}");
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(true);
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        var name = StripExe(nameOrPid);
        if (!cfg.KillableProcesses.Any(k => string.Equals(StripExe(k), name, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException($"Not allowed to kill: {nameOrPid}");

        var killed = false;
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                try { p.Kill(true); killed = true; }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        return killed;
    }

    private static string StripExe(string n) =>
        n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }
}
