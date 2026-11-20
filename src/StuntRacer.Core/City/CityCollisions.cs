namespace StuntRacer.Core.City;

internal static class CityCollisions
{
    public static bool Resolve(GameSimulation.Racer racer, double x, double z, double width, double depth,
        double baseY, double height, double yaw)
    {
        ref var car = ref racer.Car;
        if (car.Y + 0.65 < baseY || car.Y - 0.65 > baseY + height ||
            Math.Abs(x - car.X) > (width + depth) / 2 + 3 ||
            Math.Abs(z - car.Z) > (width + depth) / 2 + 3)
            return false;
        var arx = Math.Cos(car.Yaw);
        var arz = -Math.Sin(car.Yaw);
        var afx = -arz;
        var afz = arx;
        var brx = Math.Cos(yaw);
        var brz = -Math.Sin(yaw);
        var bfx = -brz;
        var bfz = brx;
        var dx = x - car.X;
        var dz = z - car.Z;
        var minimum = double.MaxValue;
        double nx = 0, nz = 0;
        for (var i = 0; i < 4; i++)
        {
            var ax = i switch { 0 => arx, 1 => afx, 2 => brx, _ => bfx };
            var az = i switch { 0 => arz, 1 => afz, 2 => brz, _ => bfz };
            var extentA = 1.05 * Math.Abs(arx * ax + arz * az) + 2 * Math.Abs(afx * ax + afz * az);
            var extentB = width / 2 * Math.Abs(brx * ax + brz * az) + depth / 2 * Math.Abs(bfx * ax + bfz * az);
            var center = dx * ax + dz * az;
            var overlap = extentA + extentB - Math.Abs(center);
            if (overlap <= 0)
                return false;
            if (overlap < minimum)
            {
                minimum = overlap;
                var sign = center >= 0 ? 1 : -1;
                nx = ax * sign;
                nz = az * sign;
            }
        }
        car.X -= nx * (minimum + 1e-5);
        car.Z -= nz * (minimum + 1e-5);
        var closing = car.Vx * nx + car.Vz * nz;
        if (closing > 0)
        {
            car.Vx -= nx * closing * 1.12;
            car.Vz -= nz * closing * 1.12;
            if (closing > 0.8)
                GameSimulation.ApplyImpact(racer, Math.Clamp(closing / 25, 0, 0.9));
        }
        return true;
    }

    public static string? ResolveWorld(GameSimulation.Racer racer, CityWorldDefinition world)
    {
        string? hit = null;
        foreach (var b in world.Buildings)
            if (Resolve(racer, b.CenterX, b.CenterZ, b.Width, b.Depth, b.BaseY, b.Height, b.Yaw))
                hit = b.Name;
        foreach (var b in world.Structures)
            if (b.Solid && Resolve(racer, b.CenterX, b.CenterZ, b.Width, b.Depth, b.BaseY, b.Height, b.Yaw))
                hit = b.Name;
        return hit;
    }
}
