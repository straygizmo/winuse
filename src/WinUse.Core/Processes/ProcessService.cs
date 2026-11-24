using System.Diagnostics;
using System.Text;
using System.Net;
using System.Net.Sockets;
using WinUse.Core.Config;
using WinUse.Core.Java;

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
        var (psi, name, port) = StartInfo(spec);

        if (spec.DetachStdio)
        {
            // Keep the child off our own stdin/stdout (the MCP JSON-RPC stream): give it pipes,
            // close its stdin and discard whatever it prints.
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }

        var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start: {spec.Path}");
        var pid = proc.Id;
        lock (_lock) _started.Add(pid);
        if (spec.DetachStdio)
        {
            try { proc.StandardInput.Close(); } catch (IOException) { }
            var stdout = proc.StandardOutput.BaseStream;
            var stderr = proc.StandardError.BaseStream;
            _ = Task.Run(async () =>
            {
                try { await Task.WhenAll(DrainAsync(stdout), DrainAsync(stderr)).ConfigureAwait(false); }
                finally { proc.Dispose(); }
            });
        }
        else
        {
            proc.Dispose();
        }
        return new RunResult(pid, port, name);
    }

    /// <summary>Most bytes of one output stream <see cref="RunToExitAsync"/> keeps; the rest is read and discarded.</summary>
    public const int MaxCapturedBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Starts the program with its stdin closed, waits for it to exit (at most <paramref name="timeout"/>; then the process
    /// tree is killed) and returns the exit code and what it printed, each stream decoded with <paramref name="encoding"/>
    /// and cut to its last <paramref name="maxChars"/> characters.
    /// </summary>
    public async Task<RunOutput> RunToExitAsync(RunSpec spec, TimeSpan timeout, int maxChars, OutputEncoding encoding, CancellationToken ct = default)
    {
        var (psi, name, _) = StartInfo(spec);
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;

        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start: {spec.Path}");
        var pid = proc.Id;
        lock (_lock) _started.Add(pid);
        try { proc.StandardInput.Close(); } catch (IOException) { }
        var stdout = CaptureAsync(proc.StandardOutput.BaseStream);
        var stderr = CaptureAsync(proc.StandardError.BaseStream);

        var timedOut = false;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(timeout);
            try { await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                timedOut = !ct.IsCancellationRequested;
                try { proc.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                if (!timedOut) throw;
            }
        }
        // A grandchild that inherited the pipes can keep them open after the process exited: do not wait for it forever.
        var both = Task.WhenAll(stdout, stderr);
        await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
        var outBytes = stdout.IsCompletedSuccessfully ? stdout.Result : SnapshotOf(stdout);
        var errBytes = stderr.IsCompletedSuccessfully ? stderr.Result : SnapshotOf(stderr);
        int? exitCode = null;
        if (!timedOut) try { exitCode = proc.ExitCode; } catch (InvalidOperationException) { }
        var (outText, outTotal) = Tail(Decode(outBytes.Data, encoding), maxChars);
        var (errText, errTotal) = Tail(Decode(errBytes.Data, encoding), maxChars);
        return new RunOutput(pid, name, exitCode, timedOut, outText, errText, outTotal, errTotal,
            outBytes.Truncated || outTotal > outText.Length, errBytes.Truncated || errTotal > errText.Length, sw.ElapsedMilliseconds);
    }

    private sealed record Captured(byte[] Data, bool Truncated);

    private static Captured SnapshotOf(Task<Captured> t) => t.IsCompletedSuccessfully ? t.Result : new Captured([], true);

    /// <summary>Reads a stream to its end, keeping the first <see cref="MaxCapturedBytes"/> bytes.</summary>
    private static async Task<Captured> CaptureAsync(Stream s)
    {
        var ms = new MemoryStream();
        var buf = new byte[8192];
        var truncated = false;
        try
        {
            int n;
            while ((n = await s.ReadAsync(buf).ConfigureAwait(false)) > 0)
            {
                var room = MaxCapturedBytes - (int)ms.Length;
                if (room > 0) ms.Write(buf, 0, Math.Min(n, room));
                if (n > room) truncated = true;
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        return new Captured(ms.ToArray(), truncated);
    }

    /// <summary>The last <paramref name="maxChars"/> characters of the text, and its full length.</summary>
    internal static (string Text, int Total) Tail(string s, int maxChars) =>
        (s.Length <= maxChars ? s : s[^maxChars..], s.Length);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Decodes program output: <see cref="OutputEncoding.Auto"/> takes UTF-8 when the bytes are valid UTF-8, else the
    /// OEM code page (what console programs such as cmd print in, CP932 on Japanese Windows).
    /// </summary>
    internal static string Decode(byte[] data, OutputEncoding encoding)
    {
        if (data.Length == 0) return "";
        var start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        switch (encoding.Kind)
        {
            case OutputEncodingKind.Utf8:
                return Encoding.UTF8.GetString(data, start, data.Length - start);
            case OutputEncodingKind.CodePage:
                return CodePage(encoding.CodePage).GetString(data);
            case OutputEncodingKind.Oem:
                return CodePage(OemCodePage()).GetString(data);
            default:
                try { return StrictUtf8.GetString(data, start, data.Length - start); }
                catch (DecoderFallbackException) { return CodePage(OemCodePage()).GetString(data); }
        }
    }

    private static Encoding CodePage(int cp)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(cp);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static int OemCodePage()
    {
        try { return (int)GetOEMCP(); }
        catch (EntryPointNotFoundException) { return System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage; }
    }

    private (ProcessStartInfo Psi, string Name, int? Port) StartInfo(RunSpec spec)
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
        ApplyJavaAccessibility(psi.Environment, cfg.JavaAccessibility);
        return (psi, name, port);
    }

    /// <summary>
    /// Starts any JVM among the children with Java Access Bridge enabled (<c>JAVA_TOOL_OPTIONS</c>), so Swing windows
    /// can be read without touching the JRE. Non-Java processes ignore the variable. Off with WINUSE_JAVA_ACCESSIBILITY=off.
    /// </summary>
    internal static void ApplyJavaAccessibility(IDictionary<string, string?> env, bool enabled)
    {
        if (!enabled) return;
        env.TryGetValue(JabEnablement.ToolOptionsVar, out var existing);
        env[JabEnablement.ToolOptionsVar] = JabEnablement.ComposeToolOptions(existing);
    }

    /// <summary>True while a process with this id is running.</summary>
    public bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
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

    /// <summary>Reads a child's output pipe to the end so it never blocks; the data is discarded.</summary>
    private static async Task DrainAsync(Stream s)
    {
        var buf = new byte[8192];
        try
        {
            while (await s.ReadAsync(buf).ConfigureAwait(false) > 0) { }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
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
