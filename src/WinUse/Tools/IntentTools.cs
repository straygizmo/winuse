using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WinUse.Core.Grounding;
using WinUse.Core.Refs;

namespace WinUse.Tools;

[McpServerToolType]
public sealed class IntentTools
{
    [McpServerTool(Name = "act")]
    [Description("Perform a natural-language intent on the window: build candidates, let the decision model (Jev or Clef) pick the target (and action kind when kind=auto), execute it when confident, then verify. When not confident (or no decision model is configured / reachable) nothing is executed and the top candidates are returned (plus a window screenshot when not confident) so you can act with click/set_text yourself.")]
    public static Task<CallToolResult> Act(
        ToolContext ctx,
        [Description("What to do, e.g. \"Click the Save button\".")] string intent,
        [Description("Window ref (win{n}) or title regex. Default: last observed window, else the foreground window.")] string? window = null,
        [Description("auto, click, type, select or toggle.")] string kind = "auto",
        [Description("Text for kind=type, or the item label for kind=select.")] string? text = null,
        [Description("Minimum decision-model confidence required to execute (default from configuration, 0.35).")] double? min_confidence = null) =>
        ctx.RunStepAsync("act", ToolContext.Args(("intent", intent), ("window", window), ("kind", kind), ("text", text),
            ("min_confidence", min_confidence)), async step =>
        {
            var executedKind = ctx.BeginKindCapture();
            var r = await ctx.Grounder.ActAsync(intent, window, ParseKind(kind), text, min_confidence);
            step.Executed = r.Executed;
            // Journal the kind actually executed so a replay does not have to guess it.
            if (r.Executed && executedKind.Value is { } ek) step.Args["kind"] = KindName(ek);
            if (r.Executed && r.Target is not null && ctx.Refs.TryGet(r.Target.Id, out var er)) step.Resolved = er.Locator;
            // Not confident: return a screenshot with the candidates so the caller can pick the target itself.
            if (!r.Executed && r.Reason == "low confidence")
            {
                try
                {
                    if (await ctx.ActScreenshot() is { } cap) step.Extra.Add(ImageContentBlock.FromBytes(cap.Png, "image/png"));
                }
                catch (Exception ex)
                {
                    ctx.Log.LogWarning(ex, "act: screenshot for the low-confidence result failed");
                }
            }
            return new
            {
                executed = r.Executed,
                target = r.Target is null ? null : CandidateJson(r.Target, r.Probability),
                probability = r.Probability,
                confidence = r.Confidence,
                verified = r.Verified,
                alternatives = r.Alternatives.Select(a => CandidateJson(a.Item1, a.Item2)).ToList(),
                reason = r.Reason,
            };
        });

    /// <summary>
    /// Replays a recorded <c>act</c> step deterministically on an already resolved ref (no decision-model call).
    /// With kind=auto the action is a type when text is given, else a click.
    /// </summary>
    internal static Task<CallToolResult> ActOnRefAsync(ToolContext ctx, string intent, string? window, string kind, string? text,
        double? minConfidence, ElementRef target) =>
        ctx.RunStepAsync("act", ToolContext.Args(("intent", intent), ("window", window), ("kind", kind), ("text", text),
            ("min_confidence", minConfidence)), async step =>
        {
            step.Resolved = target.Locator;
            step.Executed = true;
            var k = ParseKind(kind);
            if (k == ActKind.Auto) k = text is null ? ActKind.Click : ActKind.Type;
            step.Args["kind"] = KindName(k);
            var cand = new Candidate(target.Id, "", "", null, true, false, k == ActKind.Type, null, "", null);
            await ctx.ExecuteAsync(cand, k, text);
            return new
            {
                executed = true,
                target = new { @ref = target.Id },
                replayed = true,
                kind = k.ToString().ToLowerInvariant(),
            };
        });

    [McpServerTool(Name = "find", ReadOnly = true)]
    [Description("Rank the elements that best match a natural-language intent without acting. Returns refs with labels and probabilities (probability 0 and grounded=false when no decision model is configured or reachable).")]
    public static Task<CallToolResult> Find(
        ToolContext ctx,
        [Description("The element you are looking for, e.g. \"the search box\".")] string intent,
        [Description("Window ref (win{n}) or title regex. Default: last observed window, else the foreground window.")] string? window = null,
        [Description("Number of candidates to return.")] int top_k = 5) =>
        ctx.RunStepAsync("find", ToolContext.Args(("intent", intent), ("window", window), ("top_k", top_k)), async _ =>
        {
            var r = await ctx.Grounder.FindAsync(intent, window, top_k);
            return new
            {
                grounded = r.Grounded,
                confidence = r.Confidence,
                candidates = r.Ranked.Select(c => CandidateJson(c.Candidate, c.Probability)).ToList(),
            };
        });

    [McpServerTool(Name = "check", ReadOnly = true)]
    [Description("Ask the decision model (Jev or Clef) a yes/no question about the current state of the window. Returns the probability of \"yes\"; available=false when no decision model is configured or reachable.")]
    public static Task<CallToolResult> Check(
        ToolContext ctx,
        [Description("A yes/no question, e.g. \"Is the document shown as saved?\".")] string question,
        [Description("Window ref (win{n}) or title regex. Default: last observed window, else the foreground window.")] string? window = null) =>
        ctx.RunStepAsync("check", ToolContext.Args(("question", question), ("window", window)), async _ =>
        {
            var r = await ctx.Grounder.CheckAsync(question, window);
            return new { probability = r.Probability, available = r.Available };
        });

    internal static ActKind ParseKind(string kind) =>
        Enum.TryParse<ActKind>(kind, ignoreCase: true, out var k) && Enum.IsDefined(k)
            ? k
            : throw new ArgumentException($"Unknown kind '{kind}'. Use auto, click, type, select or toggle.");

    internal static string KindName(ActKind k) => k.ToString().ToLowerInvariant();

    private static object CandidateJson(Candidate c, double probability) => new
    {
        @ref = c.Id,
        role = c.Role,
        label = c.Label,
        value = c.Value,
        enabled = c.Enabled,
        @checked = c.Checked,
        region = c.Region,
        probability,
    };
}
