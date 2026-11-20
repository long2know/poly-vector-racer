using System.Diagnostics;
using StuntRacer.CompatibilityChecks;

namespace StuntRacer.SourceRuntimeChecks;

internal static class Program
{
    public static int Main(string[] arguments)
    {
        try
        {
            var options = RunOptions.Parse(arguments);
            var fixture = options.PackagePath is null ? null : new PackageFixture(options.PackagePath);
            return Run([
                .. MathChecks.All(),
                .. ControlChecks.All(),
                .. BridgeChecks.All(),
                .. options.ComponentOraclePath is null ? [] : OracleChecks.All(options.ComponentOraclePath),
                .. fixture is null ? [] : PackageChecks.All(fixture),
                .. fixture is null ? [] : SessionChecks.All(fixture.Package)
            ]);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int Run(NativeCheck[] checks)
    {
        if (checks.Length == 0)
            throw new InvalidOperationException("An empty check inventory is not a passing runtime receipt.");
        var failures = 0;
        var timer = Stopwatch.StartNew();
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
        Console.WriteLine($"{checks.Length - failures}/{checks.Length} source runtime checks passed in " +
                          $"{timer.Elapsed.TotalSeconds:F2}s; reference candidate, not stock certification.");
        return failures == 0 ? 0 : 1;
    }
}
