namespace AmigaTrackImport;

internal static class PrivateOutput
{
    internal static string WriteNew(string requestedPath, byte[] bytes)
    {
        var workingDirectory = Path.GetFullPath(Environment.CurrentDirectory);
        if (!File.Exists(Path.Combine(workingDirectory, "src", "StuntRacer.Core", "StuntRacer.Core.csproj")) ||
            !File.Exists(Path.Combine(workingDirectory, "tools", "AmigaTrackImport", "AmigaTrackImport.csproj")) ||
            !(Directory.Exists(Path.Combine(workingDirectory, ".git")) || File.Exists(Path.Combine(workingDirectory, ".git"))))
            throw new InvalidDataException("Run conversion from the repository root; no output is allowed under a browser/static directory.");
        var privateRoot = Path.Combine(workingDirectory, "artifacts", "amiga-compat");
        var output = Path.GetFullPath(requestedPath, workingDirectory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!output.StartsWith(privateRoot + Path.DirectorySeparatorChar, comparison) ||
            !output.EndsWith(".track.json", StringComparison.Ordinal))
            throw new InvalidDataException(
                "--out must be a new .track.json file strictly beneath this repository's artifacts/amiga-compat/.");
        RejectLinks(output);
        if (File.Exists(output) || Directory.Exists(output) || new FileInfo(output).LinkTarget is not null)
            throw new InvalidDataException("--out already exists; choose a new private output file.");
        var parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        RejectLinks(output);
        FileStream? stream = null;
        var created = false;
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            stream = new FileStream(output, options);
            created = true;
            RejectLinks(output);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            stream?.Dispose();
            if (created)
                File.Delete(output);
            throw;
        }
        finally
        {
            stream?.Dispose();
        }
        return Path.GetRelativePath(workingDirectory, output).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static void RejectLinks(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var component in path[root.Length..].Split(
                     Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null ||
                (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException("--out and every ancestor must be real local paths, not symlinks/reparse points.");
        }
    }
}
