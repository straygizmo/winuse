using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WinUse.Core.Refs;

namespace WinUse.Tools;

/// <summary>Assertions: a failed check returns passed=false with a reason; it is not a tool error.</summary>
[McpServerToolType]
public sealed class AssertTools
{
    [McpServerTool(Name = "assert_text", ReadOnly = true)]
    [Description("Assert the text of an element equals / contains / matches (regex) the expected value. Returns passed, actual and reason.")]
    public static Task<CallToolResult> AssertText(
        ToolContext ctx,
        [Description("Element ref (u{n} or w{n}).")] string @ref,
        [Description("Expected text or pattern.")] string expected,
        [Description("equals, contains or regex.")] string mode = "equals") =>
        ctx.RunStepAsync("assert_text", ToolContext.Args(("ref", @ref), ("expected", expected), ("mode", mode)), async step =>
        {
            var r = ctx.GetRef(@ref);
            step.Resolved = r.Locator;
            var m = mode.ToLowerInvariant();
            if (m is not ("equals" or "contains" or "regex"))
                throw new ArgumentException($"Unknown mode '{mode}'. Use equals, contains or regex.");
            var actual = await ObserveTools.ReadTextAsync(ctx, r);
            var passed = m switch
            {
                "equals" => actual == expected,
                "contains" => actual.Contains(expected, StringComparison.Ordinal),
                _ => Regex.IsMatch(actual, expected, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)),
            };
            return Result(step, passed, actual, passed ? null : $"text does not {(m == "equals" ? "equal" : m == "contains" ? "contain" : "match")} '{expected}'");
        });

    [McpServerTool(Name = "assert_exists", ReadOnly = true)]
    [Description("Assert that an element (ref, re-resolved through its locator) or a DOM selector exists, or does not exist with expected=false.")]
    public static Task<CallToolResult> AssertExists(
        ToolContext ctx,
        [Description("Element ref (u{n}, w{n} or win{n}). Either ref or selector is required.")] string? @ref = null,
        [Description("CSS / Playwright selector in the connected page.")] string? selector = null,
        [Description("Expected existence.")] bool expected = true) =>
        ctx.RunStepAsync("assert_exists", ToolContext.Args(("ref", @ref), ("selector", selector), ("expected", expected)), async step =>
        {
            bool actual;
            if (@ref is not null)
            {
                var r = ctx.GetRef(@ref);
                step.Resolved = r.Locator;
                actual = await WaitTools.ElementExistsAsync(ctx, r);
            }
            else if (!string.IsNullOrEmpty(selector))
            {
                actual = await ctx.Web.ExistsAsync(selector);
            }
            else
            {
                throw new ArgumentException("Specify ref or selector.");
            }
            var passed = actual == expected;
            return Result(step, passed, actual, passed ? null : actual ? "element exists" : "element not found");
        });

    [McpServerTool(Name = "assert_window", ReadOnly = true)]
    [Description("Assert that a window whose title matches the regex exists (or not, with expected=false).")]
    public static Task<CallToolResult> AssertWindow(
        ToolContext ctx,
        [Description("Window title regex.")] string title,
        [Description("Expected existence.")] bool expected = true) =>
        ctx.RunStepAsync("assert_window", ToolContext.Args(("title", title), ("expected", expected)), async step =>
        {
            var actual = await ctx.Windows.ExistsAsync(new WindowLocator(title, null, null));
            var passed = actual == expected;
            return Result(step, passed, actual, passed ? null : actual ? "window exists" : "window not found");
        });

    [McpServerTool(Name = "assert_file", ReadOnly = true)]
    [Description("Assert that a file exists (or not, with exists=false) and optionally contains a substring.")]
    public static Task<CallToolResult> AssertFile(
        ToolContext ctx,
        [Description("File path (under the allowed roots).")] string path,
        [Description("Expected existence.")] bool exists = true,
        [Description("Optional substring the file must contain.")] string? contains = null) =>
        ctx.RunStepAsync("assert_file", ToolContext.Args(("path", path), ("exists", exists), ("contains", contains)), step =>
        {
            var actualExists = ctx.Files.Exists(path);
            bool? actualContains = null;
            string? reason = null;
            var passed = actualExists == exists;
            if (!passed) reason = actualExists ? "file exists" : "file not found";
            else if (actualExists && contains is not null && File.Exists(path))
            {
                actualContains = ctx.Files.ReadText(path).Contains(contains, StringComparison.Ordinal);
                if (actualContains == false)
                {
                    passed = false;
                    reason = $"file does not contain '{contains}'";
                }
            }
            return Task.FromResult(Result(step, passed, new { exists = actualExists, contains = actualContains }, reason));
        });

    private static object Result(ToolStep step, bool passed, object? actual, string? reason)
    {
        step.Passed = passed;
        return new { passed, actual, reason };
    }
}
