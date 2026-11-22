using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace StuntRacer.Flight;

public static class WorldAtlasGrid
{
    public const string Id = "eqc-wft1-v1";
    public const int HalfWidth = 19_922_944;
    public const int HalfHeight = 9_961_472;
    public const int Width = 2 * HalfWidth;
    public const int Side = WorldFlightElevation.Side;

    public static int Step(int level) => level is >= -1 and <= 8 ? 16384 >> (level + 1)
        : throw new ArgumentOutOfRangeException(nameof(level));

    public static int Span(int level) => (Side - 1) * Step(level);

    public static double Wrap(double x)
    {
        x %= Width;
        return x >= HalfWidth ? x - Width : x < -HalfWidth ? x + Width : x;
    }

    public static (double X, double Z) Project(double lon, double lat)
    {
        if (!double.IsFinite(lon) || lon is < -180 or > 180 ||
            !double.IsFinite(lat) || lat is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(lon), "Atlas longitude/latitude must be within ±180°/±90°.");
        return (Wrap(lon * HalfWidth / 180), lat * HalfHeight / 90);
    }

    internal static (double Height, byte Water) Sample(short[] heights, byte[] water, int columns, int rows,
        double u, double v, bool wrapColumn)
    {
        var i = Math.Min((int)u, columns - (wrapColumn ? 1 : 2));
        var j = Math.Min((int)v, rows - 2);
        var a = j * columns + i;
        var c = j * columns + (wrapColumn && i == columns - 1 ? 0 : i + 1);
        var b = a + columns;
        var d = c + columns;
        var tx = u - i;
        var tz = v - j;
        // Match the mesh's southwest-to-northeast diagonal, including signed elevations.
        var height = tx <= tz
            ? (1 - tz) * heights[a] + (tz - tx) * heights[b] + tx * heights[d]
            : (1 - tx) * heights[a] + tz * heights[d] + (tx - tz) * heights[c];
        return (height, water[(tx < .5 ? a : c) + (tz < .5 ? 0 : columns)]);
    }

    internal static void ReadSamples(ReadOnlySpan<byte> data, short[] heights, byte[] water)
    {
        data[(heights.Length * 2)..].CopyTo(water);
        if (BitConverter.IsLittleEndian)
            MemoryMarshal.Cast<byte, short>(data[..(heights.Length * 2)]).CopyTo(heights);
        else
            for (var i = 0; i < heights.Length; i++)
                heights[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]);
        for (var i = 0; i < heights.Length; i++)
            if (heights[i] is < -12000 or > 10000 || water[i] > 2)
                throw new InvalidDataException("Atlas elevation has an invalid height or water code.");
    }
}

public readonly record struct WorldAtlasTileKey(int Level, int X, int Z)
{
    public bool IsValid
    {
        get
        {
            if (Level is < -1 or > 8) return false;
            var width = WorldAtlasGrid.HalfWidth / WorldAtlasGrid.Span(Level);
            var height = width / 2;
            return X >= -width && X < width && Z >= -height && Z < height;
        }
    }

    public WorldAtlasTileKey Parent => new(Level - 1, X >> 1, Z >> 1);
    public WorldAtlasTileKey Child(int index) => new(Level + 1, 2 * X + (index & 1), 2 * Z + (index >> 1));
    public override string ToString() => FormattableString.Invariant($"{Level}/{X}/{Z}");

    internal static WorldAtlasTileKey At(int level, double x, double z)
    {
        var span = WorldAtlasGrid.Span(level);
        return new(level, (int)Math.Floor(x / span),
            Math.Min((int)Math.Floor(z / span), WorldAtlasGrid.HalfHeight / span - 1));
    }
}

public sealed class WorldAtlasBase
{
    public const int Columns = 2432;
    public const int Rows = 1217;
    public const int Spacing = 16384;
    public const int ByteLength = 16 + Columns * Rows * 3;

    private readonly short[] heights;
    private readonly byte[] water;

    public WorldAtlasBase(ReadOnlySpan<byte> data)
    {
        if (data.Length != ByteLength || !data[..4].SequenceEqual("WFA1"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != Columns ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != Rows ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) != Spacing)
            throw new InvalidDataException("Invalid WFA1 atlas fallback header or size.");
        heights = new short[Columns * Rows];
        water = new byte[Columns * Rows];
        WorldAtlasGrid.ReadSamples(data[16..], heights, water);
    }

    public (double Height, byte Water) Sample(double x, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z))
            throw new ArgumentOutOfRangeException(nameof(x), "Atlas sample positions must be finite.");
        var u = (WorldAtlasGrid.Wrap(x) + WorldAtlasGrid.HalfWidth) / Spacing;
        // A sub-nanometre west-inclusive query can round up to the omitted eastern column.
        if (u >= Columns) u = 0;
        var v = (Math.Clamp(z, -WorldAtlasGrid.HalfHeight, WorldAtlasGrid.HalfHeight) +
            WorldAtlasGrid.HalfHeight) / Spacing;
        return WorldAtlasGrid.Sample(heights, water, Columns, Rows, u, v, wrapColumn: true);
    }
}

public sealed class WorldAtlasTile
{
    public const int ByteLength = WorldFlightElevation.TileLength;
    private const int Side = WorldAtlasGrid.Side;
    private readonly short[] heights = new short[Side * Side];
    private readonly byte[] water = new byte[Side * Side];

    public WorldAtlasTile(WorldAtlasTileKey key, ReadOnlySpan<byte> data)
    {
        if (!key.IsValid || key.Level < 1 || data.Length != ByteLength || !data[..4].SequenceEqual("WFT1"u8) ||
            BinaryPrimitives.ReadInt32LittleEndian(data[4..]) != key.Level ||
            BinaryPrimitives.ReadInt32LittleEndian(data[8..]) != key.X ||
            BinaryPrimitives.ReadInt32LittleEndian(data[12..]) != key.Z)
            throw new InvalidDataException($"Invalid WFT1 atlas tile header or size for '{key}'.");
        Key = key;
        WorldAtlasGrid.ReadSamples(data[16..], heights, water);
    }

    public WorldAtlasTileKey Key { get; }

    public (short Height, byte Water) Vertex(int x, int z)
    {
        var index = z * Side + x;
        return (heights[index], water[index]);
    }

    public (double Height, byte Water) Sample(double x, double z)
    {
        var step = WorldAtlasGrid.Step(Key.Level);
        var span = WorldAtlasGrid.Span(Key.Level);
        return WorldAtlasGrid.Sample(heights, water, Side, Side,
            Math.Clamp((x - Key.X * (double)span) / step, 0, Side - 1),
            Math.Clamp((z - Key.Z * (double)span) / step, 0, Side - 1), wrapColumn: false);
    }
}

public sealed class WorldAtlasElevation(WorldAtlasBase fallback)
{
    private readonly Dictionary<WorldAtlasTileKey, WorldAtlasTile> tiles = [];

    public void Install(WorldAtlasTileKey parent, ReadOnlySpan<byte> data)
    {
        if (!parent.IsValid || parent.Level is < 0 or >= 8 || data.Length != 4 * WorldAtlasTile.ByteLength ||
            parent.Level > 0 && !tiles.ContainsKey(parent) || tiles.ContainsKey(parent.Child(0)))
            throw new InvalidDataException("Atlas terrain activation needs a complete quartet and its active parent.");
        var children = new WorldAtlasTile[4];
        for (var i = 0; i < children.Length; i++)
            children[i] = new(parent.Child(i), data.Slice(i * WorldAtlasTile.ByteLength, WorldAtlasTile.ByteLength));
        foreach (var child in children) tiles.Add(child.Key, child);
    }

    public void Remove(WorldAtlasTileKey parent)
    {
        if (parent.Level is < 0 or >= 8 || !parent.IsValid)
            throw new ArgumentOutOfRangeException(nameof(parent));
        for (var i = 0; i < 4; i++)
        {
            var child = parent.Child(i);
            if (tiles.Remove(child) && child.Level < 8) Remove(child);
        }
    }

    public (double Height, byte Water) Sample(double x, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z))
            throw new ArgumentOutOfRangeException(nameof(x), "Atlas sample positions must be finite.");
        x = WorldAtlasGrid.Wrap(x);
        z = Math.Clamp(z, -WorldAtlasGrid.HalfHeight, WorldAtlasGrid.HalfHeight);
        WorldAtlasTile? deepest = null;
        for (var level = 1; level <= 8; level++)
        {
            var key = WorldAtlasTileKey.At(level, x, z);
            if (!tiles.TryGetValue(key, out var tile)) break;
            deepest = tile;
        }
        return deepest?.Sample(x, z) ?? fallback.Sample(x, z);
    }
}
