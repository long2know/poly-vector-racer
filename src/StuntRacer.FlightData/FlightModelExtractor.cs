namespace StuntRacer.FlightData;

/// <summary>
/// Independent structural decoder of note 02. Keeps original vertex slots, BSP nodes and
/// command words. It deliberately does NOT pretend that an animated drawing program is a
/// static mesh: unimplemented synthesis/strips are explicitly marked source-only.
/// </summary>
internal static class FlightModelExtractor
{
    internal static IReadOnlyList<FlightModel> Extract(FlightImage image)
    {
        var result = new List<FlightModel>();
        for (int id = 0; id < 134; id++)
        {
            int record = 0x25ECC + 20 * id;
            int handler = (int)image.ReadUInt32(record), modelBase = (int)image.ReadUInt32(record + 4);
            int detail = (int)image.ReadUInt32(record + 8), alternate = (int)image.ReadUInt32(record + 12);
            int aux = (int)image.ReadUInt32(record + 16);
            var levels = new List<FlightModelLevel>();
            string kind;
            if (modelBase == 0) kind = "runtime-slot-no-file-model";
            else if (handler is 0x22CFC or 0x22D0A)
            {
                kind = "type-b-ground-patch";
                int entry = modelBase;
                var seen = new HashSet<int>();
                while (true)
                {
                    if (!seen.Add(entry)) Fail(id, entry, "Cyclic type-B detail chain.");
                    ushort distance = image.ReadUInt16(entry);
                    if ((distance & 0x8000) != 0) break;
                    int next = (int)image.ReadUInt32(entry + 2), block = (int)image.ReadUInt32(entry + 6);
                    int count = image.ReadUInt16(block);
                    var decoder = new LevelDecoder(image, id, 0, block, true, block + 6, count);
                    decoder.Program(entry + 10);
                    levels.Add(decoder.Finish(entry, 0, 0, 2, 0, (byte)Math.Min(count, 255)));
                    if (next == -1 || (next == entry && distance == 0x7FFF)) break;
                    if (seen.Count >= 32) Fail(id, entry, "Oversized type-B detail chain.");
                    entry = FlightSceneExtractor.Address(unchecked((uint)next));
                }
            }
            else
            {
                kind = "normal-model-program";
                int entry = modelBase;
                var seen = new HashSet<int>();
                while (true)
                {
                    if (!seen.Add(entry) || seen.Count > 32) Fail(id, entry, "Cyclic/oversized detail list.");
                    ushort first = image.ReadUInt16(entry);
                    ushort leaf;
                    int stream, next;
                    bool inline = false;
                    if ((first & 0x8000) != 0) { leaf = first; stream = entry + 2; next = -1; }
                    else if ((first & 0x4000) != 0)
                    {
                        leaf = image.ReadUInt16(entry + 2); stream = entry + 4; next = entry + 6; inline = true;
                    }
                    else
                    {
                        int jump = image.ReadUInt16(entry + 2);
                        leaf = image.ReadUInt16(entry + 4); stream = entry + 6;
                        next = (jump & 0x8000) != 0 ? -1 : modelBase + jump;
                    }
                    if (leaf != 0xFFFF)
                    {
                        int block = modelBase + (leaf & 0xFFF);
                        byte flags = image.ReadByte(block + 7);
                        var decoder = new LevelDecoder(image, id, modelBase, block, (flags & 2) != 0,
                            block + 10, image.ReadByte(block + 8));
                        decoder.Stream(stream, inline);
                        if ((flags & 8) != 0) decoder.Limitations.Add("Strip-generated slots are source-only; no invented strip geometry.");
                        if ((flags & 4) != 0) decoder.Limitations.Add("Relative-group vertex section is source-only; no invented group geometry.");
                        levels.Add(decoder.Finish(entry, leaf, image.ReadUInt16(block), flags,
                            image.ReadByte(block + 6), image.ReadByte(block + 9)));
                    }
                    if (next < 0) break;
                    entry = next;
                }
            }
            result.Add(new FlightModel(id, handler, modelBase, detail, alternate, aux, kind, levels.AsReadOnly()));
        }
        return result.AsReadOnly();
    }

    private sealed class LevelDecoder
    {
        private readonly FlightImage image;
        private readonly int id, modelBase, block, vertexStart, initialVertexCount;
        private readonly bool flat;
        private readonly SortedDictionary<int, FlightVertex> vertices = [];
        private readonly List<FlightPolygon> polygons = [];
        private readonly List<FlightModelOperation> operations = [];
        private readonly List<FlightModelStreamNode> nodes = [];
        private readonly HashSet<int> programs = [], visitedNodes = [], activeNodes = [];
        internal HashSet<string> Limitations { get; } = [];
        internal LevelDecoder(FlightImage image, int id, int modelBase, int block, bool flat, int vertexStart, int count)
        {
            this.image = image; this.id = id; this.modelBase = modelBase; this.block = block;
            this.flat = flat; this.vertexStart = vertexStart;
            initialVertexCount = count;
            if (count > 512) Fail(id, block, "Too many original model vertices.");
            for (int i = 0; i < count; i++) Vertex(i);
        }

        private void Vertex(int slot)
        {
            if (slot is < 0 or >= 512) Fail(id, block, "Vertex slot exceeds the model budget.");
            if (vertices.ContainsKey(slot)) return;
            int p = vertexStart + slot * (flat ? 4 : 6);
            vertices.Add(slot, new FlightVertex(slot, image.ReadInt16(p), flat ? (short)0 : image.ReadInt16(p + 2), image.ReadInt16(p + (flat ? 2 : 4))));
        }

        internal void Stream(int address, bool inline = false, int depth = 0)
        {
            if (depth > 128 || nodes.Count >= 4096) Fail(id, address, "Model stream exceeds its budget.");
            if (activeNodes.Contains(address)) Fail(id, address, "Cyclic model stream.");
            if (!visitedNodes.Add(address)) return;
            ushort first = image.ReadUInt16(address);
            if (first == 0xFFFF) return;
            activeNodes.Add(address);
            var children = new List<int>();
            var progs = new List<int>();
            string kind;
            int end;
            if ((first & 0x8000) == 0)
            {
                int word = image.ReadUInt16(address + 2);
                int childStart;
                int childCount = 2;
                if ((word & 0xFC00) == 0xFC00) { kind = "jump"; childStart = address + 4; childCount = 1; }
                else if ((word & 0xC00) != 0) { kind = "axis-plane"; childStart = address + 4; }
                else if ((word & 0x2000) != 0) { kind = "point-normal-plane"; childStart = address + 16; }
                else
                {
                    if ((word & 0x1000) != 0) Fail(id, address, "Undocumented model BSP node plane source.");
                    kind = "three-vertex-plane"; childStart = address + 8;
                }
                end = childStart + childCount * 2;
                for (int j = 0; j < childCount; j++) children.Add(modelBase + image.ReadUInt16(childStart + j * 2));
            }
            else
            {
                kind = "draw";
                end = address + 2;
                int reference = (first & 0x1000) != 0 ? ReadPointer(ref end) : modelBase + (first & 0xFFF);
                if ((first & 0x2000) != 0) { progs.Add(reference); Program(reference); }
                else
                {
                    int p = reference;
                    bool last = false;
                    for (int j = 0; j < 512; j++)
                    {
                        int entry = image.ReadUInt16(p); p += 2;
                        int program = (entry & 0x1000) != 0 ? ReadPointer(ref p) : modelBase + (entry & 0xFFF);
                        progs.Add(program); Program(program);
                        if ((entry & 0x8000) != 0) { last = true; break; }
                    }
                    if (!last) Fail(id, address, "Unterminated model program-reference list.");
                    if ((first & 0x4000) == 0)
                    {
                        if (inline) Fail(id, address, "Inline detail stream does not terminate.");
                        children.Add(end);
                    }
                }
            }
            nodes.Add(new FlightModelStreamNode(address, kind, ReadWords(address, end), children.AsReadOnly(), progs.AsReadOnly()));
            foreach (int child in children) Stream(child, false, depth + 1);
            activeNodes.Remove(address);
        }

        internal void Program(int start)
        {
            if (!programs.Add(start)) return;
            if (programs.Count > 2048) Fail(id, start, "Too many model programs.");
            int p = start;
            var seen = new HashSet<int>();
            while (true)
            {
                int item = p;
                if (!seen.Add(item) || seen.Count > 4096 || operations.Count > 32768) Fail(id, item, "Model program exceeds its budget.");
                ushort operation = Word(ref p);
                if ((operation & 0x8000) == 0)
                {
                    if (operation != 0x7FFF) Word(ref p);
                    short n = unchecked((short)Word(ref p));
                    if (n > 0)
                    {
                        int offset = Word(ref p), stride = flat ? 4 : 6;
                        if (offset % stride != 0 || n > 512) Fail(id, item, "Invalid extra-vertex range.");
                        for (int i = 0; i < n; i++) Vertex(offset / stride + i);
                    }
                    operation = Word(ref p);
                }
                int op = (operation & 0x3FFF) / 4;
                if ((operation & 3) != 0 || op > 77) Fail(id, item, $"Unknown model opcode 0x{operation:X}.");
                int operandStart = p;
                bool sourceOnly = false;
                int skip = 0;
                switch (op)
                {
                    case 3: ReadPolygon(ref p, item, op); break;
                    case 4:
                        for (int faces = 0; ; faces++)
                        {
                            if (faces >= 512) Fail(id, item, "Too many detail faces.");
                            short distance = unchecked((short)Word(ref p));
                            if (distance < 0) break;
                            int n = Word(ref p); Word(ref p);
                            var slots = ReadSlots(ref p, n);
                            polygons.Add(new FlightPolygon(item, op, 0, slots));
                        }
                        break;
                    case 13: case 14: case 75:
                        Word(ref p);
                        if (op == 14) Word(ref p);
                        for (int pairs = 0; ; pairs++)
                        {
                            if (pairs >= 512) Fail(id, item, "Unterminated model line list.");
                            Word(ref p); ushort last = Word(ref p);
                            if ((last & 0x8000) != 0) break;
                        }
                        break;
                    case 17: case 18:
                        for (int quads = 0; ; quads++)
                        {
                            if (quads >= 512) Fail(id, item, "Unterminated model quad list.");
                            if ((image.ReadUInt16(p) & 0x8000) != 0) { Word(ref p); break; }
                            polygons.Add(new FlightPolygon(item, op, 12, ReadSlots(ref p, 4)));
                        }
                        break;
                    case 21: case 22: case 57:
                        if (op != 21) Word(ref p);
                        Word(ref p);
                        int rows = Word(ref p);
                        if (rows > 512) Fail(id, item, "Too many hatch rows.");
                        for (int i = 0; i < rows; i++) Word(ref p);
                        sourceOnly = true;
                        break;
                    case 24:
                        int n24 = Word(ref p);
                        var slots24 = ReadSlots(ref p, n24);
                        polygons.Add(new FlightPolygon(item, op, Word(ref p), slots24));
                        break;
                    case 25:
                        int n25 = Word(ref p);
                        if (n25 > 512) Fail(id, item, "Oversized consecutive polygon.");
                        polygons.Add(new FlightPolygon(item, op, Word(ref p), Array.AsReadOnly(Enumerable.Range(0, n25).ToArray())));
                        break;
                    case 26:
                        int n26 = Word(ref p), s26 = Slot(Word(ref p));
                        if (n26 > 512 || s26 + n26 > 512) Fail(id, item, "Oversized consecutive polygon.");
                        polygons.Add(new FlightPolygon(item, op, 7, Array.AsReadOnly(Enumerable.Range(s26, n26).ToArray())));
                        break;
                    case 55:
                        int polyBase = ReadPointer(ref p);
                        for (int i = 0; ; i++)
                        {
                            if (i >= 512) Fail(id, item, "Unterminated polygon-offset list.");
                            ushort off = Word(ref p);
                            if ((off & 0x8000) != 0) break;
                            int q = checked(polyBase + off); ReadPolygon(ref q, q, op);
                        }
                        break;
                    case 69:
                        for (int i = 0; i < 3; i++) Word(ref p);
                        ReadColourTail(ref p);
                        sourceOnly = true;
                        break;
                    case 71:
                        for (int i = 0; ; i++)
                        {
                            if (i >= 512) Fail(id, item, "Unterminated disc list.");
                            Word(ref p); Word(ref p);
                            if ((Word(ref p) & 0x8000) != 0) break;
                        }
                        sourceOnly = true;
                        break;
                    default:
                        int count = FixedArguments(op);
                        if (count < 0) Fail(id, item, $"Unsupported original model opcode {op}; no guessed operands.");
                        for (int i = 0; i < count; i++) Word(ref p);
                        sourceOnly = op is not (0 or 1 or 2 or 31 or 33 or 48 or 49 or 58 or 62 or 74);
                        break;
                }
                if (op == 53) skip = image.ReadUInt16(operandStart + 4);
                if (op is 54 or 76) skip = 2;
                if (op == 70)
                {
                    Limitations.Add("Effect timer opcode 70 is source-only (combat/effect animation excluded).");
                    sourceOnly = true;
                }
                if (op is 42 or 43 or 44 or 50 or 51 or 52 or 53 or 54 or 59 or 66 or 68 or 72 or 73 or 76 or 77)
                    Limitations.Add($"Opcode {op}: source command retained; animated/synthesis variants are not a static mesh.");
                if (sourceOnly)
                    Limitations.Add($"Opcode {op} is retained as source command data, not a fully materialized static primitive.");
                operations.Add(new FlightModelOperation(item, op, ReadWords(operandStart, p),
                    sourceOnly ? "source-only; not emitted as invented geometry" : "source command decoded")
                { ProgramAddress = start, SourceItemWords = ReadWords(item, p) });
                if ((operation & 0x4000) != 0 || op == 70) return;
                p = checked(p + skip);
            }
        }

        private static int FixedArguments(int op) => op switch
        {
            0 or 33 or 38 or 45 or 46 or 47 or 50 or 54 or 56 or 63 or 64 or 65 or 66 or 68 or 70 or 72 or 73 or 76 or 77 => 0,
            31 or 35 or 42 or 43 or 44 or 51 or 59 or 60 or 61 => 1,
            1 or 9 or 10 or 11 or 20 or 23 or 27 or 30 or 34 or 36 or 40 or 41 or 58 => 2,
            2 or 12 or 19 or 32 or 39 or 48 or 52 or 53 or 62 or 74 => 3,
            16 or 49 or 67 => 4,
            7 or 15 => 5,
            6 or 8 => 6,
            _ => -1
        };

        private void ReadPolygon(ref int p, int address, int op)
        {
            var slots = new List<int>();
            for (int i = 0; ; i++)
            {
                if (i >= 512) Fail(id, address, "Unterminated polygon vertex list.");
                ushort value = Word(ref p);
                slots.Add(Slot((ushort)(value & 0x7FFF)));
                if ((value & 0x8000) != 0) break;
            }
            ushort colour = ReadColourTail(ref p);
            polygons.Add(new FlightPolygon(address, op, colour, slots.AsReadOnly()));
        }
        private ushort ReadColourTail(ref int p)
        {
            ushort colour = Word(ref p);
            if ((colour & 0x8000) == 0)
            {
                if ((colour & 0x1000) != 0 && (colour & 0xC00) == 0) Word(ref p);
                if ((colour & 0x4000) != 0) Word(ref p);
            }
            return colour;
        }
        private IReadOnlyList<int> ReadSlots(ref int p, int n)
        {
            if (n is < 0 or > 512) Fail(id, p, "Invalid polygon vertex count.");
            var result = new int[n];
            for (int i = 0; i < n; i++) result[i] = Slot(Word(ref p));
            return Array.AsReadOnly(result);
        }
        private int Slot(ushort value)
        {
            if (value % 6 != 0 || value / 6 >= 512) Fail(id, block, $"Invalid vertex byte offset {value}.");
            return value / 6;
        }
        private ushort Word(ref int p) { ushort value = image.ReadUInt16(p); p += 2; return value; }
        private int ReadPointer(ref int p) { int value = FlightSceneExtractor.Address(image.ReadUInt32(p)); p += 4; return value; }
        private IReadOnlyList<ushort> ReadWords(int start, int end)
        {
            var words = new ushort[(end - start) / 2];
            for (int i = 0; i < words.Length; i++) words[i] = image.ReadUInt16(start + i * 2);
            return Array.AsReadOnly(words);
        }
        internal FlightModelLevel Finish(int list, ushort leaf, ushort part, byte flags, byte scale, byte projected)
        {
            var usable = new List<FlightPolygon>();
            foreach (var poly in polygons)
            {
                if (poly.Slots.All(vertices.ContainsKey)) usable.Add(poly);
                else Limitations.Add($"Polygon at 0x{poly.Address:X} needs synthesized/strip vertices; source command retained, not fabricated.");
            }
            if (nodes.Any(n => n.Kind != "draw")) Limitations.Add("Polygons are a union of source BSP branches; StreamNodes preserves selection, not a preselected painter order.");
            // Plain source vertices are exact and suitable for contact regardless of rendering limitations.
            return new FlightModelLevel(list, block, leaf, part, flags, scale, projected,
                vertices.Values.ToList().AsReadOnly(), usable.AsReadOnly(), operations.AsReadOnly(),
                Limitations.Count == 0 ? "source vertices and static polygon references" : "source vertices exact; drawing program partially materialized",
                Limitations.Order(StringComparer.Ordinal).ToList().AsReadOnly())
                { StreamNodes = nodes.AsReadOnly(), InitialVertexCount = initialVertexCount };
        }
    }

    private static void Fail(int id, int address, string message) =>
        throw new FlightDataException("models.structure", $"Model {id}: {message}", address);
}
