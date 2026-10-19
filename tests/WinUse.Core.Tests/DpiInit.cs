using System.Runtime.CompilerServices;
using WinUse.Core.Windows;

namespace WinUse.Core.Tests;

/// <summary>The testhost ignores application manifests, so opt the test process into per-monitor DPI awareness here.</summary>
internal static class DpiInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        try { NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
    }
}
