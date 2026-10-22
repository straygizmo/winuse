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
        Assert.Null(info.Layout);
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
        Assert.Equal(@"jvm: C:\jdk\bin\server\jvm.dll (21), javaaccessbridge.dll loaded (wide wire format), JavaAccessBridge-64.dll or legacy present, access-bridge jar present",
            new JavaProcessInfo(false, @"C:\jdk\bin\server\jvm.dll", "21", true, true, null, "javaaccessbridge.dll", Modular: true).Describe());
    }

    /// <summary>Java 9+ ships the wide-format bridge under the suffix-less, lowercase name; it must not be taken for the legacy one.</summary>
    [Fact]
    public void Layout_Java9Plus_LowercaseModule_IsWide()
    {
        var info = new JavaProcessInfo(false, @"C:\jdk\bin\server\jvm.dll", "21", true, true, null, "javaaccessbridge.dll", Modular: true);
        Assert.False(info.IsLegacyBridge);
        Assert.Equal(JabLayout.Wide, info.Layout);
        Assert.Contains("javaaccessbridge.dll loaded (wide wire format)", info.Describe());
    }

    [Fact]
    public void Layout_Java8_LegacyModule_IsLegacy()
    {
        var info = new JavaProcessInfo(true, @"C:\jre\bin\client\jvm.dll", "1.8.0_472", true, true, null, "JavaAccessBridge.dll", Modular: false);
        Assert.True(info.IsLegacyBridge);
        Assert.Equal(JabLayout.Legacy, info.Layout);
        Assert.Contains("JavaAccessBridge.dll loaded (legacy wire format)", info.Describe());
        Assert.DoesNotContain("AWTError", info.Describe());
    }

    [Fact]
    public void Layout_Java8_Suffix32Module_IsWide()
    {
        var info = new JavaProcessInfo(true, @"C:\jre\bin\client\jvm.dll", "1.8.0_472", true, true, null, "JavaAccessBridge-32.dll");
        Assert.False(info.IsLegacyBridge);
        Assert.Equal(JabLayout.Wide, info.Layout);
    }

    [Fact]
    public void Layout_NoBridgeLoaded_IsNull()
    {
        Assert.Null(new JavaProcessInfo(true, @"C:\jre\bin\client\jvm.dll", "1.8.0_472", true, true, null).Layout);
    }
}
