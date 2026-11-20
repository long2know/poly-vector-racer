namespace StuntRacer.FlightReference;

public sealed partial class FlightPlayerKernel
{
    private bool Water(int x, int z)
    {
        const int terrain = 0x47f40;
        const int count = 2886;
        short At(int word)
        {
            if ((uint)word >= count) throw new NotSupportedException("Malformed/out-of-envelope original terrain walk.");
            return data.FlatWord(terrain + 2 * word);
        }
        var cell = ((x >> 24) & 255) + 32 * ((z >> 24) & 255);
        var offset = At(cell);
        if (offset <= 0) return false;
        if ((offset & 1) != 0) throw new ArgumentException("Terrain record offset is not word aligned.");
        var cursor = offset / 2;
        if (At(cursor) < 0) return false;
        cursor += 2;
        var px = x & 0xffffff;
        var pz = z & 0xffffff;
        for (var polygon = 0; polygon < 512; polygon++)
        {
            var n = (int)At(cursor++);
            if (n == -1) return false;
            if (n <= 0) n = At(cursor++);
            if (n is < 1 or > 1024 || cursor + 2 * n > count)
                throw new ArgumentException("Malformed original terrain polygon.");
            var intersections = 0;
            for (var edge = 0; edge < n; edge++)
            {
                var next = (edge + 1) % n;
                var x1 = At(cursor + 2 * edge) * 4096;
                var z1 = At(cursor + 2 * edge + 1) * 4096;
                var x2 = At(cursor + 2 * next) * 4096;
                var z2 = At(cursor + 2 * next + 1) * 4096;
                if (!EdgeHeight(x1, z1, x2, z2, px, out var height) || height < pz) continue;
                if (Math.Abs(px - x1) < 768 || Math.Abs(px - x2) < 768) return false;
                if (z1 == z2 ? px > Math.Min(x1, x2) && px < Math.Max(x1, x2)
                    : height > Math.Min(z1, z2) && height < Math.Max(z1, z2))
                    intersections++;
            }
            if ((intersections & 1) != 0) return true;
            cursor += 2 * n;
        }
        throw new ArgumentException("Terrain polygon walk exceeded its finite budget.");
    }

    private static bool EdgeHeight(int x1, int z1, int x2, int z2, int px, out int height)
    {
        height = 0;
        var dx = x2 - x1;
        var dz = z2 - z1;
        if ((dx >> 8) == 0) return false;
        var shifts = 0;
        while ((Abs(dx >> 8) >> shifts) > 32767) shifts++;
        dx >>= shifts; dz >>= shifts;
        var ax = Abs(dx);
        var az = Abs(dz);
        int exponent, numerator, denominator;
        if ((long)ax >= 4L * az) { exponent = -16; numerator = unchecked(dz * 64); denominator = dx >> 10; }
        else if ((long)ax >= 2L * az) { exponent = -15; numerator = unchecked(dz * 64); denominator = dx >> 9; }
        else if (ax >= az) { exponent = -14; numerator = unchecked(dz * 64); denominator = dx >> 8; }
        else
        {
            var multiplier = 32; exponent = -13;
            while (multiplier > 4 && (long)ax * (64 / multiplier) < az) { multiplier >>= 1; exponent++; }
            if ((long)ax * (64 / multiplier) < az) { multiplier = 1; exponent = -8; }
            numerator = unchecked(dz * multiplier); denominator = dx >> 8;
        }
        var divisor = W(denominator);
        if (divisor == 0) throw new NotSupportedException("Original terrain edge would divide by zero.");
        var quotient = (long)numerator / divisor;
        var slope = quotient is >= -32768 and <= 32767 ? (int)quotient : W(numerator);
        var u = px - x1;
        var shift = 0;
        while ((Abs(u) >> shift) > 32767) shift++;
        u >>= shift; exponent += shift;
        var value = W(u) * slope;
        height = unchecked(z1 + (exponent < 0 ? value >> -exponent : value << exponent));
        return true;
    }

    private bool Pad(int x, int z)
    {
        for (var entry = 0; entry < 5; entry++)
        {
            var at = 0x47b72 + entry * 16;
            var cellX = data.FlatWord(at + 2);
            if (cellX == -1) return false;
            if (cellX < 0) continue;
            var cellZ = data.FlatWord(at + 4);
            var direction = data.FlatWord(at + 6);
            if ((uint)direction >= 16) throw new ArgumentException("Invalid pad direction index.");
            var rx = unchecked((cellX << 24) + (data.FlatWord(0x206f2 + 4 * direction) + data.FlatWord(at + 8)) * 1024);
            var rz = unchecked((cellZ << 24) + (data.FlatWord(0x206f4 + 4 * direction) + data.FlatWord(at + 10)) * 1024);
            if (Abs(unchecked(rx - x)) > 0x180000 || Abs(unchecked(rz - z) >> 8) > 0x180000) continue;
            var quads = data.FlatLong(0x2600c + data.FlatWord(at) + 0x10);
            for (var quad = 0; quad < 5; quad++)
            {
                var p = quads + quad * 16;
                if (data.FlatWord(p) == -1) break;
                var inside = true;
                for (var edge = 0; edge < 4 && inside; edge++)
                {
                    var q = (edge + 1) & 3;
                    var ax = data.FlatWord(p + edge * 4);
                    var az = data.FlatWord(p + edge * 4 + 2);
                    var bx = data.FlatWord(p + q * 4);
                    var bz = data.FlatWord(p + q * 4 + 2);
                    var ex = W(bx - ax);
                    var ez = W(-(bz - az));
                    var vx = W(unchecked((ax << 8) + rx - x) >> 8);
                    var vz = W(unchecked((az << 8) + rz - z) >> 8);
                    inside = unchecked(ez * vx + ex * vz) >= 0;
                }
                if (inside) return true;
            }
        }
        return false;
    }

    public byte QueryTerrain(int x, int z, byte previousSurface)
        => (byte)((previousSurface & ~6) | (Water(x, z) ? 2 : 0) | (Pad(x, z) ? 4 : 0));

    private List<int[]> ShipGroups(int address, int bytes)
    {
        var result = new List<int[]>();
        var current = new List<int>();
        var end = address + bytes;
        while (address + 2 <= end)
        {
            if (data.FlatWord(address) == -1)
            {
                address += 2;
                if (current.Count == 0) break;
                result.Add(current.ToArray());
                if (result.Count > 8) throw new ArgumentException("Too many ship plane groups.");
                current.Clear();
            }
            else
            {
                if (address + 4 > end || current.Count == 8) throw new ArgumentException("Malformed ship pointer group.");
                current.Add(data.FlatLong(address)); address += 4;
            }
        }
        if (current.Count > 0) result.Add(current.ToArray());
        return result;
    }

    private static RawVector ShipVertex(FlightCarrier ship, int offset)
    {
        var slot = offset / 6;
        if (offset < 0 || slot >= ship.RotatedVertices.Length)
            throw new ArgumentException("A ship structure names an unavailable vertex.");
        return ship.RotatedVertices[slot];
    }
    private static RawVector ShipFine(FlightCarrier ship) => new(
        (short)((ship.Position.X & 0x3fffff) >> 8), W(ship.Position.Y >> 8), (short)((ship.Position.Z & 0x3fffff) >> 8));

    private bool ShipVolume(FlightCarrier ship, IEnumerable<int> planes, RawVector point)
    {
        var any = false;
        var origin = ShipFine(ship);
        foreach (var address in planes)
        {
            any = true;
            var a = ShipVertex(ship, data.FlatWord(address + 2));
            var b = ShipVertex(ship, data.FlatWord(address + 4));
            var c = ShipVertex(ship, data.FlatWord(address + 6) & 0xfff);
            var u = new RawVector(W(b.X - a.X), W(b.Y - a.Y), W(b.Z - a.Z));
            var v = new RawVector(W(c.X - a.X), W(c.Y - a.Y), W(c.Z - a.Z));
            var shift = 7 + ship.UnitShift;
            var nx = W(unchecked(u.Y * v.Z - u.Z * v.Y) >> shift);
            var ny = W(unchecked(u.Z * v.X - u.X * v.Z) >> shift);
            var nz = W(unchecked(u.X * v.Y - u.Y * v.X) >> shift);
            var ax = W((a.X >> ship.UnitShift) + origin.X);
            var ay = W((a.Y >> ship.UnitShift) + origin.Y);
            var az = W((a.Z >> ship.UnitShift) + origin.Z);
            var dot = unchecked(W(point.X - ax) * nx + W(point.Y - ay) * ny + W(point.Z - az) * nz);
            if (dot >= 0) return false;
        }
        return any;
    }

    private bool DeckContains(FlightCarrier ship, int address, int end, RawVector point)
    {
        var fine = ShipFine(ship);
        for (var polygon = 0; polygon < 64; polygon++)
        {
            if (address + 2 > end) throw new ArgumentException("Unterminated deck polygon list.");
            if (data.FlatWord(address) == -1) return false;
            var vertices = new List<RawVector>();
            bool last;
            do
            {
                if (address + 2 > end || vertices.Count == 16) throw new ArgumentException("Malformed deck polygon.");
                var word = (ushort)data.FlatWord(address); address += 2;
                last = (word & 0x8000) != 0;
                vertices.Add(ShipVertex(ship, word & 0x7fff));
            } while (!last);
            var inside = vertices.Count >= 3;
            for (var edge = 0; edge < vertices.Count && inside; edge++)
            {
                var a = vertices[edge];
                var b = vertices[(edge + 1) % vertices.Count];
                var px = W(point.X - W((a.X >> ship.UnitShift) + fine.X));
                var pz = W(point.Z - W((a.Z >> ship.UnitShift) + fine.Z));
                inside = unchecked(px * W(-(b.Z - a.Z)) + pz * W(b.X - a.X)) < 0;
            }
            if (inside) return true;
        }
        throw new ArgumentException("Deck polygon budget exceeded.");
    }

    private static bool Corridor(FlightCarrier ship, FlightPlayerState state, RawVector point)
    {
        var first = ship.AircraftClass == 0x20 ? 54 : 44;
        var a = ship.RotatedVertices[first];
        var b = ship.RotatedVertices[first + 1];
        var c = ship.RotatedVertices[first + 2];
        var q = new RawVector(W(a.X + ((a.X - c.X) >> 1)), 0, W(a.Z + ((a.Z - c.Z) >> 1)));
        var fine = ShipFine(ship);
        int Dot(RawVector e1, RawVector e2, RawVector v)
        {
            var ex = W(e1.X - e2.X);
            var ez = W(-(e1.Z - e2.Z));
            var ux = W((v.X >> ship.UnitShift) + fine.X - point.X);
            var uz = W((v.Z >> ship.UnitShift) + fine.Z - point.Z);
            return unchecked(ez * ux + ex * uz);
        }
        return Dot(a, c, c) >= 0 && Dot(a, c, b) < 0 && Dot(b, c, c) < 0 && Dot(b, c, q) >= 0 &&
               unchecked(state.MatrixB.M2 * ship.MatrixB.M2 + state.MatrixB.M8 * ship.MatrixB.M8) >= 0xe000000;
    }

    private int ShipContact(ref FlightPlayerState state, FlightCarrier ship, RawPosition previous)
    {
        var raw = state.Raw;
        if ((ship.Status & 0x40) == 0 || (ship.Status & 0x600) != 0 || ship.Group == state.Group ||
            ((ship.Status | raw.Status) & 8) == 0 ||
            Abs(unchecked(previous.X - ship.Position.X)) > 0xa0000 ||
            Abs(unchecked(previous.Y - ship.Position.Y)) > 0xa0000 ||
            Abs(unchecked(previous.Z - ship.Position.Z)) > 0xa0000)
            return 0;
        state.ContactFlag = 1;
        var carrier = ship.AircraftClass == 0x20;
        var list = ShipGroups(carrier ? 0x3e0f8 : 0x3edcc, carrier ? 124 : 32);
        var second = ShipGroups(carrier ? 0x3e174 : 0x3edec, 44);
        if (list.Count == 0 || list[0].Length == 0) throw new ArgumentException("No ship deck reference.");
        var start = list[0][0];
        var limit = (carrier ? 0x3e6ae + 684 : 0x3f2b6 + 362);
        foreach (var pointer in list.SelectMany(group => group))
            if (pointer > start) limit = Math.Min(limit, pointer);
        var viewed = raw.ViewedEntityIndex == 0;
        for (var pass = 0; pass < (viewed ? 2 : 1); pass++)
        {
            var vertex = viewed ? Vertex(state, pass == 0 ? 0 : 3) : default;
            var shift = state.ModelUnitShift;
            var point = new RawVector(W(((previous.X & 0x3fffff) >> 8) + (vertex.X >> shift)),
                W((previous.Y >> 8) + (vertex.Y >> shift)),
                W(((previous.Z & 0x3fffff) >> 8) + (vertex.Z >> shift)));
            var height = ShipVertex(ship, data.FlatWord(start)).Y >> ship.UnitShift;
            if (point.Y < height)
            {
                if (ShipVolume(ship, list[0].Skip(1), point) || list.Skip(1).Any(group => ShipVolume(ship, group, point))) return 0x20;
                return 0;
            }
            if (!DeckContains(ship, start, limit, point)) continue;
            raw.Surface |= (byte)(pass == 0 ? 0x40 : 0x80);
            state.DeckHeight = W(height);
            if (pass == 1)
            {
                if (raw.OnGround && Corridor(ship, state, point)) raw.Configuration |= 0x4000;
                else if ((raw.Configuration & 0xc000) != 0xc000) raw.Configuration &= 0xbfff;
            }
            state.Raw = raw;
            int GroupHeight(int group) => ShipVertex(ship, data.FlatWord(second[group][0] + 2)).Y >> ship.UnitShift;
            if (second.Count > 0 && second[0].Length > 0)
            {
                if (W(raw.Altitude256) < GroupHeight(0))
                {
                    if (ShipVolume(ship, second[0].Skip(1), point)) return 0x20;
                }
                else if (second.Count > 1 && second[1].Length > 0 && W(raw.Altitude256) < GroupHeight(1) + 7 &&
                         ShipVolume(ship, second[1].Skip(1), point)) return 0x20;
            }
        }
        state.Raw = raw;
        return 0;
    }

    private static bool Contains(FlightContactBox box, int x, int y, int z) =>
        y <= box.MaximumY && x > box.MinimumX && x < box.MaximumX && z > box.MinimumZ && z < box.MaximumZ;

    private static int LandmarkContact(ref FlightPlayerState state, FlightEnvironment environment)
    {
        var raw = state.Raw;
        if (raw.Altitude256 > 32767) return 0;
        foreach (var slot in environment.Landmarks)
        {
            var dx = unchecked((sbyte)(raw.CellX - slot.CellX));
            var dz = unchecked((sbyte)(raw.CellZ - slot.CellZ));
            if (Math.Abs((int)dx) > 1 || Math.Abs((int)dz) > 1) continue;
            var x = W(raw.FineX + dx * 16384);
            var z = W(raw.FineZ + dz * 16384 + environment.LandmarkZBias);
            var y = W(raw.Altitude256);
            if (!Contains(slot.Box, x, y, z)) continue;
            state.ContactFlag = 1;
            foreach (var node in slot.Nodes)
            {
                if (!Contains(node.Box, x, y, z)) continue;
                if ((raw.Configuration & 2) == 0) return 0;
                for (var v = 0; v < 3; v++)
                {
                    var vertex = Vertex(state, v);
                    var point = new RawVector(W(x + (vertex.X >> state.ModelUnitShift)),
                        W(y + (vertex.Y >> state.ModelUnitShift)), W(z + (vertex.Z >> state.ModelUnitShift)));
                    var inside = true;
                    foreach (var plane in node.Planes)
                    {
                        var dot = unchecked(W(point.X - plane.Reference.X) * plane.Normal.X +
                            W(point.Y - plane.Reference.Y) * plane.Normal.Y +
                            W(point.Z - plane.Reference.Z) * plane.Normal.Z);
                        if (dot >= 0) { inside = false; break; }
                    }
                    if (inside) return 0x20;
                }
                return 0;
            }
        }
        return 0;
    }

    private int Contact(ref FlightPlayerState state, FlightEnvironment environment, RawPosition previous)
    {
        state.Raw = state.Raw with { Surface = (byte)(state.Raw.Surface & ~0xc0) };
        if (environment.Carrier is { } ship)
        {
            var result = ShipContact(ref state, ship, previous);
            if (result != 0) return result;
        }
        var landmark = LandmarkContact(ref state, environment);
        if (landmark != 0) return landmark;
        if (state.ModelPartId < 128 && (state.ModelPartId & 15) == 0)
            for (var vertex = 0; vertex < 3; vertex++)
                if ((Vertex(state, vertex).Y >> state.ModelUnitShift) + state.Raw.Altitude256 < 0)
                    return 0x10;
        return state.Raw.Altitude256 < 0 ? 0x10 : 0;
    }
}
