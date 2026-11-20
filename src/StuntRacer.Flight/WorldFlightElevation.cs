using System.Buffers.Binary;

namespace StuntRacer.Flight;

public sealed record WorldFlightDestination(
    string Id, string Name, double X, double Z, double Altitude, double Heading, double TargetX, double TargetZ);

public sealed record WorldFlightDetail(string Name, WorldFlightDestination Destination);

public sealed class WorldFlightElevation
{
    public const int Side = 33;
    public const int TileLength = 16 + Side * Side * 3;
    private readonly Dictionary<(int Level, int X, int Z), Tile> _tiles = [];

    public int Count => _tiles.Count;
    public static int Spacing(int level) => level is >= 0 and <= 8 ? 8192 >> level
        : throw new ArgumentOutOfRangeException(nameof(level));

    public void AddTile(ReadOnlySpan<byte> data)
    {
        if (data.Length != TileLength)
            throw new InvalidDataException("World Flight elevation tile has invalid dimensions.");
        AddTile(BinaryPrimitives.ReadInt32LittleEndian(data[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[8..]), BinaryPrimitives.ReadInt32LittleEndian(data[12..]), data);
    }

    public void AddTile(int level, int x, int z, ReadOnlySpan<byte> data)
    {
        _ = Spacing(level);
        if (x is < -4096 or > 4096 || z is < -4096 or > 4096 || data.Length != TileLength ||
            !data[..4].SequenceEqual("WFT1"u8) || BinaryPrimitives.ReadInt32LittleEndian(data[4..]) != level ||
            BinaryPrimitives.ReadInt32LittleEndian(data[8..]) != x || BinaryPrimitives.ReadInt32LittleEndian(data[12..]) != z)
            throw new InvalidDataException("World Flight elevation tile has invalid dimensions or coordinates.");
        var heights = new short[Side * Side];
        var water = data[(16 + Side * Side * 2)..].ToArray();
        for (var i = 0; i < heights.Length; i++)
        {
            heights[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(16 + i * 2)..]);
            if (heights[i] is < -12000 or > 10000 || water[i] > 2)
                throw new InvalidDataException("World Flight elevation tile has an invalid height or water code.");
        }
        _tiles[(level, x, z)] = new(heights, water);
    }

    public void RemoveTile(int level, int x, int z) => _tiles.Remove((level, x, z));

    public (double Height, bool IsWater)? Sample(double x, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z))
            throw new ArgumentOutOfRangeException(nameof(x), "World Flight ground positions must be finite.");
        for (var level = 8; level >= 0; level--)
        {
            var spacing = Spacing(level);
            var span = spacing * (Side - 1);
            var ix = (int)Math.Floor(x / span);
            var iz = (int)Math.Floor(z / span);
            if (!_tiles.TryGetValue((level, ix, iz), out var tile))
                continue;

            var u = (x - ix * (double)span) / spacing;
            var v = (z - iz * (double)span) / spacing;
            var i = Math.Clamp((int)u, 0, Side - 2);
            var j = Math.Clamp((int)v, 0, Side - 2);
            var a = j * Side + i;
            var b = a + Side;
            var tx = u - i;
            var tz = v - j;
            var h = tile.Heights;
            // Same diagonal as the WebGL mesh: southwest to northeast.
            var y = tx <= tz
                ? (1 - tz) * h[a] + (tz - tx) * h[b] + tx * h[b + 1]
                : (1 - tx) * h[a] + tz * h[b + 1] + (tx - tz) * h[a + 1];
            var wet = tile.Water[(j + (tz >= 0.5 ? 1 : 0)) * Side + i + (tx >= 0.5 ? 1 : 0)] != 0;
            return (y, wet);
        }
        return null;
    }

    private sealed record Tile(short[] Heights, byte[] Water);
}
