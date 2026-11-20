namespace StuntRacer.SourceRuntimeChecks;

internal sealed record RunOptions(string? PackagePath, string? ComponentOraclePath)
{
    public static RunOptions Parse(string[] arguments)
    {
        string? package = null;
        string? oracle = null;
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 == arguments.Length || string.IsNullOrWhiteSpace(arguments[index + 1]))
                throw new ArgumentException("Options require explicit local file paths.");
            var value = Path.GetFullPath(arguments[index + 1]);
            if (!File.Exists(value))
                throw new FileNotFoundException("Explicit evidence input is missing; it cannot be silently skipped.", value);
            switch (arguments[index])
            {
                case "--package" when package is null:
                    package = value;
                    break;
                case "--component-oracle" when oracle is null:
                    oracle = value;
                    break;
                default:
                    throw new ArgumentException($"Unknown or duplicate test option '{arguments[index]}'.");
            }
        }
        return new(package, oracle);
    }
}
