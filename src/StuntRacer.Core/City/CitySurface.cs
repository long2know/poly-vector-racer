namespace StuntRacer.Core.City;

internal sealed class CitySurface(CityWorldDefinition world) : IOpenDrivingSurface
{
    public RoadSample Sample(double x, double z, double carY)
    {
        var bounds = world.Bounds;
        var ground = x >= bounds.MinX && x <= bounds.MaxX && z >= bounds.MinZ && z <= bounds.MaxZ && !IsWater(x, z);
        var result = new RoadSample(0, x, world.GroundY, z, 0, 0, 0, 0, ground, 0, 0);
        var roadFound = false;
        foreach (var road in world.Roads)
        for (var i = 0; i < road.Points.Length - 1; i++)
        {
            var a = road.Points[i];
            var b = road.Points[i + 1];
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            var lengthSquared = dx * dx + dz * dz;
            var t = ((x - a.X) * dx + (z - a.Z) * dz) / lengthSquared;
            if (t is < 0 or > 1)
                continue;
            var length = Math.Sqrt(lengthSquared);
            var lateral = ((x - a.X) * dz - (z - a.Z) * dx) / length;
            var sidewalk = Math.Abs(lateral) > road.Width / 2;
            if (Math.Abs(lateral) > road.Width / 2 + road.SidewalkWidth || sidewalk && roadFound)
                continue;
            var y = a.Y + (b.Y - a.Y) * t + (sidewalk ? road.SidewalkHeight : 0);
            if (y > carY + 0.65 || roadFound && y < result.Y - 1e-6)
                continue;
            result = new RoadSample(0, x, y, z, Math.Atan2(dx, dz), 0, road.Width, lateral, true,
                (b.Y - a.Y) * dx / lengthSquared, (b.Y - a.Y) * dz / lengthSquared);
            roadFound |= !sidewalk;
        }
        return result;
    }

    public CityRoadDefinition? RoadAt(double x, double z)
    {
        foreach (var road in world.Roads)
        for (var i = 0; i < road.Points.Length - 1; i++)
        {
            var a = road.Points[i];
            var b = road.Points[i + 1];
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            var lengthSquared = dx * dx + dz * dz;
            var t = ((x - a.X) * dx + (z - a.Z) * dz) / lengthSquared;
            if (t is >= 0 and <= 1 &&
                Math.Abs((x - a.X) * dz - (z - a.Z) * dx) <= (road.Width / 2 + road.SidewalkWidth) * Math.Sqrt(lengthSquared))
                return road;
        }
        return null;
    }

    public bool IsWater(double x, double z)
    {
        foreach (var area in world.Areas)
            if (area.Kind == "water" && Contains(area.Points, x, z))
                return true;
        return false;
    }

    public string DistrictAt(double x, double z)
    {
        if (x > 640)
            return "East Harbor";
        if (x > 365)
            return "Waterfront";
        if (z < -380 || x < -510)
            return "Foundry";
        return x < -180 || z > 330 ? "Garden Quarter" : "Downtown";
    }

    internal static bool Contains(CityPoint[] points, double x, double z)
    {
        var inside = false;
        for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
        {
            var a = points[i];
            var b = points[j];
            if ((a.Z > z) != (b.Z > z) && x < (b.X - a.X) * (z - a.Z) / (b.Z - a.Z) + a.X)
                inside = !inside;
        }
        return inside;
    }
}
