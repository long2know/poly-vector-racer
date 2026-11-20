using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed record ExternalTrackEntry(string Id, string Name, string Group, string Status, string? Reason);

internal sealed partial class ExternalTrackCatalog
{
    private readonly Dictionary<string, byte[]> packages = new(StringComparer.Ordinal);
    private readonly List<ExternalTrackEntry> entries = [];
    public IReadOnlyList<ExternalTrackEntry> Entries => entries;
    public string? DefaultId => packages.ContainsKey("amiga-little-ramp") ? "amiga-little-ramp" : packages.Keys.FirstOrDefault();

    public byte[]? Find(string? id) =>
        packages.TryGetValue(id ?? DefaultId ?? "", out var bytes) ? bytes : null;

    public static ExternalTrackCatalog Load(string? manifestPath, string? singlePackagePath, string bundle)
    {
        var catalog = new ExternalTrackCatalog();
        if (!string.IsNullOrWhiteSpace(manifestPath) && !string.IsNullOrWhiteSpace(singlePackagePath))
            throw new ArgumentException("Configure either STUNT_TRACK_CATALOG or STUNT_AMIGA_PACKAGE, not both.");
        if (!string.IsNullOrWhiteSpace(singlePackagePath))
        {
            var (bytes, name) = ReadPackage(singlePackagePath, bundle);
            catalog.packages.Add("reference", bytes);
            catalog.entries.Add(new("reference", name, "Reference", "available", null));
            return catalog;
        }
        if (string.IsNullOrWhiteSpace(manifestPath)) return catalog;
        var manifest = ExternalSourceData.RequirePath(manifestPath, bundle);
        if (new FileInfo(manifest).Length > 1024 * 1024)
            throw new InvalidDataException("The external track catalog is too large.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifest), new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1 ||
            !root.TryGetProperty("tracks", out var rows) || rows.ValueKind != JsonValueKind.Array ||
            rows.GetArrayLength() is < 1 or > 32)
            throw new InvalidDataException("The external track catalog requires version 1 and 1..32 tracks.");
        var directory = Path.GetDirectoryName(manifest)!;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var id = Text(row, "id", 64);
            if (!TrackId().IsMatch(id) || !ids.Add(id))
                throw new InvalidDataException($"Invalid or duplicate external track id '{id}'.");
            var name = Text(row, "name", 128);
            var group = Text(row, "group", 64);
            var status = Text(row, "status", 24);
            if (status == "unsupported")
            {
                if (!row.TryGetProperty("packagePath", out var absent) || absent.ValueKind != JsonValueKind.Null)
                    throw new InvalidDataException($"Unsupported track '{id}' must not name a package.");
                catalog.entries.Add(new(id, name, group, status, Text(row, "reason", 512)));
                continue;
            }
            if (status != "available") throw new InvalidDataException($"Unsupported catalog status '{status}'.");
            var relative = Text(row, "packagePath", 512);
            var path = Path.GetFullPath(Path.Combine(directory, relative));
            if (Path.IsPathFullyQualified(relative) || !ExternalSourceData.Within(path, directory) ||
                ExternalSourceData.HasLink(path, directory))
                throw new InvalidDataException($"Track '{id}' must reference a regular file beneath the catalog directory.");
            var (bytes, _) = ReadPackage(path, bundle);
            totalBytes += bytes.Length;
            if (totalBytes > 64 * 1024 * 1024)
                throw new InvalidDataException("The external track catalog exceeds the 64 MiB total package limit.");
            catalog.packages.Add(id, bytes);
            catalog.entries.Add(new(id, name, group, status, null));
        }
        return catalog;
    }

    private static (byte[] Bytes, string Name) ReadPackage(string path, string bundle)
    {
        var bytes = ExternalSourceData.ReadJson(path, bundle, "stunt-amiga-source-runtime");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        var name = root.TryGetProperty("name", out var label) && label.ValueKind == JsonValueKind.String
            ? label.GetString() : Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            throw new InvalidDataException("External source package names must contain 1..128 characters.");
        return (bytes, name);
    }

    private static string Text(JsonElement row, string name, int maximum)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) ||
            value.GetString()!.Length > maximum)
            throw new InvalidDataException($"Catalog '{name}' must contain 1..{maximum} characters.");
        return value.GetString()!;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex TrackId();
}
