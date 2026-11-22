using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StuntRacer.Flight;

internal sealed partial class WorldAtlasCatalog : IDisposable
{
    private const int MaxPacks = 64;
    private const int MaxEntries = 180_000;
    private const int MaxAtlasEntries = 360_000;
    private const int MaxManifestBytes = 4 * 1024 * 1024;
    private const int MaxFeatureBytes = 1024 * 1024;
    private readonly List<Pack> packs = [];
    private readonly Dictionary<WorldAtlasTileKey, Pack> terrain = [];
    private readonly Dictionary<string, Pack> byId = new(StringComparer.Ordinal);
    private string revision = "";
    public byte[] Manifest { get; private set; } = [];

    [GeneratedRegex(@"\A[a-z0-9]+(?:-[a-z0-9]+)*\z")]
    private static partial Regex SlugPattern();

    public static WorldAtlasCatalog? LoadConfigured(WorldSceneryDirectory? scenery, bool seattleAvailable)
    {
        if (scenery is null) return null;
        var catalog = new WorldAtlasCatalog();
        try
        {
            var directory = scenery.PathOnDisk;
            var atlasCandidates = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(file => Path.GetFileName(file) != WorldSceneryDirectory.SeattleArchive &&
                    Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase));
            var basePath = WorldSceneryDirectory.Find(directory, "base.zip");
            if (basePath is null)
            {
                if (seattleAvailable && !atlasCandidates.Any()) return null;
                throw new FileNotFoundException("The configured scenery directory is missing base.zip for World Flight Atlas.");
            }

            var atlasBase = OpenPack(basePath, "base", null, null, out var baseData);
            catalog.Add(atlasBase);
            var entries = atlasBase.EntryCount;
            var manifestBytes = atlasBase.ManifestBytes;
            var candidates = atlasCandidates.Take(513).ToArray();
            if (candidates.Length > 512)
            {
                Console.Error.WriteLine("World Flight Atlas: too many ZIP candidates; optional packs excluded.");
                candidates = [];
            }
            foreach (var file in candidates.Order(StringComparer.Ordinal))
            {
                if (Path.GetFileName(file) == "base.zip") continue;
                if (catalog.packs.Count == MaxPacks)
                {
                    Console.Error.WriteLine("World Flight Atlas: optional pack limit reached; remaining packs excluded.");
                    break;
                }
                var id = Path.GetFileNameWithoutExtension(file);
                Pack? candidate = null;
                try
                {
                    if (!IsSlug(id) || id == "base" || Path.GetExtension(file) != ".zip")
                        throw new InvalidDataException("Expected a lowercase slug and .zip extension.");
                    candidate = OpenPack(file, id, atlasBase.BaseHash, baseData, out _);
                    if (entries + candidate.EntryCount > MaxAtlasEntries ||
                        manifestBytes + candidate.ManifestBytes > MaxManifestBytes)
                        throw new InvalidDataException("Combined atlas inventory exceeds limits.");
                    catalog.RequireCompatibleTerrain(candidate);
                    entries += candidate.EntryCount;
                    manifestBytes += candidate.ManifestBytes;
                    catalog.Add(candidate);
                    candidate = null;
                }
                catch (Exception error) when (BadPackage(error))
                {
                    Console.Error.WriteLine($"World Flight Atlas optional pack '{id}' excluded: {error.Message}");
                }
                finally { candidate?.Dispose(); }
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var pack in catalog.packs) hash.AppendData(pack.ArchiveHash);
            catalog.revision = Convert.ToHexStringLower(hash.GetHashAndReset());
            catalog.Manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "world-atlas", schemaVersion = 1, grid = WorldAtlasGrid.Id,
                revision = catalog.revision, packs = catalog.packs.Select(p => p.Manifest)
            });
            return catalog;
        }
        catch (Exception error) when (BadPackage(error))
        {
            catalog.Dispose();
            Console.Error.WriteLine($"World Flight Atlas disabled: {error.Message}");
            return null;
        }
    }

    private static bool BadPackage(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException;

    private void Add(Pack pack)
    {
        packs.Add(pack);
        byId.Add(pack.Id, pack);
        foreach (var key in pack.Terrain) terrain.TryAdd(key, pack);
    }

    private void RequireCompatibleTerrain(Pack candidate)
    {
        foreach (var key in candidate.Terrain)
        {
            if (!terrain.TryGetValue(key, out var accepted)) continue;
            // A self-contained add-on may carry common NOAA ancestors, but it may not replace them.
            var path = $"terrain/{key}.bin";
            if (!candidate.Read(path).AsSpan().SequenceEqual(accepted.Read(path)))
                throw new InvalidDataException($"Terrain key '{key}' conflicts with accepted pack '{accepted.Id}'.");
        }
    }

    public byte[]? Base(string version) => version == revision ? packs[0].Read("base.bin") : null;

    public byte[]? Tile(string version, string level, string x, string z) =>
        version == revision && TryRouteKey(level, x, z, out var key) && terrain.TryGetValue(key, out var pack)
            ? pack.Read($"terrain/{key}.bin") : null;

    public byte[]? Features(string version, string id, string level, string x, string z) =>
        version == revision && IsSlug(id) && TryRouteKey(level, x, z, out var key) &&
        byId.TryGetValue(id, out var pack) && pack.Features.Contains(key)
            ? pack.Read($"features/{key}.json") : null;

    private static bool TryRouteKey(string level, string x, string z, out WorldAtlasTileKey key)
    {
        key = default;
        if (!CanonicalInt(level, out var l) || !CanonicalInt(x, out var a) || !CanonicalInt(z, out var b))
            return false;
        key = new(l, a, b);
        return l >= 0 && key.IsValid;
    }

    private static bool CanonicalInt(string text, out int number) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number) &&
        text == number.ToString(CultureInfo.InvariantCulture);

    private static Pack OpenPack(string file, string id, string? expectedBaseHash, WorldAtlasBase? atlasBase,
        out WorldAtlasBase? loadedBase)
    {
        using var owner = new ArchiveOwner(file);
        var archive = owner.Archive;
        if (archive.Entries.Count is < 3 or > MaxEntries)
            throw new InvalidDataException("Atlas ZIP entry count is outside limits.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Length > 100 || !entries.TryAdd(entry.FullName, entry) ||
                entry.IsEncrypted || ((entry.ExternalAttributes >> 16) & 0xF000) is not (0 or 0x8000) ||
                (entry.ExternalAttributes & (int)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("Atlas ZIP contains a duplicate, encrypted, or non-regular entry.");
            if (entry.Length is <= 0 or > WorldAtlasBase.ByteLength ||
                entry.CompressedLength <= 0 || entry.CompressedLength > entry.Length + 65536)
                throw new InvalidDataException($"Invalid atlas ZIP entry size for '{entry.FullName}'.");
        }
        var manifestBytes = Read(Find(entries, "manifest.json"), MaxManifestBytes);
        using var document = JsonDocument.Parse(manifestBytes);
        var m = document.RootElement;
        RequireObject(m, "kind", "schemaVersion", "grid", "id", "name", "baseSha256", "terrain", "features", "presets", "credits");
        if (String(m.GetProperty("kind"), 32) != "world-atlas-pack" || Integer(m.GetProperty("schemaVersion")) != 1 ||
            String(m.GetProperty("grid"), 32) != WorldAtlasGrid.Id || String(m.GetProperty("id"), 64) != id)
            throw new InvalidDataException("Atlas pack schema, grid, or filename ID does not match.");
        _ = String(m.GetProperty("name"), 100);
        var baseHash = String(m.GetProperty("baseSha256"), 64);
        var expected = new HashSet<string>(StringComparer.Ordinal) { "manifest.json", "receipt.json", "DATA-NOTICE.txt" };
        loadedBase = null;
        if (id == "base")
        {
            expected.Add("base.bin");
            var raw = Read(Find(entries, "base.bin"), WorldAtlasBase.ByteLength, exact: true);
            if (baseHash != Convert.ToHexStringLower(SHA256.HashData(raw)))
                throw new InvalidDataException("base.bin SHA-256 does not match the manifest.");
            atlasBase = loadedBase = new WorldAtlasBase(raw);
        }
        else if (baseHash != expectedBaseHash)
            throw new InvalidDataException("Pack was compiled for a different base.bin SHA-256.");

        var terrain = ReadTerrainInventory(m.GetProperty("terrain"));
        var features = ReadFeatureInventory(m.GetProperty("features"));
        foreach (var key in terrain) expected.Add($"terrain/{key}.bin");
        foreach (var key in features) expected.Add($"features/{key}.json");
        if (!expected.SetEquals(entries.Keys))
            throw new InvalidDataException("ZIP entries do not exactly match the pack manifest.");
        if (entries.Values.Sum(entry => entry.Length) > 1200L * 1024 * 1024)
            throw new InvalidDataException("Expanded atlas ZIP exceeds the size limit.");

        using (var receipt = JsonDocument.Parse(Read(entries["receipt.json"], 2 * 1024 * 1024)))
            if (receipt.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Atlas receipt must be a JSON object.");
        if (string.IsNullOrWhiteSpace(new UTF8Encoding(false, true).GetString(Read(entries["DATA-NOTICE.txt"], 256 * 1024))))
            throw new InvalidDataException("Atlas data notice is empty.");
        var credits = ReadCredits(m.GetProperty("credits"));
        ReadPresets(m.GetProperty("presets"));
        ValidateTerrain(terrain, entries, atlasBase!);
        foreach (var key in features)
            ValidateFeatures(key, entries[$"features/{key}.json"], credits);
        var pack = new Pack(id, m.Clone(), manifestBytes.Length, terrain, features, baseHash, owner.Hash, archive, entries);
        owner.Release();
        return pack;
    }

    private static void ValidateTerrain(HashSet<WorldAtlasTileKey> keys, Dictionary<string, ZipArchiveEntry> entries,
        WorldAtlasBase atlasBase)
    {
        const int end = WorldAtlasGrid.Side - 1;
        WorldAtlasTile Tile(WorldAtlasTileKey key) =>
            new(key, Read(Find(entries, $"terrain/{key}.bin"), WorldAtlasTile.ByteLength, exact: true));
        var parents = keys.Select(key => key.Parent).ToHashSet();
        foreach (var parent in parents)
        {
            if (parent.Level > 0 && !keys.Contains(parent) ||
                Enumerable.Range(0, 4).Any(i => !keys.Contains(parent.Child(i))))
                throw new InvalidDataException($"Terrain group '{parent}' lacks same-pack siblings or a supporting parent.");
            var coarse = parent.Level == 0 ? null : Tile(parent);
            var children = Enumerable.Range(0, 4).Select(i => Tile(parent.Child(i))).ToArray();
            for (var i = 0; i <= end; i++)
            {
                if (children[0].Vertex(end, i) != children[1].Vertex(0, i) ||
                    children[2].Vertex(end, i) != children[3].Vertex(0, i) ||
                    children[0].Vertex(i, end) != children[2].Vertex(i, 0) ||
                    children[1].Vertex(i, end) != children[3].Vertex(i, 0))
                    throw new InvalidDataException($"Atlas terrain siblings '{parent}' disagree at a common edge.");
            }
            for (var n = 0; n < children.Length; n++)
            {
                var tile = children[n];
                var step = WorldAtlasGrid.Step(tile.Key.Level);
                var span = end * step;
                void Match(int x, int z)
                {
                    var e = tile.Key.X * span + x * step;
                    var north = tile.Key.Z * span + z * step;
                    var (height, wet) = coarse?.Sample(e, north) ?? atlasBase.Sample(e, north);
                    var vertex = tile.Vertex(x, z);
                    if (vertex.Height != Math.Floor(height + .5) || vertex.Water != wet)
                        throw new InvalidDataException($"Atlas tile '{tile.Key}' disagrees with its parent perimeter.");
                }
                for (var i = 0; i <= end; i++)
                {
                    Match((n & 1) == 0 ? 0 : end, i);
                    Match(i, n < 2 ? 0 : end);
                }
            }
        }
    }

    private static HashSet<WorldAtlasTileKey> ReadTerrainInventory(JsonElement list)
    {
        var result = new HashSet<WorldAtlasTileKey>();
        RequireArray(list, MaxEntries);
        foreach (var rectangle in list.EnumerateArray())
        {
            RequireArray(rectangle, 5, 5);
            var l = Integer(rectangle[0]);
            var w = Integer(rectangle[1]);
            var s = Integer(rectangle[2]);
            var e = Integer(rectangle[3]);
            var n = Integer(rectangle[4]);
            if (l < 1 || w >= e || s >= n || !new WorldAtlasTileKey(l, w, s).IsValid ||
                !new WorldAtlasTileKey(l, e - 1, n - 1).IsValid ||
                (long)(e - w) * (n - s) > MaxEntries)
                throw new InvalidDataException("Atlas terrain inventory has an invalid extent.");
            for (var x = w; x < e; x++)
            for (var z = s; z < n; z++)
                if (!result.Add(new(l, x, z)) || result.Count > MaxEntries)
                    throw new InvalidDataException("Atlas terrain inventory contains overlaps or exceeds limits.");
        }
        return result;
    }

    private static HashSet<WorldAtlasTileKey> ReadFeatureInventory(JsonElement list)
    {
        RequireArray(list, MaxEntries);
        var result = new HashSet<WorldAtlasTileKey>();
        foreach (var row in list.EnumerateArray())
        {
            var key = ReadKey(row);
            if (key.Level is not (0 or 5) || !result.Add(key))
                throw new InvalidDataException("Atlas feature inventory has an invalid or duplicate tile.");
        }
        return result;
    }

    private static HashSet<string> ReadCredits(JsonElement credits)
    {
        if (credits.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid atlas source credits.");
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in credits.EnumerateObject())
        {
            if (!IsSlug(item.Name) || !sources.Add(item.Name) || sources.Count > 48)
                throw new InvalidDataException("Invalid atlas source credit identifier.");
            RequireArray(item.Value, 3, 3);
            _ = String(item.Value[0], 384);
            _ = String(item.Value[1], 100);
            var url = String(item.Value[2], 512);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != "https" ||
                address.Host.Length == 0 || address.UserInfo.Length != 0)
                throw new InvalidDataException("Atlas source credits require an HTTPS link.");
        }
        if (sources.Count == 0) throw new InvalidDataException("Atlas pack has no source credits.");
        return sources;
    }

    private static void ReadPresets(JsonElement presets)
    {
        RequireArray(presets, 128);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preset in presets.EnumerateArray())
        {
            RequireObject(preset, "id", "name", "lon", "lat");
            var id = String(preset.GetProperty("id"), 64);
            if (!IsSlug(id) || !ids.Add(id)) throw new InvalidDataException("Invalid or duplicate atlas preset.");
            _ = String(preset.GetProperty("name"), 100);
            if (preset.GetProperty("lon").ValueKind != JsonValueKind.Number ||
                preset.GetProperty("lat").ValueKind != JsonValueKind.Number ||
                !preset.GetProperty("lon").TryGetDouble(out var lon) || !preset.GetProperty("lat").TryGetDouble(out var lat))
                throw new InvalidDataException("Invalid atlas preset coordinates.");
            _ = WorldAtlasGrid.Project(lon, lat);
        }
    }

    private static WorldAtlasTileKey ReadKey(JsonElement data)
    {
        RequireArray(data, 3, 3);
        var key = new WorldAtlasTileKey(Integer(data[0]), Integer(data[1]), Integer(data[2]));
        if (!key.IsValid) throw new InvalidDataException("Atlas tile key is outside the canonical world.");
        return key;
    }

    private static void ValidateFeatures(WorldAtlasTileKey key, ZipArchiveEntry entry, HashSet<string> sources)
    {
        using var doc = JsonDocument.Parse(Read(entry, MaxFeatureBytes));
        var tile = doc.RootElement;
        RequireObject(tile, "schemaVersion", "tile", "rows");
        if (Integer(tile.GetProperty("schemaVersion")) != 1 || ReadKey(tile.GetProperty("tile")) != key)
            throw new InvalidDataException($"Atlas feature tile '{key}' does not match its entry.");
        var rows = tile.GetProperty("rows");
        RequireArray(rows, 4096);
        var span = WorldAtlasGrid.Span(key.Level);
        var pointCount = 0;
        foreach (var row in rows.EnumerateArray())
        {
            RequireArray(row, 6, 5);
            var kind = String(row[0], 24);
            var style = String(row[1], 32);
            var source = String(row[3], 64);
            if (!KnownFeatureStyle(kind, style) || !(source == "osm" || key.Level == 0 && source == "natural-earth") ||
                !sources.Contains(source))
                throw new InvalidDataException($"Atlas feature tile '{key}' has an unknown kind/style or uncredited source.");
            if (row.GetArrayLength() == 6 && (kind != "building" || source != "osm" || key.Level != 5 ||
                row[5].ValueKind != JsonValueKind.Number || !row[5].TryGetInt32(out var height) || height is < 1 or > 1000))
                throw new InvalidDataException($"Atlas feature tile '{key}' has an invalid building height.");
            if (row[2].ValueKind != JsonValueKind.Null) _ = String(row[2], 160);
            var (min, max) = kind switch
            {
                "place" or "landmark" or "airport" => (1, 1),
                "road" or "rail" => (2, 2048),
                "building" or "landuse" => (3, 2048),
                _ => throw new InvalidDataException($"Atlas feature tile '{key}' has an unsupported feature kind.")
            };
            var points = row[4];
            RequireArray(points, max, min);
            pointCount += points.GetArrayLength();
            if (pointCount > 32_768) throw new InvalidDataException($"Atlas feature tile '{key}' exceeds its vertex budget.");
            foreach (var point in points.EnumerateArray())
            {
                RequireArray(point, 2, 2);
                var x = Integer(point[0]);
                var z = Integer(point[1]);
                if (x < 0 || x > span || z < 0 || z > span)
                    throw new InvalidDataException($"Atlas feature tile '{key}' has a point outside its clipped bounds.");
            }
        }
    }

    private static bool KnownFeatureStyle(string kind, string style) => kind switch
    {
        "road" => style is "motorway" or "trunk" or "primary" or "secondary" or "tertiary" or "local" or "service" or "path",
        "rail" => style is "rail" or "light-rail" or "tram",
        "place" => style is "capital" or "city" or "town" or "village" or "hamlet",
        "landmark" => style is "peak" or "historic" or "monument" or "attraction" or "viewpoint" or "stadium" or "natural",
        "airport" => style is "airport" or "airfield",
        "building" => style is "building" or "commercial" or "industrial" or "residential",
        "landuse" => style is "forest" or "park" or "grass",
        _ => false
    };

    private static void RequireObject(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected an atlas JSON object.");
        var remaining = new HashSet<string>(keys, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!remaining.Remove(property.Name)) throw new InvalidDataException("Unexpected or duplicate atlas JSON property.");
        if (remaining.Count != 0) throw new InvalidDataException("Required atlas JSON properties are missing.");
    }

    private static void RequireArray(JsonElement element, int max, int min = 0)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() < min || element.GetArrayLength() > max)
            throw new InvalidDataException("Atlas JSON array has an invalid size.");
    }

    private static int Integer(JsonElement element) => element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt32(out var result) ? result : throw new InvalidDataException("Expected an atlas integer.");

    private static string String(JsonElement element, int max) =>
        element.ValueKind == JsonValueKind.String && element.GetString() is { } text &&
        text.Length <= max && !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl)
            ? text : throw new InvalidDataException("Expected a bounded atlas label or identifier.");

    private static bool IsSlug(string value) => value.Length is > 0 and <= 64 && SlugPattern().IsMatch(value);

    private static ZipArchiveEntry Find(Dictionary<string, ZipArchiveEntry> entries, string name) =>
        entries.TryGetValue(name, out var entry) ? entry : throw new InvalidDataException($"Atlas ZIP is missing '{name}'.");

    private static byte[] Read(ZipArchiveEntry entry, int limit, bool exact = false)
    {
        if (entry.Length <= 0 || entry.Length > limit || exact && entry.Length != limit ||
            entry.CompressedLength <= 0 || entry.CompressedLength > entry.Length + 65536)
            throw new InvalidDataException($"Invalid atlas ZIP entry size for '{entry.FullName}'.");
        using var stream = entry.Open();
        var bytes = new byte[(int)entry.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1 || Crc32(bytes) != entry.Crc32)
            throw new InvalidDataException($"Atlas ZIP entry '{entry.FullName}' has an invalid size or checksum.");
        return bytes;
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var value = (uint)n;
        for (var i = 0; i < 8; i++) value = (value & 1) != 0 ? 0xedb88320u ^ (value >> 1) : value >> 1;
        return value;
    }).ToArray();

    private static uint Crc32(byte[] bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes) crc = (crc >> 8) ^ CrcTable[(byte)(crc ^ value)];
        return ~crc;
    }

    private sealed class ArchiveOwner : IDisposable
    {
        private ZipArchive? archive;
        public ZipArchive Archive => archive!;
        public byte[] Hash { get; }

        public ArchiveOwner(string path)
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new InvalidDataException("Atlas ZIP must be a regular file, not a link.");
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (stream.Length is < 22 or > 768L * 1024 * 1024)
                    throw new InvalidDataException("Atlas ZIP file size is outside limits.");
                Hash = SHA256.HashData(stream);
                stream.Position = 0;
                archive = new ZipArchive(stream, ZipArchiveMode.Read);
            }
            catch { stream.Dispose(); throw; }
        }

        public void Release() => archive = null;
        public void Dispose() => archive?.Dispose();
    }

    private sealed class Pack(string id, JsonElement manifest, int manifestBytes, HashSet<WorldAtlasTileKey> terrain,
        HashSet<WorldAtlasTileKey> features, string baseHash, byte[] hash, ZipArchive archive,
        Dictionary<string, ZipArchiveEntry> entries) : IDisposable
    {
        private readonly object gate = new();
        public string Id { get; } = id;
        public JsonElement Manifest { get; } = manifest;
        public HashSet<WorldAtlasTileKey> Terrain { get; } = terrain;
        public HashSet<WorldAtlasTileKey> Features { get; } = features;
        public string BaseHash { get; } = baseHash;
        public byte[] ArchiveHash { get; } = hash;
        public int EntryCount => entries.Count;
        public int ManifestBytes { get; } = manifestBytes;
        public byte[] Read(string name)
        {
            // ZIP entry streams share a seek position, so keep each bounded read together.
            lock (gate)
            {
                return WorldAtlasCatalog.Read(entries[name], name switch
                {
                    "base.bin" => WorldAtlasBase.ByteLength,
                    _ when name.EndsWith(".bin", StringComparison.Ordinal) => WorldAtlasTile.ByteLength,
                    _ => MaxFeatureBytes
                });
            }
        }
        public void Dispose() => archive.Dispose();
    }

    public void Dispose()
    {
        foreach (var pack in packs) pack.Dispose();
    }
}
