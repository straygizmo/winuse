using System.IO.Compression;
using System.Text;
using WinUse.Core.Config;

namespace WinUse.Core.Files;

public sealed class FileService(WinUseConfig cfg)
{
    static readonly UTF8Encoding Utf8 = new(false);
    readonly IReadOnlyList<string> _roots = cfg.AllowedRoots;

    internal static string EnsureAllowed(string path, IReadOnlyList<string> roots)
    {
        var full = Path.GetFullPath(path);
        foreach (var r in roots)
        {
            var root = TrimSep(Path.GetFullPath(r));
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return full;
        }
        throw new UnauthorizedAccessException($"Path outside allowed roots: {full}");
    }

    static string TrimSep(string p)
    {
        var t = p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // keep drive roots like "C:\" intact
        return t.Length == 2 && t[1] == ':' ? p : t;
    }

    string G(string p) => EnsureAllowed(p, _roots);

    public bool Exists(string path)
    {
        var p = G(path);
        return File.Exists(p) || Directory.Exists(p);
    }

    public string ReadText(string path) => File.ReadAllText(G(path), Utf8);

    public void WriteText(string path, string text)
    {
        var p = G(path);
        EnsureParent(p);
        File.WriteAllText(p, text, Utf8);
    }

    public void AppendText(string path, string text)
    {
        var p = G(path);
        EnsureParent(p);
        File.AppendAllText(p, text, Utf8);
    }

    static void EnsureParent(string p)
    {
        var d = Path.GetDirectoryName(p);
        if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
    }

    public void Copy(string src, string dst, bool overwrite)
    {
        var s = G(src);
        var d = G(dst);
        if (Directory.Exists(s)) CopyDir(s, d, overwrite);
        else
        {
            EnsureParent(d);
            File.Copy(s, d, overwrite);
        }
    }

    static void CopyDir(string s, string d, bool overwrite)
    {
        Directory.CreateDirectory(d);
        foreach (var f in Directory.GetFiles(s))
            File.Copy(f, Path.Combine(d, Path.GetFileName(f)), overwrite);
        foreach (var sub in Directory.GetDirectories(s))
            CopyDir(sub, Path.Combine(d, Path.GetFileName(sub)), overwrite);
    }

    public void Move(string src, string dst)
    {
        var s = G(src);
        var d = G(dst);
        EnsureParent(d);
        if (Directory.Exists(s)) Directory.Move(s, d);
        else File.Move(s, d);
    }

    public void Delete(string path)
    {
        var p = G(path);
        if (Directory.Exists(p)) Directory.Delete(p, true);
        else File.Delete(p);
    }

    public IReadOnlyList<string> ListDir(string path)
    {
        var p = G(path);
        var dirs = Directory.GetDirectories(p).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        var files = Directory.GetFiles(p).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        return dirs.Concat(files).ToList();
    }

    public void Zip(string dir, string zipPath)
    {
        var d = G(dir);
        var z = G(zipPath);
        EnsureParent(z);
        ZipFile.CreateFromDirectory(d, z);
    }

    public void Unzip(string zipPath, string dir)
    {
        var z = G(zipPath);
        var d = G(dir);
        using var za = ZipFile.OpenRead(z);
        var targets = new List<(ZipArchiveEntry e, string path)>();
        foreach (var e in za.Entries)
            targets.Add((e, EnsureAllowed(Path.Combine(d, e.FullName), [d])));
        Directory.CreateDirectory(d);
        foreach (var (e, path) in targets)
        {
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith((char)92))
            {
                Directory.CreateDirectory(path);
                continue;
            }
            EnsureParent(path);
            e.ExtractToFile(path, true);
        }
    }
}
