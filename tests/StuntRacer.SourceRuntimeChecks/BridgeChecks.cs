using StuntRacer.CompatibilityChecks;
using StuntRacer.Core.SourceRuntime;

namespace StuntRacer.SourceRuntimeChecks;

internal static class BridgeChecks
{
    public static IEnumerable<NativeCheck> All()
    {
        yield return new("source units / native height TZDIV/ASHR and positive display axes", () =>
        {
            foreach (var item in ReferenceCases.Heights)
            {
                Check.Equal(item.RoadHeight, SourceSnapshotAdapter.ToWheelRoadHeight(item.StorageHeightQ14),
                    $"stored height {item.StoredHeight} wheel units");
                Check.Near(item.RenderPCY, SourceSnapshotAdapter.ToDisplayY(item.PlayerYAtRoad, 1),
                    $"stored height {item.StoredHeight} display PC units");
            }
            Check.Equal(0, SourceSnapshotAdapter.ToWheelRoadHeight(-1), "TZDIV2 must precede ASHR11 for negative odd heights");
            Check.Equal(-1, SourceSnapshotAdapter.ToWheelRoadHeight(-4095), "negative native height preserves per-operation rounding");
            Check.Near(-1, SourceSnapshotAdapter.ToDisplayY(-4096, 1), "raw Y stays up; no old view-pointer minus");
            Check.Near(1, SourceSnapshotAdapter.ToDisplayXz(16384, 1), "raw XZ Q14 precision");
            foreach (var scale in new[] { 0, -1, double.NaN, double.PositiveInfinity })
            {
                Check.Throws<ArgumentOutOfRangeException>(() => SourceSnapshotAdapter.ToDisplayY(4096, scale), "invalid display Y scale");
                Check.Throws<ArgumentOutOfRangeException>(() => SourceSnapshotAdapter.ToDisplayXz(16384, scale), "invalid display XZ scale");
            }
        });
    }
}
