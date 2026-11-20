using System.Diagnostics;

namespace StuntRacer.CompatibilityChecks;

internal static class Program
{
    public static int Main()
    {
        NativeCheck[] checks =
        [
            .. LayoutChecks.All(),
            .. CommonChecks.All(),
            .. TrackChecks.All(),
            .. GeometryFactoryChecks.All(),
            .. ArithmeticChecks.All(),
            .. TraceChecks.All(),
            .. CliChecks.All()
        ];
        var failures = 0;
        var watch = Stopwatch.StartNew();
        foreach (var check in checks)
        {
            try
            {
                check.Run();
                Console.WriteLine($"PASS {check.Name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {check.Name}: {exception}");
            }
        }

        Console.WriteLine($"{checks.Length - failures}/{checks.Length} synthetic compatibility checks passed " +
                          $"in {watch.Elapsed.TotalSeconds:F2}s; {PrivateFixture.InvocationCount} actual CLI invocations; " +
                          "no original-game qualification.");
        return failures == 0 ? 0 : 1;
    }
}
