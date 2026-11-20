using StuntRacer.Core.SourceRuntime;

namespace StuntRacer.SourceRuntimeChecks;

internal sealed class PackageFixture
{
    public PackageFixture(string path)
    {
        Bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(Bytes, writable: false);
        Package = SourceAmigaPackageReader.Read(stream);
    }

    public byte[] Bytes { get; }
    public SourceAmigaPackage Package { get; }
}
