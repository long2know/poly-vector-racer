using System.Buffers.Binary;

namespace StuntRacer.FlightData;

public static class FlightDiskReader
{
    public const int DiskByteCount = 901120;
    public const int MaximumInputBytes = 2 * 1024 * 1024;
    public const string SupportedExecutableSha256 = "38b6e1f331460aa4939690173d95c38b105d1d089226359a4c8655052ea29ab5";
    public const string SupportedFlatImageSha256 = "40388db2b579abf89048a844b5502b79e8818c26b9253eba83705529f32a35a2";

    /// <summary>
    /// Accepts an OFS DD ADF, one-member bounded ZIP of that ADF, or an unpacked HUNK executable.
    /// Identifies the original game structurally, including renamed copies. Never executes code.
    /// Unsupported layouts/data variants fail closed; there are no synthetic-table fallbacks.
    /// </summary>
    public static FlightDiskPackage Read(ReadOnlySpan<byte> input) => Read(input, new FlightReadOptions());

    public static FlightDiskPackage Read(ReadOnlySpan<byte> input, FlightReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (input.Length < 4 || input.Length > MaximumInputBytes)
            throw new FlightDataException("source.size", "Input is truncated or exceeds the two-MiB input budget.");
        string? provided = options.FileName;
        if (provided != null && (provided.Length is < 1 or > 255 || provided.Any(c => c < 32 || c is '/' or '\\' or ':')))
            throw new FlightDataException("source.name", "FileName must be a plain, bounded file name.");
        byte[] sourceBytes = input.ToArray();
        byte[] exe;
        string kind, exePath, inputName;
        FlightFileIdentity? archiveIdentity = null, diskIdentity = null;
        IReadOnlyList<FlightDiskFile> files = Array.Empty<FlightDiskFile>();
        var findings = new List<string>();
        if (BinaryPrimitives.ReadUInt32BigEndian(input) == 0x3F3)
        {
            kind = "executable"; inputName = provided ?? "user-supplied.hunk"; exePath = inputName;
            exe = sourceBytes;
            findings.Add("Direct executable input: no disk/archive provenance was supplied or inferred.");
        }
        else
        {
            byte[] disk;
            string diskName;
            if (input[..4].SequenceEqual("PK\u0003\u0004"u8))
            {
                kind = "zip"; inputName = provided ?? "user-supplied.zip";
                archiveIdentity = Identity(inputName, sourceBytes);
                (disk, diskName) = BoundedZip.Read(input);
            }
            else
            {
                kind = "adf"; inputName = provided ?? "user-supplied.adf"; diskName = inputName;
                disk = sourceBytes;
            }
            diskIdentity = Identity(diskName, disk);
            var filesystem = new OfsDisk(disk);
            files = filesystem.Files.Select(f => f.Metadata).ToList().AsReadOnly();
            // Neither the ZIP label nor a file name establishes identity. Find exactly one
            // full-size game load-file, rather than choosing an intro or an OS utility.
            var candidates = filesystem.Files.Where(f => f.Bytes.Length == 331232 &&
                BinaryPrimitives.ReadUInt32BigEndian(f.Bytes) == 0x3F3).ToArray();
            if (candidates.Length != 1)
                throw new FlightDataException("source.executable", $"Expected one 331232-byte HUNK game executable; found {candidates.Length}.");
            exe = candidates[0].Bytes; exePath = candidates[0].Metadata.Path;
            findings.Add($"OFS volume {filesystem.VolumeName}: validated {files.Count} complete files and {filesystem.ValidatedBlocks} reachable blocks, including every file payload/checksum and extension pointer.");
            findings.Add("Free/unreferenced sectors are retained in the source identity but are not interpreted as files.");
            if (files.Any(f => f.Path.Equals("unit", StringComparison.OrdinalIgnoreCase)))
                findings.Add("Separate file 'unit' is present; it is not the selected game executable.");
        }
        var parsed = FlightHunkReader.Read(exe);
        parsed = ReferenceLayout.VerifyAndRelocate(parsed, out FlightImage reference);
        var tables = FlightTableExtractor.Extract(parsed.FlatImage, reference, out var pads, out var ships);
        string exeName = exePath.Split('/')[^1];
        var exeIdentity = Identity(exeName, exe);
        if (exeIdentity.Sha256 != SupportedExecutableSha256 || parsed.FlatImage.Sha256 != SupportedFlatImageSha256)
            throw new FlightDataException("source.variant", $"Unsupported executable variant: SHA256 {exeIdentity.Sha256}, flat SHA256 {parsed.FlatImage.Sha256}. Layout alone is not proof of original data compatibility.");
        var scene = FlightSceneExtractor.Extract(parsed.FlatImage, pads, ships);
        bool renamed = !exeName.Equals("F-18 Interceptor", StringComparison.OrdinalIgnoreCase) && kind != "executable";
        if (renamed) findings.Add($"Game executable is renamed '{exePath}', with the complete documented 185-hunk layout, not a packed replacement.");
        findings.Add("All twelve flight/contact blobs match full SHA256 pins of the independently extracted supplied baseline; the airfield and first-quad FNV pins also match the published functional specification.");
        findings.Add("Thirteen valid static landmark footprint records are decoded, with fourteen inner nodes and the three original auxiliary reorder-plane sets. Effect model 34's odd-address/inverted-bounds false positive in the permissive upstream recognizer is not fabricated into a static volume.");
        findings.Add("No pristine publisher-disk/executable hash is available in the local reference. This profile identifies the supplied modified-disk variant; clean-disk equality is NOT asserted.");
        var limitations = new List<string>
        {
            "The reference segment map is one captured Kickstart 1.2 loader layout, not universal stock RAM placement.",
            "Dynamic text bytes 0x440C9..0x440CC and 0x44EA1 are not reproducible from the file image; runtime must resolve or explicitly reject/count these reads.",
            "0x49A12..0x4AE92 is runtime display/variable memory, not a static numeric table. RuntimeWord/state handling is required; zero-filled file bytes are not a live snapshot.",
            "Scene vertices, terrain water polygons, pads and contact structures are original. Animated vertex synthesis, relative groups and strips are explicitly marked source-only where not materialized; never label these complete static meshes.",
            "The package is source data, not a 68000 emulator or a claim that original flight trajectories have been verified."
        };
        var compatibility = new FlightCompatibility(true, true, true, true, true, renamed, false,
            "fa18-x1-38b6e1f3-reference2475dcd", findings.AsReadOnly(), limitations.AsReadOnly());
        var identity = new FlightSourceIdentity(kind, Identity(inputName, sourceBytes), archiveIdentity, diskIdentity, exeIdentity, exePath);
        return new FlightDiskPackage(identity, sourceBytes, provided, parsed, reference, ReferenceLayout.Segments, files, tables, scene, compatibility);
    }

    private static FlightFileIdentity Identity(string name, byte[] bytes) => new(name, bytes.Length, Hashes.Sha256(bytes));
}
