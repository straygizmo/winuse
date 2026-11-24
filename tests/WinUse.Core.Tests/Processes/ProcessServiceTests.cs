using System.Diagnostics;
using WinUse.Core.Processes;

namespace WinUse.Core.Tests.Processes;

public class ProcessServiceTests
{
    [Fact]
    public void Run_WithWebViewDebug_SetsEnvAndReturnsPort()
    {
        var dir = Directory.CreateTempSubdirectory("winuse-ps").FullName;
        var outFile = Path.Combine(dir, "out.txt");
        try
        {
            var svc = new ProcessService(TestConfig.Default);
            var r = svc.Run(new RunSpec("cmd.exe", "/c echo %WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS% > \"" + outFile + "\"", null, null, WebViewDebug: true, RequireFresh: false));
            Assert.NotNull(r.CdpPort);
            Assert.Equal("cmd", r.ProcessName);
            try { using var p = Process.GetProcessById(r.Pid); p.WaitForExit(10000); } catch (ArgumentException) { }
            Assert.Contains($"--remote-debugging-port={r.CdpPort} --remote-allow-origins=*", File.ReadAllText(outFile));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Run_WithDetachStdio_DrainsOutputAndKeepsOurStdoutClean()
    {
        var svc = new ProcessService(TestConfig.Default);
        var original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            // ~30 KB of output: more than a pipe buffer, so the child only exits if the pipe is drained.
            var r = svc.Run(new RunSpec("cmd.exe", "/c echo hi& for /l %i in (1,1,3000) do @echo line %i", null, null, false, false, DetachStdio: true));
            using var p = Process.GetProcessById(r.Pid);
            Assert.True(p.WaitForExit(15000), "child did not exit; its output was not drained");
        }
        catch (ArgumentException)
        {
            // already exited
        }
        finally
        {
            Console.SetOut(original);
        }
        Assert.Equal("", captured.ToString());
    }

    [Fact]
    public void Kill_RefusesProcessNotStartedByUs()
    {
        var svc = new ProcessService(TestConfig.Default);
        Assert.Throws<UnauthorizedAccessException>(() => svc.Kill("explorer"));
        Assert.Throws<UnauthorizedAccessException>(() => svc.Kill("999999"));
    }

    [Fact]
    public void Kill_AllowsProcessStartedByUs()
    {
        var svc = new ProcessService(TestConfig.Default);
        var r = svc.Run(new RunSpec("cmd.exe", "/c ping -n 30 127.0.0.1 > nul", null, null, false, false));
        Assert.Contains(r.Pid, svc.Started);
        Assert.True(svc.Kill(r.Pid.ToString()));
    }

    [Fact]
    public void Run_RequireFresh_ThrowsWhenSameNameRunning()
    {
        var svc = new ProcessService(TestConfig.Default);
        var r = svc.Run(new RunSpec("cmd.exe", "/c ping -n 30 127.0.0.1 > nul", null, null, false, false));
        try
        {
            Assert.True(svc.Exists("cmd.exe"));
            Assert.Throws<InvalidOperationException>(() =>
                svc.Run(new RunSpec("cmd.exe", "/c exit", null, null, false, RequireFresh: true)));
        }
        finally { svc.Kill(r.Pid.ToString()); }
    }
    [Fact]
    public void ApplyJavaAccessibility_SetsOrExtendsToolOptions_OnlyWhenEnabled()
    {
        var env = new Dictionary<string, string?>();
        ProcessService.ApplyJavaAccessibility(env, enabled: true);
        Assert.Equal(WinUse.Core.Java.JabEnablement.ToolOption, env["JAVA_TOOL_OPTIONS"]);

        env["JAVA_TOOL_OPTIONS"] = "-Xmx1g";
        ProcessService.ApplyJavaAccessibility(env, enabled: true);
        Assert.Equal("-Xmx1g " + WinUse.Core.Java.JabEnablement.ToolOption, env["JAVA_TOOL_OPTIONS"]);

        var off = new Dictionary<string, string?>();
        ProcessService.ApplyJavaAccessibility(off, enabled: false);
        Assert.Empty(off);
    }

    [Fact]
    public void Run_PassesJavaToolOptions_ToTheChild_UnlessDisabled()
    {
        var dir = Directory.CreateTempSubdirectory("winuse-ps").FullName;
        var outFile = Path.Combine(dir, "out.txt");
        try
        {
            new ProcessService(TestConfig.Default).Run(new RunSpec("cmd.exe", "/c echo %JAVA_TOOL_OPTIONS% > \"" + outFile + "\"", null, null, false, false));
            WaitFor(outFile);
            Assert.Contains("-Djavax.accessibility.assistive_technologies=com.sun.java.accessibility.AccessBridge", File.ReadAllText(outFile));

            File.Delete(outFile);
            new ProcessService(TestConfig.Default with { JavaAccessibility = false })
                .Run(new RunSpec("cmd.exe", "/c echo %JAVA_TOOL_OPTIONS% > \"" + outFile + "\"", null, null, false, false));
            WaitFor(outFile);
            Assert.DoesNotContain("assistive_technologies", File.ReadAllText(outFile));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void WaitFor(string file)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10) && (!File.Exists(file) || new FileInfo(file).Length == 0)) Thread.Sleep(50);
    }

    [Fact]
    public async Task RunToExit_ReturnsExitCodeStdoutAndStderr()
    {
        var svc = new ProcessService(TestConfig.Default);
        var o = await svc.RunToExitAsync(new RunSpec("cmd.exe", "/c echo out-line& echo err-line 1>&2& exit /b 3", null, null, false, false),
            TimeSpan.FromSeconds(30), 1000, OutputEncoding.Auto);
        Assert.Equal(3, o.ExitCode);
        Assert.False(o.TimedOut);
        Assert.Equal("out-line", o.Stdout.Trim());
        Assert.Equal("err-line", o.Stderr.Trim());
        Assert.False(o.StdoutTruncated);
        Assert.Contains(o.Pid, svc.Started);
    }

    [Fact]
    public async Task RunToExit_KeepsTheEndOfLongOutput()
    {
        var svc = new ProcessService(TestConfig.Default);
        var o = await svc.RunToExitAsync(new RunSpec("cmd.exe", "/c for /l %i in (1,1,3000) do @echo line %i", null, null, false, false),
            TimeSpan.FromSeconds(60), 100, OutputEncoding.Auto);
        Assert.Equal(0, o.ExitCode);
        Assert.True(o.StdoutTruncated);
        Assert.Equal(100, o.Stdout.Length);
        Assert.True(o.StdoutChars > 30_000);
        Assert.Contains("line 3000", o.Stdout);
    }

    [Fact]
    public async Task RunToExit_TimeoutKillsTheProcess()
    {
        var svc = new ProcessService(TestConfig.Default);
        var o = await svc.RunToExitAsync(new RunSpec("cmd.exe", "/c ping -n 30 127.0.0.1 >nul", null, null, false, false),
            TimeSpan.FromSeconds(1), 1000, OutputEncoding.Auto);
        Assert.True(o.TimedOut);
        Assert.Null(o.ExitCode);
        Assert.False(svc.IsAlive(o.Pid));
    }

    [Fact]
    public void Decode_AutoTakesUtf8WhenValid_ElseTheOemCodePage()
    {
        Assert.Equal("保存", ProcessService.Decode(System.Text.Encoding.UTF8.GetBytes("保存"), OutputEncoding.Auto));
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var sjis = System.Text.Encoding.GetEncoding(932).GetBytes("保存");
        Assert.Equal("保存", ProcessService.Decode(sjis, OutputEncoding.Parse("932")));
        Assert.Equal("保存", ProcessService.Decode(sjis, OutputEncoding.Parse("shift_jis")));
        Assert.Equal("", ProcessService.Decode([], OutputEncoding.Auto));
    }

    [Theory]
    [InlineData(null, OutputEncodingKind.Auto)]
    [InlineData("auto", OutputEncodingKind.Auto)]
    [InlineData("UTF-8", OutputEncodingKind.Utf8)]
    [InlineData("oem", OutputEncodingKind.Oem)]
    [InlineData("65001", OutputEncodingKind.CodePage)]
    public void OutputEncoding_Parses(string? input, OutputEncodingKind expected) => Assert.Equal(expected, OutputEncoding.Parse(input).Kind);

    [Fact]
    public void OutputEncoding_RejectsUnknownNames() => Assert.Throws<ArgumentException>(() => OutputEncoding.Parse("klingon"));
}
