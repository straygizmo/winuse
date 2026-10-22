using WinUse.Core.Java;

namespace WinUse.Cli;

/// <summary><c>winuse jab enable | disable | status</c>: Java Access Bridge enablement for this user's JVMs.</summary>
public static class JabCommand
{
    public const string Usage = "usage: winuse jab enable | disable | status";

    /// <param name="args">The full argument list (<c>["jab", "enable"]</c>).</param>
    /// <param name="propertiesPath">Overrides <see cref="JabEnablement.DefaultPropertiesPath"/> (tests).</param>
    /// <returns>0 on success, 2 on a usage error.</returns>
    public static int Execute(string[] args, TextWriter output, string? propertiesPath = null)
    {
        var path = propertiesPath ?? JabEnablement.DefaultPropertiesPath;
        switch (args.Length >= 2 ? args[1] : null)
        {
            case "enable":
                JabEnablement.Enable(path);
                output.WriteLine($"Java accessibility enabled for this user ({path}).");
                output.WriteLine("Restart the Java application: the setting is read when a JVM starts.");
                return 0;
            case "disable":
                JabEnablement.Disable(path);
                output.WriteLine($"Java accessibility disabled for this user ({path}).");
                return 0;
            case "status":
                output.WriteLine(JabEnablement.IsEnabled(path)
                    ? $"Java accessibility (user): enabled ({path})"
                    : $"Java accessibility (user): not enabled ({path}; run: winuse jab enable)");
                return 0;
            default:
                output.WriteLine(Usage);
                return 2;
        }
    }
}
