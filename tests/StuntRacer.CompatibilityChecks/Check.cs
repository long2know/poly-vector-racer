namespace StuntRacer.CompatibilityChecks;

internal sealed record NativeCheck(string Name, Action Run);

internal static class Check
{
    public static void Require(bool condition, string context)
    {
        if (!condition)
            throw new InvalidOperationException(context);
    }

    public static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    public static void Near(double expected, double actual, string context, double tolerance = 1e-10)
    {
        Require(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance,
            $"{context}: expected {expected:R}, got {actual:R}");
    }

    public static TException Throws<TException>(Action action, string context) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }
        throw new InvalidOperationException($"{context}: expected {typeof(TException).Name}");
    }

    public static void Fields(System.Text.Json.JsonElement element, string context, params string[] expected)
    {
        var actual = element.EnumerateObject().Select(property => property.Name).Order().ToArray();
        Require(actual.SequenceEqual(expected.Order()),
            $"{context}: fields [{string.Join(", ", actual)}], expected [{string.Join(", ", expected.Order())}]");
    }
}
