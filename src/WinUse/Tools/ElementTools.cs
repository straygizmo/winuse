using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WinUse.Core.Input;
using WinUse.Core.Refs;
using WinUse.Core.Windows;

namespace WinUse.Tools;

[McpServerToolType]
public sealed class ElementTools
{
    [McpServerTool(Name = "set_text", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Replace the text of an editable element and report the path taken: method \"value_pattern\" (UIA Value pattern) or \"accessible_text\" (Java AccessibleEditableText) set the text through the accessibility API, so the control sees no key events; \"keyboard\" focused the field, selected all and typed, so key listeners (auto-upper-casing, validation on key) run as for a user; \"fill\" is a DOM fill. method=auto takes the API when the field offers it and falls back to the keyboard; method=keyboard forces typing; method=api fails instead of falling back. With secret=true the text is kept out of the log, the journal and saved scenarios; the result never carries the text.")]
    public static Task<CallToolResult> SetText(
        ToolContext ctx,
        [Description("Element ref (u{n}, j{n} or w{n}).")] string @ref,
        [Description("The new text.")] string text,
        [Description(ToolContext.SecretArg)] bool secret = false,
        [Description("auto (default), api or keyboard: see the tool description.")] string method = "auto") =>
        OnRef(ctx, "set_text", ToolContext.Args(("ref", @ref), ("text", ToolContext.Journaled(text, secret)), ("secret", secret ? true : null),
                ("method", method == "auto" ? null : method)), @ref,
            async r => new { method = await ctx.Uia.SetTextAsync(r.Id, text, ParseTextEntry(method)) },
            async r =>
            {
                if (ParseTextEntry(method) == TextEntryMethod.Keyboard)
                    throw new NotSupportedException("method \"keyboard\" is not available for DOM refs; use type_text after focus.");
                await ctx.Web.FillAsync(r.Id, text);
                return new { method = TextEntryPath.DomFill };
            },
            async r => new { method = await ctx.Java.SetTextAsync(r.Id, text, ParseTextEntry(method)) });

    [McpServerTool(Name = "select_item", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Select an item by its label in a list, combo box, tab or tree (UIA, Java), or an <option> by label in a <select> (DOM), then read the selection back and report it: item is the label that was matched and acted on, selected what the container reports as selected afterwards, outcome \"done\" when they agree. A read-back that names another item (the app refused or changed the selection) is an error that quotes what is selected, so a success result always means the item is selected. outcome \"unknown\" (with note) means the container exposes no way to read its selection (no UIA Selection / Value pattern or SelectionItem state, no AccessibleSelection): the request was delivered but not confirmed; read the element or take a screenshot. The read-back is retried for about half a second before a mismatch is reported, for apps that update the selection asynchronously. On a UIA container a read-back that still disagrees makes winuse try the other path (a combo box: mouse click in the popup first, then the SelectionItem pattern; lists, tabs, trees: the pattern first, then the click); actions counts the inputs that reached the app (1, or 2 when both paths acted), and a mismatch error names the paths delivered. single_action=true stops after the first path that delivered an input, so the app never receives a second selection: for callers that must not resend on an unconfirmed result.")]
    public static Task<CallToolResult> SelectItem(
        ToolContext ctx,
        [Description("Element ref (u{n}, j{n} or w{n}) of the container.")] string @ref,
        [Description("Item label (exact match preferred, then substring).")] string item,
        [Description("true: deliver at most one input (no fallback to the other path when the read-back disagrees); the mismatch is then reported as an error naming the path that acted. Default false.")] bool single_action = false) =>
        OnRef(ctx, "select_item", ToolContext.Args(("ref", @ref), ("item", item), ("single_action", single_action ? (object)true : null)), @ref,
            async r => Describe(await ctx.Uia.SelectItemAsync(r.Id, item, single_action)),
            async r => Describe(await ctx.Web.SelectOptionAsync(r.Id, item)),
            async r => Describe(await ctx.Java.SelectItemAsync(r.Id, item)));

    private static object Describe(SelectItemResult r) => new { method = r.Method, outcome = r.Outcome, item = r.Item, selected = r.Selected, actions = r.Actions, note = r.Note };

    [McpServerTool(Name = "set_checked", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Set a check box / toggle / radio button to the given state and read it back. Reports method (\"toggle\" UIA Toggle pattern, \"selection_item\" UIA SelectionItem, \"action\" Java click action, \"dom\"), toggles (how many toggles were delivered: 0 when the control already had the state), checked (the state read back afterwards) and outcome: \"done\" when the read-back shows the requested state. By default a toggle that has not taken within about half a second is followed by another, up to 3, and a state that is still wrong is an error naming the toggles delivered. single_action=true delivers at most one toggle and reports outcome \"unknown\" with note when the state still differs, so a caller that must not resend can read the element and decide.")]
    public static Task<CallToolResult> SetChecked(
        ToolContext ctx,
        [Description("Element ref (u{n}, j{n} or w{n}).")] string @ref,
        [Description("true to check, false to uncheck.")] bool @checked,
        [Description("true: toggle at most once and report outcome \"unknown\" instead of toggling again when the state read back still differs. Default false (up to 3 toggles, then an error).")] bool single_action = false) =>
        OnRef(ctx, "set_checked", ToolContext.Args(("ref", @ref), ("checked", @checked), ("single_action", single_action ? (object)true : null)), @ref,
            async r => Describe(await ctx.Uia.SetCheckedAsync(r.Id, @checked, single_action)),
            async r => Describe(await ctx.Web.SetCheckedAsync(r.Id, @checked)),
            async r => Describe(await ctx.Java.SetCheckedAsync(r.Id, @checked, single_action)));

    private static object Describe(SetCheckedResult r) => new { method = r.Method, outcome = r.Outcome, toggles = r.Toggles, @checked = r.Checked, note = r.Note };

    [McpServerTool(Name = "focus", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Give keyboard focus to an element.")]
    public static Task<CallToolResult> Focus(
        ToolContext ctx,
        [Description("Element ref (u{n}, j{n} or w{n}).")] string @ref) =>
        OnRef(ctx, "focus", ToolContext.Args(("ref", @ref)), @ref,
            r => ctx.Uia.FocusAsync(r.Id),
            r => ctx.Web.FocusAsync(r.Id),
            r => ctx.Java.FocusAsync(r.Id));

    [McpServerTool(Name = "invoke", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Activate an element and report how: method \"action\" (UIA Invoke / Toggle / SelectionItem, Java click action; the action name is in action), \"mouse\" (a click at the centre of its visible part) or \"selection\" (Java: selected in its parent). outcome is \"done\" when the target confirmed it, or \"unknown\" when the request was delivered but never answered: a Swing action that opens a modal dialog returns only when the dialog closes, so the action most likely ran and a dialog is up (foreground / new_windows show it) while that JVM's Java Access Bridge stays blocked until the dialog is closed; do not repeat the invoke, read the dialog with screenshot / the UIA tree and operate it with click / type_text / press_keys. method=auto clicks a visible Swing push button with the mouse for that reason and uses the accessibility action for everything else (menu items work while their menu is closed); method=action forces the action, method=mouse forces the click (fails when none of the element is on screen).")]
    public static Task<CallToolResult> Invoke(
        ToolContext ctx,
        [Description("Element ref (u{n}, j{n} or w{n}).")] string @ref,
        [Description("auto (default), action or mouse: see the tool description.")] string method = "auto") =>
        OnRef(ctx, "invoke", ToolContext.Args(("ref", @ref), ("method", method == "auto" ? null : method)), @ref,
            async r => Describe(await ctx.Uia.InvokeAsync(r.Id, ParseInvoke(method))),
            async r =>
            {
                if (ParseInvoke(method) == InvokeMethod.Mouse)
                    throw new NotSupportedException("method \"mouse\" is not available for DOM refs; use click.");
                await ctx.Web.ClickAsync(r.Id);
                return Describe(InvokeResult.Did("action", "click"));
            },
            async r =>
            {
                var owner = await ctx.Windows.FindWithFallbackAsync(((JavaLocator)r.Locator).Window);
                var before = owner is null ? [] : (await ctx.Windows.ListAsync()).Where(w => w.Pid == owner.Pid).Select(w => w.Hwnd).ToHashSet();
                var result = await ctx.Java.InvokeAsync(r.Id, ParseInvoke(method));
                if (result.Outcome != InvokeResult.Unknown) return Describe(result);
                // The action never answered: show what came up so the caller can tell a dialog from a hang.
                var fg = await ctx.Windows.ForegroundAsync();
                var opened = owner is null ? [] : (await ctx.Windows.ListAsync()).Where(w => w.Pid == owner.Pid && !before.Contains(w.Hwnd)).ToList();
                return new
                {
                    method = result.Method,
                    action = result.Action,
                    outcome = result.Outcome,
                    note = result.Note,
                    foreground = fg is null ? null : ObserveTools.WindowJson(ctx.RegisterWindow(fg).Id, fg),
                    new_windows = opened.Select(w => ObserveTools.WindowJson(ctx.RegisterWindow(w).Id, w)).ToList(),
                };
            });

    private static object Describe(InvokeResult r) => new { method = r.Method, action = r.Action, outcome = r.Outcome, note = r.Note };

    private static TextEntryMethod ParseTextEntry(string method) => method.ToLowerInvariant() switch
    {
        "" or "auto" => TextEntryMethod.Auto,
        "api" => TextEntryMethod.Api,
        "keyboard" => TextEntryMethod.Keyboard,
        _ => throw new ArgumentException($"Unknown method '{method}'. Use auto, api or keyboard."),
    };

    private static InvokeMethod ParseInvoke(string method) => method.ToLowerInvariant() switch
    {
        "" or "auto" => InvokeMethod.Auto,
        "action" => InvokeMethod.Action,
        "mouse" => InvokeMethod.Mouse,
        _ => throw new ArgumentException($"Unknown method '{method}'. Use auto, action or mouse."),
    };

    private static Task<CallToolResult> OnRef(ToolContext ctx, string tool, System.Text.Json.Nodes.JsonObject args, string refId,
        Func<ElementRef, Task> uia, Func<ElementRef, Task> dom, Func<ElementRef, Task> java) =>
        OnRef(ctx, tool, args, refId,
            async r => { await uia(r); return (object?)null; },
            async r => { await dom(r); return (object?)null; },
            async r => { await java(r); return (object?)null; });

    /// <summary>Runs the backend-specific body for the ref and merges its (anonymous) result into <c>{ ref, ... }</c>.</summary>
    private static Task<CallToolResult> OnRef(ToolContext ctx, string tool, System.Text.Json.Nodes.JsonObject args, string refId,
        Func<ElementRef, Task<object?>> uia, Func<ElementRef, Task<object?>> dom, Func<ElementRef, Task<object?>> java) =>
        ctx.RunStepAsync(tool, args, async step =>
        {
            var r = ctx.GetRef(refId);
            step.Resolved = r.Locator;
            var extra = r.Kind switch
            {
                RefKind.Uia => await uia(r),
                RefKind.Dom => await dom(r),
                RefKind.Java => await java(r),
                _ => throw new NotSupportedException($"{tool} needs an element ref (u{{n}}, j{{n}} or w{{n}}), got '{refId}'."),
            };
            if (extra is null) return new { @ref = r.Id };
            var o = System.Text.Json.JsonSerializer.SerializeToNode(extra, extra.GetType(), ToolResult.Json)!.AsObject();
            var merged = new System.Text.Json.Nodes.JsonObject { ["ref"] = r.Id };
            foreach (var (k, v) in o.ToList()) { o.Remove(k); merged[k] = v; }
            return merged;
        });
}
