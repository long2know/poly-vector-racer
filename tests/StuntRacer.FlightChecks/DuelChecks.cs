using System.Text.Json;
using StuntRacer.Flight;

namespace StuntRacer.FlightChecks;

internal static class DuelChecks
{
    private static readonly FlightInput Cruise = new(0, 0, 0, .35, false, false);
    private const double Frame = 1.0 / 60;

    public static void ProximityAndPause()
    {
        var duel = new FlightTagDuel();
        var ready = duel.Snapshot;
        duel.Command("scan");
        Check.That(duel.Snapshot == ready && ready is { Phase: "ready", Duel.Score: 0, Duel.Success: null },
            "Garage briefing is frozen and rejects scans.");
        duel.Command("start");
        var start = duel.Snapshot;
        Check.That(start is { Phase: "flying", Aircraft.OnGround: false, Aircraft.GearDown: false } &&
            !start.Duel!.InRange, "The airborne launch starts beyond scanner reach.");
        duel.Command("scan");
        Check.That(duel.Snapshot.Duel is { Score: 0, CooldownSeconds: > 0 },
            "An early scan cannot score and still recharges.");
        var near = FlyUntil(duel, s => s.Duel is { InRange: true, CooldownSeconds: 0 });
        var rival = near.Duel!.Rival;
        Check.That(rival.Position != start.Duel!.Rival.Position && near.Aircraft.Z - start.Aircraft.Z > 40,
            "The unaided first pass crosses space on both sides.");
        Check.Near(rival.Distance, Math.Sqrt(Math.Pow(rival.Position.X - near.Aircraft.X, 2) +
            Math.Pow(rival.Position.Y - near.Aircraft.Y, 2) + Math.Pow(rival.Position.Z - near.Aircraft.Z, 2)),
            1e-8, "Scanner range uses actual three-dimensional separation");
        duel.Command("scan");
        var first = duel.Snapshot;
        Check.That(first.Duel is { Score: 1, NextPass: false, CooldownSeconds: > 0 } &&
            first.Aircraft == near.Aircraft && first.Duel.Rival == rival,
            "A real proximity tag scores and leaves both aircraft unchanged.");
        duel.Command("scan");
        Check.That(duel.Snapshot == first, "A scan during cooldown does nothing.");

        duel.Command("pause");
        var paused = duel.Snapshot;
        duel.Command("scan");
        Check.That(paused.Phase == "paused" && duel.Advance(.25, Cruise with { Roll = 1 }) == paused &&
            paused.Duel!.TimeRemaining == first.Duel!.TimeRemaining, "Pause freezes timer, scanner and both poses.");
        duel.Command("resume");
        var samePass = FlyUntil(duel, s => s.Duel!.CooldownSeconds == 0);
        Check.That(samePass.Duel is { InRange: true, NextPass: false }, "The glider is still on the same pass after recharge.");
        duel.Command("scan");
        Check.That(duel.Snapshot.Duel!.Score == 1, "Cooling down in-place cannot tag a pass twice.");

        var nextPass = FlyUntil(duel, s => s.Duel is { CooldownSeconds: 0, NextPass: true });
        Check.That(nextPass.Duel is { InRange: false } && nextPass.Duel.Rival.Position.X - nextPass.Aircraft.X > 150 &&
            nextPass.Duel.Rival.Distance > nextPass.Duel.ScanRange + 15,
            "Staying straight after the first pass does not follow the glider's bend.");
        duel.Command("scan");
        Check.That(duel.Snapshot.Duel is { Score: 1, Success: null }, "A later pass requires closing the gap again.");
    }

    public static void DeadlineAndReplay()
    {
        var duel = new FlightTagDuel();
        var ready = duel.Snapshot;
        duel.Command("start");
        var start = duel.Snapshot;
        for (var frame = 0; frame < 160 && duel.Snapshot.Phase == "flying"; frame++)
            duel.Advance(.23, Cruise);
        var ended = duel.Snapshot;
        Check.That(ended is { Phase: "finished", Duel: { Success: false, TimeRemaining: 0, Score: 0 }, Aircraft.Crashed: false },
            "Flying without scoring loses to the fixed round clock, not a crash.");
        Check.Near(ended.Time, start.Duel!.TimeRemaining, 1e-8, "Irregular frames stop flight at the exact deadline");
        duel.Command("resume");
        duel.Command("scan");
        Check.That(duel.Advance(.25, Cruise) == ended, "A completed attempt cannot resume or scan.");
        duel.Command("restart");
        Check.That(duel.Snapshot == start, "Restart restores the airborne pose, clock, score and glider path.");
        for (var frame = 0; frame < 160 && duel.Snapshot.Phase == "flying"; frame++)
            duel.Advance(.23, Cruise with { Pitch = -.65 });
        Check.That(duel.Snapshot is { Duel: { Success: false, TimeRemaining: > 0 }, Aircraft.Crashed: false },
            "The practice floor ends a low pass before any contact.");
        duel.Command("garage");
        Check.That(duel.Snapshot == ready, "Garage restores the original briefing.");
    }

    public static void ThreePassClear()
    {
        ThreePassClear(Cruise, 1.75, 6.65);
        ThreePassClear(Cruise with { Assisted = true }, 3.8, 6.5);
    }

    private static void ThreePassClear(FlightInput cruise, double bankUntil, double oppositeUntil)
    {
        var duel = new FlightTagDuel();
        duel.Command("start");
        var lastTag = duel.Snapshot;
        for (var i = 0; i < 32 * 60 && duel.Snapshot.Phase == "flying"; i++)
        {
            var seconds = i / 60.0;
            var input = seconds switch
            {
                >= 1.3 when seconds < bankUntil => cruise with { Roll = 1 },
                >= 6 when seconds < oppositeUntil => cruise with { Roll = -1 },
                _ => cruise
            };
            var snapshot = duel.Advance(Frame, input);
            if (snapshot.Duel is not { InRange: true, NextPass: true, CooldownSeconds: 0, Success: null }) continue;
            duel.Command("scan");
            var tagged = duel.Snapshot;
            Check.That(tagged.Duel!.Score == lastTag.Duel!.Score + 1 && tagged.Duel.Rival == snapshot.Duel.Rival &&
                tagged.Aircraft.Z - lastTag.Aircraft.Z > 35, "Each pass scores at a different genuine flight position.");
            lastTag = tagged;
        }
        var clear = duel.Snapshot;
        Check.That(clear is { Phase: "finished", Duel: { Score: 3, TimeRemaining: > 0, Success: true }, Aircraft.Crashed: false },
            $"A steered flight can follow the moving glider and win before the clock expires (assisted: {cruise.Assisted}): " +
            $"score={clear.Duel?.Score}; {FlightScenarios.Describe(clear)}");
        duel.Command("resume");
        Check.That(duel.Advance(.25, cruise) == clear, "Winning freezes flight just like a timeout.");
    }

    public static void IsolatedActivity()
    {
        var duel = new FlightTagDuel();
        var training = new FlightTagRange();
        Check.That(duel.World is { Geography: null, SourceWorld: null } && duel.World != training.World &&
            duel.Snapshot is { Duel: not null, Training: null } &&
            training.Snapshot is { Duel: null, Training.Props.Count: 2 },
            "The separate authored duel does not substitute for Coppercloud or add geographic assets.");
        var plain = new FlightSimulation();
        var coast = new WorldFlightElevation();
        var tile = new byte[WorldFlightElevation.TileLength];
        "WFT1"u8.CopyTo(tile);
        coast.AddTile(tile);
        var world = FlightWorldDefinition.CreateGeographic("Synthetic World Flight",
            new("start", "Start", 1000, 1000, 500, 0, 1000, 2000), null, coast);
        var geographic = new FlightSimulation(world);
        foreach (var session in new IFlightSession[] { plain, geographic, training })
            Check.That(!JsonSerializer.SerializeToElement(session.Snapshot).TryGetProperty("Duel", out _),
                $"{session.World.Name} has no serialized duel state.");
    }

    private static FlightSnapshot FlyUntil(FlightTagDuel duel, Predicate<FlightSnapshot> ready)
    {
        for (var frame = 0; frame < 600; frame++)
        {
            var snapshot = duel.Advance(Frame, Cruise);
            if (ready(snapshot)) return snapshot;
            if (snapshot.Phase != "flying") break;
        }
        throw new InvalidOperationException("Steady flight did not reach the expected scanner state.");
    }
}
