using System.Reflection;
using System.Runtime.CompilerServices;
using NetCat.UI;

// Native fixtures share Windows process scheduling and probe/release ephemeral
// ports. Run collections sequentially; preserve every assertion and deadline.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace NetCat.Tests;

internal static class TestIsolation
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Unit fixtures must never start application monitors against the developer's VPN.
        typeof(App).GetProperty(nameof(App.IsSmoke))!.SetValue(null, true);
    }
}
