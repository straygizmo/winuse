using Microsoft.Win32;

namespace WinUse.Core.Java;

/// <summary>Finds <c>WindowsAccessBridge-64.dll</c>: configured path, exe directory, JAVA_HOME, registered JDK/JRE homes, PATH.</summary>
public static class JabDllLocator
{
    public const string FileName = "WindowsAccessBridge-64.dll";
    /// <summary>The client DLL for 32-bit assistive technologies (ships with 32-bit JREs). Not loadable by the 64-bit winuse.</summary>
    public const string FileName32 = "WindowsAccessBridge-32.dll";
    /// <summary>The legacy client DLL (32-bit Windows). Not loadable by the 64-bit winuse.</summary>
    public const string FileNameLegacy = "WindowsAccessBridge.dll";

    /// <returns>The first existing candidate, or null. <paramref name="searched"/> lists every place tried, for doctor.</returns>
    /// <param name="thirtyTwoBit">
    /// When nothing was found: the 32-bit client DLLs (<see cref="FileName32"/>, <see cref="FileNameLegacy"/>) that exist in the
    /// searched directories or in System32/SysWOW64. A machine with only a 32-bit Java has these and not the 64-bit one;
    /// doctor explains why they do not help.
    /// </param>
    public static string? Locate(string? configuredPath, string exeDirectory, Func<string, string?> env,
        Func<string, bool> fileExists, Func<IEnumerable<string>> registryJavaHomes,
        out IReadOnlyList<string> searched, out IReadOnlyList<string> thirtyTwoBit)
    {
        var tried = new List<string>();
        var dirs = new List<string>();
        string? found = null;

        void Try(string label, string? path)
        {
            tried.Add(label);
            if (path is null) return;
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir) dirs.Add(dir);
            if (found is null && fileExists(path)) found = path;
        }

        Try(configuredPath is null ? "WINUSE_JAB_DLL (not set)" : $"WINUSE_JAB_DLL={configuredPath}", configuredPath);
        Try(exeDirectory, Path.Combine(exeDirectory, FileName));
        var javaHome = env("JAVA_HOME");
        Try(javaHome is null ? "JAVA_HOME (not set)" : $"JAVA_HOME={javaHome}",
            javaHome is null ? null : Path.Combine(javaHome, "bin", FileName));
        var homes = new List<string>();
        try { homes.AddRange(registryJavaHomes()); } catch { }
        tried.Add(homes.Count == 0 ? "registry (no JavaSoft entries)" : "registry: " + string.Join(", ", homes));
        foreach (var h in homes)
        {
            var bin = Path.Combine(h, "bin");
            dirs.Add(bin);
            var p = Path.Combine(bin, FileName);
            if (found is null && fileExists(p)) found = p;
        }
        tried.Add("PATH");
        searched = tried;

        var other = new List<string>();
        if (found is null)
        {
            var systemRoot = env("SystemRoot");
            if (systemRoot is not null)
            {
                dirs.Add(Path.Combine(systemRoot, "System32"));
                dirs.Add(Path.Combine(systemRoot, "SysWOW64"));
            }
            foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var name in new[] { FileName32, FileNameLegacy })
                {
                    var p = Path.Combine(dir, name);
                    if (fileExists(p)) other.Add(p);
                }
        }
        thirtyTwoBit = other;
        return found;
    }

    /// <summary>The reason reported when <see cref="Locate"/> found nothing: where it looked, the 32-bit DLLs that exist, and how to fix it.</summary>
    public static string NotFoundReason(IReadOnlyList<string> searched, IReadOnlyList<string> thirtyTwoBit)
    {
        var reason = $"not found (searched: {string.Join("; ", searched)}).";
        if (thirtyTwoBit.Count > 0)
            reason += $" Only 32-bit client DLLs exist ({string.Join("; ", thirtyTwoBit)}); the 64-bit winuse cannot load them (the Java app itself may stay 32-bit).";
        reason += $" {FileName} ships next to winuse.exe in releases: restore it there (scripts/fetch-jab.ps1 fetches it from Temurin) or point WINUSE_JAB_DLL at one.";
        return reason;
    }

    /// <summary>JavaHome values registered under HKLM\SOFTWARE\JavaSoft (JDK, Java Runtime Environment, JRE).</summary>
    public static IEnumerable<string> RegistryJavaHomes()
    {
        var result = new List<string>();
        foreach (var family in new[] { "JDK", "Java Runtime Environment", "JRE", "Java Development Kit" })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\JavaSoft\" + family);
                if (key is null) continue;
                foreach (var version in key.GetSubKeyNames())
                {
                    using var vk = key.OpenSubKey(version);
                    if (vk?.GetValue("JavaHome") is string home && home.Length > 0 && !result.Contains(home)) result.Add(home);
                }
            }
            catch { }
        }
        return result;
    }
}
