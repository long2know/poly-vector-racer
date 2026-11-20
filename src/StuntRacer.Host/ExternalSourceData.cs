using System.Text.Json;

internal static class ExternalSourceData
{
    public static byte[] ReadJson(string path, string bundle, string kind)
    {
        var full = RequirePath(path, bundle);
        if (new FileInfo(full).Length is <= 0 or > 16 * 1024 * 1024)
            throw new InvalidDataException("External source packages must contain 1 byte to 16 MiB.");
        var bytes = File.ReadAllBytes(full);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1 ||
            !root.TryGetProperty("kind", out var tag) || tag.ValueKind != JsonValueKind.String || tag.GetString() != kind)
            throw new InvalidDataException($"The configured file is not a supported {kind} source package.");
        return bytes;
    }

    public static string RequirePath(string path, string bundle)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("External source-data paths must be absolute.");
        var full = Path.GetFullPath(path);
        if (Within(full, bundle)) throw new ArgumentException("External source data must stay outside the static bundle.");
        if (!File.Exists(full)) throw new FileNotFoundException("The external source file does not exist.", full);
        if (HasLink(full, Path.GetPathRoot(full)!))
            throw new InvalidDataException("External source files and directories must not be symbolic links.");
        return full;
    }

    public static bool Within(string path, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !Path.IsPathFullyQualified(relative);
    }

    public static bool HasLink(string path, string root)
    {
        for (var current = path; current != root; current = Path.GetDirectoryName(current)!)
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
}
