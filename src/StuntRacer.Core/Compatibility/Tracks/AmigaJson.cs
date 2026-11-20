using System.Text.Json;

namespace StuntRacer.Core.Compatibility.Tracks;

internal static class AmigaJson
{
    internal static JsonDocument ReadDocument(Stream stream, int maximumBytes, string description)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int read;
        while ((read = stream.Read(block, 0, block.Length)) != 0)
        {
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException($"{description} exceeds its {maximumBytes}-byte safety limit.");
            buffer.Write(block, 0, read);
        }
        try
        {
            return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{description} is not strict UTF-8 JSON: {exception.Message}", exception);
        }
    }

    internal static Dictionary<string, JsonElement> Object(
        JsonElement element, string path, params string[] requiredNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path} must be an object.");
        var allowed = new HashSet<string>(requiredNames, StringComparer.Ordinal);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new InvalidDataException($"{path} contains unknown field '{property.Name}'.");
            if (!values.TryAdd(property.Name, property.Value))
                throw new InvalidDataException($"{path} contains duplicate field '{property.Name}'.");
        }
        foreach (var name in requiredNames)
            if (!values.ContainsKey(name))
                throw new InvalidDataException($"{path} is missing required field '{name}'.");
        return values;
    }

    internal static long Integer(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
            throw new InvalidDataException($"{path} must be a signed 64-bit JSON integer.");
        return value;
    }

    internal static int Integer(JsonElement element, string path, int minimum, int maximum)
    {
        var value = Integer(element, path);
        if (value < minimum || value > maximum)
            throw new InvalidDataException($"{path} must be in {minimum}..{maximum}.");
        return (int)value;
    }

    internal static long Positive(JsonElement element, string path)
    {
        var value = Integer(element, path);
        if (value <= 0)
            throw new InvalidDataException($"{path} must be a positive integer.");
        return value;
    }

    internal static bool Boolean(JsonElement element, string path)
    {
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"{path} must be a boolean.");
        return element.GetBoolean();
    }

    internal static string Text(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{path} must be a nonempty string.");
        var value = element.GetString()!;
        if (value.Length is < 1 or > 512 || value != value.Trim() || value.Any(char.IsControl))
            throw new InvalidDataException($"{path} must be 1..512 trimmed characters without control characters.");
        return value;
    }

    internal static string Identifier(JsonElement element, string path)
    {
        var value = Text(element, path);
        if (Path.IsPathRooted(value) || value[0] is '/' or '\\' ||
            (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' &&
             value[2] is '/' or '\\') ||
            value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{path} must be a stable identifier, not an absolute private file path.");
        return value;
    }

    internal static string? NullableIdentifier(JsonElement element, string path) =>
        element.ValueKind == JsonValueKind.Null ? null : Identifier(element, path);

    internal static string Choice(JsonElement element, string path, params string[] choices)
    {
        var value = Text(element, path);
        if (!choices.Contains(value, StringComparer.Ordinal))
            throw new InvalidDataException($"{path} has unsupported value '{value}'; expected {string.Join(" or ", choices)}.");
        return value;
    }

    internal static JsonElement.ArrayEnumerator Array(
        JsonElement element, string path, int minimum, int maximum)
    {
        if (element.ValueKind != JsonValueKind.Array ||
            element.GetArrayLength() < minimum || element.GetArrayLength() > maximum)
            throw new InvalidDataException($"{path} must be an array containing {minimum}..{maximum} items.");
        return element.EnumerateArray();
    }
}
