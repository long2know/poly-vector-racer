using StuntRacer.Flight;

namespace StuntRacer.FlightChecks;

internal static class TrainingChecks
{
    private static readonly FlightInput Cruise = new(0, 0, 0, .35, false, false);

    public static void ScanBothProps()
    {
        var range = new FlightTagRange();
        var initial = range.Snapshot.Training!;
        var drone = initial.Props.Single(prop => prop.Kind == "drone");
        var beacon = initial.Props.Single(prop => prop.Kind == "beacon");
        Check.That(range.World.Geography is null && range.World.SourceWorld is null &&
            !ReferenceEquals(range.World, FlightWorldDefinition.Default), "Training has its own authored world.");
        var ground = range.World.Sample(beacon.Position.X, beacon.Position.Z);
        Check.That(!ground.IsWater, "The beacon is on authored ground.");
        Check.Near(beacon.Position.Y, ground.Height, .001, "The ground beacon does not float");
        Check.That(new FlightSimulation().Snapshot.Training is null, "Normal Bay flight has no training props.");

        range.Command("scan");
        Check.That(range.Snapshot.Training is { Score: 0, Remaining: 2, CooldownSeconds: 0 }, "No scanner before launch.");
        range.Command("start");
        var launch = range.Snapshot;
        Check.That(launch.Phase == "flying" && !launch.Aircraft.OnGround && !launch.Aircraft.GearDown &&
            launch.Aircraft.Speed > 70, "Range launch is already airborne and moving.");
        range.Command("scan");
        Check.That(range.Snapshot.Training is { Score: 0, CooldownSeconds: > 0, InRangeId: null },
            "An out-of-range scan uses cooldown but must not award completion.");

        var nearDrone = FlyUntilInRange(range, drone.Id);
        var movedDrone = nearDrone.Training!.Props.Single(prop => prop.Id == drone.Id);
        Check.That(movedDrone.Position != drone.Position, "The drone must move on native simulation time.");
        Check.That(nearDrone.Training.Props.Single(prop => prop.Id == beacon.Id).Position == beacon.Position,
            "The ground beacon must stay at its authored location.");
        range.Command("scan");
        var tagged = range.Snapshot.Training!;
        Check.That(tagged is { Score: 1, Remaining: 1, CooldownSeconds: > 0 } &&
            tagged.Props.Single(prop => prop.Id == drone.Id).Tagged && !tagged.Props.Single(prop => prop.Id == beacon.Id).Tagged,
            "First scan tags the moving drone only.");
        range.Command("scan");
        Check.That(range.Snapshot.Training!.Score == 1, "A repeat scan cannot score again on the same drone.");

        FlyUntilInRange(range, beacon.Id);
        range.Command("scan");
        var complete = range.Snapshot;
        Check.That(complete.Phase == "finished" && !complete.Aircraft.Crashed &&
            complete.Training is { Score: 2, Remaining: 0 } && complete.Training.Props.All(prop => prop.Tagged),
            "A distinct scan tags the actual beacon and clears the range without a crash.");
        range.Command("resume");
        Check.That(range.Advance(.25, Cruise).Time == complete.Time, "The cleared range stops the run clock.");
    }

    public static void PauseAndReplay()
    {
        var range = new FlightTagRange();
        range.Command("start");
        var near = FlyUntilInRange(range, "cloud-hopper");
        range.Command("pause");
        var paused = range.Snapshot;
        range.Command("scan");
        var stillPaused = range.Advance(.25, Cruise);
        Check.That(stillPaused is { Phase: "paused", Training.Score: 0, Training.CooldownSeconds: 0 } &&
            stillPaused.Time == near.Time && stillPaused.Aircraft == paused.Aircraft &&
            stillPaused.Training.Props.SequenceEqual(paused.Training!.Props),
            "Pause freezes poses and rejects scans even beside a practice prop.");
        range.Command("resume");
        range.Command("scan");
        Check.That(range.Snapshot.Training!.Score == 1, "Resuming permits a legitimate scan again.");
        var activeCooldown = range.Snapshot.Training.CooldownSeconds;
        range.Command("pause");
        Check.Near(range.Advance(.25, Cruise).Training!.CooldownSeconds, activeCooldown, 0,
            "Scanner cooldown freezes while paused");
        range.Command("restart");
        var restarted = range.Snapshot;
        Check.That(restarted is { Phase: "flying", Time: 0, Training.Score: 0, Training.CooldownSeconds: 0 } &&
            !restarted.Aircraft.OnGround && restarted.Training.Props.All(prop => !prop.Tagged),
            "Restart relaunches airborne with clean practice props and score.");
        range.Command("garage");
        Check.That(range.Snapshot is { Phase: "ready", Training.Score: 0, Training.Remaining: 2 },
            "Garage returns to a clean range briefing.");
    }

    private static FlightSnapshot FlyUntilInRange(FlightTagRange range, string id)
    {
        for (var frame = 0; frame < 600; frame++)
        {
            var snapshot = range.Advance(1.0 / 60, Cruise);
            if (snapshot.Training is { CooldownSeconds: 0 } training && training.InRangeId == id)
                return snapshot;
        }
        throw new InvalidOperationException($"Straight flight did not bring the aircraft within reach of {id}.");
    }
}
