internal sealed class WorldSceneryDirectory(string directory, string webRoot)
{
    public const string SeattleArchive = "seattle.zip";
    public string PathOnDisk { get; } = directory;

    public static WorldSceneryDirectory? Resolve(string? path, string bundle)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string resolvedDirectory, resolvedWebRoot;
        try
        {
            if (!Path.IsPathFullyQualified(path))
                throw new ArgumentException("STUNT_WORLD_ATLAS must point to an absolute directory.");
            var configured = new DirectoryInfo(path);
            if (!configured.Exists)
                throw new DirectoryNotFoundException("The configured scenery directory does not exist or is inaccessible.");
            resolvedDirectory = ResolveDirectory(configured);
            resolvedWebRoot = ResolveDirectory(new DirectoryInfo(bundle));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"World Flight scenery disabled: {error.Message}");
            return null;
        }

        if (ExternalSourceData.Within(path, bundle) || ExternalSourceData.Within(resolvedDirectory, resolvedWebRoot))
            throw new InvalidDataException("The configured scenery directory must be outside the public WASM bundle.");
        return new(resolvedDirectory, resolvedWebRoot);
    }

    public WorldFlightCatalog? LoadSeattle()
    {
        if (Find(PathOnDisk, SeattleArchive) is { } zip)
            return WorldFlightCatalog.Load(RequireType(zip, isDirectory: false), webRoot);
        if (Find(PathOnDisk, "seattle") is not { } folder)
            return null;
        var manifest = Find(RequireType(folder, isDirectory: true), "manifest.json")
            ?? throw new FileNotFoundException("The extracted World Flight package is missing seattle/manifest.json.");
        return WorldFlightCatalog.Load(RequireType(manifest, isDirectory: false), webRoot);
    }

    public static string? Find(string parent, string name) =>
        Directory.EnumerateFileSystemEntries(parent, name).FirstOrDefault(path => Path.GetFileName(path) == name);

    private static string RequireType(string path, bool isDirectory)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("External source files and directories must not be symbolic links.");
        if (attributes.HasFlag(FileAttributes.Directory) != isDirectory)
            throw new InvalidDataException($"The World Flight entry '{Path.GetFileName(path)}' has the wrong file type.");
        return path;
    }

    private static string ResolveDirectory(DirectoryInfo directory)
    {
        if (directory.ResolveLinkTarget(returnFinalTarget: true) is DirectoryInfo target)
            return ResolveDirectory(target);
        return directory.Parent is { } parent ? Path.Combine(ResolveDirectory(parent), directory.Name) : directory.FullName;
    }
}
