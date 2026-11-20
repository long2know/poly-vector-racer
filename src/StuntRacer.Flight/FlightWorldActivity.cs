namespace StuntRacer.Flight;

public sealed record FlightAmbientActor(
    string Id, string Kind, double X, double Y, double Z, double Yaw, double Pitch, double Roll, double Phase);

public sealed record FlightActivityApron(
    double CenterX, double CenterZ, double Height, double Heading, double Width, double Length);

public sealed record FlightWorldActivity(
    string Profile, FlightActivityApron Apron, IReadOnlyList<FlightAmbientActor> Actors);

public sealed class FlightAmbientSimulation
{
    private readonly FlightActivityApron apron;
    private readonly FlightVector origin;
    private double cachedTime = double.NaN;
    private FlightWorldActivity? cached;

    public FlightAmbientSimulation(FlightWorldDefinition world)
    {
        origin = world.RunwayStart;
        var runway = world.LandingSurfaces.MinBy(p =>
            Math.Pow(p.CenterX - origin.X, 2) + Math.Pow(p.CenterZ - origin.Z, 2))
            ?? throw new ArgumentException("World activity requires a landing surface.", nameof(world));
        var usableHalf = Math.Max(0, runway.Length / 2 - 150);
        var along = Math.Clamp((origin.X - runway.CenterX) * Math.Sin(runway.Heading) +
            (origin.Z - runway.CenterZ) * Math.Cos(runway.Heading), -usableHalf, usableHalf);
        var side = runway.Width / 2 + 40;
        apron = new(runway.CenterX + Math.Sin(runway.Heading) * along + Math.Cos(runway.Heading) * side,
            runway.CenterZ + Math.Cos(runway.Heading) * along - Math.Sin(runway.Heading) * side,
            runway.Height + 0.035, runway.Heading, 38, 260);
    }

    public FlightWorldActivity Sample(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        if (cached is not null && seconds == cachedTime) return cached;
        var actors = new List<FlightAmbientActor>();
        FlightAmbientActor Ground(string id, string kind, double x, double z, double yaw, double phase) =>
            new(id, kind, apron.CenterX + Math.Cos(apron.Heading) * x + Math.Sin(apron.Heading) * z,
                apron.Height, apron.CenterZ - Math.Sin(apron.Heading) * x + Math.Cos(apron.Heading) * z,
                apron.Heading + yaw, 0, 0, phase);
        for (var i = 0; i < 5; i++)
        {
            var s = (seconds * (5.5 + i * 0.3) + i * 97) % 512;
            double x, z, heading;
            if (s < 240) { x = -8; z = s - 120; heading = 0; }
            else if (s < 256) { x = s - 248; z = 120; heading = Math.PI / 2; }
            else if (s < 496) { x = 8; z = 376 - s; heading = Math.PI; }
            else { x = 504 - s; z = -120; heading = -Math.PI / 2; }
            actors.Add(Ground($"service-{i}", "service-car", x, z, heading, seconds * 7));
        }
        for (var i = 0; i < 8; i++)
        {
            var travel = (seconds * 1.1 + i * 23) % 320;
            actors.Add(Ground($"crew-{i}", "walker", i % 2 == 0 ? -16 : 16,
                travel < 160 ? travel - 80 : 240 - travel, travel < 160 ? 0 : Math.PI, seconds * 7 + i));
        }
        for (var i = 0; i < 2; i++)
        {
            var angle = seconds * (0.055 + i * 0.01) + 1.5 + i * Math.PI;
            var radius = 1150 + i * 450;
            actors.Add(new($"air-traffic-{i}", "aircraft", origin.X + Math.Sin(angle) * radius,
                220 + i * 100, origin.Z + 600 + Math.Cos(angle) * radius,
                angle + Math.PI / 2, 0, -0.13, seconds));
        }
        cachedTime = seconds;
        return cached = new("authored-ambient-activity / not-original-mission-AI", apron, actors.ToArray());
    }
}
