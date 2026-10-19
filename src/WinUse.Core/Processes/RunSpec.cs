namespace WinUse.Core.Processes;

public sealed record RunSpec(
    string Path,
    string? Args,
    string? Cwd,
    IReadOnlyDictionary<string, string>? Env,
    bool WebViewDebug,
    bool RequireFresh);

public sealed record RunResult(int Pid, int? CdpPort, string ProcessName);
