using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WinUse.Core.Refs;
using WinUse.Core.Windows;

namespace WinUse.Tools;

[McpServerToolType]
public sealed class WaitTools
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    [McpServerTool(Name = "wait_for", ReadOnly = true)]
    [Description("Wait until a condition holds, polling every 200 ms. Conditions: window (title), window_closed (title), element (ref or selector), text (ref contains expected), enabled (ref). Returns passed=false on timeout.")]
    public static Task<CallToolResult> WaitFor(
        ToolContext ctx,
        [Description("window, window_closed, element, text or enabled.")] string condition,
        [Description("Timeout in milliseconds.")] int timeout_ms = 10000,
        [Description("Window title regex (window / window_closed).")] string? title = null,
        [Description("Element ref (element / text / enabled).")] string? @ref = null,
        [Description("CSS / Playwright selector in the connected page (element).")] string? selector = null,
        [Description("Expected substring (text).")] string? expected = null) =>
        ctx.RunStepAsync("wait_for", ToolContext.Args(("condition", condition), ("timeout_ms", timeout_ms), ("title", title),
            ("ref", @ref), ("selector", selector), ("expected", expected)), async step =>
        {
            var timeout = TimeSpan.FromMilliseconds(Math.Max(0, timeout_ms));
            var sw = Stopwatch.StartNew();
            ElementRef? r = null;
            if (@ref is not null)
            {
                r = ctx.GetRef(@ref);
                step.Resolved = r.Locator;
            }
            object? window = null;
            bool ok;
            switch (condition.ToLowerInvariant())
            {
                case "window":
                {
                    var w = await ctx.Windows.WaitForAsync(new WindowLocator(Require(title, "title"), null, null), timeout);
                    ok = w is not null;
                    if (w is not null)
                    {
                        ctx.LastWindow = w.Hwnd;
                        window = new { @ref = ctx.RegisterWindow(w).Id, title = w.Title };
                    }
                    break;
                }
                case "window_closed":
                    ok = await ctx.Windows.WaitClosedAsync(new WindowLocator(Require(title, "title"), null, null), timeout);
                    break;
                case "element":
                    if (selector is not null) ok = await PollAsync(() => ctx.Web.ExistsAsync(selector), timeout);
                    else ok = await PollAsync(() => ElementExistsAsync(ctx, RequireRef(r)), timeout);
                    break;
                case "text":
                {
                    var target = RequireRef(r);
                    var want = Require(expected, "expected");
                    ok = await PollAsync(async () => (await ObserveTools.ReadTextAsync(ctx, target)).Contains(want, StringComparison.Ordinal), timeout);
                    break;
                }
                case "enabled":
                {
                    var target = RequireRef(r);
                    ok = await PollAsync(async () => target.Kind == RefKind.Dom
                        ? await ctx.Web.IsEnabledAsync(target.Id)
                        : (await ctx.Uia.GetElementAsync(target.Id)).Enabled, timeout);
                    break;
                }
                default:
                    throw new ArgumentException($"Unknown condition '{condition}'. Use window, window_closed, element, text or enabled.");
            }
            step.Passed = ok;
            return new
            {
                passed = ok,
                elapsed_ms = sw.ElapsedMilliseconds,
                window,
                reason = ok ? null : $"condition '{condition}' not met within {timeout_ms} ms",
            };
        });

    /// <summary>True when the ref's element can be (re-)resolved right now.</summary>
    internal static async Task<bool> ElementExistsAsync(ToolContext ctx, ElementRef r)
    {
        switch (r.Kind)
        {
            case RefKind.Dom:
                return await ctx.Web.ExistsAsync(((DomLocator)r.Locator).Selector);
            case RefKind.Uia:
                try { await ctx.Uia.GetElementAsync(r.Id); return true; }
                catch (Exception) { return false; }
            default:
                return await ctx.Windows.ExistsAsync((WindowLocator)r.Locator);
        }
    }

    /// <summary>Polls <paramref name="condition"/> every 200 ms; exceptions count as "not yet".</summary>
    internal static async Task<bool> PollAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (await condition()) return true;
            }
            catch (Exception)
            {
                // not there yet
            }
            if (sw.Elapsed >= timeout) return false;
            await Task.Delay(PollInterval);
        }
    }

    private static string Require(string? value, string name) =>
        string.IsNullOrEmpty(value) ? throw new ArgumentException($"This condition requires '{name}'.") : value;

    private static ElementRef RequireRef(ElementRef? r) =>
        r ?? throw new ArgumentException("This condition requires 'ref'.");
}
