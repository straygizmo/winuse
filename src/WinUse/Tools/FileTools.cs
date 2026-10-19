using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WinUse.Tools;

/// <summary>File tools; every path must be under the configured allowed roots (WINUSE_ALLOWED_ROOTS).</summary>
[McpServerToolType]
public sealed class FileTools
{
    [McpServerTool(Name = "file_exists", ReadOnly = true)]
    [Description("Check whether a file or directory exists (path must be under the allowed roots).")]
    public static Task<CallToolResult> FileExists(ToolContext ctx, [Description("File or directory path.")] string path) =>
        Sync(ctx, "file_exists", ToolContext.Args(("path", path)), () => new { exists = ctx.Files.Exists(path) });

    [McpServerTool(Name = "read_text", ReadOnly = true)]
    [Description("Read a UTF-8 text file (path must be under the allowed roots).")]
    public static Task<CallToolResult> ReadText(ToolContext ctx, [Description("File path.")] string path) =>
        Sync(ctx, "read_text", ToolContext.Args(("path", path)), () => new { text = ctx.Files.ReadText(path) });

    [McpServerTool(Name = "write_text")]
    [Description("Write a UTF-8 text file, replacing it and creating parent directories (path must be under the allowed roots).")]
    public static Task<CallToolResult> WriteText(ToolContext ctx, [Description("File path.")] string path, [Description("Content.")] string text) =>
        Sync(ctx, "write_text", ToolContext.Args(("path", path), ("text", text)), () =>
        {
            ctx.Files.WriteText(path, text);
            return new { path };
        });

    [McpServerTool(Name = "append_text")]
    [Description("Append UTF-8 text to a file, creating it when missing (path must be under the allowed roots).")]
    public static Task<CallToolResult> AppendText(ToolContext ctx, [Description("File path.")] string path, [Description("Text to append.")] string text) =>
        Sync(ctx, "append_text", ToolContext.Args(("path", path), ("text", text)), () =>
        {
            ctx.Files.AppendText(path, text);
            return new { path };
        });

    [McpServerTool(Name = "copy")]
    [Description("Copy a file or directory (both paths under the allowed roots).")]
    public static Task<CallToolResult> Copy(
        ToolContext ctx,
        [Description("Source path.")] string src,
        [Description("Destination path.")] string dst,
        [Description("Overwrite existing files.")] bool overwrite = false) =>
        Sync(ctx, "copy", ToolContext.Args(("src", src), ("dst", dst), ("overwrite", overwrite)), () =>
        {
            ctx.Files.Copy(src, dst, overwrite);
            return new { src, dst };
        });

    [McpServerTool(Name = "move", Destructive = true)]
    [Description("Move or rename a file or directory (both paths under the allowed roots).")]
    public static Task<CallToolResult> Move(ToolContext ctx, [Description("Source path.")] string src, [Description("Destination path.")] string dst) =>
        Sync(ctx, "move", ToolContext.Args(("src", src), ("dst", dst)), () =>
        {
            ctx.Files.Move(src, dst);
            return new { src, dst };
        });

    [McpServerTool(Name = "delete", Destructive = true)]
    [Description("Delete a file, or a directory recursively (path must be under the allowed roots).")]
    public static Task<CallToolResult> Delete(ToolContext ctx, [Description("File or directory path.")] string path) =>
        Sync(ctx, "delete", ToolContext.Args(("path", path)), () =>
        {
            ctx.Files.Delete(path);
            return new { path };
        });

    [McpServerTool(Name = "list_dir", ReadOnly = true)]
    [Description("List a directory: sub-directories first, then files, as full paths.")]
    public static Task<CallToolResult> ListDir(ToolContext ctx, [Description("Directory path.")] string path) =>
        Sync(ctx, "list_dir", ToolContext.Args(("path", path)), () => new { entries = ctx.Files.ListDir(path) });

    [McpServerTool(Name = "zip")]
    [Description("Create a zip archive from a directory.")]
    public static Task<CallToolResult> Zip(ToolContext ctx, [Description("Directory to archive.")] string dir, [Description("Zip file to create.")] string zip_path) =>
        Sync(ctx, "zip", ToolContext.Args(("dir", dir), ("zip_path", zip_path)), () =>
        {
            ctx.Files.Zip(dir, zip_path);
            return new { zip_path };
        });

    [McpServerTool(Name = "unzip")]
    [Description("Extract a zip archive into a directory (entries escaping the directory are rejected).")]
    public static Task<CallToolResult> Unzip(ToolContext ctx, [Description("Zip file.")] string zip_path, [Description("Target directory.")] string dir) =>
        Sync(ctx, "unzip", ToolContext.Args(("zip_path", zip_path), ("dir", dir)), () =>
        {
            ctx.Files.Unzip(zip_path, dir);
            return new { dir };
        });

    private static Task<CallToolResult> Sync(ToolContext ctx, string tool, System.Text.Json.Nodes.JsonObject args, Func<object> body) =>
        ctx.RunStepAsync(tool, args, _ => Task.FromResult(body()));
}
