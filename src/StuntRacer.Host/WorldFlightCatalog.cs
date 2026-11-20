using System.Text.Json;

internal sealed class WorldFlightCatalog(byte[] manifest, string directory, string bundle,
    string version, IReadOnlyDictionary<string, int[][][]> regions, IReadOnlySet<string> cities)
{
    public byte[] Manifest { get; } = manifest;

    public static WorldFlightCatalog Load(string path, string bundle)
    {
        var data = ExternalSourceData.ReadJson(path, bundle, "world-flight-terrain");
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        var version = root.GetProperty("version").GetString();
        if (version is not { Length: 20 } || version.Any(c => !char.IsAsciiHexDigitLower(c)) ||
            root.GetProperty("side").GetInt32() != 33 ||
            !root.GetProperty("spacing").EnumerateArray().Select(p => p.GetInt32())
                .SequenceEqual(Enumerable.Range(0, 9).Select(level => 8192 >> level)))
            throw new InvalidDataException("World Flight terrain has an unsupported revision or grid.");
        var regions = new Dictionary<string, int[][][]>(StringComparer.Ordinal);
        var cities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var region in root.GetProperty("regions").EnumerateArray())
        {
            var id = region.GetProperty("id").GetString();
            var bounds = region.GetProperty("coverage").EnumerateArray()
                .Select(level => new[] { ReadRectangle(level) }).ToArray();
            if (id is not ("conus" or "alaska" or "hawaii") || bounds.Length is < 3 or > 9 ||
                !regions.TryAdd(id, bounds))
                throw new InvalidDataException("World Flight region has invalid tile coverage.");
            if (region.TryGetProperty("patches", out var patches))
            {
                if (patches.GetArrayLength() != bounds.Length)
                    throw new InvalidDataException("World Flight detail patches must specify their tile levels.");
                for (var level = 0; level < bounds.Length; level++)
                {
                    if (patches[level].GetArrayLength() > 8 || level < 3 && patches[level].GetArrayLength() != 0)
                        throw new InvalidDataException("World Flight has invalid detail patch counts.");
                    bounds[level] = [.. bounds[level], .. patches[level].EnumerateArray().Select(ReadRectangle)];
                }
            }
            for (var level = 1; level < bounds.Length; level++)
            {
                var rectangles = bounds[level];
                for (var i = 0; i < rectangles.Length; i++)
                {
                    var b = rectangles[i];
                    if (b.Any(v => v % 2 != 0) || !bounds[level - 1].Any(parent =>
                        b[0] >= parent[0] * 2 && b[1] >= parent[1] * 2 &&
                        b[2] <= parent[2] * 2 && b[3] <= parent[3] * 2))
                        throw new InvalidDataException("World Flight detail tiles must have complete parent coverage.");
                    if (rectangles.Take(i).Any(p => b[0] < p[2] && b[2] > p[0] && b[1] < p[3] && b[3] > p[1]))
                        throw new InvalidDataException("World Flight detail patches must not overlap.");
                }
            }
            if (id == "conus")
            {
                if (!region.TryGetProperty("cities", out var footprints))
                    cities.Add("seattle"); // The first package stored one Seattle extract.
                else
                    foreach (var city in footprints.EnumerateArray())
                    {
                        var cityId = city.GetProperty("id").GetString();
                        if (cityId is not ("seattle" or "san-francisco") || !cities.Add(cityId))
                            throw new InvalidDataException("Unknown or duplicate World Flight building area.");
                    }
            }
        }
        if (regions.Count != 3)
            throw new InvalidDataException("World Flight must describe the lower 48, Alaska and Hawaii.");

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        // Check a known file up front; the remainder is requested and checked on demand.
        var catalog = new WorldFlightCatalog(data, directory, bundle, version, regions, cities);
        foreach (var (id, levels) in regions)
        {
            var b = levels[0][0];
            if (!catalog.TryTile(version, id, 0, b[0], b[1], out _))
                throw new FileNotFoundException($"The World Flight package is missing its base tiles for {id}.");
        }
        foreach (var city in cities)
            _ = catalog.BuildingsPath(version, city) ?? throw new FileNotFoundException($"The {city} footprint database is missing.");
        return catalog;
    }

    private static int[] ReadRectangle(JsonElement rectangle)
    {
        var b = rectangle.EnumerateArray().Select(v => v.GetInt32()).ToArray();
        return b.Length == 4 && b.All(v => v is >= -4096 and <= 4096) && b[0] < b[2] && b[1] < b[3]
            ? b : throw new InvalidDataException("World Flight has an invalid tile rectangle.");
    }

    public bool TryTile(string revision, string region, int level, int x, int z, out string? path)
    {
        path = null;
        if (revision != version || !regions.TryGetValue(region, out var coverage) ||
            level < 0 || level >= coverage.Length)
            return false;
        if (!coverage[level].Any(box => x >= box[0] && z >= box[1] && x < box[2] && z < box[3]))
            return false;

        path = Readable(Path.Combine(directory, "tiles", region, level.ToString(System.Globalization.CultureInfo.InvariantCulture),
            x.ToString(System.Globalization.CultureInfo.InvariantCulture),
            z.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bin"), 16 + 33 * 33 * 3);
        return path is not null;
    }

    public string? BuildingsPath(string revision, string city)
        => revision == version && cities.Contains(city)
            ? Readable(Path.Combine(directory, $"{city}-buildings.json"), null) : null;

    private string? Readable(string candidate, int? size)
    {
        if (!File.Exists(candidate))
            return null;
        var path = ExternalSourceData.RequirePath(candidate, bundle);
        var length = new FileInfo(path).Length;
        if (size is int exact ? length != exact : length is <= 0 or > 4 * 1024 * 1024)
            throw new InvalidDataException("The World Flight package contains an invalid data file.");
        return path;
    }
}
