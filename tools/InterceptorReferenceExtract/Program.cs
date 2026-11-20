using System.Text;
using StuntRacer.FlightData;

return ExtractCommand.Run(args);

internal static class ExtractCommand
{
    private static readonly string[] OracleTables =
    [
        "flight_stick_player", "flight_stick_ai", "flight_gust", "flight_sink", "flight_len", "flight_terrain", "flight_dir",
        "flight_pads", "flight_span_pitch", "flight_span_trig", "flight_carrier", "flight_ship2"
    ];

    internal static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h")
            {
                Console.WriteLine("Nonexecuting F/A-18 original-data ingestion (no original data distributed).");
                Console.WriteLine("  inspect --input ADF_OR_ZIP_OR_HUNK [--oracle LOCAL_BASELINE_DIR]");
                Console.WriteLine("  extract --input ADF_OR_ZIP_OR_HUNK --output artifacts/flight-source/ingestion/NAME [--oracle LOCAL_BASELINE_DIR] [--overwrite]");
                Console.WriteLine("  verify-package --input PACKAGE_JSON");
                return args.Length == 0 ? 2 : 0;
            }
            string command = args[0];
            if (command is not ("inspect" or "extract" or "verify-package")) throw new ArgumentException("Unknown command.");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            bool overwrite = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--overwrite")
                {
                    if (overwrite) throw new ArgumentException("Duplicate --overwrite.");
                    overwrite = true; continue;
                }
                if (args[i] is not ("--input" or "--output" or "--oracle") || i + 1 >= args.Length || !options.TryAdd(args[i], args[++i]))
                    throw new ArgumentException("Unknown/duplicate option or missing value.");
            }
            if (!options.TryGetValue("--input", out string? input)) throw new ArgumentException("--input is required.");
            if (command != "extract" && (overwrite || options.ContainsKey("--output")))
                throw new ArgumentException("Output/overwrite options require extract.");
            if (command == "verify-package" && options.ContainsKey("--oracle")) throw new ArgumentException("--oracle requires original source ingestion.");
            string inputPath = Path.GetFullPath(input);
            int limit = command == "verify-package" ? FlightPackageSerializer.MaximumPackageBytes : FlightDiskReader.MaximumInputBytes;
            var info = new FileInfo(inputPath);
            if (info.Length > limit) throw new ArgumentException($"Input exceeds {limit} bytes.");
            byte[] bytes = File.ReadAllBytes(inputPath);
            var package = command == "verify-package"
                ? FlightPackageSerializer.ReadPackage(bytes)
                : FlightDiskReader.Read(bytes, new FlightReadOptions(Path.GetFileName(inputPath)));
            if (options.TryGetValue("--oracle", out string? oracle)) VerifyOracle(package, Path.GetFullPath(oracle));
            if (command == "extract")
            {
                if (!options.TryGetValue("--output", out string? output)) throw new ArgumentException("--output is required.");
                string outputPath = ScopedOutput(output);
                if (inputPath.Equals(outputPath, StringComparison.Ordinal) || inputPath.StartsWith(outputPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new ArgumentException("Output must not contain/replace the supplied input.");
                WriteAtomic(package, outputPath, overwrite);
                Console.WriteLine($"External package: {Path.Combine(outputPath, "package.json")}");
                Console.WriteLine($"Metadata receipt: {Path.Combine(outputPath, "receipt.json")}");
            }
            else Console.WriteLine(Encoding.UTF8.GetString(FlightPackageSerializer.WriteReceipt(package)));
            return 0;
        }
        catch (FlightDataException e) { Console.Error.WriteLine($"{e.Code}: {e.Message}" + (e.Offset is null ? "" : $" (offset/address 0x{e.Offset:X})")); return 1; }
        catch (IOException e) { Console.Error.WriteLine($"I/O error: {e.Message}"); return 1; }
        catch (UnauthorizedAccessException e) { Console.Error.WriteLine($"Access denied: {e.Message}"); return 1; }
        catch (ArgumentException e) { Console.Error.WriteLine($"Usage error: {e.Message}"); return 2; }
    }

    private static void VerifyOracle(FlightDiskPackage package, string directory)
    {
        foreach (string name in OracleTables)
        {
            string path = Path.Combine(directory, "tables", name + ".bin");
            var table = package.Tables[name];
            if (new FileInfo(path).Length != table.Bytes.Length || !File.ReadAllBytes(path).AsSpan().SequenceEqual(table.Bytes.Span))
                throw new FlightDataException("oracle.table", $"C# bytes differ from local nonexecuting oracle: {name}.");
        }
        Compare("flat-image.bin", package.FlatImage.Bytes.Span);
        Compare("reference-window-image.bin", package.ReferenceWindowImage.Bytes.Span);
        Console.WriteLine("Local oracle: all 12 original flight/contact tables and both 298988-byte image/window buffers are byte-identical.");

        void Compare(string name, ReadOnlySpan<byte> expected)
        {
            string path = Path.Combine(directory, name);
            if (new FileInfo(path).Length != expected.Length || !File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))
                throw new FlightDataException("oracle.image", $"C# image differs from local oracle: {name}.");
        }
    }

    private static string ScopedOutput(string output)
    {
        DirectoryInfo? root = new(Directory.GetCurrentDirectory());
        bool found = false;
        for (int i = 0; i < 32 && root != null; i++, root = root.Parent)
            if (File.Exists(Path.Combine(root.FullName, "Directory.Build.props")) && Directory.Exists(Path.Combine(root.FullName, "src")))
            { found = true; break; }
        if (!found || root == null) throw new ArgumentException("Run the tool inside this repository.");
        string allowed = Path.Combine(root.FullName, "artifacts", "flight-source", "ingestion");
        string path = Path.GetFullPath(output, Directory.GetCurrentDirectory()).TrimEnd(Path.DirectorySeparatorChar);
        if (!path.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Output must be an explicit child directory under ignored artifacts/flight-source/ingestion/.");
        EnsureNoLinks(path, root.FullName);
        return path;
    }

    private static void EnsureNoLinks(string path, string stop)
    {
        for (DirectoryInfo? d = new(path); d != null; d = d.Parent)
        {
            if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Output paths must not traverse symlinks/reparse points.");
            if (d.FullName == stop) return;
        }
        throw new ArgumentException("Output path is not inside the repository.");
    }

    private static void WriteAtomic(FlightDiskPackage package, string output, bool overwrite)
    {
        if (File.Exists(output)) throw new IOException("Output names an existing file.");
        if (Directory.Exists(output) && !overwrite) throw new IOException("Output already exists; use --overwrite explicitly.");
        string parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        string temp = Path.Combine(parent, "." + Path.GetFileName(output) + ".tmp-" + Guid.NewGuid().ToString("N"));
        string backup = output + ".old-" + Guid.NewGuid().ToString("N");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(temp);
        else Directory.CreateDirectory(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        bool installed = false, movedOld = false;
        try
        {
            Write("package.json", FlightPackageSerializer.WritePackage(package));
            Write("receipt.json", FlightPackageSerializer.WriteReceipt(package));
            Write("scene.json", FlightPackageSerializer.WriteScene(package));
            Write("flat-image.bin", package.FlatImage.Bytes.Span);
            Write("reference-image.bin", package.ReferenceImage.Bytes.Span);
            Write("reference-window-image.bin", package.ReferenceWindowImage.Bytes.Span);
            Directory.CreateDirectory(Path.Combine(temp, "tables"));
            foreach (var (name, table) in package.Tables) Write(Path.Combine("tables", name + ".bin"), table.Bytes.Span);
            // Validate our serialized boundary before exposing ANY final output.
            FlightPackageSerializer.ReadPackage(File.ReadAllBytes(Path.Combine(temp, "package.json")));
            if (Directory.Exists(output))
            {
                var pending = new Stack<DirectoryInfo>();
                pending.Push(new DirectoryInfo(output));
                while (pending.TryPop(out var directory))
                    foreach (var entry in directory.EnumerateFileSystemInfos())
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("Existing output contains a link/reparse point; refusing replacement.");
                        if (entry is DirectoryInfo child) pending.Push(child);
                    }
                Directory.Move(output, backup); movedOld = true;
            }
            try { Directory.Move(temp, output); installed = true; }
            catch (IOException)
            {
                if (movedOld && !Directory.Exists(output)) { Directory.Move(backup, output); movedOld = false; }
                throw;
            }
            if (movedOld) { Directory.Delete(backup, true); movedOld = false; }
        }
        finally
        {
            if (!installed && Directory.Exists(temp)) Directory.Delete(temp, true);
        }

        void Write(string relative, ReadOnlySpan<byte> bytes)
        {
            string path = Path.Combine(temp, relative);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
