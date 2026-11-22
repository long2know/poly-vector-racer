using StuntRacer.Core;
using StuntRacer.Core.GeographicDrive;

internal static class ManualSteeringChecks
{
    private const double Step = 1.0 / 120;

    public static void Run(SeattleDrivingSession game)
    {
        CheckTurn(game, "Keyboard right", 118, 1, 80, minTurnIn: .04, maxRadius: 8);
        CheckTurn(game, "Keyboard left", 118, -1, 80, minTurnIn: .04, maxRadius: 8);
        CheckTurn(game, "Analog left", 172, -.55, 74, minTurnIn: .03, maxRadius: 17.5);

        game.Command("restart");
        var initialYaw = game.Snapshot.Player.Yaw;
        var still = Drive(game, 80, new(1, 0, 0, false));
        Check(still.Distance < .1 && Math.Abs(Angle(still.Yaw - initialYaw)) < .01,
            "Steering without throttle must not spin the car in place on the slightly sloped start.");

        game.Command("restart");
        var reverse = Drive(game, 135, new(0, 0, 1, false));
        var reverseTurn = Drive(game, 48, new(1, 0, 0, false));
        Check(reverse.Speed < -3 && reverseTurn.Speed < -3 &&
            Angle(reverseTurn.Yaw - reverse.Yaw) < -.1 && Angle(reverseTurn.PathHeading - reverse.PathHeading) < -.07,
            "Backing up with right steer must turn the chassis and the traveled heading left.");

        game.Command("restart");
        var fast = Drive(game, 205, new(0, 1, 0, true));
        var fastTurn = Drive(game, 24, new(.7, 0, 0, false));
        var fastYaw = Angle(fastTurn.Yaw - fast.Yaw);
        var fastHeading = Angle(fastTurn.PathHeading - fast.PathHeading);
        Check(fastTurn.Speed > 25 && fastYaw is > 0 and < .035 && fastHeading is > 0 and < .015,
            $"A turn at {fast.Speed:F1} m/s must not snap the yaw ({fastYaw:F4} rad) or path ({fastHeading:F4} rad).");
    }

    private static void CheckTurn(SeattleDrivingSession game, string name, int launchTicks, double steer, int holdTicks,
        double minTurnIn, double maxRadius)
    {
        game.Command("restart");
        var start = Drive(game, launchTicks, new(0, 1, 0, false));
        var input = new DriverInput(steer, 0, 0, false);
        var tap = Drive(game, 24, input);
        var arcStart = Drive(game, holdTicks - 48, input);
        var held = Drive(game, 24, input);
        var recentered = Drive(game, 36, default);
        var coast = Drive(game, 24, default);

        var sign = Math.Sign(steer);
        var turnIn = sign * Angle(tap.PathHeading - start.PathHeading);
        var arc = sign * Angle(held.PathHeading - arcStart.PathHeading);
        var radius = (held.Distance - arcStart.Distance) / arc;
        var slip = Math.Abs(Angle(held.Yaw - held.PathHeading));
        var drift = Math.Abs(Angle(coast.PathHeading - recentered.PathHeading));
        Check(start.Speed is > 7 and < 12 && coast.Speed > 5, $"{name}: must keep rolling at city speeds.");
        Check(turnIn > minTurnIn, $"{name}: 0.2-second steer must turn the traveled heading promptly ({turnIn:F4} rad).");
        Check(arc > 0 && radius < maxRadius && slip < .08,
            $"{name}: turn in a tight arc ({radius:F2} m) without excessive slip ({slip:F4} rad).");
        Check(drift < .025 && Math.Abs(Angle(coast.Yaw - coast.PathHeading)) < .02,
            $"{name}: the path must straighten after release ({drift:F4} rad still turning from 0.3 to 0.5 seconds).");
        Console.WriteLine($"{name} at {start.Speed:F2} m/s: 0.2s path/yaw {turnIn:F4}/" +
            $"{sign * Angle(tap.Yaw - start.Yaw):F4} rad, radius {radius:F2} m, slip {slip:F4} rad, release drift {drift:F4} rad.");
    }

    private static Sample Drive(SeattleDrivingSession game, int ticks, DriverInput input)
    {
        var startTick = game.Snapshot.Tick;
        for (var i = 1; i < ticks; i++) game.Advance(Step, input);
        var before = game.Snapshot.Player;
        game.Advance(Step, input);
        var after = game.Snapshot;
        var player = after.Player;
        Check(after.Tick == startTick + ticks && player.Grounded && game.Activity.Status == "manual",
            $"Steering fixture must stay on supported ground and advance every tick ({after.Tick}, {game.Activity.Status}).");
        // The last fixed-step displacement gives the traveled heading, independently of body yaw.
        return new(player.Yaw, player.Speed, game.Activity.DrivenDistance, Math.Atan2(player.X - before.X, player.Z - before.Z));
    }

    private static double Angle(double angle) => Math.IEEERemainder(angle, Math.Tau);

    private static void Check(bool success, string failure)
    {
        if (!success) throw new InvalidOperationException(failure);
    }

    private readonly record struct Sample(double Yaw, double Speed, double Distance, double PathHeading);
}
