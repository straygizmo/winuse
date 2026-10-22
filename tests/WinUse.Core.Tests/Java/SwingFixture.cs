using System.Diagnostics;
using WinUse.Core.Windows;

namespace WinUse.Core.Tests.Java;

/// <summary>
/// Java runtimes <c>scripts/fetch-dev-java.ps1</c> puts under <c>tools\</c>. <see cref="Jre8x86Legacy"/> is the 32-bit JRE 8
/// started with an ext directory holding only <c>access-bridge.jar</c> (+ <c>jaccess.jar</c>), so the JVM loads the legacy
/// <c>JavaAccessBridge.dll</c> like a 32-bit JRE that ships no <c>-32</c> bridge files.
/// </summary>
public enum Jvm { Jdk21x64, Jre8x86, Jre8x86Legacy }

internal static class Jvms
{
    /// <summary>Repository root: the directory above the test output that holds winuse.sln.</summary>
    public static string RepoRoot { get; } = FindRoot();

    public static string? Javac => Existing(Path.Combine(RepoRoot, "tools", "jdk", "bin", "javac.exe"));

    public static string? JavaExe(Jvm jvm) => Existing(jvm switch
    {
        Jvm.Jdk21x64 => Path.Combine(RepoRoot, "tools", "jdk", "bin", "java.exe"),
        Jvm.Jre8x86 or Jvm.Jre8x86Legacy => Path.Combine(RepoRoot, "tools", "jre8-x86", "bin", "java.exe"),
        _ => throw new ArgumentOutOfRangeException(nameof(jvm)),
    });

    /// <summary>True when the JVM exists and the fixture can be compiled (javac) and the JVM ships the Java-side bridge.</summary>
    public static bool Available(Jvm jvm)
    {
        if (Javac is null || JavaExe(jvm) is null) return false;
        if (jvm == Jvm.Jre8x86)
            return File.Exists(Path.Combine(RepoRoot, "tools", "jre8-x86", "bin", "JavaAccessBridge-32.dll"));
        if (jvm == Jvm.Jre8x86Legacy)
            return File.Exists(Path.Combine(RepoRoot, "tools", "jre8-x86", "bin", "JavaAccessBridge.dll"))
                   && LegacyExtJars.All(j => File.Exists(Path.Combine(RepoRoot, "tools", "jre8-x86", "lib", "ext", j)));
        return true;
    }

    public static readonly string[] LegacyExtJars = ["access-bridge.jar", "jaccess.jar"];

    /// <summary>An ext directory with only the legacy bridge jars, copied from tools\jre8-x86 into %TEMP% (once per content).</summary>
    public static string LegacyExtDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "winuse-swing-fixture", "ext-legacy");
        Directory.CreateDirectory(dir);
        foreach (var jar in LegacyExtJars)
        {
            var src = Path.Combine(RepoRoot, "tools", "jre8-x86", "lib", "ext", jar);
            var dst = Path.Combine(dir, jar);
            if (!File.Exists(dst) || File.GetLastWriteTimeUtc(dst) < File.GetLastWriteTimeUtc(src)) File.Copy(src, dst, true);
        }
        return dir;
    }

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "winuse.sln"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}

/// <summary>
/// Compiles (once) and launches <c>SwingFixture.java</c> on the given JVM with Java Access Bridge enabled through a
/// system property, so the user's <c>.accessibility.properties</c> is never touched. Kills only the process it started.
/// Tests using it must share the "Desktop" collection (window focus and input are global).
/// </summary>
internal sealed class SwingFixture : IDisposable
{
    public const string Title = "WinUse Swing Fixture";
    private static readonly object CompileGate = new();
    private Process? _proc;

    public WindowInfo Window { get; private set; } = null!;
    public int Pid => _proc?.Id ?? 0;

    public static async Task<SwingFixture> LaunchAsync(WindowService windows, Jvm jvm)
    {
        var java = Jvms.JavaExe(jvm) ?? throw new InvalidOperationException($"{jvm} is not installed under tools/");
        var classDir = Compile();
        var psi = new ProcessStartInfo(java)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-Djavax.accessibility.assistive_technologies=com.sun.java.accessibility.AccessBridge");
        if (jvm == Jvm.Jre8x86Legacy) psi.ArgumentList.Add("-Djava.ext.dirs=" + Jvms.LegacyExtDir());
        psi.ArgumentList.Add("-cp");
        psi.ArgumentList.Add(classDir);
        psi.ArgumentList.Add("SwingFixture");
        var f = new SwingFixture { _proc = Process.Start(psi) ?? throw new InvalidOperationException("java did not start") };
        f._proc.BeginOutputReadLine();
        f._proc.BeginErrorReadLine();
        try
        {
            var sw = Stopwatch.StartNew();
            WindowInfo? w = null;
            while (w is null && sw.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (f._proc.HasExited) throw new InvalidOperationException($"fixture JVM exited with {f._proc.ExitCode}");
                w = (await windows.ListAsync("^" + Title + "$")).FirstOrDefault(x => x.Pid == f._proc.Id);
                if (w is null) await Task.Delay(200);
            }
            f.Window = w ?? throw new TimeoutException("No fixture window appeared within 20 s.");
            await Task.Delay(500); // let Swing finish laying out before the first bridge call
            return f;
        }
        catch
        {
            f.Dispose();
            throw;
        }
    }

    /// <summary>Compiles the fixture into %TEMP%\winuse-swing-fixture with --release 8 (runs on the 32-bit Java 8 too).</summary>
    private static string Compile()
    {
        var javac = Jvms.Javac ?? throw new InvalidOperationException("tools/jdk/bin/javac.exe is missing; run scripts/fetch-dev-java.ps1");
        var src = Path.Combine(AppContext.BaseDirectory, "Java", "SwingFixture.java");
        var outDir = Path.Combine(Path.GetTempPath(), "winuse-swing-fixture");
        var cls = Path.Combine(outDir, "SwingFixture.class");
        lock (CompileGate)
        {
            if (File.Exists(cls) && File.GetLastWriteTimeUtc(cls) >= File.GetLastWriteTimeUtc(src)) return outDir;
            Directory.CreateDirectory(outDir);
            var psi = new ProcessStartInfo(javac) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "--release", "8", "-d", outDir, src }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException("javac failed: " + err);
            return outDir;
        }
    }

    public void Dispose()
    {
        if (_proc is null) return;
        try { if (!_proc.HasExited) _proc.Kill(true); } catch { }
        _proc.Dispose();
        _proc = null;
    }
}
