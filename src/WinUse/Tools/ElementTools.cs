using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WinUse.Core.Refs;

namespace WinUse.Tools;

[McpServerToolType]
public sealed class ElementTools
{
    [McpServerTool(Name = "set_text", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Replace the text of an editable element (UIA Value pattern or select-all + typing; DOM fill).")]
    public static Task<CallToolResult> SetText(
        ToolContext ctx,
        [Description("Element ref (u{n}, j{n} or w{n}).")] string @ref,
        [Description("The new text.")] string text) =>
        OnRef(ctx, "set_text", ToolContext.Args(("ref", @ref), ("text", text)), @ref,
            r => ctx.Uia.SetTextAsync(r.Id, text),
            r => ctx.Web.FillAsync(r.Id, text),
            r => ctx.Java.SetTextAsync(r.Id, text));

    [McpServerTool(Name = "select_item", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Select an item by its label in a list, combo box, tab or tree (UIA), or an <option> by label in a <select> (DOM).")]
    public static Task<CallToolResult> SelectItem(
        ToolContext ctx,
        [Description("Element ref (u{n}, j{n} or w{n}) of the container.")] string @ref,
        [Description("Item label (exact match preferred, then substring).")] string item) =>
        OnRef(ctx, "select_item", ToolContext.Args(("ref", @ref), ("item", item)), @ref,
            r => ctx.Uia.SelectItemAsync(r.Id, item),
            r => ctx.Web.SelectOptionAsync(r.Id, item),
            r => ctx.Java.SelectItemAsync(r.Id, item));

    [McpServerTool(Name = "set_checked", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Set a check box / toggle / radio button to the given state.")]
    public static Task<CallToolResult> SetChecked(
        ToolContext ctx,
        [Description("Element ref (u{n}, j{n} or w{n}).")] string @ref,
        [Description("true to check, false to uncheck.")] bool @checked) =>
        OnRef(ctx, "set_checked", ToolContext.Args(("ref", @ref), ("checked", @checked)), @ref,
            r => ctx.Uia.SetCheckedAsync(r.Id, @checked),
            r => ctx.Web.SetCheckedAsync(r.Id, @checked),
            r => ctx.Java.SetCheckedAsync(r.Id, @checked));

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
    [Description("Activate an element: UIA Invoke (or Toggle / SelectionItem, else a click at its centre); DOM click; Java click action (menu items work while the menu is closed).")]
    public static Task<CallToolResult> Invoke(
        ToolContext ctx,
        [Description("Element ref (u{n}, j{n} or w{n}).")] string @ref) =>
        OnRef(ctx, "invoke", ToolContext.Args(("ref", @ref)), @ref,
            r => ctx.Uia.InvokeAsync(r.Id),
            r => ctx.Web.ClickAsync(r.Id),
            r => ctx.Java.InvokeAsync(r.Id));

    private static Task<CallToolResult> OnRef(ToolContext ctx, string tool, System.Text.Json.Nodes.JsonObject args, string refId,
        Func<ElementRef, Task> uia, Func<ElementRef, Task> dom, Func<ElementRef, Task> java) =>
        ctx.RunStepAsync(tool, args, async step =>
        {
            var r = ctx.GetRef(refId);
            step.Resolved = r.Locator;
            switch (r.Kind)
            {
                case RefKind.Uia: await uia(r); break;
                case RefKind.Dom: await dom(r); break;
                case RefKind.Java: await java(r); break;
                default: throw new NotSupportedException($"{tool} needs an element ref (u{{n}}, j{{n}} or w{{n}}), got '{refId}'.");
            }
            return new { @ref = r.Id };
        });
}
