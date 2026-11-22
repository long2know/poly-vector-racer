using StuntRacer.Engine;

namespace StuntRacer.Flight;

public sealed partial class FlightSimulation
{
    // Both base and streamed detail are bounded at decode; streaming cannot raise this floor.
    private const double AtlasCruiseFloor = WorldAtlasGrid.MaximumElevation + 500;
    private const double AtlasCaptureDistance = 5_000;
    private const double AtlasApproachDistance = 25_000;
    private const double AtlasPolarBuffer = 40_000;
    private const double AtlasLowFuel = 0.025;
    // Cruise is an explicit game endurance setting, not a change to the authored forces.
    private const double AtlasDryFuelScale = 0.01;
    private AtlasPlan? _atlasPlan;
    private int _atlasRequestedRate = 1;
    private double _atlasLapOffset;
    private string _atlasOffReason = "Autopilot is off.";

    public void ConfigureAtlasAutopilot(AtlasAutopilotCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (World.AtlasElevation is null)
            throw new InvalidOperationException("Autopilot is only available in World Flight Atlas.");

        if (command.Mode is not null && command.Mode is not ("route" or "hold"))
            throw new ArgumentException("Expected Atlas mode route or hold.", nameof(command.Mode));
        ValidateAtlasRange(command.HeadingDeg, 0, 360, nameof(command.HeadingDeg));
        ValidateAtlasRange(command.AltitudeM, 1500, 12_000, nameof(command.AltitudeM));
        ValidateAtlasRange(command.SpeedMps, 180, 300, nameof(command.SpeedMps));
        if (command.Rate is not null and not (1 or 2 or 5 or 10))
            throw new ArgumentOutOfRangeException(nameof(command.Rate), "Expected Atlas rate 1, 2, 5 or 10.");

        AtlasAutopilotTarget? target = null;
        if (command.Destination is { } destination)
        {
            var (x, z) = WorldAtlasGrid.Project(destination.LonDeg, destination.LatDeg);
            // Choose the nearest unwrapped copy once. True antipodes tie to the west,
            // allowing for rounding when subtracting the two projected coordinates.
            const double projectionTolerance = 1e-8;
            var delta = x - _position.X;
            var offset = delta >= WorldAtlasGrid.HalfWidth - projectionTolerance ? -WorldAtlasGrid.Width
                : delta < -WorldAtlasGrid.HalfWidth - projectionTolerance ? WorldAtlasGrid.Width : 0;
            target = new(x + (_atlasLapOffset + offset), z);
        }

        switch (command.Action)
        {
            case "engage":
                if (command.Mode is null || command.Mode == "route" && target is null)
                    throw new ArgumentException("Atlas engage needs a mode and route mode needs a destination.", nameof(command));
                if (_phase == Phase.Crashed)
                    throw new InvalidOperationException("Recover before engaging Atlas autopilot.");
                DropCompressedAtlasTime();
                var route = command.Mode == "route" ? target : null;
                _atlasPlan = new(
                    route,
                    command.HeadingDeg is { } degrees ? degrees % 360 * Math.PI / 180 : GroundCourse(),
                    command.AltitudeM ?? AtlasCruiseFloor,
                    command.SpeedMps ?? 250)
                {
                    PolarEntry = route is not null ? AtlasPolarEntry(route) : null
                };
                if (command.Rate is { } engageRate) _atlasRequestedRate = (int)engageRate;
                break;
            case "setRate":
                if (command.Rate is not { } rate)
                    throw new ArgumentException("Atlas setRate needs a rate.", nameof(command));
                if (rate != _atlasRequestedRate)
                {
                    DropCompressedAtlasTime();
                    _atlasRequestedRate = (int)rate;
                }
                break;
            case "takeControl":
                DisengageAtlasAutopilot("Autopilot off — you have control.");
                break;
            default:
                throw new ArgumentException("Expected Atlas action engage, setRate or takeControl.", nameof(command.Action));
        }
    }

    public FlightSnapshot AdvanceAtlas(double seconds, FlightInput input, bool manualIntent) =>
        AdvanceFlight(seconds, input, manualIntent);

    private FlightSnapshot AdvanceFlight(double seconds, FlightInput input, bool manualIntent)
    {
        FixedStepClock.ValidateDelta(seconds);
        ArgumentNullException.ThrowIfNull(input);
        var controls = input.Normalize();
        if (_phase is not (Phase.Countdown or Phase.Flying))
            return CreateSnapshot();

        if (_atlasPlan is not null && (manualIntent || Math.Abs(controls.Roll) > 1e-6 ||
            Math.Abs(controls.Pitch) > 1e-6 || Math.Abs(controls.Yaw) > 1e-6 || controls.Afterburner || controls.Brake))
            DisengageAtlasAutopilot("Autopilot off — manual flight input.");

        if (_atlasPlan is not { } plan)
        {
            _clock.Advance(seconds, step => Step(step, controls));
        }
        else
        {
            UpdateAtlasArrival(plan);
            var frameRate = AtlasGuidanceFor(plan).Rate;
            bool PilotStep(double step)
            {
                var guidance = AtlasGuidanceFor(plan);
                if (frameRate > 1 && guidance.Rate != frameRate)
                    return false;
                if (!Step(step, controls, guidance))
                    return false;
                UpdateAtlasArrival(plan);
                return true;
            }

            if (frameRate == 1)
                _clock.Advance(seconds, PilotStep);
            else
            {
                const int acceleratedStepBudget = 60;
                _clock.Advance(Math.Min(seconds, _clock.MaximumFrameSeconds) * frameRate, acceleratedStepBudget, PilotStep);
                // Also discard a fractional tick if the transition happened on this frame's last tick.
                if (_atlasPlan is null || AtlasGuidanceFor(plan).Rate != frameRate)
                    _clock.DiscardRemainder();
            }
        }
        return CreateSnapshot();
    }

    private AtlasGuidance AtlasGuidanceFor(AtlasPlan plan)
    {
        var altitude = Math.Max(plan.Altitude, AtlasCruiseFloor);
        var course = GroundCourse();
        var heading = plan.ArrivalHeading ?? plan.Heading;
        double? remaining = null;
        var nearingPolarEntry = false;
        if (plan.Target is { } target)
        {
            var x = _position.X + _atlasLapOffset;
            var dx = target.X - x;
            var dz = target.Z - _position.Z;
            remaining = plan.ArrivalHeading.HasValue ? 0 : Math.Sqrt(dx * dx + dz * dz);
            if (!plan.ArrivalHeading.HasValue && remaining > 0)
            {
                if (plan.PolarEntry is { } entry)
                {
                    dx = entry.X - x;
                    dz = entry.Z - _position.Z;
                    nearingPolarEntry = remaining <= AtlasApproachDistance ||
                        dx * dx + dz * dz <= AtlasApproachDistance * AtlasApproachDistance;
                }
                else
                {
                    // Aim just inside the bounded map but still inside the true capture circle.
                    const double polarAimZ = WorldAtlasGrid.HalfHeight - 2300;
                    dz = Math.Clamp(target.Z, -polarAimZ, polarAimZ) - _position.Z;
                }
                heading = Math.Atan2(dx, dz);
            }
        }

        var safeHeading = plan.Target is null || plan.ArrivalHeading.HasValue ? AtlasPolarHeading(heading, course) : heading;
        var polarLimit = safeHeading != heading;
        heading = safeHeading;
        var error = Math.IEEERemainder(heading - course, Math.Tau);
        // An exactly opposed course has no preferred side; keep the tie deterministic.
        if (Math.Abs(error) > Math.PI - 0.01) error = Math.PI;

        var status = "acquiring";
        string reason;
        var rate = 1;
        if (_phase == Phase.Paused)
            reason = "Paused — simulation time is stopped.";
        else if (_phase is Phase.Ready or Phase.Countdown)
            reason = "Autopilot armed — awaiting departure.";
        else if (_onGround || _phase == Phase.Crashed || _stalled)
            reason = "Autopilot limited to 1× — aircraft not in stable airborne flight.";
        else if (_gearDown)
            reason = "Landing gear extended — time compression is unavailable.";
        else if (_fuel < AtlasLowFuel)
            reason = "Low fuel — time compression is unavailable.";
        else if (plan.ArrivalHeading.HasValue)
            reason = "Destination reached — holding airborne course at 1×.";
        else if (nearingPolarEntry)
            reason = "Aligning for polar capture at 1×.";
        else if (remaining <= AtlasApproachDistance)
            reason = "Approaching destination at 1×.";
        else if (Math.Abs(altitude - _position.Y) > 180 || Math.Abs(_velocity.Y) > 7)
            reason = "Acquiring cruise altitude at 1×.";
        else if (Math.Abs(_velocity.Length - plan.Speed) > 12 || Math.Abs(error) > 0.09 || Math.Abs(RenderRoll()) > 0.15)
            reason = "Acquiring cruise speed and course at 1×.";
        else
        {
            status = "cruise";
            reason = "Stable dry cruise.";
            rate = _atlasRequestedRate;
        }
        if (plan.ArrivalHeading.HasValue) status = "arrived";
        return new(status, reason, rate, altitude, plan.Speed, heading, error, remaining, polarLimit);
    }

    private FlightInput AtlasControls(AtlasGuidance guidance, double speed, double fast,
        double alpha, double pressureArea, double mach)
    {
        var liftHeadroom = pressureArea * AttachedLiftCoefficient(AssistedTrimAngleLimit) / (Mass * Gravity);
        var bankLimit = AssistedBankLimit(speed, liftHeadroom);
        var safeBank = 0.58 * Smooth(100, 160, speed);
        var climbRate = Math.Clamp((guidance.Altitude - _position.Y) * 0.045, -38, 52);
        if (_position.Y < WorldAtlasGrid.MaximumElevation + 350 && World.AtlasElevation is { } terrain)
        {
            // The climb begins below the global floor. Check the real active surface along
            // the current velocity and favor wings-level climb if clearance is tightening.
            var height = terrain.Sample(_position.X, _position.Z).Height;
            for (var ahead = 10; ahead <= 30; ahead += 10)
                height = Math.Max(height, terrain.Sample(_position.X + _velocity.X * ahead,
                    _position.Z + _velocity.Z * ahead).Height);
            if (_position.Y - height < 450)
            {
                climbRate = Math.Max(climbRate, 52);
                safeBank = Math.Min(safeBank, 0.18);
            }
        }
        var bank = Math.Clamp(guidance.HeadingError * 1.65, -safeBank, safeBank);

        var pitchAuthority = Aircraft.Tuning.FastPitchAuthority;
        var verticalResponse = Math.Max(20, speed * (0.72 * (1 - fast) + pitchAuthority * fast));
        var pitch = Math.Clamp((0.20 * climbRate + 0.40 * (climbRate - _velocity.Y)) / verticalResponse, -0.65, 0.65);
        if (speed < 130)
            pitch = Math.Min(pitch, 0.45 * Smooth(75, 130, speed) - 0.20);
        pitch *= Smooth(0.1, 0.65, _up.Y);

        // Request normal dry throttle from estimated drag and climb load, with speed feedback.
        var stallLoss = StallLoss(alpha);
        var drag = DragAcceleration(pressureArea, AttachedLiftCoefficient(alpha) * (1 - stallLoss),
            stallLoss, fast, speed, mach, false);
        var direction = _velocity.Unit(_forward);
        var alongTrack = Math.Max(0.5, FlightVec.Dot(direction, _forward));
        var thrust = (drag + Gravity * direction.Y + 0.16 * (guidance.Speed - speed)) / alongTrack;
        var throttle = Math.Clamp(thrust * Mass / Aircraft.Tuning.DryThrust, 0, 1);
        return new(bank / bankLimit, pitch, 0, throttle, false, false) { Assisted = true };
    }

    private void UpdateAtlasArrival(AtlasPlan plan)
    {
        if (_phase != Phase.Flying || _onGround || _stalled || _fuel == 0 ||
            plan.Target is not { } target || plan.ArrivalHeading.HasValue)
            return;
        var x = _position.X + _atlasLapOffset;
        var dx = target.X - x;
        var dz = target.Z - _position.Z;
        if (dx * dx + dz * dz <= AtlasCaptureDistance * AtlasCaptureDistance)
        {
            var course = GroundCourse();
            plan.ArrivalHeading = AtlasPolarHeading(course, course);
        }
        else if (plan.PolarEntry is { } entry)
        {
            dx = entry.X - x;
            dz = entry.Z - _position.Z;
            const double entryCapture = 6_000;
            if (dx * dx + dz * dz <= entryCapture * entryCapture)
                plan.PolarEntry = null;
        }
    }

    private AtlasAutopilotTarget? AtlasPolarEntry(AtlasAutopilotTarget target)
    {
        var dx = target.X - (_position.X + _atlasLapOffset);
        var dz = target.Z - _position.Z;
        if (Math.Abs(target.Z) < WorldAtlasGrid.HalfHeight - 15_000 || Math.Abs(dz) < 0.35 * Math.Abs(dx))
            return null;

        // A straight-in polar arrival cannot turn tangent in a 5 km circle at cruise speed.
        // Intercept the row obliquely from a point with enough room to bank.
        var direction = Math.Abs(dx) > 8000 ? Math.Sign(dx) : Math.CopySign(1, Math.Sin(GroundCourse()));
        return new(target.X - direction * 45_000, Math.CopySign(WorldAtlasGrid.HalfHeight - 22_000, target.Z));
    }

    private double GroundCourse() =>
        _velocity.X * _velocity.X + _velocity.Z * _velocity.Z > 225
        ? Math.Atan2(_velocity.X, _velocity.Z) : _lastYaw;

    private double AtlasPolarHeading(double heading, double course)
    {
        var polarSign = Math.Sign(_position.Z);
        if (Math.Abs(_position.Z) < WorldAtlasGrid.HalfHeight - AtlasPolarBuffer || polarSign * Math.Cos(heading) <= 1e-6)
            return heading;
        var eastward = Math.Sin(heading);
        if (Math.Abs(eastward) <= 1e-6) eastward = Math.Sin(course);
        return Math.CopySign(Math.PI / 2, Math.Abs(eastward) <= 1e-6 ? 1 : eastward);
    }

    private void DropCompressedAtlasTime()
    {
        if (_atlasPlan is { } plan && AtlasGuidanceFor(plan).Rate > 1)
            _clock.DiscardRemainder();
    }

    private void DisengageAtlasAutopilot(string reason)
    {
        if (_atlasPlan is null) return;
        _atlasPlan = null;
        _atlasOffReason = reason;
        _clock.DiscardRemainder();
    }

    private void ResetAtlasAutopilot()
    {
        DisengageAtlasAutopilot("Autopilot is off.");
        _atlasOffReason = "Autopilot is off.";
        _atlasRequestedRate = 1;
    }

    private AtlasAutopilotSnapshot? CreateAtlasAutopilotSnapshot()
    {
        if (World.Atlas is null) return null;
        if (_atlasPlan is not { } plan)
            return new("off", _atlasOffReason, null, _atlasRequestedRate, 1, null, null, 1, null, null, null);

        var guidance = AtlasGuidanceFor(plan);
        var reason = guidance.Reason;
        if (plan.Altitude < guidance.Altitude)
            reason += $" Selected altitude raised to {AtlasCruiseFloor:0} m for Atlas terrain clearance.";
        if (guidance.PolarLimit)
            reason += " Polar boundary: holding a tangent course.";
        var heading = guidance.Heading * 180 / Math.PI;
        if (heading < 0) heading += 360;
        return new(guidance.Status, reason, plan.Target is null ? "hold" : "route",
            _atlasRequestedRate, guidance.Rate, plan.Target, guidance.Remaining,
            _phase == Phase.Flying ? AtlasDryFuelScale : 1, guidance.Speed, guidance.Altitude, heading)
        {
            ProjectedFuel = EstimateAtlasFuelAtArrival(plan, guidance)
        };
    }

    private double? EstimateAtlasFuelAtArrival(AtlasPlan plan, AtlasGuidance guidance)
    {
        if (guidance.Remaining is not { } distance || plan.ArrivalHeading.HasValue) return null;
        if (plan.PolarEntry is { } entry && plan.Target is { } target)
        {
            var dx = entry.X - (_position.X + _atlasLapOffset);
            var dz = entry.Z - _position.Z;
            var finalX = target.X - entry.X;
            var finalZ = target.Z - entry.Z;
            distance = Math.Sqrt(dx * dx + dz * dz) + Math.Sqrt(finalX * finalX + finalZ * finalZ);
        }

        var speed = guidance.Speed;
        var pressureArea = 0.5 * (1.225 * Math.Exp(-guidance.Altitude / 9500)) * speed * speed * WingArea;
        var liftCoefficient = Gravity * Mass / pressureArea;
        var drag = DragAcceleration(pressureArea, liftCoefficient, 0,
            Smooth(175, 285, speed), speed, speed / SpeedOfSound(guidance.Altitude), false);
        var throttle = Math.Clamp(drag * Mass / Aircraft.Tuning.DryThrust, 0, 1);
        var cruiseBurn = (0.00004 + 0.00045 * throttle) * distance / speed;
        var climbSeconds = Math.Max(0, guidance.Altitude - _position.Y) / 38;
        var turnSeconds = Math.Abs(guidance.HeadingError) * speed / (Gravity * Math.Tan(0.5));
        var acquisitionAllowance = 0.00049 * (climbSeconds + turnSeconds);
        return Math.Max(0, _fuel - AtlasDryFuelScale * (cruiseBurn + acquisitionAllowance));
    }

    private static void ValidateAtlasRange(double? value, double minimum, double maximum, string name)
    {
        if (value is { } number && (!double.IsFinite(number) || number < minimum || number > maximum))
            throw new ArgumentOutOfRangeException(name);
    }

    private sealed record AtlasPlan(AtlasAutopilotTarget? Target, double Heading, double Altitude, double Speed)
    {
        public double? ArrivalHeading { get; set; }
        public AtlasAutopilotTarget? PolarEntry { get; set; }
    }

    private readonly record struct AtlasGuidance(string Status, string Reason, int Rate, double Altitude, double Speed,
        double Heading, double HeadingError, double? Remaining, bool PolarLimit);
}
