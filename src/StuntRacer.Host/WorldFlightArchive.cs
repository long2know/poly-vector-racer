using System.IO.Compression;
using System.Text.RegularExpressions;

internal sealed partial class WorldFlightArchive(ZipArchive archive) : IDisposable
{
    private readonly object gate = new();

    [GeneratedRegex(@"\Atiles/(conus|alaska|hawaii)/[0-8]/(0|-?[1-9][0-9]{0,3})/(0|-?[1-9][0-9]{0,3})\.bin\z")]
    private static partial Regex TileName();

    public static WorldFlightArchive Open(string path)
    {
        ZipArchive? zip = null;
        try
        {
            zip = ZipFile.OpenRead(path);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                var tile = TileName().IsMatch(name);
                var maxBytes = name switch
                {
                    "manifest.json" or "receipt.json" => ExternalSourceData.MaxJsonBytes,
                    "DATA-NOTICE.txt" or "seattle-buildings.json" or "san-francisco-buildings.json"
                        or "seattle-roads.json" => WorldFlightCatalog.MaxSceneryBytes,
                    _ when tile => WorldFlightCatalog.TileBytes,
                    _ => throw new InvalidDataException("Unexpected or unsafe package entry name."),
                };
                if (!names.Add(name))
                    throw new InvalidDataException($"Duplicate package entry '{name}'.");
                var fileType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (entry.IsEncrypted || fileType is not (0 or 0x8000) ||
                    (entry.ExternalAttributes & (int)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new InvalidDataException($"Package entry '{name}' is not an unencrypted regular file.");
                if (entry.Length <= 0 || entry.Length > maxBytes || tile && entry.Length != maxBytes ||
                    entry.CompressedLength <= 0 || entry.CompressedLength > entry.Length + 65536)
                    throw new InvalidDataException($"Invalid package entry size for '{name}'.");
            }
            return new WorldFlightArchive(zip);
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            zip?.Dispose();
            throw new InvalidDataException($"Cannot open World Flight ZIP. {error.Message}", error);
        }
    }

    public byte[]? Read(string name)
    {
        // Entry streams share the archive's seek position. Finish the bounded read before returning.
        lock (gate)
        {
            if (archive.GetEntry(name) is not { } entry)
                return null;
            try
            {
                using var stream = entry.Open();
                var bytes = new byte[(int)entry.Length];
                stream.ReadExactly(bytes);
                if (stream.ReadByte() != -1)
                    throw new InvalidDataException("The ZIP entry exceeds its declared size.");
                return bytes;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException)
            {
                throw new InvalidDataException($"Cannot read World Flight ZIP entry '{name}'.", error);
            }
        }
    }

    public void Dispose() => archive.Dispose();
}
