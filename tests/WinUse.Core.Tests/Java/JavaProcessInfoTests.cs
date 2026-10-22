using WinUse.Core.Java;

namespace WinUse.Core.Tests.Java;

public class JavaProcessInfoTests
{
    [Fact]
    public void Inspect_OwnProcess_Is64BitWithoutJvm()
    {
        var info = JavaProcessInfo.Inspect(Environment.ProcessId);
        Assert.Null(info.Error);
        Assert.False(info.Is32Bit);
        Assert.Null(info.JvmDllPath);
        Assert.Null(info.JavaBridgeDllPresent);
    }

    [Fact]
    public void Inspect_SystemProcess_ReportsNotAccessible()
    {
        var info = JavaProcessInfo.Inspect(4);
        Assert.NotNull(info.Error);
        Assert.Contains("not accessible", info.Error);
    }

    [Fact]
    public void Describe_FormatsTheJvmLine()
    {
        Assert.Equal("jvm: (not accessible: x)", new JavaProcessInfo(null, null, null, null, null, "not accessible: x").Describe());
        Assert.Equal("jvm: not loaded", new JavaProcessInfo(false, null, null, null, null, null).Describe());
        Assert.Equal(@"jvm: C:\app\jre\bin\client\jvm.dll (1.8.0_392), JavaAccessBridge-32.dll present, access-bridge jar missing",
            new JavaProcessInfo(true, @"C:\app\jre\bin\client\jvm.dll", "1.8.0_392", true, false, null).Describe());
    }
}
