using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinUse.Core.Tests.Gate;

public sealed record HookResult(int ExitCode, string Stdout, string Stderr)
{
    public JsonNode? Json => string.IsNullOrWhiteSpace(Stdout) ? null : JsonNode.Parse(Stdout);
}

/// <summary>A throwaway git repository plus a plugin data dir, and runners for the winuse-gate PowerShell scripts.</summary>
public sealed class GateFixture : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly string _root;

    public string Repo { get; }
    public string DataDir { get; }
    public static string ScriptsDir { get; } = FindScriptsDir();

    public GateFixture()
    {
        _root = Path.Combine(Path.GetTempPath(), "winuse-gate-" + Guid.NewGuid().ToString("N")[..8]);
        Repo = Path.Combine(_root, "repo");
        DataDir = Path.Combine(_root, "data");
        Directory.CreateDirectory(Repo);
        Directory.CreateDirectory(DataDir);
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "gate-test");
        Git("config", "user.email", "gate-test@example.invalid");
        Git("config", "core.autocrlf", "false");
        Write("README.md", "readme\n");
        Commit("init");
    }

    /// <summary>A fixture whose repository has no commits yet.</summary>
    public static GateFixture Unborn()
    {
        var f = new GateFixture();
        f.Git("update-ref", "-d", "HEAD");
        f.Git("rm", "-q", "--cached", "README.md");
        return f;
    }

    public void Write(string relPath, string text)
    {
        var full = Path.Combine(Repo, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, Utf8NoBom);
    }

    public void Delete(string relPath) => File.Delete(Path.Combine(Repo, relPath));

    public void WriteConfig(string json) => Write(".claude/winuse-gate.json", json);

    public void Commit(string msg)
    {
        Git("add", "-A");
        Git("commit", "-q", "-m", msg);
    }

    public string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    /// <summary>Runs a hook script. <c>cwd</c> and <c>session_id</c> are filled in when the input lacks them.
    /// Hooks fail open and only report errors on stderr, so any stderr output fails the test unless allowed.</summary>
    public HookResult RunHook(string script, JsonObject input, string? cwd = null, bool allowStderr = false)
    {
        input["cwd"] ??= cwd ?? Repo;
        input["session_id"] ??= "s1";
        var args = new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(ScriptsDir, script), "-DataDir", DataDir };
        var r = RunPowerShell(args, input.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), cwd ?? Repo);
        if (!allowStderr && r.Stderr.Trim().Length > 0) throw new InvalidOperationException($"{script} wrote to stderr: {r.Stderr}");
        return r;
    }

    /// <summary>Starts a hook script without waiting, for concurrency tests.</summary>
    public Task<HookResult> RunHookAsync(string script, JsonObject input) => Task.Run(() => RunHook(script, input));

    /// <summary>Dot-sources gate-lib.ps1 and evaluates <paramref name="expression"/>; returns trimmed stdout.</summary>
    public string RunLib(string expression)
    {
        var lib = Path.Combine(ScriptsDir, "gate-lib.ps1");
        var command = $". '{lib}'; $DataDir = '{DataDir}'; {expression}";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var r = RunPowerShell(["-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded], null, Repo);
        if (r.ExitCode != 0 || r.Stderr.Trim().Length > 0) throw new InvalidOperationException($"lib call failed ({r.ExitCode}): {r.Stderr}");
        return r.Stdout.Trim();
    }

    private static HookResult RunPowerShell(IEnumerable<string> args, string? stdin, string cwd)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using (var w = new StreamWriter(p.StandardInput.BaseStream, Utf8NoBom))
        {
            if (stdin is not null) w.Write(stdin);
        }
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30_000))
        {
            p.Kill(true);
            throw new TimeoutException("powershell did not finish in 30 s");
        }
        return new HookResult(p.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static string FindScriptsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "winuse.sln"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("winuse.sln not found above the test output directory");
        return Path.Combine(dir.FullName, "plugins", "winuse-gate", "scripts");
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
