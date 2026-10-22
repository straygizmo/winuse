using Microsoft.Win32;

namespace WinUse.Core.Java;

/// <summary>Finds <c>WindowsAccessBridge-64.dll</c>: configured path, exe directory, JAVA_HOME, registered JDK/JRE homes, PATH.</summary>
public static class JabDllLocator
{
    public const string FileName = "WindowsAccessBridge-64.dll";

    /// <returns>The first existing candidate, or null. <paramref name="searched"/> lists every place tried, for doctor.</returns>
    public static string? Locate(string? configuredPath, string exeDirectory, Func<string, string?> env,
        Func<string, bool> fileExists, Func<IEnumerable<string>> registryJavaHomes, out IReadOnlyList<string> searched)
    {
        var tried = new List<string>();
        string? found = null;

        void Try(string label, string? path)
        {
            tried.Add(label);
            if (found is null && path is not null && fileExists(path)) found = path;
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
            var p = Path.Combine(h, "bin", FileName);
            if (found is null && fileExists(p)) found = p;
        }
        tried.Add("PATH");
        searched = tried;
        return found;
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
