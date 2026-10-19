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
}
