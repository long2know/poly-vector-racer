using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using StuntRacer.CompatibilityChecks;
using StuntRacer.Core.SourceRuntime;

namespace StuntRacer.SourceRuntimeChecks;

internal static class PackageChecks
{
    public static IEnumerable<NativeCheck> All(PackageFixture fixture)
    {
        yield return new("LittleRamp / actual source layout, tables, spawn and shared exact road", () => RealData(fixture));
        yield return new("LittleRamp / malformed packages cannot default data, timing or qualification", () => RejectMalformed(fixture));
    }

    private static void RealData(PackageFixture fixture)
    {
        var package = fixture.Package;
        var textReader = SourceAmigaPackageReader.Read(Encoding.UTF8.GetString(fixture.Bytes));
        Check.Equal(44, package.Layout.SectionCount, "actual LittleRamp source sections");
        Check.Equal(15, package.Layout.StartSectionIndex, "actual LittleRamp source start");
        Check.Equal(398, package.RawRoad.Count, "actual source segments");
        Check.Equal(442, package.ImportedTrack.Edges.Count, "actual full-section edges");
        Check.Equal(81920, package.Lookups.SinQ14.Length, "actual source sine plus quarter overlap");
        Check.Equal(128, package.Lookups.CosineConversion.Count, "actual external inclination table");
        Check.Equal("amiga-derived-source-compatible", package.ProfileId, "source candidate profile");
        Check.Require(!package.OriginalStockParity, "external extraction must not certify stock parity");
        var layout = package.Provenance.Single(asset => asset.Role == "layout");
        Check.Equal(804L, layout.ByteLength, "actual layout byte count");
        Check.Equal("0945e51ca007d9695f877eeec03cc4282f49f2e8ef3cfc63c8281f592a25a3a2",
            layout.Sha256, "independently identified LittleRamp layout hash");
        Check.Equal(layout.Sha256, Convert.ToHexStringLower(SHA256.HashData(package.Layout.SourceBytes.Span)),
            "embedded layout must be the actual identified bytes");
        Check.Equal(package.Start, textReader.Start, "both public package-reader paths");
        Check.Equal(771751936L, package.Start.XQ14, "source start x");
        Check.Equal(36175872L, package.Start.ZQ14, "source start z");
        Check.Equal(41943040L, package.Start.HeightQ14, "source native stored-height Q14");
        Check.Equal(3407872L, package.Start.InitialRawY, "source native initial drop height");
        Check.Equal(49152, package.Start.HeadingAngle, "source start heading");
        Check.Require(package.TrySampleRawRoad(package.Start.XQ14, package.Start.ZQ14,
            package.Start.InitialRawY * 16, out var road), "actual source spawn has no native road");
        Check.Equal(41943040L, road.HeightQ14, "source sampler must not pre-divide the native Y by four");
        Check.Near(10, package.Track.Sample(736, 34.5).Y, "same canonical road display Y");
        Check.Near(13, SourceSnapshotAdapter.ToDisplayY(checked((int)package.Start.InitialRawY),
            package.Units.DisplayMetresPerPcUnit), "raw player Y display mapping");
        var scale = package.Units.DisplayMetresPerPcUnit;
        for (var index = 0; index < package.RawRoad.Count; index++)
        {
            var source = package.RawRoad[index];
            var next = package.RawRoad[(index + 1) % package.RawRoad.Count];
            Check.Equal(source.LeftEnd, next.LeftStart, $"source left seam {index}");
            Check.Equal(source.RightEnd, next.RightStart, $"source right seam {index}");
            var rotated = (index - package.ImportedTrack.StartSegmentIndex + package.RawRoad.Count) % package.RawRoad.Count;
            var edge = package.Track.Edges[rotated];
            Check.Near(source.LeftStart.X * scale, edge.Left.X, $"canonical raw/display left x {index}");
            Check.Near(source.LeftStart.Y / 4.0 * scale, edge.Left.Y, $"canonical raw/display left y {index}");
            Check.Near(source.LeftStart.Z * scale, edge.Left.Z, $"canonical raw/display left z {index}");
            Check.Near(source.RightStart.Y / 4.0 * scale, edge.Right.Y, $"canonical raw/display right y {index}");
        }
        Console.WriteLine($"  real package SHA256 {Convert.ToHexStringLower(SHA256.HashData(fixture.Bytes))}; " +
                          $"{package.Layout.SectionCount} sections / {package.RawRoad.Count} segments / " +
                          $"{package.Lookups.SinQ14.Length} source sine entries.");
    }

    private static void RejectMalformed(PackageFixture fixture)
    {
        Action<JsonObject>[] mutations =
        [
            json => json.Remove("commonData"),
            json => json.Remove("lookups"),
            json => json["originalStockParity"] = true,
            json => json["kind"] = "stunt-authored-track",
            json => json["layoutBase64"] = "",
            json => json["units"]!["roadHeightDivisor"] = 1,
            json => json["clock"]!["simulationStepSeconds"] = 1.0 / 120,
            json => json["lookups"]!["sinQ14"] = new JsonArray(),
            json => json["lookups"]!["cosineConversion"] = new JsonArray()
        ];
        for (var index = 0; index < mutations.Length; index++)
        {
            var json = JsonNode.Parse(fixture.Bytes)!.AsObject();
            mutations[index](json);
            Check.Throws<InvalidDataException>(() => SourceAmigaPackageReader.Read(json.ToJsonString()),
                $"malformed source package {index}");
        }
    }
}
