namespace StuntRacer.FlightData;

/// <summary>Original, unreflected position-unit offset from a placed model's origin.</summary>
public readonly record struct FlightModelPoint(long X, long Y, long Z);
public sealed record FlightModelTriangle(int A, int B, int C, ushort ColourWord, int SourceAddress);
public sealed record FlightModelLine(int A, int B, ushort ColourWord, int SourceAddress);
/// <summary>
/// SourceVertices is the model-unit slot table including generated slots (not a new contact
/// model). Vertices contains position-unit offsets; triangle/line indices address that list.
/// Add the original placement before converting units or reflecting X for display.
/// </summary>
public sealed record FlightModelGeometry(
    IReadOnlyList<FlightVertex> SourceVertices,
    IReadOnlyList<FlightModelPoint> Vertices,
    IReadOnlyList<FlightModelTriangle> Triangles,
    IReadOnlyList<FlightModelLine> Lines,
    IReadOnlyList<string> Limitations,
    int DegeneratePolygons);

/// <summary>
/// Nonexecuting materialization of the documented model programs (reference notes 02/03).
/// Does not modify Scene, package hashes, raw contact slots or player state. Vertices are
/// nearest-detail source position units, not camera coordinates or collision units.
/// Static geometry is the union of BSP/visibility branches; this is not a painter emulator.
/// </summary>
public static class FlightModelMaterializer
{
    public static FlightModelGeometry Materialize(FlightDiskPackage package, int modelId, int levelIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(package);
        var model = package.Scene.Models.SingleOrDefault(m => m.Id == modelId)
            ?? throw new ArgumentOutOfRangeException(nameof(modelId));
        if (levelIndex < 0 || levelIndex >= model.Levels.Count)
            throw new FlightDataException("models.materialize", $"Model {modelId} has no level {levelIndex}.");
        return Materialize(package.FlatImage, model, model.Levels[levelIndex]);
    }

    internal static FlightModelGeometry Materialize(FlightImage image, FlightModel model, FlightModelLevel level) =>
        new Builder(image, model, level).Build();

    private readonly record struct Point(short X, short Y, short Z)
    {
        public static Point operator +(Point a, Point b) => new(
            unchecked((short)(a.X + b.X)), unchecked((short)(a.Y + b.Y)), unchecked((short)(a.Z + b.Z)));
        public static Point operator -(Point a, Point b) => new(
            unchecked((short)(a.X - b.X)), unchecked((short)(a.Y - b.Y)), unchecked((short)(a.Z - b.Z)));
        public static Point operator *(Point a, int n) => new(
            unchecked((short)(a.X * n)), unchecked((short)(a.Y * n)), unchecked((short)(a.Z * n)));
        public Point Half => new((short)(X >> 1), (short)(Y >> 1), (short)(Z >> 1));
        public Point Shift(int shift) => shift >= 0
            ? new(unchecked((short)(X << shift)), unchecked((short)(Y << shift)), unchecked((short)(Z << shift)))
            : new((short)(X >> -shift), (short)(Y >> -shift), (short)(Z >> -shift));
    }

    private sealed class Builder(FlightImage image, FlightModel model, FlightModelLevel level)
    {
        private readonly SortedDictionary<int, Point> slots = [];
        private readonly Dictionary<Point, int> pointIndices = [];
        private readonly List<FlightModelPoint> points = [];
        private readonly List<FlightModelTriangle> triangles = [];
        private readonly List<FlightModelLine> lines = [];
        private readonly HashSet<(int, int, int, int)> triangleKeys = [];
        private readonly HashSet<(int, int, int)> lineKeys = [];
        private readonly HashSet<string> limitations = [];
        private int degenerates;
        private int address = level.BlockAddress;
        private long Unit => 1L << level.ModelToPositionShift;

        internal FlightModelGeometry Build()
        {
            if (level.ModelToPositionShift is < 0 or > 8)
                Fail("Unsupported model-to-position exponent.");
            foreach (var vertex in level.Vertices) Put(vertex.Slot, new(vertex.X, vertex.Y, vertex.Z));
            if ((level.Flags & 8) != 0) Strips();
            if ((level.Flags & 4) != 0 && level.InitialVertexCount >= 2) RelativeGroups();
            foreach (var operation in level.Operations)
            {
                address = operation.Address;
                Operation(operation);
            }
            return new(slots.Select(v => new FlightVertex(v.Key, v.Value.X, v.Value.Y, v.Value.Z)).ToArray(),
                points.AsReadOnly(), triangles.AsReadOnly(), lines.AsReadOnly(),
                limitations.Order(StringComparer.Ordinal).ToArray(), degenerates);
        }

        private void Fail(string message) => throw new FlightDataException("models.materialize",
            $"Model {model.Id}, 0x{address:X}: {message}", address);

        private Point Get(int slot)
        {
            if (!slots.TryGetValue(slot, out var point)) Fail($"Undefined source vertex slot {slot}.");
            return point;
        }

        private void Put(int slot, Point point)
        {
            if (slot is < 0 or >= 512) Fail($"Generated vertex slot {slot} exceeds the source budget.");
            slots[slot] = point;
        }

        private int Slot(ushort word)
        {
            if (word % 6 != 0 || word / 6 >= 512) Fail($"Invalid vertex byte offset {word}.");
            return word / 6;
        }

        private ushort Word(ref int p)
        {
            var value = image.ReadUInt16(p);
            p = checked(p + 2);
            return value;
        }

        private Point ReadPoint(ref int p, bool flat = false)
        {
            short x = unchecked((short)Word(ref p));
            short y = flat ? (short)0 : unchecked((short)Word(ref p));
            return new(x, y, unchecked((short)Word(ref p)));
        }

        private void Strips()
        {
            int p = level.BlockAddress + 10 + level.InitialVertexCount * 6;
            int first = level.InitialVertexCount;
            for (int chains = 0; chains < 512; chains++)
            {
                address = p;
                short count = unchecked((short)Word(ref p));
                if (count < 0) return;
                if (count > 512) Fail("Oversized strip chain.");
                var previous = ReadPoint(ref p);
                Put(first, previous);
                int next = first + 2;
                for (int i = 0; i < Math.Max(1, (int)count); i++)
                {
                    var end = ReadPoint(ref p);
                    int divisions = unchecked((short)Word(ref p));
                    Segment(previous, end, divisions, ref next, 2);
                    previous = end;
                }
                ushort back = Word(ref p);
                int backCount = back & 0x7FFF;
                if (backCount > 512) Fail("Oversized strip return chain.");
                int reverse = next - ((back & 0x8000) == 0 ? 3 : 1);
                for (int i = 0; i < backCount; i++)
                {
                    var end = ReadPoint(ref p);
                    int divisions = unchecked((short)Word(ref p));
                    Segment(previous, end, divisions, ref reverse, -2);
                    previous = end;
                }
                first = next;
            }
            Fail("Unterminated strip section.");
        }

        private void Segment(Point a, Point b, int divisions, ref int slot, int stride)
        {
            if (divisions > 512) Fail("Oversized strip subdivision.");
            if (divisions > 1)
            {
                int dx = ((b.X - a.X) << 4) / divisions;
                int dy = ((b.Y - a.Y) << 4) / divisions;
                int dz = ((b.Z - a.Z) << 4) / divisions;
                for (int k = 1; k < divisions; k++)
                {
                    Put(slot, new(unchecked((short)(((a.X << 4) + k * dx) >> 4)),
                        unchecked((short)(((a.Y << 4) + k * dy) >> 4)),
                        unchecked((short)(((a.Z << 4) + k * dz) >> 4))));
                    slot += stride;
                }
            }
            Put(slot, b);
            slot += stride;
        }

        private void RelativeGroups()
        {
            int p = level.BlockAddress + 10 + level.InitialVertexCount * 4;
            int slot = level.InitialVertexCount;
            for (int groups = 0; groups < 512; groups++)
            {
                address = p;
                short count = unchecked((short)Word(ref p));
                if (count < 0) return;
                if (count > 256) Fail("Oversized relative vertex group.");
                var delta = Get(slot - 1) - Get(slot - 2);
                for (int i = 0; i < Math.Max(1, (int)count); i++)
                {
                    var point = ReadPoint(ref p, true);
                    Put(slot++, point);
                    Put(slot++, point + delta);
                }
                if (unchecked((short)Word(ref p)) < 0) return;
                Put(slot++, ReadPoint(ref p, true));
                Put(slot++, ReadPoint(ref p, true));
            }
            Fail("Unterminated relative vertex section.");
        }

        private int Index(Point point)
        {
            if (pointIndices.TryGetValue(point, out int index)) return index;
            if (points.Count >= 65536) Fail("Materialized point budget exceeded.");
            index = points.Count;
            pointIndices.Add(point, index);
            points.Add(new(point.X * Unit, point.Y * Unit, point.Z * Unit));
            return index;
        }

        private void Line(Point a, Point b, ushort colour)
        {
            if (a == b) return;
            int ia = Index(a), ib = Index(b);
            if (lineKeys.Add((Math.Min(ia, ib), Math.Max(ia, ib), colour & 15)))
                lines.Add(new(ia, ib, colour, address));
        }

        private void Polygon(ushort colour, params Point[] vertices)
        {
            var polygon = vertices.Distinct().ToArray();
            if (polygon.Length < 3) { degenerates++; return; }
            // Source model faces are planar. Project on the dominant normal axis for
            // ear clipping; the ground patches are not all convex.
            long nx = 0, ny = 0, nz = 0;
            for (int i = 0; i < polygon.Length; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Length];
                nx += (long)(a.Y - b.Y) * (a.Z + b.Z);
                ny += (long)(a.Z - b.Z) * (a.X + b.X);
                nz += (long)(a.X - b.X) * (a.Y + b.Y);
            }
            if (nx == 0 && ny == 0 && nz == 0) { degenerates++; return; }
            int axis = Math.Abs(nx) >= Math.Abs(ny) && Math.Abs(nx) >= Math.Abs(nz) ? 0
                : Math.Abs(ny) >= Math.Abs(nz) ? 1 : 2;
            (long X, long Y) Project(Point p) => axis == 0 ? (p.Y, p.Z) : axis == 1 ? (p.Z, p.X) : (p.X, p.Y);
            var projected = polygon.Select(Project).ToArray();
            long Cross(int a, int b, int c) =>
                (projected[b].X - projected[a].X) * (projected[c].Y - projected[a].Y) -
                (projected[b].Y - projected[a].Y) * (projected[c].X - projected[a].X);
            long area = projected.Select((p, i) => p.X * projected[(i + 1) % polygon.Length].Y -
                projected[(i + 1) % polygon.Length].X * p.Y).Sum();
            int sign = Math.Sign(area);
            var remaining = Enumerable.Range(0, polygon.Length).ToList();
            while (remaining.Count > 3)
            {
                bool clipped = false;
                for (int i = 0; i < remaining.Count; i++)
                {
                    int a = remaining[(i + remaining.Count - 1) % remaining.Count], b = remaining[i],
                        c = remaining[(i + 1) % remaining.Count];
                    long cross = Cross(a, b, c) * sign;
                    if (cross == 0) { remaining.RemoveAt(i); clipped = true; break; }
                    if (cross < 0 || remaining.Any(p => p != a && p != b && p != c &&
                        Cross(a, b, p) * sign >= 0 && Cross(b, c, p) * sign >= 0 && Cross(c, a, p) * sign >= 0)) continue;
                    Triangle(polygon[a], polygon[b], polygon[c], colour);
                    remaining.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) Fail("Source polygon is not a simple planar boundary.");
            }
            if (remaining.Count == 3)
                Triangle(polygon[remaining[0]], polygon[remaining[1]], polygon[remaining[2]], colour);
        }

        private void Triangle(Point a, Point b, Point c, ushort colour)
        {
            int ia = Index(a), ib = Index(b), ic = Index(c);
            int low = Math.Min(ia, Math.Min(ib, ic)), high = Math.Max(ia, Math.Max(ib, ic));
            if (triangleKeys.Add((low, ia + ib + ic - low - high, high, colour & 15)))
                triangles.Add(new(ia, ib, ic, colour, address));
        }

        private void Operation(FlightModelOperation item)
        {
            var w = item.Operands;
            int op = item.Operation;
            Point V(int operand) => Get(Slot(w[operand]));
            Point S(int slot) => Get(slot);
            switch (op)
            {
                case 0: case 31: case 33: case 67:
                    return; // Camera/size/visibility gates do not remove static geometry.
                case 1: case 49: case 58: case 62: case 70: case 71:
                    limitations.Add("Camera-sized point/disc sprites and combat/effect animation are not static world surfaces.");
                    return;
                case 68: case 72: case 73:
                    limitations.Add("Animated source selectors use their decoded phase-zero geometry, not an invented animation.");
                    return;
                case 2: case 48: case 74:
                    Line(V(0), V(1), w[2]);
                    return;
                case 3:
                {
                    int end = 0;
                    while ((w[end] & 0x8000) == 0) end++;
                    Polygon(w[end + 1], w.Take(end + 1).Select(value => Get(Slot((ushort)(value & 0x7FFF)))).ToArray());
                    return;
                }
                case 4:
                    foreach (var polygon in level.Polygons.Where(p => p.Address == address))
                        Polygon(polygon.ColourWord, polygon.Slots.Select(Get).ToArray());
                    return;
                case 6:
                {
                    int count = unchecked((short)w[0]);
                    if (count is < 2 or > 257) Fail("Unsupported rung count (original buffer would overflow).");
                    Point Interpolate(Point a, Point b, int k)
                    {
                        short Axis(short x, short y) => unchecked((short)(((x << 4) + k * (((y - x) << 4) / count) + 8) >> 4));
                        return new(Axis(a.X, b.X), Axis(a.Y, b.Y), Axis(a.Z, b.Z));
                    }
                    for (int k = 1; k < count; k++) Line(Interpolate(V(1), V(2), k), Interpolate(V(3), V(4), k), w[5]);
                    return;
                }
                case 7:
                {
                    int b = Slot(w[4]);
                    Polygon(w[0], V(3), S(b), S(b + 2));
                    Polygon(w[0], V(3), S(b + 1), S(b) - S(b + 1) + S(b + 2));
                    return;
                }
                case 8:
                {
                    int c = Slot(w[1]), b = Slot(w[5]);
                    var p = V(4) - (S(c + 1) - S(c));
                    var e1 = S(b + 1) - S(b);
                    var e2 = S(b + 2) - S(b + 1);
                    Polygon(w[0], p, S(b + 3), S(b + 3) + e1 + e2);
                    Polygon(w[0], p, S(b + 3) + e1, S(b + 3) + e2);
                    return;
                }
                case 9: case 10: case 27: case 30:
                {
                    int b = Slot(w[1]);
                    var a = S(b); var c = S(b + 2); var d = a - S(b + 1) + c;
                    if (op == 10)
                    {
                        var offset = S(b + 3) - a;
                        Polygon(w[0], a + offset, S(b + 1) + offset, c + offset, d + offset);
                    }
                    else Polygon(w[0], a, S(b + 1), c, d);
                    return;
                }
                case 11: case 12:
                {
                    int count = Math.Max(1, w[0] >> 8), b = Slot(w[op == 11 ? 1 : 2]);
                    var delta = op == 11 ? default : S(Slot(w[1]) + 1) - V(1);
                    for (int i = 0; i < count; i++) Line(S(b + i * 2) - delta, S(b + i * 2 + 1) - delta, (ushort)(w[0] & 63));
                    return;
                }
                case 13: case 14: case 75:
                {
                    int start = op == 14 ? 2 : 1;
                    var delta = op == 14 ? S(Slot(w[1]) + 1) - V(1) : default;
                    for (int i = start; i + 1 < w.Count; i += 2)
                        Line(Get(Slot(w[i])) - delta, Get(Slot((ushort)(w[i + 1] & 0x7FFF))) - delta, w[0]);
                    return;
                }
                case 15:
                    Polygon(w[0], V(2), V(3), V(4), V(4) - (S(Slot(w[1]) + 1) - V(1)));
                    return;
                case 16:
                {
                    var delta = S(Slot(w[2]) + 1) - V(2);
                    Polygon(w[0], V(1), V(1) - delta, V(3) - delta, V(3));
                    return;
                }
                case 17: case 18:
                    for (int i = 0; i < w.Count && (w[i] & 0x8000) == 0; i += 4)
                        Polygon(12, Get(Slot(w[i])), Get(Slot(w[i + 1])), Get(Slot(w[i + 2])), Get(Slot(w[i + 3])));
                    return;
                case 19: case 20: case 21: case 22: case 23: case 32: case 57:
                    Hatch(op, w);
                    return;
                case 24:
                    Polygon(w[w.Count - 1], w.Skip(1).Take(w[0]).Select(value => Get(Slot(value))).ToArray());
                    return;
                case 25:
                    Polygon(w[1], Enumerable.Range(0, w[0]).Select(Get).ToArray());
                    return;
                case 26:
                    Polygon(7, Enumerable.Range(Slot(w[1]), w[0]).Select(Get).ToArray());
                    return;
                case 34: case 35: case 36: case 41:
                    Synthesize(op, Slot(w[0]), op == 35 ? 0 : unchecked((short)w[1]));
                    return;
                case 39:
                {
                    Polygon(w[0], S(0), S(1), S(2) + S(3) - S(1), S(0) - S(1) * 2 + S(2) + S(3));
                    Polygon(w[1], S(2), S(3), S(3) + S(0) - S(1), S(2) + S(0) - S(1));
                    Polygon(w[2], S(0), S(0) + S(2) - S(1), S(0) - S(1) * 2 + S(2) + S(3), S(0) + S(3) - S(1));
                    return;
                }
                case 40:
                    Polygon(w[0], S(0), S(1), S(4));
                    Polygon(w[1], S(0), S(2), S(3));
                    return;
                case 55:
                    foreach (var polygon in level.Polygons.Where(p => p.Operation == 55))
                        Polygon(polygon.ColourWord, polygon.Slots.Select(Get).ToArray());
                    return;
                case 69:
                    Polygon(w[3], V(0), V(1), V(2), V(0) - V(1) + V(2));
                    return;
                default:
                    Fail($"Opcode {op} requires a non-static entity state; no substitute geometry was emitted.");
                    return;
            }
        }

        private void Synthesize(int op, int b, int shift)
        {
            if (shift is < -15 or > 15) Fail("Invalid synthesis shift.");
            Point V(int i) => Get(b + i);
            void Set(int i, Point p) => Put(b + i, p);
            if (op is 34 or 35)
            {
                Set(4, V(0) - V(1) + V(2)); Set(5, V(0) - V(1) + V(3));
                Set(6, V(3) + V(2) - V(1)); Set(7, V(0) - V(1) * 2 + V(2) + V(3));
                if (op == 34)
                {
                    var d = (V(2) - V(3)).Shift(shift);
                    Set(8, V(3) - d); Set(9, V(3) - d - V(1) + V(2));
                }
            }
            else if (op == 36)
            {
                for (int i = 0; i < 4; i++) Set(6 + i, V(0) - V(1) + V(2 + i));
                Set(10, V(2) + V(5) - V(1)); Set(11, V(0) - V(1) * 2 + V(2) + V(5));
                var d = (V(3) - V(4)).Shift(shift);
                Set(12, V(4) - d); Set(13, V(4) - d - V(2) + V(3));
            }
            else
            {
                var d = (V(2) - V(3)).Shift(shift);
                Set(9, V(3) + V(2) - V(1)); Set(10, V(3) - d);
                Set(11, V(5) - V(6) + V(7)); Set(12, V(11) + V(8) - V(6));
                var offset = V(4) - V(6);
                Set(13, V(5) + offset); Set(14, V(8) + offset); Set(15, V(7) + offset);
                Set(16, V(11) + offset); Set(17, V(12) + offset);
            }
        }

        private void Hatch(int op, IReadOnlyList<ushort> words)
        {
            bool explicitPen = op is 19 or 22 or 32 or 57;
            ushort pen = explicitPen ? words[0] : (ushort)13;
            int start = explicitPen ? 1 : 0, b = Slot(words[start]);
            int count = Math.Max(1, (int)unchecked((short)words[start + 1]));
            if (count > 512) Fail("Oversized hatch count.");
            Point S(int i) => Get(b + i);
            if (op is 19 or 20 or 32)
            {
                var p = S(0) - S(2); var q = S(1) - S(2);
                var acc = default(Point);
                for (int i = 0; i < count; i++, acc += q)
                {
                    var offset = acc.Half;
                    if (op == 19) Line(S(2) + offset, S(0) + offset, pen);
                    else Polygon(pen, S(2) + offset, S(0) + offset, S(3) + p + offset, S(3) + offset);
                }
                acc = default;
                for (int i = 0; i < count; i++, acc += q)
                {
                    var offset = q.Half + acc.Half;
                    if (op == 19) Line(S(0) - offset, S(2) - offset, pen);
                    else Polygon(pen, S(0) - offset, S(2) - offset, S(3) - offset, S(3) + p - offset);
                }
            }
            else if (op == 23)
            {
                var p = S(0) - S(3); var q = S(1) - S(3); var r = S(2) - S(3);
                var width = p + (q + q * 2).Half;
                for (int i = 0; i < count; i++)
                {
                    var a = S(3 + i);
                    Polygon(pen, a, a + width, a + r + width, a + r);
                }
            }
            else
            {
                var p = op == 22 ? default : S(0) - S(3);
                var q = op == 22 ? S(0) - S(2) : S(1) - S(3);
                var r = op == 22 ? default : S(2) - S(3);
                for (int row = 0; row < count; row++)
                {
                    int strokes = Math.Max(1, (int)unchecked((short)words[start + 2 + row]));
                    if (strokes > 512) Fail("Oversized hatch row.");
                    var a = S((op == 22 ? 2 : 3) + row);
                    var acc = default(Point);
                    for (int j = 0; j < strokes; j++)
                    {
                        var offset = acc.Half;
                        if (op == 22)
                        {
                            Line(a + offset, a + S(1) - S(2) + offset, pen);
                            acc += q;
                        }
                        else
                        {
                            Polygon(pen, a + offset, a + p + offset, a + p + r + offset, a + r + offset);
                            acc += q;
                        }
                    }
                }
            }
        }
    }
}
