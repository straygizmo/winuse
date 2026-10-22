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
        Assert.Null(info.LoadedBridgeModule);
        Assert.False(info.IsLegacyBridge);
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
        Assert.Equal(@"jvm: C:\app\jre\bin\client\jvm.dll (1.8.0_392), bridge not loaded, JavaAccessBridge-32.dll or legacy present, access-bridge jar missing"
            + " - this JRE has no Access Bridge: apps started by process_run with WINUSE_JAVA_ACCESSIBILITY=on fail with AWTError; set it to off",
            new JavaProcessInfo(true, @"C:\app\jre\bin\client\jvm.dll", "1.8.0_392", true, false, null).Describe());
        Assert.Equal(@"jvm: C:\jdk\bin\server\jvm.dll (21), JavaAccessBridge-64.dll loaded, JavaAccessBridge-64.dll or legacy present, access-bridge jar present",
            new JavaProcessInfo(false, @"C:\jdk\bin\server\jvm.dll", "21", true, true, null, "JavaAccessBridge-64.dll").Describe());
    }

    [Fact]
    public void Describe_ExplainsTheLegacyBridge()
    {
        var info = new JavaProcessInfo(true, @"C:\jre7\bin\client\jvm.dll", "7.0.450", true, true, null, "JavaAccessBridge.dll");
        Assert.True(info.IsLegacyBridge);
        var line = info.Describe();
        Assert.Contains("JavaAccessBridge.dll loaded (legacy bridge", line);
        Assert.Contains("64-bit winuse cannot read its tree", line);
        Assert.Contains("JavaAccessBridge-32.dll and access-bridge-32.jar", line);
        Assert.DoesNotContain("AWTError", line);
    }
}
