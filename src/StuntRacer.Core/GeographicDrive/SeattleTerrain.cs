namespace StuntRacer.Core.GeographicDrive;

internal sealed class SeattleTerrain(Func<double, double, (double Height, bool IsWater)?> ground) : IOpenDrivingSurface
{
    public bool Missing { get; set; }

    public (double Height, bool IsWater)? At(double x, double z) =>
        ground(x, z) is { } value && double.IsFinite(value.Height) ? value : null;

    public bool SegmentIsDry(DrivePoint a, DrivePoint b, double length)
    {
        var checks = (int)Math.Ceiling(length / 4);
        for (var i = 0; i <= checks; i++)
        {
            var point = a.Toward(b, (double)i / checks);
            var sample = At(point.X, point.Z) ??
                throw new InvalidDataException("Seattle Drive is missing required street terrain.");
            if (sample.IsWater) return false;
        }
        return true;
    }

    public RoadSample Sample(double x, double z, double carY)
    {
        var center = At(x, z);
        var x0 = At(x - 0.25, z);
        var x1 = At(x + 0.25, z);
        var z0 = At(x, z - 0.25);
        var z1 = At(x, z + 0.25);
        if (center is null || x0 is null || x1 is null || z0 is null || z1 is null)
        {
            Missing = true;
            return default;
        }
        return new RoadSample(0, x, center.Value.Height, z, 0, 0, 0, 0, !center.Value.IsWater,
            (x1.Value.Height - x0.Value.Height) * 2, (z1.Value.Height - z0.Value.Height) * 2);
    }
}
