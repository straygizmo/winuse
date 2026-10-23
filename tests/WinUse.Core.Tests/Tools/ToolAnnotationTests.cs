using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WinUse.Core.Tests.Grounding;
using WinUse.Tools;

namespace WinUse.Core.Tests.Tools;

/// <summary>
/// Every tool must declare all four MCP annotation hints explicitly, so hosts that warn before destructive
/// or open-world calls get a real answer for each tool instead of the SDK's silent default.
/// </summary>
public class ToolAnnotationTests
{
    private static IReadOnlyDictionary<string, ToolAnnotations?> Annotations()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => ToolContext.Create(TestConfig.WithRoots(Path.GetTempPath())));   // never resolved: only its registration matters
        using var sp = services.BuildServiceProvider();
        var map = new Dictionary<string, ToolAnnotations?>(StringComparer.Ordinal);
        foreach (var type in typeof(ToolDispatcher).Assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null))
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.GetCustomAttribute<McpServerToolAttribute>() is { } attr)
                    map[attr.Name ?? m.Name] = McpServerTool.Create(m, options: new() { Services = sp }).ProtocolTool.Annotations;
        return map;
    }

    [Fact]
    public void EveryToolDeclaresAllFourHints()
    {
        var missing = Annotations()
            .Where(kv => kv.Value is not { ReadOnlyHint: not null, DestructiveHint: not null, IdempotentHint: not null, OpenWorldHint: not null })
            .Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "tools missing an annotation hint: " + string.Join(", ", missing));
    }

    [Fact]
    public void ReadOnlyToolsAreNeverDestructive()
    {
        var wrong = Annotations()
            .Where(kv => kv.Value is { ReadOnlyHint: true, DestructiveHint: true })
            .Select(kv => kv.Key).ToList();
        Assert.Empty(wrong);
    }

    [Theory]
    [InlineData("screenshot", true, false, true, true)]
    [InlineData("click", false, true, false, true)]
    [InlineData("delete", false, true, true, false)]
    [InlineData("read_text", true, false, true, false)]
    [InlineData("process_kill", false, true, false, true)]
    [InlineData("scenario_run", false, true, false, true)]
    public void RepresentativeToolsCarryTheExpectedHints(string tool, bool readOnly, bool destructive, bool idempotent, bool openWorld)
    {
        var a = Annotations()[tool];
        Assert.NotNull(a);
        Assert.Equal((readOnly, destructive, idempotent, openWorld), (a.ReadOnlyHint, a.DestructiveHint, a.IdempotentHint, a.OpenWorldHint));
    }
}
