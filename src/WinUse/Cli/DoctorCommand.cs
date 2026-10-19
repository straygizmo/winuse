using Microsoft.Win32;
using WinUse.Core.Jev;
using WinUse.Tools;

namespace WinUse.Cli;

/// <summary>Environment check. Returns 0 when UIA works, otherwise 1.</summary>
public static class DoctorCommand
{
    private const string WebView2Key = @"SOFTWARE\{0}EdgeUpdate\Clients\{{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}}";

    public static async Task<int> ExecuteAsync(ToolContext ctx, TextWriter output)
    {
        var exeEnv = Path.Combine(AppContext.BaseDirectory, ".env");
        var cwdEnv = Path.Combine(Environment.CurrentDirectory, ".env");
        output.WriteLine($".env (exe dir): {exeEnv} [{(File.Exists(exeEnv) ? "found" : "missing")}]");
        output.WriteLine($".env (cwd):     {cwdEnv} [{(File.Exists(cwdEnv) ? "found" : "missing")}]");
        output.WriteLine($"JEV_API_KEY: {(ctx.Config.JevApiKey is null ? "not set" : "set")}");

        output.WriteLine($"Jev: configured: {(ctx.Jev.IsConfigured ? "yes" : "no")}");
        if (ctx.Jev.IsConfigured)
        {
            try
            {
                var r = await ctx.Jev.AskAsync(new { question = "1+1=2" },
                    new Dictionary<string, JevQuestion> { ["q"] = new NoulQuestion("Is 1+1 equal to 2?") }).ConfigureAwait(false);
                output.WriteLine(r.Answers.TryGetValue("q", out var a) && a is NoulAnswer n
                    ? $"Jev: probe ok, probability {n.Probability:0.000}"
                    : "Jev: probe returned an unexpected answer");
            }
            catch (Exception ex)
            {
                output.WriteLine($"Jev: probe failed: {ex.Message}");
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

        output.WriteLine($"WebView2 runtime: {WebView2Version() ?? "not found"}");
        var pw = Path.Combine(AppContext.BaseDirectory, ".playwright");
        output.WriteLine($"Playwright driver: {(Directory.Exists(pw) ? "present" : "missing")} ({pw})");

        return uiaOk ? 0 : 1;
    }

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
