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
