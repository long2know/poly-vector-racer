using StuntRacer.CompatibilityChecks;
using StuntRacer.Core;
using StuntRacer.Core.Compatibility.Physics;
using StuntRacer.Core.SourceRuntime;

namespace StuntRacer.SourceRuntimeChecks;

internal static class SessionChecks
{
    private static readonly DriverInput Idle = new(0, 0, 0, false);

    public static IEnumerable<NativeCheck> All(SourceAmigaPackage package)
    {
        yield return new("LittleRamp runtime / exact first falling tick and active source integration", () => FirstTick(package));
        yield return new("LittleRamp runtime / three physical contacts, landing and rear-only visual duplication", () => Landing(package));
        yield return new("LittleRamp runtime / real throttle, brake and right-positive world steering", () => DriveAndSteer(package));
        yield return new("LittleRamp runtime / actual boost subcounter boundaries and recovery preserves fuel", () => Boost(package));
        yield return new("LittleRamp runtime / fixed ticks agree at 30/60/120/144Hz and jitter", () => Cadences(package));
        yield return new("LittleRamp runtime / interpolation, pause and restart preserve authoritative state", () => ObserveAndCommands(package));
        yield return new("LittleRamp runtime / real off-road fall and automatic recovery", () => FallAndRecovery(package));
        yield return new("LittleRamp runtime / public-input seam traversal and natural ramp flight", () => DriveSeamsAndJump(package));
    }

    private static SourceAmigaSimulation NewRace(SourceAmigaPackage package)
    {
        var session = SourceAmigaSimulation.Create(package);
        session.Command("start");
        return session;
    }

    private static void FirstTick(SourceAmigaPackage package)
    {
        var session = NewRace(package);
        Check.Require(ReferenceEquals(package.Track, session.Track), "Classic session must use the exact package track instance");
        var initial = session.RawState;
        Check.Equal(0L, initial.Tick, "initial source tick");
        Check.Equal(checked((int)package.Start.InitialRawY), initial.Y, "actual native spawn height");
        session.Step(Idle);
        var first = session.RawState;
        Check.Equal(1L, first.Tick, "one exact source tick");
        Check.Equal(-317, first.WorldAccelerationY, "independent source zero-angle gravity case");
        Check.Equal(-295, first.WorldVelocityY, "independent source ASHR8(-317*238)");
        Check.Equal(initial.Y - 35105, first.Y, "independent ACTIVE vertical integration case");
        Check.Equal(initial.X, first.X, "initial idle source x");
        Check.Equal(initial.Z, first.Z, "initial idle source z");
        Check.Require(first.Airborne && !first.TouchingRoad && first.AverageResponse == 0,
            "initial source drop acquired fabricated contact");
        Check.Equal(-768, first.FrontLeft.Penetration, "airborne front-left penetration clamp");
        Check.Equal(-768, first.FrontRight.Penetration, "airborne front-right penetration clamp");
        Check.Equal(-768, first.Rear.Penetration, "airborne rear-central penetration clamp");
        Check.Near(0.12, session.Snapshot.Time, "chosen reference time is runnable, not authored120Hz");
        Check.Near((initial.Y - 35105) / 4096.0 * package.Units.DisplayMetresPerPcUnit +
                   64 * package.Units.DisplayMetresPerPcUnit,
            session.Snapshot.Player.Y, "64PC mesh-origin lift is presentation-only; native falling state remains exact");
    }

    private static void Landing(SourceAmigaPackage package)
    {
        var session = NewRace(package);
        var airTicks = 0;
        var maximumResponse = 0;
        for (var tick = 0; tick < 120; tick++)
        {
            var before = session.RawState;
            session.Step(Idle);
            var state = session.RawState;
            if (state.Airborne) airTicks++;
            maximumResponse = Math.Max(maximumResponse, Math.Max(state.FrontLeft.Response,
                Math.Max(state.FrontRight.Response, state.Rear.Response)));
            Check.Equal(before.FrontLeft.Penetration, state.FrontLeft.PreviousPenetration, "front-left derivative history");
            Check.Equal(before.FrontRight.Penetration, state.FrontRight.PreviousPenetration, "front-right derivative history");
            Check.Equal(before.Rear.Penetration, state.Rear.PreviousPenetration, "rear-central derivative history");
            var visual = session.Snapshot.Player.Suspension;
            Check.Equal(4, visual.Length, "presentation wheel count");
            Check.Near(visual[2], visual[3], "only rear-central contact is duplicated for visual wheels");
            Check.Require(session.Snapshot.PaceCar is null, "Classic unexpectedly gained a physical rival");
            if (state.TouchingRoad && airTicks > 0)
            {
                Check.Require(maximumResponse > 0 && state.Recoveries == 0, "source landing has no contact response or teleported");
                Console.WriteLine($"  native drop landed tick={state.Tick}, peak response={maximumResponse}, three contacts.");
                return;
            }
        }
        throw new InvalidOperationException($"Actual LittleRamp drop never landed: {session.RawState}");
    }

    private static void DriveAndSteer(SourceAmigaPackage package)
    {
        var throttle = Settled(package);
        var brake = Settled(package);
        for (var tick = 0; tick < 12; tick++)
        {
            throttle.Step(new(0, 1, 0, false));
            brake.Step(new(0, 0, 1, false));
        }
        Check.Require(throttle.RawState.BodyVelocityZ > 0, $"source throttle failed: {throttle.RawState}");
        Check.Require(brake.RawState.BodyVelocityZ < throttle.RawState.BodyVelocityZ, "actual brake did not oppose throttle");
        Check.Require(throttle.RawState.X != package.Start.XQ14 || throttle.RawState.Z != package.Start.ZQ14,
            "actual throttle did not integrate native position");
        var left = Settled(package);
        var right = Settled(package);
        var straight = Settled(package);
        for (var tick = 0; tick < 8; tick++)
        {
            left.Step(new(0, 1, 0, false));
            right.Step(new(0, 1, 0, false));
            straight.Step(new(0, 1, 0, false));
        }
        Check.Equal(left.RawState, right.RawState, "paired steering runs must start from identical raw state");
        var yaw = right.Snapshot.Player.Yaw;
        for (var tick = 0; tick < 4; tick++)
        {
            left.Step(new(-1, 1, 0, false));
            right.Step(new(1, 1, 0, false));
            straight.Step(new(0, 1, 0, false));
        }
        var delta = AmigaAngle.ShortestDelta(left.RawState.Yaw, right.RawState.Yaw);
        var dx = right.RawState.X - (long)left.RawState.X;
        var dz = right.RawState.Z - (long)left.RawState.Z;
        var rightward = dx * Math.Cos(yaw) - dz * Math.Sin(yaw);
        var leftFromStraight = AmigaAngle.ShortestDelta(straight.RawState.Yaw, left.RawState.Yaw);
        var rightFromStraight = AmigaAngle.ShortestDelta(straight.RawState.Yaw, right.RawState.Yaw);
        Check.Require(delta > 0 && rightward > 0 && leftFromStraight < 0 && rightFromStraight > 0,
            $"right-positive steering failed: deltaYaw={delta}, rightwardRaw={rightward:R}, " +
            $"leftDelta={leftFromStraight}, rightDelta={rightFromStraight}, " +
            $"left={left.RawState.Steering}/{left.RawState.TouchingRoad}, right={right.RawState.Steering}/{right.RawState.TouchingRoad}");
    }

    private static void Boost(SourceAmigaPackage package)
    {
        var session = NewRace(package);
        var reserve = session.RawState.BoostReserve;
        Check.Require(reserve >= 2, "LittleRamp functional fuel test requires its real nonempty standard reserve");
        for (var activation = 1; activation <= 18; activation++)
        {
            session.Step(new(0, 1, 0, true));
            if (activation is 1 or 2 or 17 or 18)
            {
                var expected = ReferenceCases.Boost.Single(item => item.Activation == activation);
                Check.Equal(reserve - 2 + expected.Reserve, session.RawState.BoostReserve,
                    $"actual source boost reserve activation {activation}");
                Check.Equal(expected.Subunit, session.RawState.BoostSubunit, $"actual source subunit activation {activation}");
                Check.Require(session.RawState.Boosting && session.RawState.EnginePower == 480,
                    $"actual source boost boundary lost doubled engine activation {activation}");
            }
        }
        var before = session.RawState;
        session.Command("recover");
        Check.Equal(before.BoostReserve, session.RawState.BoostReserve, "source recovery must not refill reserve");
        Check.Equal(before.BoostSubunit, session.RawState.BoostSubunit, "source recovery must not reset fuel subunit");
        Check.Equal(before.Tick, session.RawState.Tick, "manual recovery is not a fabricated source tick");
        Check.Near(before.Tick * 0.12, session.Snapshot.Time, "source recovery must not inherit authored five-second penalty");
    }

    private static void Cadences(SourceAmigaPackage package)
    {
        var reference = NewRace(package);
        foreach (var segment in RenderSchedules.Inputs)
            for (var tick = 0; tick < segment.Ticks; tick++)
                reference.Step(segment.Input);
        foreach (var (name, frames) in RenderSchedules.All)
        {
            var session = NewRace(package);
            var frame = 0;
            foreach (var segment in RenderSchedules.Inputs)
            {
                var remaining = segment.Ticks * session.SimulationStepSeconds;
                while (remaining > 1e-10)
                {
                    var delta = Math.Min(remaining, frames[frame++ % frames.Length]);
                    session.Advance(delta, segment.Input);
                    remaining -= delta;
                }
            }
            Check.Equal(reference.RawState, session.RawState, $"{name} changed authoritative integer state");
        }
        Check.Equal(120L, reference.RawState.Tick, "fixed trace executes all requested source ticks");
        var catchup = NewRace(package);
        catchup.Advance(1.2, Idle);
        Check.Equal(10L, catchup.RawState.Tick, "source clock must not inherit authored 250ms discard policy");
    }

    private static void ObserveAndCommands(SourceAmigaPackage package)
    {
        var session = NewRace(package);
        var initial = session.RawState;
        session.Advance(0.06, new(1, 1, 0, true));
        Check.Equal(initial, session.RawState, "normalized source first interval executed early");
        for (var frame = 0; frame < 20; frame++)
        {
            session.Advance(0, new(1, 1, 0, true));
            _ = session.Snapshot;
        }
        Check.Equal(initial, session.RawState, "render interpolation/observation mutated source state");
        session.Advance(0.06, new(1, 1, 0, true));
        Check.Equal(1L, session.RawState.Tick, "first full source interval");
        session.Command("pause");
        var paused = session.RawState;
        session.Advance(60, new(-1, 1, 1, true));
        session.Step(new(1, 1, 0, true));
        session.Command("recover");
        Check.Equal(paused, session.RawState, "paused Classic state changed");
        session.Command("resume");
        session.Step(Idle);
        Check.Equal(paused.Tick + 1, session.RawState.Tick, "resumed exact source step");
        session.Command("restart");
        Check.Equal(initial, session.RawState, "restart must reset complete integer state/resources");
        session.Command("garage");
        Check.Equal("ready", session.Snapshot.Phase, "Classic did not return to the garage menu");
        var garage = session.RawState;
        session.Advance(1, new(1, 1, 0, true));
        Check.Equal(garage, session.RawState, "Classic kept simulating in the garage");
        foreach (var delta in new[] { -1, double.NaN, double.PositiveInfinity, 3600.01 })
            Check.Throws<ArgumentOutOfRangeException>(() => session.Advance(delta, Idle), "invalid elapsed source interval");
        var modern = new GameSimulation();
        modern.Command("start");
        modern.Advance(0.12, Idle);
        Check.Equal(14L, modern.Snapshot.Tick, "loading/using a source package must not replace the authored120Hz default");
    }

    private static void FallAndRecovery(SourceAmigaPackage package)
    {
        var session = Settled(package);
        var minimumHeight = double.PositiveInfinity;
        var maximumOffRoad = 0;
        var offMap = false;
        var airborne = false;
        for (var tick = 0; tick < 320; tick++)
        {
            session.Step(new(1, 1, 0, false));
            var state = session.RawState;
            maximumOffRoad = Math.Max(maximumOffRoad, state.OffRoadTicks);
            offMap |= state.OffMap;
            airborne |= state.Airborne;
            var playerY = SourceSnapshotAdapter.ToDisplayY(state.Y, package.Units.DisplayMetresPerPcUnit);
            minimumHeight = Math.Min(minimumHeight, playerY);
            if (state.Recoveries > 0)
            {
                Check.Require(offMap && airborne && maximumOffRoad > 0 && minimumHeight < 5,
                    $"no real off-road fall: offMap={offMap}, airborne={airborne}, offTicks={maximumOffRoad}, minY={minimumHeight}");
                Check.Near(state.Tick * 0.12, session.Snapshot.Time, "automatic source recovery timer remains tick-based");
                Console.WriteLine($"  real source fall/recover tick={state.Tick}, offTicksMax={maximumOffRoad}, minY={minimumHeight:F3}m.");
                return;
            }
        }
        throw new InvalidOperationException($"Public-input fall did not recover: {session.RawState}");
    }

    private static void DriveSeamsAndJump(SourceAmigaPackage package)
    {
        var session = Settled(package);
        var sections = new HashSet<int> { session.RawState.SectionIndex };
        var takeoff = false;
        var landing = false;
        var maximumClearance = 0.0;
        var wasGrounded = true;
        for (var tick = 0; tick < 1600 && !landing; tick++)
        {
            session.Step(Drive(session));
            var state = session.RawState;
            sections.Add(state.SectionIndex);
            var snapshot = session.Snapshot.Player;
            NearAngle(state.Yaw * Math.Tau / 65536, snapshot.Yaw, "source yaw projects toward canonical +X");
            NearAngle(unchecked((short)state.Pitch) * Math.Tau / 65536, snapshot.Pitch, "source positive pitch raises the nose");
            NearAngle(-unchecked((short)state.Roll) * Math.Tau / 65536, snapshot.Roll, "source roll sign maps to right-edge-up display");
            Check.Near(snapshot.Suspension[2], snapshot.Suspension[3], "ramp retains one physical rear contact in visual adapter");
            var road = session.Track.Sample(snapshot.X, snapshot.Z);
            var rawY = SourceSnapshotAdapter.ToDisplayY(state.Y, package.Units.DisplayMetresPerPcUnit);
            if (state.Recoveries == 0 && road.Road)
            {
                maximumClearance = Math.Max(maximumClearance, rawY - road.Y);
                if (!state.TouchingRoad && state.WorldVelocityY > 0 && rawY - road.Y > 0.35)
                    takeoff = true;
                if (takeoff && !wasGrounded && state.TouchingRoad)
                    landing = true;
            }
            wasGrounded = state.TouchingRoad;
            if (state.Recoveries > 0)
                throw new InvalidOperationException($"Public-input source driver fell before ramp landing: " +
                    $"tick={state.Tick}, sections={sections.Count}, takeoff={takeoff}, clearance={maximumClearance:F3}m.");
        }
        Check.Require(sections.Count >= 3 && takeoff && landing,
            $"real source seam/jump outcome missing: sections={sections.Count}, takeoff={takeoff}, landing={landing}, clearance={maximumClearance:F3}m");
        Console.WriteLine($"  public-input source ramp: tick={session.RawState.Tick}, sections={sections.Count}, " +
                          $"takeoff/landing=true, clearance={maximumClearance:F3}m.");
    }

    private static SourceAmigaSimulation Settled(SourceAmigaPackage package)
    {
        var session = NewRace(package);
        for (var tick = 0; tick < 120; tick++)
        {
            session.Step(Idle);
            if (session.RawState.TouchingRoad)
            {
                for (var extra = 0; extra < 6; extra++) session.Step(Idle);
                return session;
            }
        }
        throw new InvalidOperationException("Actual source spawn did not settle through public inputs.");
    }

    private static DriverInput Drive(SourceAmigaSimulation session)
    {
        var snapshot = session.Snapshot.Player;
        var road = session.Track.Sample(snapshot.X, snapshot.Z);
        var look = 8 + Math.Min(snapshot.Speed, 40) * 0.4;
        var target = session.Track.AtDistance(road.S + look);
        var error = Math.IEEERemainder(Math.Atan2(target.X - snapshot.X, target.Z - snapshot.Z) - snapshot.Yaw, Math.Tau);
        var bend = Math.Abs(Math.IEEERemainder(session.Track.AtDistance(road.S + 35).Heading - road.Heading, Math.Tau));
        var desired = bend > 0.2 ? 10 : 20;
        var steer = Math.Abs(error) < 0.025 ? 0 : Math.Sign(error);
        return new(steer, snapshot.Speed < desired ? 1 : 0, snapshot.Speed > desired + 1 ? 1 : 0, false);
    }

    private static void NearAngle(double expected, double actual, string context) =>
        Check.Near(0, Math.IEEERemainder(actual - expected, Math.Tau), context);
}
