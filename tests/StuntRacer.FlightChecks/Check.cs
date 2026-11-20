namespace StuntRacer.FlightChecks;

internal static class Check
{
    public static void That(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    public static void Near(double actual, double expected, double tolerance, string message) =>
        That(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
            $"{message}: expected {expected:G17} ± {tolerance:G6}, got {actual:G17}");

    public static void Throws<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"{message}: expected {typeof(T).Name}");
    }
}
