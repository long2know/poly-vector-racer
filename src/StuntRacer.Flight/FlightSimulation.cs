using StuntRacer.Engine;

namespace StuntRacer.Flight;

/// <summary>
/// Independently authored, fixed-step jet-flight game dynamics. This is not an original-game
/// port, an aircraft specification, or a model intended for real-world flight training.
/// </summary>
public sealed partial class FlightSimulation : IFlightSession
{
    private const double Gravity = 9.81;
    private const double Mass = 10_000;
    private const double WingArea = 34;
    private const double TrimAngle = 0.06;
    private const double AssistedTrimAngleLimit = 0.20;
    private const double ZeroAngleLift = 0.24;
    private const double LiftSlope = 4.7;
    private const double MaximumSpeed = 450;
    private const string DefaultAircraftId = "f18";
    private readonly FixedStepClock _clock = new(1.0 / 120.0, 0.25);
    private readonly bool _worldJet;
    private FlightVec _position;
    private FlightVec _velocity;
    private FlightVec _forward = FlightVec.Forward;
    private FlightVec _up = FlightVec.Up;
    private double _rollRate;
    private double _pitchRate;
    private double _yawRate;
    private double _lastYaw;
    private double _throttle;
    private double _fuel = 1;
    private double _gForce = 1;
    private double _time;
    private double _countdown;
    private double _airborneSeconds;
    private bool _afterburner;
    private bool _stalled;
    private bool _onGround = true;
    private bool _gearDown = true;
    private Phase _phase = Phase.Ready;
    private Phase _resumePhase;
    private int _landings;
    private int _crashes;
    private int _recoveries;
    private string _message = "Runway ready — start, throttle up, then pull up above 58 m/s.";
    private string _resumeMessage = "";
    private bool _terrainWaiting;

    public static IReadOnlyList<FlightAircraftProfile> WorldAircraft { get; } =
        Array.AsReadOnly<FlightAircraftProfile>(
        [
            new(DefaultAircraftId, "F/A-18", 1.8, new(68_000, 112_000, BoostAcceleration: 8.5,
                RollAuthority: 1.5, FastRollReduction: 0.65, FastPitchAuthority: 0.095)),
            new("f14", "F-14", 2.1, new(72_000, 120_000, BoostAcceleration: 9.6,
                RollAuthority: 1.32, FastRollReduction: 0.62, FastPitchAuthority: 0.085)),
            new("f15", "F-15", 2.4, new(82_000, 142_000, BoostAcceleration: 11,
                RollAuthority: 1.72, FastRollReduction: 0.70, FastPitchAuthority: 0.09)),
            new("sr71", "SR-71 Blackbird", 3.2, new(64_000, 112_000, BoostAcceleration: 7.2,
                RollAuthority: 1.10, FastRollReduction: 0.58, FastPitchAuthority: 0.045, HighAltitudeBoost: 13))
        ]);

    public FlightSimulation() : this(FlightWorldDefinition.Default) { }
    public FlightSimulation(FlightWorldDefinition world) : this(world, DefaultAircraftId) { }

    public FlightSimulation(FlightWorldDefinition world, string aircraftId)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        if (world.SourceWorld is not null)
            throw new ArgumentException("Use the original player session for original disk worlds.", nameof(world));
        ArgumentNullException.ThrowIfNull(aircraftId);
        var id = aircraftId == "default" ? DefaultAircraftId : aircraftId;
        Aircraft = WorldAircraft.FirstOrDefault(profile => profile.Id == id)
            ?? throw new ArgumentException($"Unknown World Flight aircraft '{aircraftId}'.", nameof(aircraftId));
        _worldJet = world.Geography is not null || world.Atlas is not null;
        if (!_worldJet && Aircraft.Id != DefaultAircraftId)
            throw new ArgumentException("Alternate aircraft are only available in World Flight.", nameof(aircraftId));
        PlaceOnRunway();
    }

    public FlightWorldDefinition World { get; }
    public FlightAircraftProfile Aircraft { get; }
    public bool TerrainWaiting => _terrainWaiting;
    public FlightSnapshot Snapshot => CreateSnapshot();

    public FlightSnapshot Advance(double seconds, FlightInput input) => AdvanceFlight(seconds, input, false);

    public void Command(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command.Trim().ToLowerInvariant())
        {
            case "start":
                if (_phase == Phase.Ready)
                {
                    _phase = Phase.Countdown;
                    _countdown = 3;
                    _message = _worldJet && !_onGround
                        ? "Scenic departure in 3 seconds." : "Runway departure in 3 seconds.";
                }
                break;
            case "pause":
                if (_phase is Phase.Flying or Phase.Countdown)
                {
                    DropCompressedAtlasTime();
                    _resumePhase = _phase;
                    _resumeMessage = _message;
                    _phase = Phase.Paused;
                    _message = "Paused.";
                }
                break;
            case "resume":
                if (_phase == Phase.Paused)
                {
                    _phase = _resumePhase;
                    _message = _resumeMessage;
                }
                break;
            case "restart":
            case "garage":
                ResetAtlasAutopilot();
                _clock.Reset();
                _time = 0;
                _fuel = 1;
                _landings = 0;
                _crashes = 0;
                _recoveries = 0;
                PlaceOnRunway();
                var garage = command.Trim().Equals("garage", StringComparison.OrdinalIgnoreCase);
                _phase = garage ? Phase.Ready : Phase.Countdown;
                _countdown = garage ? 0 : 3;
                if (!_worldJet)
                    _message = garage ? "Runway ready — choose an experience or take off." : "Runway departure in 3 seconds.";
                else if (!garage)
                    _message = _onGround ? "Runway departure in 3 seconds." : "Scenic departure in 3 seconds.";
                break;
            case "recover":
                ResetAtlasAutopilot();
                PlaceOnRunway();
                _phase = Phase.Ready;
                _countdown = 0;
                if (_recoveries < int.MaxValue)
                    _recoveries++;
                _message = !_worldJet ? "Recovered to Bay Runway — start when ready. Fuel retained."
                    : "Returned to the selected departure. Fuel retained.";
                break;
            case "air-start":
                if (_phase is Phase.Ready or Phase.Paused or Phase.Crashed)
                {
                    if (_phase != Phase.Ready) ResetAtlasAutopilot();
                    PlaceOnRunway();
                    if ((World.Atlas?.Destination ?? World.Geography?.Destination) is { } departure)
                        BeginAirborne(departure);
                    else
                    {
                        _position = new FlightVec(0, 258, -4000);
                        _velocity = new FlightVec(0, 0, 95);
                        SetAttitude(0, TrimAngle, 0);
                        _throttle = 0.35;
                        _onGround = false;
                        _airborneSeconds = 1;
                    }
                    _phase = Phase.Flying;
                    _countdown = 0;
                    _message = !_worldJet ? "Air-start training — gear down, Bay Runway ahead."
                        : "Scenic air start — you have control.";
                }
                break;
            case "gear-toggle":
                // Paused Advance and non-recovery controls cannot mutate the frozen aircraft.
                if (_phase is Phase.Ready or Phase.Countdown or Phase.Flying)
                {
                    DisengageAtlasAutopilot("Autopilot off — landing gear input.");
                    _gearDown = !_gearDown;
                    _message = _gearDown ? "Landing gear extended." : "Landing gear retracted.";
                }
                break;
            default:
                throw new ArgumentException($"Unknown flight command: {command}", nameof(command));
        }
    }

    private bool Step(double dt, FlightInput input, AtlasGuidance? guidance = null)
    {
        if (_phase is not (Phase.Countdown or Phase.Flying))
            return false;

        if (World.Elevation is not null && _phase == Phase.Flying)
        {
            // Wait explicitly instead of integrating above fabricated sea level.
            _terrainWaiting = World.Elevation.Sample(_position.X, _position.Z) is null ||
                World.Elevation.Sample(_position.X + _velocity.X * dt, _position.Z + _velocity.Z * dt) is null;
            if (_terrainWaiting) return false;
        }

        _time += dt;
        if (_phase == Phase.Countdown)
        {
            _countdown = Math.Max(0, _countdown - dt);
            if (_countdown <= 1e-9)
            {
                _countdown = 0;
                _phase = Phase.Flying;
                _message = _onGround ? "Throttle up; rotate above 58 m/s. Keep pitch gentle after liftoff."
                    : "Scenic flight — you have control.";
            }
            return true;
        }

        var speed = _velocity.Length;
        var density = 1.225 * Math.Exp(-Math.Max(0, _position.Y) / 9500);
        var pressureArea = 0.5 * density * speed * speed * WingArea;
        var soundSpeed = _worldJet ? SpeedOfSound(_position.Y) : 1;
        var mach = speed / soundSpeed;
        var fast = _worldJet && !_onGround ? Smooth(175, 285, speed) : 0;
        var authority = Math.Clamp((speed - 12) / 73, 0, 1);
        var right = FlightVec.Cross(_up, _forward).Unit(new FlightVec(1, 0, 0));
        var forwardSpeed = FlightVec.Dot(_velocity, _forward);
        var alpha = Math.Atan2(-FlightVec.Dot(_velocity, _up), Math.Max(1, forwardSpeed));
        var sideslip = Math.Atan2(FlightVec.Dot(_velocity, right), Math.Max(12, forwardSpeed));

        if (guidance is { } pilot)
            input = AtlasControls(pilot, speed, fast, alpha, pressureArea, mach);
        var throttleTarget = _fuel > 0 ? input.Throttle : 0;
        _throttle += (throttleTarget - _throttle) * (1 - Math.Exp(-dt / 0.65));
        _afterburner = input.Afterburner && _throttle >= 0.85 && _fuel > 0;
        var burnRate = 0.00004 + 0.00045 * _throttle + (_afterburner ? 0.0019 : 0);
        if (guidance is not null)
            burnRate *= AtlasDryFuelScale;
        _fuel = Math.Max(0, _fuel - burnRate * dt);
        if (_fuel == 0)
            _afterburner = false;
        var tuning = Aircraft.Tuning;
        var thrust = _fuel > 0 ? _throttle * (_afterburner ? tuning.WetThrust : tuning.DryThrust) / Mass : 0;
        if (_afterburner && _worldJet && !_onGround)
        {
            var boost = tuning.BoostAcceleration;
            // Extra cruise thrust blends in aloft with the gear retracted.
            if (tuning.HighAltitudeBoost > 0 && !_gearDown)
                boost += tuning.HighAltitudeBoost * Smooth(2500, 7500, _position.Y);
            thrust += _throttle * boost * Smooth(100, 235, speed);
        }

        UpdateAttitude(dt, input, speed, authority, alpha, sideslip, right, fast, pressureArea, thrust);
        right = FlightVec.Cross(_up, _forward).Unit(right);
        forwardSpeed = FlightVec.Dot(_velocity, _forward);
        alpha = Math.Atan2(-FlightVec.Dot(_velocity, _up), Math.Max(1, forwardSpeed));
        sideslip = Math.Atan2(FlightVec.Dot(_velocity, right), Math.Max(12, forwardSpeed));

        // Angle-dependent airfoil lift falls away beyond the authored stall angle. The
        // direction is perpendicular to velocity, so banking redirects lift into a turn.
        var stallLoss = StallLoss(alpha);
        var liftCoefficient = AttachedLiftCoefficient(alpha) * (1 - stallLoss);
        var direction = _velocity.Unit(_forward);
        var liftDirection = (_up - direction * FlightVec.Dot(_up, direction)).Unit(_up);
        var liftAcceleration = LiftAcceleration(pressureArea, liftCoefficient);
        var dragAcceleration = DragAcceleration(pressureArea, liftCoefficient, stallLoss, fast, speed, mach, input.Brake);
        var sideAcceleration = SideAcceleration(sideslip, pressureArea);
        var properAcceleration = _forward * thrust + liftDirection * liftAcceleration -
                                 direction * dragAcceleration + right * sideAcceleration;
        var acceleration = properAcceleration - FlightVec.Up * Gravity;
        _gForce = Math.Clamp(FlightVec.Dot(properAcceleration, _up) / Gravity, -9, 9);
        _stalled = !_onGround && (speed < 52 || Math.Abs(alpha) > 0.34 || forwardSpeed < 0);

        if (_onGround)
        {
            var contact = World.Sample(_position.X, _position.Z);
            if (!_gearDown || contact.LandingSurface is null)
            {
                Crash(!_gearDown ? "Landing gear retracted on the ground." : "Left the paved landing surface.");
                return true;
            }

            var horizontal = new FlightVec(_velocity.X, 0, _velocity.Z);
            var groundRight = new FlightVec(right.X, 0, right.Z).Unit(new FlightVec(1, 0, 0));
            acceleration -= horizontal.Unit(default) * (0.32 + (input.Brake ? 6 : 0));
            acceleration -= groundRight * Math.Clamp(FlightVec.Dot(horizontal, groundRight) * 4, -10, 10);
            // The gear supplies the normal reaction until lift genuinely unloads it.
            if (acceleration.Y <= 0.35 || speed < 40)
                acceleration = acceleration with { Y = 0 };
            else
            {
                _onGround = false;
                _airborneSeconds = 0;
                _message = "Airborne — retract gear for lower drag.";
            }
            _gForce = _onGround ? 1 : _gForce;
        }

        var oldVelocity = _velocity;
        _velocity = (_velocity + acceleration * dt).Limit(_worldJet ? soundSpeed * Aircraft.MaximumMach : MaximumSpeed);
        if (_onGround)
        {
            _velocity = _velocity with { Y = 0 };
            if (_throttle < 0.05 && FlightVec.Dot(oldVelocity, _velocity) < 0)
                _velocity = default;
        }
        _position += _velocity * dt;
        if (World.Atlas is not null)
        {
            const int pole = WorldAtlasGrid.HalfHeight;
            if (_position.X >= WorldAtlasGrid.HalfWidth) _atlasLapOffset += WorldAtlasGrid.Width;
            if (_position.X < -WorldAtlasGrid.HalfWidth) _atlasLapOffset -= WorldAtlasGrid.Width;
            _position = _position with { X = WorldAtlasGrid.Wrap(_position.X), Z = Math.Clamp(_position.Z, -pole, pole) };
            if (_position.Z >= pole && _velocity.Z > 0 || _position.Z <= -pole && _velocity.Z < 0)
                _velocity = _velocity with { Z = 0 };
        }
        if (!_onGround)
            _airborneSeconds += dt;

        ResolveContact();
        var limit = World.Geography is null ? 200_000 : 10_000_000;
        if (World.Atlas is null && _phase == Phase.Flying &&
            (Math.Abs(_position.X) > limit || Math.Abs(_position.Z) > limit || _position.Y > 20_000))
        {
            _position = new FlightVec(
                Math.Clamp(_position.X, -limit, limit),
                Math.Min(_position.Y, 20_000),
                Math.Clamp(_position.Z, -limit, limit));
            Crash("Training area boundary reached — recover or restart.", false);
        }
        return true;
    }

    private void UpdateAttitude(
        double dt, FlightInput input, double speed, double authority,
        double alpha, double sideslip, FlightVec right, double fast, double pressureArea, double thrust)
    {
        var response = 1 - Math.Exp(-dt / 0.18);
        if (_onGround)
        {
            var pitch = Math.Asin(Math.Clamp(_forward.Y, -1, 1));
            var targetPitchRate = input.Pitch * 0.5 * authority -
                                  pitch * 1.1 * (1 - Math.Abs(input.Pitch));
            _pitchRate += (targetPitchRate - _pitchRate) * response;
            _yawRate += (input.Yaw * 0.4 * Math.Clamp(speed / 25, 0, 1) - _yawRate) * response;
            _rollRate = 0;
            SetAttitude(
                _lastYaw + _yawRate * dt,
                Math.Clamp(pitch + _pitchRate * dt, -0.025, 0.314),
                0);
            return;
        }

        // Stability seeks a small fixed angle of attack, not world altitude or heading.
        // A slow aircraft still falls; these are damped aerodynamic controls, not rails.
        var targetPitch = (input.Pitch * 0.72 + (TrimAngle - alpha) * 0.9) * authority;
        var targetYaw = (input.Yaw * 0.44 + sideslip * 0.6) * authority;
        var tuning = Aircraft.Tuning;
        var rollAuthority = tuning.RollAuthority;
        if (fast > 0)
        {
            var pathAngle = Math.Asin(Math.Clamp(_velocity.Y / speed, -1, 1));
            var liftUp = Math.Clamp(_up.Y, 0.48, 1);
            var pathRate = input.Pitch * tuning.FastPitchAuthority - pathAngle * 0.14 * (1 - Math.Abs(input.Pitch));
            var targetLift = Math.Clamp((Gravity * Math.Cos(pathAngle) + speed * pathRate) / liftUp, -3 * Gravity, 7 * Gravity);
            var targetAlpha = (targetLift * Mass / pressureArea - ZeroAngleLift) / LiftSlope;
            var lift = LiftAcceleration(pressureArea, AttachedLiftCoefficient(alpha));
            var currentPathRate = (lift * liftUp - Gravity * Math.Cos(pathAngle)) / speed;
            var speedPitch = Math.Clamp(currentPathRate + 2.8 * (targetAlpha - alpha), -.55, .55);
            targetPitch += (speedPitch - targetPitch) * fast;
            targetYaw *= 1 - 0.5 * fast;
            rollAuthority -= tuning.FastRollReduction * fast;
        }
        var targetRoll = input.Roll * rollAuthority;
        if (input.Assisted)
        {
            var bank = RenderRoll();
            var usableLoad = pressureArea * AttachedLiftCoefficient(AssistedTrimAngleLimit) / (Mass * Gravity);
            var bankLimit = AssistedBankLimit(speed, usableLoad);
            // The input is positive-right; the rendered bank angle is negative-right.
            targetRoll = Math.Clamp(3.0 * Math.IEEERemainder(bank + input.Roll * bankLimit, Math.Tau),
                -rollAuthority, rollAuthority);

            var direction = _velocity.Unit(_forward);
            var liftDirection = (_up - direction * FlightVec.Dot(_up, direction)).Unit(_up);
            var liftUp = liftDirection.Y;
            var bankBlend = Smooth(0.04, 0.40, Math.Abs(bank));
            var trim = Smooth(0.30, 0.48, liftUp) * Smooth(0.95, 1.55, usableLoad) *
                       (1 - Smooth(0.21, 0.30, Math.Abs(alpha)));
            if (trim > 0)
            {
                // Trim the wing against its actual tilt and flight path, never a target altitude.
                var pathAngle = Math.Asin(Math.Clamp(direction.Y, -1, 1));
                var vertical = FlightVec.Up - direction * direction.Y;
                var pathAcceleration = _forward * thrust + right * SideAcceleration(sideslip, pressureArea) - FlightVec.Up * Gravity;
                var damping = 0.24 * speed * pathAngle * vertical.Length;
                var liftTarget = Math.Clamp((-FlightVec.Dot(pathAcceleration, vertical) - damping) / liftUp, 0, 2.2 * Gravity);
                var alphaTarget = Math.Min((liftTarget * Mass / pressureArea - ZeroAngleLift) / LiftSlope, AssistedTrimAngleLimit);
                var lift = LiftAcceleration(pressureArea, AttachedLiftCoefficient(alpha) * (1 - StallLoss(alpha)));
                pathAcceleration += liftDirection * lift; // Drag is parallel to velocity, so it cannot change the path direction.
                var normalRate = FlightVec.Dot(pathAcceleration, liftDirection) / speed;
                var pitchControl = input.Pitch * (0.72 * authority * (1 - fast) + tuning.FastPitchAuthority * fast / liftUp);
                var trimmedPitch = Math.Clamp(normalRate + 2.8 * (alphaTarget - alpha) + pitchControl, -0.55, 0.55);
                // With gear down and wings near level, leave the slow approach and flare to the pilot.
                var approachTrim = _gearDown ? Smooth(90, 105, speed) : 1;
                var pitchBlend = (bankBlend + (1 - bankBlend) * approachTrim) * trim * (1 - Math.Abs(input.Pitch));
                targetPitch += (trimmedPitch - targetPitch) * pitchBlend;

                // Follow the turn the aerodynamic forces are already making, leaving rudder independent.
                var pathRight = FlightVec.Cross(FlightVec.Up, direction);
                var turnRate = FlightVec.Dot(pathAcceleration, pathRight) / Math.Max(45, speed * pathRight.LengthSquared);
                targetYaw += Math.Clamp(turnRate * _up.Y, -0.18, 0.18) * 0.96 * trim * bankBlend * (1 - Math.Abs(input.Yaw));
            }
        }
        _rollRate += (targetRoll * authority - _rollRate) * response;
        _pitchRate += (targetPitch - _pitchRate) * response;
        _yawRate += (targetYaw - _yawRate) * response;
        var omega = right * -_pitchRate + _up * _yawRate + _forward * -_rollRate;
        var rate = omega.Length;
        if (rate > 1e-12)
        {
            var axis = omega / rate;
            _forward = FlightVec.Rotate(_forward, axis, rate * dt).Unit(FlightVec.Forward);
            _up = FlightVec.Rotate(_up, axis, rate * dt);
            right = FlightVec.Cross(_up, _forward).Unit(right);
            _up = FlightVec.Cross(_forward, right).Unit(FlightVec.Up);
        }
        UpdateYaw();
    }

    private void ResolveContact()
    {
        var sample = World.TrySample(_position.X, _position.Z);
        if (sample is null)
        {
            _terrainWaiting = true;
            return;
        }
        var clearance = _gearDown ? World.GearClearance : 1;
        var contactY = sample.Height + clearance;
        if (_position.Y > contactY + 1e-9)
        {
            if (_onGround)
            {
                _onGround = false;
                _airborneSeconds = 0;
            }
            return;
        }

        _position = _position with { Y = contactY };
        if (sample.IsWater)
        {
            Crash(!_worldJet ? "Water impact — recover to Bay Runway." : "Water impact — return to the departure point.");
            return;
        }
        if (sample.LandingSurface is null)
        {
            Crash(World.Atlas is null ? "Terrain impact — land on the paved runway."
                : "Terrain impact — return to the departure point.");
            return;
        }
        if (!_gearDown)
        {
            Crash("Gear-up landing — extend the landing gear before touchdown.");
            return;
        }
        if (_onGround)
        {
            _velocity = _velocity with { Y = 0 };
            return;
        }

        var surface = sample.LandingSurface;
        var along = new FlightVec(Math.Sin(surface.Heading), 0, Math.Cos(surface.Heading));
        var horizontal = new FlightVec(_velocity.X, 0, _velocity.Z);
        var runwayAlignment = Math.Abs(FlightVec.Dot(horizontal.Unit(along), along));
        var forwardAlignment = FlightVec.Dot(horizontal.Unit(_forward), _forward);
        var pitch = Math.Asin(Math.Clamp(_forward.Y, -1, 1));
        var roll = RenderRoll();
        if (_velocity.Y < -4.5 || _velocity.Y > 1 || horizontal.Length > 105 ||
            Math.Abs(roll) > 0.22 || pitch < -0.08 || pitch > 0.32 ||
            runwayAlignment < 0.9 || forwardAlignment < 0.85 || _up.Y < 0.9)
        {
            Crash("Hard landing — reduce sink, level the wings, and align with the runway.");
            return;
        }

        if (_airborneSeconds >= 0.75 && _landings < int.MaxValue)
            _landings++;
        _onGround = true;
        _airborneSeconds = 0;
        _velocity = _velocity with { Y = 0 };
        _rollRate = 0;
        _pitchRate = 0;
        _yawRate = 0;
        SetAttitude(_lastYaw, Math.Clamp(pitch, 0, 0.08), 0);
        _stalled = false;
        _gForce = 1;
        _message = $"Landed on {surface.Name} — brake to slow down.";
    }

    private void Crash(string message, bool onGround = true)
    {
        if (_phase == Phase.Crashed)
            return;
        _phase = Phase.Crashed;
        if (_crashes < int.MaxValue)
            _crashes++;
        _velocity = default;
        _onGround = onGround;
        _afterburner = false;
        _stalled = false;
        _gForce = 0;
        _message = message;
        DisengageAtlasAutopilot("Autopilot off — flight ended.");
    }

    private void PlaceOnRunway()
    {
        var spawn = World.RunwayStart;
        _position = new FlightVec(spawn.X, spawn.Y, spawn.Z);
        _atlasLapOffset = 0;
        _velocity = default;
        _rollRate = 0;
        _pitchRate = 0;
        _yawRate = 0;
        _throttle = 0;
        _afterburner = false;
        _stalled = false;
        _onGround = true;
        _gearDown = true;
        _airborneSeconds = 0;
        _gForce = 1;
        _terrainWaiting = false;
        SetAttitude(World.RunwayHeading, 0, 0);
        if ((World.Atlas?.Destination ?? World.Geography?.Destination) is { } departure)
        {
            if (World.LandingSurfaces.Count == 0) BeginAirborne(departure);
            _message = _onGround ? "Boeing Field — runway ready." : "Scenic departure ready.";
        }
    }

    private void BeginAirborne(WorldFlightDestination destination)
    {
        var altitude = World.Atlas is null ? destination.Altitude
            : Math.Max(destination.Altitude, World.Sample(destination.X, destination.Z).Height + 1400);
        _position = new FlightVec(destination.X, altitude, destination.Z);
        var speed = altitude > 2000 ? 175 : 145;
        _velocity = new FlightVec(Math.Sin(destination.Heading) * speed, 0, Math.Cos(destination.Heading) * speed);
        SetAttitude(destination.Heading, 0.025, 0);
        _throttle = 0.47;
        _gearDown = false;
        _onGround = false;
        _airborneSeconds = 1;
    }

    private void SetAttitude(double yaw, double pitch, double roll)
    {
        _forward = new FlightVec(Math.Sin(yaw) * Math.Cos(pitch), Math.Sin(pitch),
            Math.Cos(yaw) * Math.Cos(pitch));
        var right = new FlightVec(Math.Cos(yaw), 0, -Math.Sin(yaw));
        var up = FlightVec.Cross(_forward, right);
        _up = up * Math.Cos(roll) - right * Math.Sin(roll);
        UpdateYaw();
    }

    private void UpdateYaw()
    {
        if (_forward.X * _forward.X + _forward.Z * _forward.Z > 1e-12)
            _lastYaw = Math.Atan2(_forward.X, _forward.Z);
    }

    private double RenderRoll()
    {
        var referenceRight = new FlightVec(Math.Cos(_lastYaw), 0, -Math.Sin(_lastYaw));
        var referenceUp = FlightVec.Cross(_forward, referenceRight).Unit(_up);
        var right = FlightVec.Cross(_up, _forward);
        return Math.Atan2(FlightVec.Dot(right, referenceUp), FlightVec.Dot(right, referenceRight));
    }

    private static double SpeedOfSound(double altitude) =>
        Math.Sqrt(401.87 * (288.15 - .0065 * Math.Clamp(altitude, 0, 11000)));

    private static double AttachedLiftCoefficient(double alpha) =>
        Math.Clamp(ZeroAngleLift + LiftSlope * alpha, -1.45, 1.7);

    private static double StallLoss(double alpha) =>
        Math.Clamp((Math.Abs(alpha) - 0.29) * 2.8, 0, 0.9);

    private static double LiftAcceleration(double pressureArea, double coefficient) =>
        Math.Clamp(pressureArea * coefficient / Mass, -8.5 * Gravity, 8.5 * Gravity);

    private double DragAcceleration(
        double pressureArea, double liftCoefficient, double stallLoss, double fast, double speed, double mach, bool brake)
    {
        var dragCoefficient = 0.024 + 0.065 * liftCoefficient * liftCoefficient +
                              (_gearDown ? 0.034 : 0) + (brake ? 0.16 : 0) +
                              0.19 * stallLoss;
        if (fast > 0)
        {
            // Smooth wave drag at transonic speed. The clean jet can overcome it on afterburner;
            // brakes, gear and induced drag still take energy out of the aircraft.
            var wave = 0.010 * Math.Exp(-Math.Pow((mach - 1.03) / 0.18, 2));
            dragCoefficient += fast * (-0.008 + wave + 0.004 * Smooth(1.05, 1.7, mach));
        }
        dragCoefficient += (1 - fast) * 0.000004 * Math.Pow(Math.Max(0, speed - 260), 2);
        return Math.Min(90, pressureArea * dragCoefficient / Mass);
    }

    private double AssistedBankLimit(double speed, double usableLoad)
    {
        var maximumBank = _worldJet ? 1.05 - 0.40 * Smooth(270, 460, speed) : 1.01;
        return 0.25 + (maximumBank - 0.25) * Smooth(0.95, 2.15, usableLoad);
    }

    private static double SideAcceleration(double sideslip, double pressureArea) =>
        Math.Clamp(-sideslip * pressureArea * 0.75 / Mass, -18, 18);

    private static double Smooth(double lower, double upper, double value)
    {
        var t = Math.Clamp((value - lower) / (upper - lower), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private FlightSnapshot CreateSnapshot() => new(
        new AircraftSnapshot(
            _position.X, _position.Y, _position.Z,
            _lastYaw, Math.Asin(Math.Clamp(_forward.Y, -1, 1)), RenderRoll(),
            _velocity.Length, _velocity.Y, _throttle, _fuel, _gForce,
            _afterburner, _stalled, _onGround, _gearDown, _phase == Phase.Crashed,
            _forward.Snapshot, _up.Snapshot)
        {
            Mach = _worldJet ? _velocity.Length / SpeedOfSound(_position.Y) : null
        },
        _phase switch
        {
            Phase.Ready => "ready",
            Phase.Countdown => "countdown",
            Phase.Flying => "flying",
            Phase.Paused => "paused",
            Phase.Crashed => "crashed",
            _ => throw new InvalidOperationException()
        },
        _time, _countdown, _clock.Alpha, _clock.Tick,
        _landings, _crashes, _recoveries, _terrainWaiting ? "Waiting for streamed terrain." : _message)
    {
        AtlasAutopilot = CreateAtlasAutopilotSnapshot()
    };

    private enum Phase { Ready, Countdown, Flying, Paused, Crashed }
}
