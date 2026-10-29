using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WinUse.Core.Input;

namespace WinUse.Tools;

/// <summary>
/// The clipboard: the one reliable channel into rich editors (CodeMirror, Monaco, WebView2 text areas) that UI
/// Automation cannot read or fill.
/// </summary>
[McpServerToolType]
public sealed class ClipboardTools
{
    /// <summary>Wait after ctrl+v before the previous clipboard text is restored, so the target has read the clipboard.</summary>
    public static readonly TimeSpan PasteSettle = TimeSpan.FromMilliseconds(300);

    private const string WindowArg = "Window ref (win{n}) or title regex to activate before pasting; fails when it does not exist or cannot take the foreground.";

    [McpServerTool(Name = "clipboard_get", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read the clipboard: its text (null when it holds no text) and whether it holds an image. Copy an editor's content with press_keys \"ctrl+a ctrl+c\" first to read what UIA cannot.")]
    public static Task<CallToolResult> ClipboardGet(ToolContext ctx) =>
        ctx.RunStepAsync("clipboard_get", ToolContext.Args(), async _ =>
        {
            var c = await ctx.Clipboard.GetAsync();
            return new { text = c.Text, has_image = c.HasImage };
        });

    [McpServerTool(Name = "clipboard_set", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Put text on the clipboard (replacing its content).")]
    public static Task<CallToolResult> ClipboardSet(ToolContext ctx, [Description("The text.")] string text) =>
        ctx.RunStepAsync("clipboard_set", ToolContext.Args(("text", text)), async _ =>
        {
            await ctx.Clipboard.SetTextAsync(text);
            return new { length = text.Length };
        });

    [McpServerTool(Name = "clipboard_set_image", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Put an image file (PNG, JPEG, BMP, GIF; path under the allowed roots) on the clipboard as a bitmap plus PNG, replacing its content.")]
    public static Task<CallToolResult> ClipboardSetImage(ToolContext ctx, [Description("Image file path.")] string path) =>
        ctx.RunStepAsync("clipboard_set_image", ToolContext.Args(("path", path)), async _ =>
        {
            await ctx.Clipboard.SetImageAsync(ctx.Files.ReadBytes(path));
            return new { path };
        });

    [McpServerTool(Name = "paste_text", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Paste text into the focused control through the clipboard (clipboard_set + ctrl+v), then restore the clipboard's previous text. The way to fill rich editors that set_text / type_text cannot.")]
    public static Task<CallToolResult> PasteText(
        ToolContext ctx,
        [Description("The text to paste.")] string text,
        [Description(WindowArg)] string? window = null,
        [Description("Restore the previous clipboard text afterwards (only text is restored; a previous image is lost).")] bool restore = true) =>
        ctx.RunStepAsync("paste_text", ToolContext.Args(("text", text), ("window", window), ("restore", restore)), async _ =>
        {
            var target = await InputTools.ResolveTargetWindowAsync(ctx, window);
            var restored = await PasteAsync(ctx, () => ctx.Clipboard.SetTextAsync(text), restore);
            return new { pasted = text.Length, restored, window = target?.Json };
        });

    [McpServerTool(Name = "paste_image", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Paste an image file into the focused control through the clipboard (clipboard_set_image + ctrl+v); the fastest way to upload a screenshot into a web editor. Restores the clipboard's previous text afterwards.")]
    public static Task<CallToolResult> PasteImage(
        ToolContext ctx,
        [Description("Image file path (under the allowed roots).")] string path,
        [Description(WindowArg)] string? window = null,
        [Description("Restore the previous clipboard text afterwards (only text is restored; a previous image is lost).")] bool restore = true) =>
        ctx.RunStepAsync("paste_image", ToolContext.Args(("path", path), ("window", window), ("restore", restore)), async _ =>
        {
            var bytes = ctx.Files.ReadBytes(path);
            var target = await InputTools.ResolveTargetWindowAsync(ctx, window);
            var restored = await PasteAsync(ctx, () => ctx.Clipboard.SetImageAsync(bytes), restore);
            return new { path, restored, window = target?.Json };
        });

    /// <summary>Sets the clipboard, presses ctrl+v, and puts the previous text back; returns whether anything was restored.</summary>
    private static async Task<bool> PasteAsync(ToolContext ctx, Func<Task> set, bool restore)
    {
        var previous = restore ? (await ctx.Clipboard.GetAsync()).Text : null;
        await set();
        await ctx.Input.PressAsync(KeyChord.Parse("ctrl+v"));
        if (previous is null) return false;
        await Task.Delay(PasteSettle);
        await ctx.Clipboard.SetTextAsync(previous);
        return true;
    }
}
