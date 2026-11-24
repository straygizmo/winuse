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

/// <summary>
/// A program run to its end: exit code (null when it timed out and was killed), the tail of stdout / stderr and their
/// full lengths in characters; *Truncated when the text returned is only the end of what was printed.
/// </summary>
public sealed record RunOutput(int Pid, string ProcessName, int? ExitCode, bool TimedOut, string Stdout, string Stderr,
    int StdoutChars, int StderrChars, bool StdoutTruncated, bool StderrTruncated, long ElapsedMs);

public enum OutputEncodingKind { Auto, Utf8, Oem, CodePage }

/// <summary>How program output is decoded: auto (UTF-8 when valid, else the OEM code page), utf-8, oem or a code page.</summary>
public readonly record struct OutputEncoding(OutputEncodingKind Kind, int CodePage = 0)
{
    public static OutputEncoding Auto => new(OutputEncodingKind.Auto);

    /// <summary>Parses "auto", "utf-8" / "utf8", "oem", a code page number ("932") or an encoding name ("shift_jis").</summary>
    public static OutputEncoding Parse(string? s)
    {
        var v = (s ?? "auto").Trim().ToLowerInvariant();
        if (v is "" or "auto") return Auto;
        if (v is "utf-8" or "utf8") return new(OutputEncodingKind.Utf8);
        if (v == "oem") return new(OutputEncodingKind.Oem);
        if (int.TryParse(v, out var cp) && cp > 0) return new(OutputEncodingKind.CodePage, cp);
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        try { return new(OutputEncodingKind.CodePage, System.Text.Encoding.GetEncoding(v).CodePage); }
        catch (ArgumentException) { throw new ArgumentException($"Unknown output_encoding '{s}'. Use auto, utf-8, oem, a code page number or an encoding name."); }
    }
}
