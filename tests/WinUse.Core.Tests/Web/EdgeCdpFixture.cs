using System.Net;
using System.Net.Sockets;
using WinUse.Core.Processes;
using WinUse.Core.Refs;
using WinUse.Core.Web;

namespace WinUse.Core.Tests.Web;

public sealed class EdgeCdpFixture : IAsyncLifetime
{
    private const string EdgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    private ProcessService? _ps;
    private int _pid;
    private string? _dir;

    public RefRegistry Refs { get; } = new();
    public WebService Web { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!File.Exists(EdgePath)) throw new FileNotFoundException("Edge not found for integration test: " + EdgePath);
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _dir = Directory.CreateTempSubdirectory("winuse-edge").FullName;
        _ps = new ProcessService(TestConfig.Default);
        var r = _ps.Run(new RunSpec(EdgePath,
            $"--remote-debugging-port={port} --remote-allow-origins=* --user-data-dir=\"{_dir}\" --no-first-run --headless=new about:blank",
            null, null, WebViewDebug: false, RequireFresh: false));
        _pid = r.Pid;
        Web = new WebService(Refs);
        await Web.ConnectAsync(port, TimeSpan.FromSeconds(30));
        // The initial about:blank/NTP page enforces TrustedHTML, which breaks SetContentAsync; move to a plain data: page.
        await Web.CurrentPage!.GotoAsync("data:text/html,<html><body></body></html>");
    }

    public async Task DisposeAsync()
    {
        if (Web is not null) await Web.DisposeAsync();
        try { _ps?.Kill(_pid.ToString()); } catch { }
        await Task.Delay(500);
        try { if (_dir is not null) Directory.Delete(_dir, true); } catch { }
    }
}
