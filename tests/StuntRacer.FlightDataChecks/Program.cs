using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StuntRacer.FlightData;

var checks = new Checks();
checks.Synthetic();
string? source = Value("--source"), packagePath = Value("--package"), oracle = Value("--oracle"), completeOracle = Value("--complete-oracle");
if (source != null) checks.RealSource(source, oracle, completeOracle);
if (packagePath != null) checks.ExternalPackage(packagePath, Value("--geometry-oracle"));
if (source == null && packagePath == null) Console.WriteLine("Real-data checks not requested (use --source and/or --package; original data is never a fixture).");
Console.WriteLine($"{checks.Passed} passed, {checks.Failed} failed.");
return checks.Failed == 0 ? 0 : 1;

string? Value(string flag)
{
    int i = Array.IndexOf(args, flag);
    if (i < 0) return null;
    if (i + 1 >= args.Length) throw new ArgumentException($"{flag} requires a path.");
    return args[i + 1];
}

internal sealed class Checks
{
    internal int Passed { get; private set; }
    internal int Failed { get; private set; }
    private void Check(string name, Action action)
    {
        try { action(); Passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) when (e is FlightDataException or InvalidOperationException or JsonException or IOException or ArgumentException)
        { Failed++; Console.Error.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Reject(Action action, string prefix)
    {
        try { action(); }
        catch (FlightDataException e)
        {
            Assert(e.Code.StartsWith(prefix, StringComparison.Ordinal), $"Expected {prefix}, got {e.Code}: {e.Message}");
            return;
        }
        throw new InvalidOperationException($"Malformed artifact was accepted; expected {prefix}.");
    }
    internal void Synthetic()
    {
        ModelChecks.Synthetic(Check, Assert);
        Check("HUNK repeated target runs preserve both actual operands and symbols", () =>
        {
            var h = FlightHunkReader.Read(Fixtures.Hunk());
            Assert(h.Hunks.Count == 2 && h.Relocations.Count == 2 && h.Symbols.Count == 1, "Hunk metadata lost.");
            Assert(h.FlatImage.ReadUInt32(0x10004) == 0x10017 && h.FlatImage.ReadUInt32(0x10008) == 0x10013, "Wrong target relocation.");
            Assert(h.Symbols[0].Name == "sample" && h.Symbols[0].FlatAddress == 0x10004, "Wrong symbol relocation.");
            Reject(() => h.FlatImage.ReadInt16(0xFFFF), "image.range");
            Reject(() => h.FlatImage.Slice(h.FlatImage.EndAddress - 1, 2), "image.range");
        });
        Check("HUNK compact relocation records preserve both runs", () =>
        {
            var h = FlightHunkReader.Read(Fixtures.Hunk(true));
            Assert(h.Relocations.Count == 2 && h.FlatImage.ReadUInt32(0x10008) == 0x10013, "Compact relocations differ.");
        });
        HunkMutation("out-of-range target", b => Fixtures.W(b, 15 * 4, 2));
        HunkMutation("odd offset", b => Fixtures.W(b, 16 * 4, 5));
        HunkMutation("offset past source end", b => Fixtures.W(b, 16 * 4, 14));
        HunkMutation("overlapping long relocations", b => Fixtures.W(b, 19 * 4, 6));
        HunkMutation("payload larger than allocation", b => Fixtures.W(b, 5 * 4, 3));
        HunkMutation("oversized allocation", b => Fixtures.W(b, 5 * 4, 0x20000000));
        HunkMutation("unsupported relocation semantics", b => Fixtures.W(b, 13 * 4, 0x3FD));
        Check("HUNK missing END is rejected", () => Reject(() => FlightHunkReader.Read(Fixtures.Hunk()[..^4]), "hunk."));
        Check("HUNK truncated symbols are rejected", () => Reject(() => FlightHunkReader.Read(Fixtures.Hunk()[..100]), "hunk."));
        Check("HUNK reference profile refuses invented-but-valid layout", () =>
        {
            var h = FlightHunkReader.Read(Fixtures.Hunk());
            Reject(() => ReferenceLayout.VerifyAndRelocate(h, out _), "layout.");
        });
        Check("OFS whole invented file is reconstructed and checksummed", () =>
        {
            var ofs = new OfsDisk(Fixtures.Ofs());
            Assert(ofs.Files.Count == 1 && ofs.ValidatedBlocks == 6, "Not all OFS structures traversed.");
            Assert(ofs.Files[0].Metadata.Path == "example.bin" &&
                ofs.Files[0].Bytes.AsSpan().SequenceEqual("invented-123"u8), "OFS file data is not exact.");
        });
        OfsMutation("boot checksum", b => b[100] ^= 1, "ofs.boot-checksum");
        OfsMutation("root checksum", b => b[880 * 512 + 200] ^= 1, "ofs.checksum");
        OfsMutation("file checksum", b => b[882 * 512 + 200] ^= 1, "ofs.checksum");
        OfsMutation("payload checksum", b => b[883 * 512 + 24] ^= 1, "ofs.checksum");
        OfsMutation("file extension cycle", b => { Fixtures.W(b, 882 * 512 + 504, 882); Fixtures.Checksum(b, 882); }, "ofs.cycle");
        OfsMutation("out-of-range data pointer", b => { Fixtures.W(b, 882 * 512 + 308, 1760); Fixtures.Checksum(b, 882); }, "ofs.pointer");
        OfsMutation("wrong data sequence", b => { Fixtures.W(b, 883 * 512 + 8, 2); Fixtures.Checksum(b, 883); }, "ofs.data");
        OfsMutation("wrong data parent", b => { Fixtures.W(b, 883 * 512 + 4, 880); Fixtures.Checksum(b, 883); }, "ofs.data");
        OfsMutation("cyclic data next pointer", b => { Fixtures.W(b, 883 * 512 + 16, 883); Fixtures.Checksum(b, 883); }, "ofs.data");
        OfsMutation("file size/pointer mismatch", b => { Fixtures.W(b, 882 * 512 + 324, 489); Fixtures.Checksum(b, 882); }, "ofs.pointers");
        OfsMutation("wrong directory parent", b => { Fixtures.W(b, 882 * 512 + 500, 881); Fixtures.Checksum(b, 882); }, "ofs.header");
        OfsMutation("wrong filename hash", b => { b[882 * 512 + 433] = (byte)'z'; Fixtures.Checksum(b, 882); }, "ofs.hash");
        OfsMutation("bitmap marks a used block free", b =>
        {
            int bit = 883 - 2, p = 881 * 512 + 4 + 4 * (bit / 32);
            Fixtures.W(b, p, Fixtures.U(b, p) | (1u << (bit % 32))); Fixtures.Checksum(b, 881, 0);
        }, "ofs.bitmap");
        Check("OFS invented disk cannot become a status-only original package", () =>
            Reject(() => FlightDiskReader.Read(Fixtures.Ofs()), "source.executable"));
        Check("ZIP actual decompressed bytes and CRC are checked", () =>
        {
            byte[] expected = Fixtures.Ofs(), zip = Fixtures.Zip(expected);
            var result = BoundedZip.Read(zip);
            Assert(result.Name == "invented.adf" && result.Disk.AsSpan().SequenceEqual(expected), "ZIP output bytes differ.");
        });
        Check("ZIP legal maximum-deflate flags are accepted", () =>
        {
            byte[] zip = Fixtures.Zip(Fixtures.Ofs()); int c = Fixtures.Central(zip);
            Fixtures.Little16(zip, 6, 2); Fixtures.Little16(zip, c + 8, 2);
            Assert(BoundedZip.Read(zip).Disk.Length == 901120, "ZIP compressor option was treated as encryption.");
        });
        Check("ZIP stored ADF is supported", () =>
        {
            byte[] expected = Fixtures.Ofs();
            Assert(BoundedZip.Read(Fixtures.Zip(expected, stored: true)).Disk.AsSpan().SequenceEqual(expected), "Stored ZIP differs.");
        });
        Check("ZIP multi-member input is rejected", () => Reject(() => BoundedZip.Read(Fixtures.Zip(Fixtures.Ofs(), twoEntries: true)), "zip.structure"));
        foreach (string path in new[] { "../invented.adf", "/invented.adf", "folder\\invented.adf", "C:invented.adf" })
            Check("ZIP traversal rejected: " + path, () => Reject(() => BoundedZip.Read(Fixtures.Zip(Fixtures.Ofs(), path)), "zip.structure"));
        Check("ZIP CRC declaration mutation is rejected", () =>
        {
            byte[] zip = Fixtures.Zip(Fixtures.Ofs()); int c = Fixtures.Central(zip);
            Fixtures.Little32(zip, c + 16, 123); Fixtures.Little32(zip, 14, 123);
            Reject(() => BoundedZip.Read(zip), "zip.crc");
        });
        Check("ZIP encrypted entry is rejected before inflation", () =>
        {
            byte[] zip = Fixtures.Zip(Fixtures.Ofs()); int c = Fixtures.Central(zip);
            Fixtures.Little16(zip, c + 8, 1); Fixtures.Little16(zip, 6, 1);
            Reject(() => BoundedZip.Read(zip), "zip.structure");
        });
        Check("ZIP declared oversize is rejected before allocation", () =>
        {
            byte[] zip = Fixtures.Zip(Fixtures.Ofs()); int c = Fixtures.Central(zip);
            Fixtures.Little32(zip, c + 24, 901121); Fixtures.Little32(zip, 22, 901121);
            Reject(() => BoundedZip.Read(zip), "zip.structure");
        });
        Check("ZIP undeclared extra decompressed byte is rejected", () =>
        {
            byte[] zip = Fixtures.Zip(new byte[901121]); int c = Fixtures.Central(zip);
            Fixtures.Little32(zip, c + 24, 901120); Fixtures.Little32(zip, 22, 901120);
            Reject(() => BoundedZip.Read(zip), "zip.structure");
        });
        Check("ZIP local/central name disagreement is rejected", () =>
        {
            byte[] zip = Fixtures.Zip(Fixtures.Ofs()); zip[30] ^= 1;
            Reject(() => BoundedZip.Read(zip), "zip.structure");
        });
        Check("Input two-MiB budget is enforced", () => Reject(() => FlightDiskReader.Read(new byte[FlightDiskReader.MaximumInputBytes + 1]), "source.size"));
        Check("Wrong package schema is rejected", () =>
            Reject(() => FlightPackageSerializer.ReadPackage("{\"schemaVersion\":2,\"kind\":\"fa18-amiga-runtime\"}"u8), "package.schema"));
        Check("Duplicate package fields are rejected", () =>
            Reject(() => FlightPackageSerializer.ReadPackage("{\"schemaVersion\":1,\"schemaVersion\":2}"u8), "package.duplicate"));
        Check("Package sixteen-MiB budget is enforced", () =>
            Reject(() => FlightPackageSerializer.ReadPackage(new byte[FlightPackageSerializer.MaximumPackageBytes + 1]), "package.size"));
        Check("Terrain item sub-cell and source fine units are exact, not an authored placement", () =>
        {
            var c = new FlightTerrainTier("C", 22, 128, Array.Empty<FlightTerrainCell>());
            var a = new FlightTerrainTier("A", 24, 32, Array.Empty<FlightTerrainCell>());
            var cell = new FlightTerrainCell(10, 11, 0);
            var item = new FlightTerrainItem(0, 86, 2048, 1024);
            var placed = FlightScenePlacements.Place(c, cell, item);
            Assert(placed.PositionX == (10L << 22) + (3L << 20) + (2048L << 8) &&
                placed.PositionZ == (11L << 22) + (3L << 20) + (1024L << 8) &&
                placed.FineOffsetUnitShift == 8 && placed.PositionY == 0, "C tier sub-cell/fine coordinate conversion differs.");
            Assert(FlightScenePlacements.Place(a, cell, item).FineOffsetUnitShift == 10, "A/B offset units are not 1024 positions.");
            Assert(FlightScenePlacements.Place(c, cell, item with { ModelId = 20 }).RuntimeModelSlot, "Runtime slot was silently assigned a made-up model.");
        });
        Check("Collision count-zero node retains the original first plane; malformed nodes are not empty-volume fallbacks", () =>
        {
            byte[] bytes = new byte[64];
            short[] box = [10, -4, 4, -5, 5];
            for (int i = 0; i < 5; i++)
            {
                BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(i * 2, 2), box[i]);
                BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(10 + i * 2, 2), box[i]);
            }
            Fixtures.W(bytes, 20, uint.MaxValue);
            BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(34, 2), -1);
            Assert(FlightCollisionExtractor.TryFootprint(new FlightImage(bytes), 0x10000, out _, out var nodes) &&
                nodes![0].PlaneCountWord == 0 && nodes[0].Planes.Count == 1, "Count-zero original first-dot plane was dropped.");
            byte[] bad = bytes.ToArray(); Fixtures.W(bad, 20, 0x1000A);
            Assert(!FlightCollisionExtractor.TryFootprint(new FlightImage(bad), 0x10000, out _, out _), "Cyclic footprint accepted.");
            bad = bytes.ToArray(); BinaryPrimitives.WriteUInt16BigEndian(bad.AsSpan(24, 2), 9);
            Assert(!FlightCollisionExtractor.TryFootprint(new FlightImage(bad), 0x10000, out _, out _), "Oversized plane count accepted.");
        });
    }

    private void HunkMutation(string name, Action<byte[]> change) =>
        Check("HUNK rejects " + name, () => { byte[] b = Fixtures.Hunk(); change(b); Reject(() => FlightHunkReader.Read(b), "hunk."); });
    private void OfsMutation(string name, Action<byte[]> change, string code) =>
        Check("OFS rejects " + name, () => { byte[] b = Fixtures.Ofs(); change(b); Reject(() => new OfsDisk(b), code); });

    internal void RealSource(string path, string? oracle, string? completeOracle)
    {
        byte[] source = File.ReadAllBytes(path);
        FlightDiskPackage? actual = null;
        Check("REAL whole supplied source parses into exact pinned image/tables/contact geometry", () =>
        {
            actual = FlightDiskReader.Read(source, new FlightReadOptions(Path.GetFileName(path)));
            Assert(actual.Source.Executable.Sha256 == FlightDiskReader.SupportedExecutableSha256 &&
                actual.FlatImage.Sha256 == FlightDiskReader.SupportedFlatImageSha256, "Actual executable/image identity changed.");
            Assert(actual.Hunks.Count == 185 && actual.Relocations.Count == 8441, "Whole hunk table was not parsed.");
            Assert(actual.Source.Disk?.ByteCount == 901120 || actual.Source.InputKind == "executable", "Invalid whole-disk provenance.");
            Assert(actual.Tables["flight_stick_player"].Metadata.ElementCount == 62 &&
                actual.Tables["flight_stick_ai"].Metadata.ElementCount == 64 &&
                actual.Tables["flight_sine"].Metadata.ElementCount == 901 &&
                actual.Tables["flight_asin_0"].Metadata.ElementCount == 259 &&
                actual.Tables["flight_asin_1"].Metadata.ElementCount == 552 &&
                actual.Tables["flight_asin_2"].Metadata.ElementCount == 256, "Required original numeric tables missing.");
            Assert(actual.Tables["flight_pads"].Metadata.ByteCount == 256 &&
                FlightTableExtractor.Fnv1A(actual.Tables["flight_pads"].Bytes.Span) == 0xE89381F3, "Published original pad fingerprint differs.");
            Assert(actual.Scene.WaterRegions.Count == 103 && actual.Scene.WaterRegions.Sum(r => r.Polygons.Count) == 103 &&
                actual.Scene.Pads.Count == 4 && actual.Scene.Ships.Count == 2, "Original physical world geometry missing.");
            var player = actual.Scene.Models.Single(m => m.Id == 4).Levels[0];
            var carrier = actual.Scene.Models.Single(m => m.Id == 8).Levels[0];
            Assert(player.Vertices.Take(4).Select(v => v.Slot).SequenceEqual(Enumerable.Range(0, 4)) &&
                carrier.Vertices.Take(58).Select(v => v.Slot).SequenceEqual(Enumerable.Range(0, 58)), "Original contact slots missing/reordered.");
            Assert(player.ScaleLowNibble == 3 && player.ModelToPositionShift == 5, "Collision L and model->world exponent were conflated.");
            Assert(player.InitialVertexCount == 21 && carrier.InitialVertexCount == 69, "Initial transformed source slots were conflated with later extra vertices.");
            Assert(actual.Scene.ModelCollisions.Count == 13 && actual.Scene.ModelCollisions.Sum(c => c.Nodes.Count) == 14 &&
                actual.Scene.ModelCollisions.All(c => c.ReferenceLandmarkZBias == 48) &&
                actual.Scene.ModelCollisions.Count(c => c.ReorderPlanes.Count > 0) == 3, "Original static footprint/auxiliary mapping differs.");
            Assert(!actual.Compatibility.CleanExecutableIdentityVerified, "Modified disk was falsely asserted clean.");
            Assert(actual.Relocations.Count(r => r.FlatAddress >= 0x3249A && r.FlatAddress + 4 <= 0x325B8) == 3 &&
                actual.Relocations.Count(r => r.FlatAddress >= 0x3AE92 && r.FlatAddress + 4 <= 0x49A12) == 337, "Reference span relocation inventory differs.");
            foreach (var r in actual.Relocations)
                Assert(actual.ReferenceImage.ReadUInt32(r.FlatAddress) == unchecked(r.Addend + actual.ReferenceSegments[r.TargetHunk].RamAddress), "Reference relocation used wrong hunk or lost a run.");
        });
        if (actual == null) return;
        var p = actual;
        if (completeOracle != null)
            Check("REAL static landmark boxes and EVERY plane match independently decoded original lists_land", () =>
            {
                byte[] desc = File.ReadAllBytes(Path.Combine(completeOracle, "tables", "lists_desc.bin"));
                byte[] land = File.ReadAllBytes(Path.Combine(completeOracle, "tables", "lists_land.bin"));
                Assert(desc.Length == 134 * 8, "Unexpected descriptor oracle dimensions.");
                foreach (var collision in p.Scene.ModelCollisions)
                {
                    int row = collision.ModelId * 8;
                    Assert((desc[row + 1] & 4) != 0, "Oracle has no matching footprint.");
                    int offset = BinaryPrimitives.ReadUInt16LittleEndian(desc.AsSpan(row + 2, 2));
                    Box(collision.OuterBox, ref offset);
                    Assert(land[offset++] == collision.Nodes.Count, "Oracle collision node count differs.");
                    foreach (var node in collision.Nodes)
                    {
                        Box(node.Box, ref offset);
                        Assert(land[offset++] == node.PlaneCountWord, "Oracle plane count word differs.");
                        foreach (var plane in node.Planes) Plane(plane, ref offset);
                    }
                    if (collision.ReorderPlanes.Count > 0)
                    {
                        offset = BinaryPrimitives.ReadUInt16LittleEndian(desc.AsSpan(row + 4, 2));
                        Assert(Read(ref offset) == collision.ReorderPlaneCountWord, "Oracle reorder count differs.");
                        foreach (var plane in collision.ReorderPlanes) Plane(plane, ref offset);
                    }
                }
                // This is an upstream structural-recognizer false positive, not missing world data.
                Assert((desc[34 * 8 + 1] & 4) != 0 && p.Scene.ModelCollisions.All(c => c.ModelId != 34) &&
                    !FlightScenePlacements.Enumerate(p.Scene).Any(i => i.ModelId == 34), "Effect-only false-positive qualification changed.");
                short Read(ref int offset)
                { short v = BinaryPrimitives.ReadInt16LittleEndian(land.AsSpan(offset, 2)); offset += 2; return v; }
                void Box(FlightCollisionBox box, ref int offset)
                {
                    foreach (short v in new[] { box.MaximumHeight, box.MinimumX, box.MaximumX, box.MinimumZ, box.MaximumZ })
                        Assert(Read(ref offset) == v, "Oracle original collision box differs.");
                }
                void Plane(FlightCollisionPlane plane, ref int offset)
                {
                    foreach (short v in new[] { plane.ReferenceX, plane.ReferenceY, plane.ReferenceZ, plane.NormalX, plane.NormalY, plane.NormalZ })
                        Assert(Read(ref offset) == v, "Oracle original collision plane differs.");
                }
            });
        if (oracle != null)
            Check("REAL every flight table/image is byte-identical to independent local oracle", () =>
            {
                string[] names = ["flight_stick_player", "flight_stick_ai", "flight_gust", "flight_sink", "flight_len", "flight_terrain", "flight_dir",
                    "flight_pads", "flight_span_pitch", "flight_span_trig", "flight_carrier", "flight_ship2"];
                foreach (string name in names)
                    Assert(File.ReadAllBytes(Path.Combine(oracle, "tables", name + ".bin")).AsSpan().SequenceEqual(p.Tables[name].Bytes.Span), "Oracle differs: " + name);
                Assert(File.ReadAllBytes(Path.Combine(oracle, "flat-image.bin")).AsSpan().SequenceEqual(p.FlatImage.Bytes.Span), "Oracle flat image differs.");
                Assert(File.ReadAllBytes(Path.Combine(oracle, "reference-window-image.bin")).AsSpan().SequenceEqual(p.ReferenceWindowImage.Bytes.Span), "Oracle reference window differs.");
            });
        byte[] rawExe;
        if (p.Source.InputKind == "executable") rawExe = source;
        else
        {
            byte[] disk = p.Source.InputKind == "zip" ? BoundedZip.Read(source).Disk : source;
            var ofs = new OfsDisk(disk);
            rawExe = ofs.Files.Single(f => f.Metadata.Path == p.Source.ExecutablePath).Bytes;
        }
        Check("REAL direct executable path matches disk extraction", () =>
        {
            var direct = FlightDiskReader.Read(rawExe);
            Assert(direct.FlatImage.Sha256 == p.FlatImage.Sha256 && direct.ReferenceImage.Sha256 == p.ReferenceImage.Sha256 &&
                direct.Source.Disk == null && direct.Source.Archive == null, "Direct executable provenance/image differs.");
        });
        Check("REAL mutation of original stick table is rejected (not padded or regenerated)", () =>
        {
            byte[] changed = rawExe.ToArray();
            int offset = changed.AsSpan().IndexOf(p.FlatImage.Slice(0x425D8, 124));
            Assert(offset >= 0, "Original source-table location not found.");
            changed[offset + 11] ^= 1;
            Reject(() => FlightDiskReader.Read(changed), "tables.fingerprint");
        });
        Check("REAL hunk allocation/layout mutation is rejected", () =>
        {
            byte[] changed = rawExe.ToArray();
            Fixtures.W(changed, 24, Fixtures.U(changed, 24) + 1);
            Reject(() => FlightDiskReader.Read(changed), "layout.");
        });
        Check("REAL source code/model byte mutation is rejected even if flight tables remain valid", () =>
        {
            byte[] changed = rawExe.ToArray();
            int offset = 24 + 185 * 4 + 8;
            changed[offset + 8] ^= 1;
            Reject(() => FlightDiskReader.Read(changed), "source.variant");
        });
        byte[] package = FlightPackageSerializer.WritePackage(p);
        Check("REAL package round-trip revalidates original source and all extracted data", () =>
        {
            var roundTrip = FlightPackageSerializer.ReadPackage(package);
            Assert(roundTrip.Source == p.Source && roundTrip.FlatImage.Sha256 == p.FlatImage.Sha256 &&
                roundTrip.Scene.Models.Sum(m => m.Levels.Count) == p.Scene.Models.Sum(m => m.Levels.Count), "Package round-trip lost actual data.");
        });
        PackageMutation("source identity", package, root => root["source"]!["executable"]!["sha256"] = new string('0', 64));
        PackageMutation("layout base", package, root => root["layout"]!["imageBase"] = 0x20000);
        PackageMutation("table bytes", package, root => root["tables"]![0]!["bytes"] = Convert.ToBase64String(new byte[124]));
        PackageMutation("table fingerprint", package, root => root["pins"]![0]!["sha256"] = new string('0', 64));
        PackageMutation("reference RAM map", package, root => root["referenceSegments"]![0]!["ramAddress"] = 0x123456);
        PackageMutation("flat image", package, root =>
        {
            byte[] image = Convert.FromBase64String(root["flatImage"]!.GetValue<string>()); image[0] ^= 1;
            root["flatImage"] = Convert.ToBase64String(image);
        });
        PackageMutation("scene contact vertex", package, root => root["scene"]!["models"]![4]!["levels"]![0]!["vertices"]![0]!["x"] = 1);
        PackageMutation("landmark collision plane", package, root => root["scene"]!["modelCollisions"]![0]!["nodes"]![0]!["planes"]![0]!["normalX"] = 123);
        Check("REAL valid receipt does not contain original byte/vertex arrays", () =>
        {
            string receipt = Encoding.UTF8.GetString(FlightPackageSerializer.WriteReceipt(p));
            Assert(!receipt.Contains("\"sourceBytes\"", StringComparison.Ordinal) && !receipt.Contains("\"sourceItemWords\"", StringComparison.Ordinal) &&
                !receipt.Contains("\"vertices\"", StringComparison.Ordinal), "Receipt unexpectedly exports original arrays.");
        });
        Console.WriteLine($"Real identity: executable {p.Source.Executable.Sha256}; flat {p.FlatImage.Sha256}; tables {p.Tables.Count}; original water polygons {p.Scene.WaterRegions.Sum(r => r.Polygons.Count)}.");
    }

    private void PackageMutation(string name, byte[] package, Action<JsonNode> mutation) =>
        Check("REAL package rejects mutated " + name, () =>
        {
            JsonNode root = JsonNode.Parse(package) ?? throw new InvalidOperationException("No package root.");
            mutation(root);
            Reject(() => FlightPackageSerializer.ReadPackage(Encoding.UTF8.GetBytes(root.ToJsonString())), "package.integrity");
        });
    internal void ExternalPackage(string path, string? geometryOracle)
    {
        Check("EXTERNAL package includes real source and validated usable runtime data", () =>
        {
            var p = FlightPackageSerializer.ReadPackage(File.ReadAllBytes(path));
            Assert(p.Tables["flight_len"].Metadata.ElementCount == 257 && p.Scene.WaterCells.Count == 1024 &&
                p.Scene.Models.Single(m => m.Id == 4).Levels[0].Vertices.Count >= 4, "External package is only a declaration.");
            ModelChecks.Package(p, geometryOracle, Check, Assert);
        });
    }
}
