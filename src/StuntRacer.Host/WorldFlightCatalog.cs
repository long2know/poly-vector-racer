using System.Globalization;
using System.Text.Json;

internal sealed class WorldFlightCatalog(byte[] manifest, string directory, string bundle, WorldFlightArchive? archive,
    string version, IReadOnlyDictionary<string, int[][][]> regions, IReadOnlySet<string> cities, byte[]? roads) : IDisposable
{
    private const string Kind = "world-flight-terrain";
    private const string RoadsName = "seattle-roads.json";
    internal const int TileBytes = 16 + 33 * 33 * 3;
    internal const int MaxSceneryBytes = 4 * 1024 * 1024;

    public byte[] Manifest { get; } = manifest;

    public static WorldFlightCatalog Load(string path, string bundle)
    {
        if (!string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            return Load(path, bundle, null);
        path = ExternalSourceData.RequirePath(path, bundle);
        var archive = WorldFlightArchive.Open(path);
        try { return Load(path, bundle, archive); }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private static WorldFlightCatalog Load(string path, string bundle, WorldFlightArchive? archive)
    {
        var data = archive is null ? ExternalSourceData.ReadJson(path, bundle, Kind) :
            archive.Read("manifest.json") ?? throw new InvalidDataException("The World Flight ZIP is missing manifest.json.");
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        if (archive is not null)
            ExternalSourceData.RequireJsonKind(root, Kind);
        var version = root.GetProperty("version").GetString();
        if (version is not { Length: 20 } || version.Any(c => !char.IsAsciiHexDigitLower(c)) ||
            root.GetProperty("side").GetInt32() != 33 ||
            !root.GetProperty("spacing").EnumerateArray().Select(p => p.GetInt32())
                .SequenceEqual(Enumerable.Range(0, 9).Select(level => 8192 >> level)))
            throw new InvalidDataException("World Flight terrain has an unsupported revision or grid.");
        var regions = new Dictionary<string, int[][][]>(StringComparer.Ordinal);
        var cities = new HashSet<string>(StringComparer.Ordinal);
        JsonElement? roadArea = null;
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
            if (region.TryGetProperty("roads", out var area))
            {
                if (id != "conus")
                    throw new InvalidDataException("The World Flight Seattle road layer must be in the lower 48.");
                roadArea = area;
            }
        }
        if (regions.Count != 3)
            throw new InvalidDataException("World Flight must describe the lower 48, Alaska and Hawaii.");

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        // Check a known file up front; the remainder is requested and checked on demand.
        var catalog = new WorldFlightCatalog(data, directory, bundle, archive, version, regions, cities,
            roadArea is { } seattle ? ReadRoads(seattle, directory, bundle, archive) : null);
        foreach (var (id, levels) in regions)
        {
            var b = levels[0][0];
            if (catalog.Tile(version, id, 0, b[0], b[1]) is null)
                throw new FileNotFoundException($"The World Flight package is missing its base tiles for {id}.");
        }
        foreach (var city in cities)
            _ = catalog.Buildings(version, city) ?? throw new FileNotFoundException($"The {city} footprint database is missing.");
        return catalog;
    }

    private static byte[] ReadRoads(JsonElement area, string directory, string bundle, WorldFlightArchive? archive)
    {
        static InvalidDataException Invalid() => new("World Flight Seattle road database or metadata is malformed.");
        try
        {
            var features = area.GetProperty("featureCount").GetInt32();
            var points = area.GetProperty("pointCount").GetInt32();
            var b = area.GetProperty("bounds").EnumerateArray().Select(p => p.GetDouble()).ToArray();
            if (area.GetProperty("id").GetString() != "seattle" || features is < 1 or > 12000 ||
                points < 2 * features || points > 100000 || b.Length != 4 ||
                b.Any(v => !double.IsFinite(v) || Math.Abs(v) > 40000) ||
                b[2] - b[0] is <= 0 or > 20000 || b[3] - b[1] is <= 0 or > 20000)
                throw Invalid();
            var bytes = (archive is not null ? archive.Read(RoadsName) : ReadDirectoryRoads(directory, bundle))
                ?? throw new FileNotFoundException("The World Flight Seattle road database is missing.");
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var data = document.RootElement;
            var rows = data.GetProperty("roads");
            if (data.GetProperty("license").GetString() != "ODbL-1.0" ||
                data.GetProperty("attribution").GetString() != "© OpenStreetMap contributors" ||
                data.GetProperty("licenseUrl").GetString() != "https://www.openstreetmap.org/copyright" ||
                rows.GetArrayLength() != features)
                throw Invalid();
            var total = 0;
            foreach (var row in rows.EnumerateArray())
            {
                if (row.GetArrayLength() != 4 || row[0].GetInt64() is < 1 or > 9007199254740991 ||
                    row[1].GetString() is not ("freeway" or "arterial" or "local"))
                    throw Invalid();
                var bridge = row[2].GetString() switch
                {
                    "bridge" => true,
                    "surface" => false,
                    _ => throw Invalid(),
                };
                var line = row[3];
                var count = line.GetArrayLength();
                if (count is < 2 or > 256 || (total += count) > points)
                    throw Invalid();
                double? lastX = null, lastZ = null;
                foreach (var point in line.EnumerateArray())
                {
                    if (point.GetArrayLength() != (bridge ? 3 : 2))
                        throw Invalid();
                    var x = point[0].GetDouble();
                    var z = point[1].GetDouble();
                    var height = bridge ? point[2].GetDouble() : 0;
                    if (!double.IsFinite(x) || !double.IsFinite(z) || !double.IsFinite(height) ||
                        x < b[0] || x > b[2] || z < b[1] || z > b[3] || height is < -50 or > 300)
                        throw Invalid();
                    if (lastX is double previousX && lastZ is double previousZ &&
                        ((x - previousX) * (x - previousX) + (z - previousZ) * (z - previousZ)) is <= 0 or > 128.03 * 128.03)
                        throw Invalid();
                    (lastX, lastZ) = (x, z);
                }
            }
            if (total != points)
                throw Invalid();
            return bytes;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new InvalidDataException("World Flight Seattle road database or metadata is malformed.", error);
        }
    }

    private static byte[]? ReadDirectoryRoads(string directory, string bundle)
    {
        var path = Path.Combine(directory, RoadsName);
        if (!File.Exists(path))
            return null;
        path = ExternalSourceData.RequirePath(path, bundle);
        if (new FileInfo(path).Length is <= 0 or > MaxSceneryBytes)
            throw new InvalidDataException("World Flight Seattle road database or metadata is malformed.");
        return File.ReadAllBytes(path);
    }

    private static int[] ReadRectangle(JsonElement rectangle)
    {
        var b = rectangle.EnumerateArray().Select(v => v.GetInt32()).ToArray();
        return b.Length == 4 && b.All(v => v is >= -4096 and <= 4096) && b[0] < b[2] && b[1] < b[3]
            ? b : throw new InvalidDataException("World Flight has an invalid tile rectangle.");
    }

    public IResult? Tile(string revision, string region, int level, int x, int z)
    {
        if (revision != version || !regions.TryGetValue(region, out var coverage) ||
            level < 0 || level >= coverage.Length)
            return null;
        if (!coverage[level].Any(box => x >= box[0] && z >= box[1] && x < box[2] && z < box[3]))
            return null;
        return DataFile(string.Create(CultureInfo.InvariantCulture, $"tiles/{region}/{level}/{x}/{z}.bin"),
            "application/octet-stream", TileBytes);
    }

    public IResult? Buildings(string revision, string city)
        => revision == version && cities.Contains(city)
            ? DataFile($"{city}-buildings.json", "application/json") : null;

    public byte[]? SeattleRoads(string revision) => revision == version ? roads : null;

    private IResult? DataFile(string name, string contentType, int? size = null)
    {
        if (archive is not null)
            return archive.Read(name) is { } bytes ? Results.Bytes(bytes, contentType) : null;

        var candidate = Path.Combine(directory, name);
        if (!File.Exists(candidate))
            return null;
        var path = ExternalSourceData.RequirePath(candidate, bundle);
        var length = new FileInfo(path).Length;
        if (size is int exact ? length != exact : length is <= 0 or > MaxSceneryBytes)
            throw new InvalidDataException("The World Flight package contains an invalid data file.");
        return Results.File(path, contentType);
    }

    public void Dispose() => archive?.Dispose();
}
