using System.IO.Compression;
using WinUse.Core.Files;

namespace WinUse.Core.Tests.Files;

public sealed class FileServiceTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory().FullName;
    FileService Svc => new(TestConfig.WithRoots(_root));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void WriteAndRead_InsideAllowedRoot_Works()
    {
        var svc = Svc;
        var f = Path.Combine(_root, "a.txt");
        svc.WriteText(f, "hi"); svc.AppendText(f, "!");
        Assert.Equal("hi!", svc.ReadText(f));
    }

    [Theory]
    [InlineData("src")]
    [InlineData(@"src\sub")]
    [InlineData(@"src\sub\deeper\")]
    public void Copy_DirectoryIntoItself_Throws(string dst)
    {
        var svc = Svc;
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "a.txt"), "a");
        var ex = Assert.Throws<ArgumentException>(() => svc.Copy(src, Path.Combine(_root, dst), true));
        Assert.StartsWith("Cannot copy a directory into itself", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(src, "sub", "src")));
    }

    [Fact]
    public void Copy_DirectoryToSiblingWithSamePrefix_Works()
    {
        var svc = Svc;
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "a");
        svc.Copy(src, Path.Combine(_root, "src2"), false);
        Assert.Equal("a", File.ReadAllText(Path.Combine(_root, "src2", "a.txt")));
    }

    [Fact]
    public void Delete_OutsideAllowedRoot_Throws()
    {
        Assert.Throws<UnauthorizedAccessException>(() => Svc.Delete(@"C:\Windows\notepad.exe"));
    }

    [Fact]
    public void TraversalOutsideRoot_Throws()
    {
        Assert.Throws<UnauthorizedAccessException>(() => Svc.Exists(Path.Combine(_root, "..", "x.txt")));
    }

    [Fact]
    public void Copy_Move_Delete_RoundTrip()
    {
        var svc = Svc;
        var a = Path.Combine(_root, "a.txt");
        var b = Path.Combine(_root, "sub", "b.txt");
        var c = Path.Combine(_root, "c.txt");
        svc.WriteText(a, "x");
        svc.WriteText(b, "y");
        svc.Copy(a, c, false);
        Assert.Equal("x", svc.ReadText(c));
        Assert.Throws<IOException>(() => svc.Copy(a, c, false));
        svc.Copy(b, c, true);
        Assert.Equal("y", svc.ReadText(c));
        var d = Path.Combine(_root, "moved.txt");
        svc.Move(c, d);
        Assert.False(svc.Exists(c));
        Assert.True(svc.Exists(d));
        svc.Copy(Path.Combine(_root, "sub"), Path.Combine(_root, "sub2"), false);
        Assert.Equal("y", svc.ReadText(Path.Combine(_root, "sub2", "b.txt")));
        svc.Delete(d);
        svc.Delete(Path.Combine(_root, "sub2"));
        Assert.False(svc.Exists(d));
        Assert.False(svc.Exists(Path.Combine(_root, "sub2")));
        Assert.Throws<UnauthorizedAccessException>(() => svc.Copy(a, @"C:\Windows\x.txt", true));
    }

    [Fact]
    public void ListDir_ReturnsDirectoriesThenFiles()
    {
        var svc = Svc;
        svc.WriteText(Path.Combine(_root, "b.txt"), "");
        svc.WriteText(Path.Combine(_root, "A.txt"), "");
        svc.WriteText(Path.Combine(_root, "zdir", "q.txt"), "");
        svc.WriteText(Path.Combine(_root, "Adir", "q.txt"), "");
        var l = svc.ListDir(_root);
        Assert.Equal(new[]
        {
            Path.Combine(_root, "Adir"), Path.Combine(_root, "zdir"),
            Path.Combine(_root, "A.txt"), Path.Combine(_root, "b.txt"),
        }, l);
    }

    [Fact]
    public void Zip_Unzip_RoundTrip()
    {
        var svc = Svc;
        svc.WriteText(Path.Combine(_root, "src", "a.txt"), "A");
        svc.WriteText(Path.Combine(_root, "src", "n", "b.txt"), "B");
        var zip = Path.Combine(_root, "out.zip");
        svc.Zip(Path.Combine(_root, "src"), zip);
        svc.Unzip(zip, Path.Combine(_root, "dst"));
        Assert.Equal("A", svc.ReadText(Path.Combine(_root, "dst", "a.txt")));
        Assert.Equal("B", svc.ReadText(Path.Combine(_root, "dst", "n", "b.txt")));
    }

    [Theory]
    [InlineData(@"..\evil.txt")]
    [InlineData("../evil.txt")]
    public void Unzip_RejectsZipSlipEntry(string entryName)
    {
        var svc = Svc;
        var zip = Path.Combine(_root, "bad.zip");
        using (var fs = File.Create(zip))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var w = new StreamWriter(za.CreateEntry(entryName).Open())) w.Write("evil");
        var dir = Path.Combine(_root, "dst");
        Assert.Throws<UnauthorizedAccessException>(() => svc.Unzip(zip, dir));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public void EnsureAllowed_AcceptsRootItself_AndRejectsSiblingWithSamePrefix()
    {
        string[] roots = [@"C:\tmp\a\"];
        Assert.Equal(@"C:\tmp\a", FileService.EnsureAllowed(@"C:\tmp\a", roots));
        Assert.Equal(@"C:\TMP\a\x.txt", FileService.EnsureAllowed(@"C:\TMP\a\x.txt", roots));
        Assert.Throws<UnauthorizedAccessException>(() => FileService.EnsureAllowed(@"C:\tmp\ab\x.txt", roots));
    }
}
