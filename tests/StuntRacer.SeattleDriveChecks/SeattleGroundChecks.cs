using System.IO.Compression;
using System.Text.Json;
using StuntRacer.Core;
using StuntRacer.Core.GeographicDrive;
using StuntRacer.Flight;

internal static class SeattleGroundChecks
{
    private const double Step = 1.0 / 120;
    private const string ResumeGps = "Off-road — return to a mapped street to resume GPS.";

    public static void CheckSpawn(ZipArchive package, SeattleDrivingSession game, WorldFlightElevation elevation)
    {
        var pose = game.Snapshot.Player;
        var spawn = SeattleDriveArea.Spawn;
        Check(new DrivePoint(pose.X, pose.Z) == spawn &&
            elevation.Sample(spawn.X, spawn.Z) is { IsWater: false } ground && Math.Abs(pose.Y - ground.Height - 1) < 1e-8,
            "The physical car must start at the authored dry spawn.");
        Check(game.MapStreets.Any(road => FacesStreet(road, spawn, pose.Yaw)),
            "The exact spawn node and heading must face along a connected street.");

        using var stream = package.GetEntry("seattle-buildings.json")!.Open();
        using var buildings = JsonDocument.Parse(stream);
        var ring = buildings.RootElement.GetProperty("buildings").EnumerateArray()
            .Single(row => row[0].GetInt64() == 1154870285)[3].EnumerateArray()
            .Select(point => new DrivePoint(point[0].GetDouble(), point[1].GetDouble())).ToArray();
        var minimum = double.PositiveInfinity;
        foreach (var right in new[] { -1.05, 1.05 })
        foreach (var forward in new[] { -2.15, 2.15 })
        {
            var corner = new DrivePoint(spawn.X + Math.Cos(pose.Yaw) * right + Math.Sin(pose.Yaw) * forward,
                spawn.Z - Math.Sin(pose.Yaw) * right + Math.Cos(pose.Yaw) * forward);
            minimum = Math.Min(minimum, FootprintClearance(corner, ring));
        }
        var center = FootprintClearance(spawn, ring);
        Check(center >= 7 && minimum >= 4.5, $"Spawn intersects or crowds the original building ({center:F2}/{minimum:F2} m).");
        Console.WriteLine($"Spawn building clearance: {center:F2} m at center, {minimum:F2} m at tightest chassis corner.");
    }

    public static void Run(string roads, WorldFlightElevation elevation)
    {
        var available = true;
        var bounds = SeattleDriveArea.Bounds;
        // Use the real heights, but don't supply terrain beyond the playable area.
        var game = new SeattleDrivingSession(roads, (x, z) => available &&
            x >= bounds.MinX && x <= bounds.MaxX && z >= bounds.MinZ && z <= bounds.MaxZ ? elevation.Sample(x, z) : null);
        var map = game.MapStreets.ToArray();
        game.Command("start");
        game.Command("destination:great-wheel");
        var originalPlan = game.Plan!;
        LaunchLeft(game);
        var coast = new DriverInput(0, .55, 0, false);
        for (var i = 0; i < 720 && game.Activity.Status != "off-road"; i++) game.Advance(Step, coast);
        var leftAt = game.Snapshot.Tick;
        Check(game.Activity.Status == "off-road" && game.Plan is null, "Manual input must cross the mapped shoulder and clear the plan.");
        var offRevision = game.Activity.PlanRevision;
        var firstOff = Position(game.Snapshot);
        for (var i = 0; i < 96; i++) game.Advance(Step, default);
        var off = game.Snapshot;
        var point = Position(off);
        var clearance = map.Min(road => DistanceToSegment(point, new(road[0], road[1]), new(road[2], road[3])));
        Check(clearance > 9.5 && Distance(firstOff, point) > 5 && off.Player.Grounded &&
            elevation.Sample(point.X, point.Z) is { IsWater: false },
            $"Manual excursion must keep driving on real dry ground past the full mapped shoulder ({clearance:F2} m from centerline).");
        Check(offRevision > originalPlan.Revision && game.Plan is null &&
            game.Activity is { Status: "off-road", DestinationId: "great-wheel", Remaining: null, Waypoint: null } &&
            game.Activity.PlanRevision == offRevision && game.Activity.Message == ResumeGps,
            "Off-road guidance must retain the stop without a stale route, projected station, or repeated plan churn.");
        game.Command("autopilot-on");
        Check(!game.Activity.Autopilot && game.Activity.Message == ResumeGps && game.Plan is null &&
            Position(game.Snapshot) == point && game.Snapshot.Tick == off.Tick,
            "Autopilot must reject an off-road start without snapping back or stepping time.");
        game.Command("gps-clear");
        Check(game.Activity is { Status: "off-road", DestinationId: null, Message: "Off-road — manual driving on dry ground." },
            "Dry off-road exploration has distinct feedback when no stop is selected.");
        game.Command("destination:great-wheel");
        available = false;
        game.Advance(.25, new(0, 1, 0, false));
        Check(game.Activity.TerrainWaiting && game.Snapshot.Tick == off.Tick && Position(game.Snapshot) == point,
            "Missing terrain while off-road must freeze the vehicle and clock.");
        available = true;

        for (var i = 0; i < 2400 && game.Plan is null; i++) game.Advance(Step, new(0, 0, 1, false));
        var reentered = game.Snapshot;
        Check(game.Plan is { } restored && restored.Revision > offRevision && game.Activity is
            { Status: "guidance", DestinationId: "great-wheel", Remaining: > 0, Waypoint: not null, Autopilot: false } &&
            Distance(Position(reentered), restored.Points[0]) < 9.01 && reentered.Tick > off.Tick,
            $"Returning to a street must restore a fresh route without teleporting or automatically engaging ({game.Activity.Status}).");
        game.Command("autopilot-on");
        Check(game.Activity.Autopilot && Position(game.Snapshot) == Position(reentered), "Autopilot may re-engage on the mapped street.");
        Console.WriteLine($"Dry excursion: {clearance:F2} m from the nearest street; GPS restored at tick {reentered.Tick} after leaving at {leftAt}.");

        // Reproduce the same approach one tick before leaving; Autopilot cannot coast across the shoulder.
        game.Command("restart");
        LaunchLeft(game);
        while (game.Snapshot.Tick < leftAt - 1) game.Advance(Step, coast);
        var beforeEdge = game.Snapshot;
        game.Command("autopilot-on");
        Check(game.Activity.Autopilot, "Autopilot edge fixture must engage on the mapped side of the shoulder.");
        game.Advance(Step, default);
        var afterEdge = game.Snapshot;
        Check(Position(afterEdge) == Position(beforeEdge) && afterEdge.Player.Speed == 0 &&
            game.Activity is { Autopilot: false, Status: "boundary", Message: "Autopilot stopped at the street boundary — take manual control." },
            "An Autopilot crossing attempt must stop on the connected street instead of entering dry off-road terrain.");

        game.Command("gps-clear");
        game.Command("restart");
        var water = DriveToLimit(game, elevation, "water", new(0, 0, 1, false), "Water ahead — back up or recover to the start.");
        Check(elevation.Sample(water.Player.X - .25 * Math.Sin(water.Player.Yaw),
            water.Player.Z - .25 * Math.Cos(water.Player.Yaw)) is { IsWater: true },
            "The stopped reverse approach must be at the real water line.");

        game.Command("restart");
        var boundary = DriveToLimit(game, elevation, "boundary", new(0, 1, 0, false),
            "Seattle drive area boundary — back up or recover to the start.");
        Check(SeattleDriveArea.Bounds.MaxX - boundary.Player.X < .5, "The straight drive stops at the actual east area limit.");
        game.Command("recover");
        Check(Position(game.Snapshot) == SeattleDriveArea.Spawn && game.Snapshot.Tick == boundary.Tick && game.Snapshot.Recoveries == 1,
            "Manual recovery returns to the authored safe spawn without consuming extra ticks.");
        Console.WriteLine($"Real guards: shore ({water.Player.X:F3}, {water.Player.Z:F3}), east edge ({boundary.Player.X:F3}, {boundary.Player.Z:F3}).");
    }

    private static void LaunchLeft(SeattleDrivingSession game)
    {
        for (var i = 0; i < 118; i++) game.Advance(Step, new(0, 1, 0, false));
        for (var i = 0; i < 92; i++) game.Advance(Step, new(-.55, .45, 0, false));
    }

    private static GameSnapshot DriveToLimit(SeattleDrivingSession game, WorldFlightElevation elevation, string status,
        DriverInput input, string message)
    {
        var bounds = SeattleDriveArea.Bounds;
        var before = game.Snapshot;
        for (var i = 0; i < 8000; i++)
        {
            game.Advance(Step, input);
            var after = game.Snapshot;
            var p = Position(after);
            Check(after.Tick == before.Tick + 1 && p.X >= bounds.MinX && p.X <= bounds.MaxX &&
                p.Z >= bounds.MinZ && p.Z <= bounds.MaxZ &&
                elevation.Sample(p.X, p.Z) is { IsWater: false } && Distance(p, Position(before)) < .5,
                $"Manual {status} approach must stay dry and inside the area without position jumps or a terrain hold.");
            if (game.Activity.Status == status)
            {
                Check(game.Activity.Message == message && p == Position(before) && after.Player.Speed == 0,
                    $"The {status} guard must give distinct feedback and stop at the previous legal pose.");
                return after;
            }
            before = after;
        }
        throw new InvalidOperationException($"Manual drive never reached {status}: {Position(before)} — {game.Activity.Message}");
    }

    private static bool FacesStreet(double[] road, DrivePoint spawn, double yaw)
    {
        var a = new DrivePoint(road[0], road[1]);
        var b = new DrivePoint(road[2], road[3]);
        if (a != spawn && b != spawn) return false;
        var next = a == spawn ? b : a;
        var heading = Math.Atan2(next.X - spawn.X, next.Z - spawn.Z);
        return Math.Abs(Math.IEEERemainder(yaw - heading, Math.Tau)) < .02;
    }

    private static double FootprintClearance(DrivePoint p, DrivePoint[] ring)
    {
        var inside = false;
        var closest = double.PositiveInfinity;
        for (var i = 0; i < ring.Length; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Length];
            if ((a.Z > p.Z) != (b.Z > p.Z) && p.X < a.X + (b.X - a.X) * (p.Z - a.Z) / (b.Z - a.Z))
                inside = !inside;
            closest = Math.Min(closest, DistanceToSegment(p, a, b));
        }
        return inside ? -closest : closest;
    }

    private static double DistanceToSegment(DrivePoint p, DrivePoint a, DrivePoint b)
    {
        var dx = b.X - a.X;
        var dz = b.Z - a.Z;
        var lengthSquared = dx * dx + dz * dz;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Z - a.Z) * dz) / lengthSquared, 0, 1);
        return double.Hypot(p.X - a.X - dx * t, p.Z - a.Z - dz * t);
    }

    private static DrivePoint Position(GameSnapshot snapshot) => new(snapshot.Player.X, snapshot.Player.Z);
    private static double Distance(DrivePoint a, DrivePoint b) => double.Hypot(a.X - b.X, a.Z - b.Z);

    private static void Check(bool success, string failure)
    {
        if (!success) throw new InvalidOperationException(failure);
    }
}
