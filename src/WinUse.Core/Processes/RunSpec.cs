namespace WinUse.Core.Processes;

public sealed record RunSpec(
    string Path,
    string? Args,
    string? Cwd,
    IReadOnlyDictionary<string, string>? Env,
    bool WebViewDebug,
    bool RequireFresh,
    bool DetachStdio = false);

public sealed record RunResult(int Pid, int? CdpPort, string ProcessName);
