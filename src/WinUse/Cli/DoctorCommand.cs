using Microsoft.Win32;
using WinUse.Core.Config;
using WinUse.Core.Java;
using WinUse.Core.Jev;
using WinUse.Tools;

namespace WinUse.Cli;

/// <summary>Environment check. Returns 0 when UIA works, otherwise 1.</summary>
public static class DoctorCommand
{
    private const string WebView2Key = @"SOFTWARE\{0}EdgeUpdate\Clients\{{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}}";

    public static async Task<int> ExecuteAsync(ToolContext ctx, TextWriter output)
    {
        output.WriteLine($"winuse {Program.Version}");
        var exeEnv = Path.Combine(AppContext.BaseDirectory, ".env");
        var cwdEnv = Path.Combine(Environment.CurrentDirectory, ".env");
        output.WriteLine($".env (exe dir): {exeEnv} [{(File.Exists(exeEnv) ? "found" : "missing")}]");
        output.WriteLine($".env (cwd):     {cwdEnv} [{(File.Exists(cwdEnv) ? "found" : "missing")}]");
        var cfg = ctx.Config;
        var provider = cfg.Provider;
        var chosenBy = cfg.ProviderOverride is not null ? " (WINUSE_DECISION_PROVIDER)"
            : provider == DecisionProvider.None ? " (set JEV_API_KEY, or CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_API_TOKEN for Clef)"
            : " (auto)";
        output.WriteLine($"Decision model: {(provider == DecisionProvider.None ? "none" : provider.ToString())}{chosenBy}");
        output.WriteLine($"JEV_API_KEY: {Source(cfg.JevApiKey, "JEV_API_KEY")}");
        output.WriteLine($"CLOUDFLARE_ACCOUNT_ID: {Source(cfg.CloudflareAccountId, "CLOUDFLARE_ACCOUNT_ID")}");
        output.WriteLine($"CLOUDFLARE_API_TOKEN: {Source(cfg.CloudflareApiToken, "CLOUDFLARE_API_TOKEN")}");
        if (provider == DecisionProvider.Clef) output.WriteLine($"Clef model: {cfg.ClefModel}");
        if (JevClient.EndpointOf(cfg) is { } endpoint) output.WriteLine($"Endpoint: {endpoint}");

        if (provider != DecisionProvider.None)
            output.WriteLine($"{provider}: {(ctx.Jev.IsConfigured ? "configured" : "not configured (missing values above)")}");
        if (ctx.Jev.IsConfigured)
        {
            try
            {
                var r = await ctx.Jev.AskAsync(new { question = "1+1=2" },
                    new Dictionary<string, JevQuestion> { ["q"] = new NoulQuestion("Is 1+1 equal to 2?") }).ConfigureAwait(false);
                output.WriteLine(r.Answers.TryGetValue("q", out var a) && a is NoulAnswer n
                    ? $"{provider}: probe ok, probability {n.Probability:0.000}"
                    : $"{provider}: probe returned an unexpected answer");
            }
            catch (Exception ex)
            {
                output.WriteLine($"{provider}: probe failed: {ex.Message}");
            }
        }

        var uiaOk = false;
        try
        {
            var windows = await ctx.Windows.ListAsync().ConfigureAwait(false);
            output.WriteLine($"UIA: ok, {windows.Count} top-level windows");
            uiaOk = true;
        }
        catch (Exception ex)
        {
            output.WriteLine($"UIA: failed: {ex.Message}");
        }

        await ReportJavaAsync(ctx, output).ConfigureAwait(false);

        output.WriteLine($"WebView2 runtime: {WebView2Version() ?? "not found"}");
        var pw = Path.Combine(AppContext.BaseDirectory, ".playwright");
        output.WriteLine($"Playwright driver: {(Directory.Exists(pw) ? "present" : "missing")} ({pw})");

        return uiaOk ? 0 : 1;
    }

    /// <summary>Java Access Bridge: the client DLL, this user's enablement, and every Java window with its JVM.</summary>
    private static async Task ReportJavaAsync(ToolContext ctx, TextWriter output)
    {
        var java = ctx.Java;
        // Probe the windows first: the JVMs answer the client's hello asynchronously, and probing waits for them, so the
        // status line printed afterwards lists the JVMs the window lines talk about.
        IReadOnlyList<WinUse.Core.Windows.WindowInfo> windows;
        try { windows = (await ctx.Windows.ListAsync().ConfigureAwait(false)).Where(JavaService.LooksLikeJavaWindow).ToList(); }
        catch { windows = []; }
        var windowLines = new List<string>();
        foreach (var w in windows)
        {
            var info = JavaProcessInfo.Inspect(w.Pid);
            var bits = info.Is32Bit switch { true => "32-bit", false => "64-bit", null => "?-bit" };
            var responding = java.BridgeAvailable && await java.IsJavaWindowAsync(w).ConfigureAwait(false);
            var version = responding ? await java.GetVersionAsync(w).ConfigureAwait(false) : null;
            // The format the client sends with (it may have been assumed) rather than what the process inspection says.
            var layout = (responding ? await java.GetLayoutAsync(w).ConfigureAwait(false) : null) ?? info.Layout;
            var state = responding
                ? "bridge responding (" + string.Join(", ", new[]
                    {
                        layout is null ? null : $"{JavaProcessInfo.FormatName(layout)} wire format",
                        version is null ? null : $"Java {version.VmVersion}",
                        version is null ? null : $"bridge {version.JavaDllVersion}",
                    }.Where(s => s is not null)) + ")"
                : java.BridgeAvailable ? "bridge NOT responding (accessibility not enabled when the app started?)"
                : "bridge unavailable";
            windowLines.Add($"  \"{w.Title}\" (pid {w.Pid}, {bits}, {w.ClassName}): {state}");
            windowLines.Add($"      {info.Describe()}");
        }

        output.WriteLine(java.BridgeAvailable
            ? $"Java Access Bridge: {java.BridgeStatus}"
            : $"Java Access Bridge: {java.BridgeUnavailableReason}");
        var props = JabEnablement.DefaultPropertiesPath;
        output.WriteLine(JabEnablement.IsEnabled(props)
            ? $"Java accessibility (user): enabled ({props})"
            : $"Java accessibility (user): not enabled (run: winuse jab enable; process_run enables it for the processes it starts)");
        output.WriteLine($"Java windows: {windows.Count}");
        foreach (var line in windowLines) output.WriteLine(line);
    }

    /// <summary>Where a configuration value came from: the process environment wins over .env (see <see cref="WinUseConfig.Load"/>).</summary>
    private static string Source(string? value, string envName) => value is null ? "not set"
        : !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envName)) ? "set (environment)"
        : "set (.env)";

    private static string? WebView2Version()
    {
        try
        {
            using var hklm = Registry.LocalMachine.OpenSubKey(string.Format(WebView2Key, @"WOW6432Node\Microsoft\"));
            if (hklm?.GetValue("pv") is string v1 && v1.Length > 0) return v1;
            using var hkcu = Registry.CurrentUser.OpenSubKey(string.Format(WebView2Key, @"Microsoft\"));
            return hkcu?.GetValue("pv") as string is { Length: > 0 } v2 ? v2 : null;
        }
        catch { return null; }
    }
}
