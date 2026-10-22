using System.Diagnostics;
using System.Runtime.InteropServices;
using WinUse.Core.Windows;

namespace WinUse.Core.Java;

/// <summary>
/// What <c>winuse doctor</c> reports about a process that owns a Java window: its bitness, the JRE it runs
/// (from the loaded <c>jvm.dll</c>) and whether that JRE ships the Java side of the Access Bridge.
/// </summary>
public sealed record JavaProcessInfo(bool? Is32Bit, string? JvmDllPath, string? JvmVersion, bool? JavaBridgeDllPresent,
    bool? AccessBridgeJarPresent, string? Error)
{
    public static JavaProcessInfo Inspect(int pid)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_VM_READ, false, (uint)pid);
        if (h == 0)
            return new JavaProcessInfo(null, null, null, null, null,
                $"not accessible (error {Marshal.GetLastPInvokeError()}; running elevated or as another user?)");
        try
        {
            bool? is32 = NativeMethods.IsWow64Process(h, out var wow) ? wow : null;
            var jvm = Modules(h).FirstOrDefault(m => string.Equals(Path.GetFileName(m), "jvm.dll", StringComparison.OrdinalIgnoreCase));
            if (jvm is null) return new JavaProcessInfo(is32, null, null, null, null, null);

            string? version = null;
            try { version = FileVersionInfo.GetVersionInfo(jvm).ProductVersion; } catch { }
            // <jre>\bin\{server|client}\jvm.dll
            var bin = Path.GetDirectoryName(Path.GetDirectoryName(jvm));
            var jre = bin is null ? null : Path.GetDirectoryName(bin);
            bool? bridgeDll = null, jar = null;
            if (bin is not null && jre is not null)
            {
                var suffix = is32 == true ? "32" : "64";
                bridgeDll = File.Exists(Path.Combine(bin, $"JavaAccessBridge-{suffix}.dll"));
                jar = File.Exists(Path.Combine(jre, "lib", "modules")) // Java 9+: jdk.accessibility is a module
                      || File.Exists(Path.Combine(jre, "lib", "ext", $"access-bridge-{suffix}.jar"));
            }
            return new JavaProcessInfo(is32, jvm, version, bridgeDll, jar, null);
        }
        catch (Exception ex)
        {
            return new JavaProcessInfo(null, null, null, null, null, $"not accessible ({ex.Message})");
        }
        finally
        {
            NativeMethods.CloseHandle(h);
        }
    }

    /// <summary>Full paths of the modules loaded in the process (32-bit processes included, from a 64-bit caller).</summary>
    private static List<string> Modules(nint process)
    {
        var result = new List<string>();
        if (!NativeMethods.EnumProcessModulesEx(process, null, 0, out var needed, NativeMethods.LIST_MODULES_ALL)) return result;
        var handles = new nint[needed / nint.Size + 16];
        if (!NativeMethods.EnumProcessModulesEx(process, handles, (uint)(handles.Length * nint.Size), out needed, NativeMethods.LIST_MODULES_ALL)) return result;
        var count = Math.Min(handles.Length, (int)(needed / nint.Size));
        var buf = new char[1024];
        for (var i = 0; i < count; i++)
        {
            var n = NativeMethods.GetModuleFileNameExW(process, handles[i], buf, (uint)buf.Length);
            if (n > 0) result.Add(new string(buf, 0, (int)n));
        }
        return result;
    }

    /// <summary>One doctor line.</summary>
    public string Describe()
    {
        if (Error is not null) return $"jvm: ({Error})";
        if (JvmDllPath is null) return "jvm: not loaded";
        var suffix = Is32Bit == true ? "32" : "64";
        var line = $"jvm: {JvmDllPath} ({JvmVersion ?? "unknown version"}), JavaAccessBridge-{suffix}.dll {Presence(JavaBridgeDllPresent)}, access-bridge jar {Presence(AccessBridgeJarPresent)}";
        if (JavaBridgeDllPresent == false || AccessBridgeJarPresent == false)
            line += " - this JRE has no Access Bridge: apps started by process_run with WINUSE_JAVA_ACCESSIBILITY=on fail with AWTError; set it to off";
        return line;
    }

    private static string Presence(bool? present) => present switch { true => "present", false => "missing", null => "unknown" };
}
