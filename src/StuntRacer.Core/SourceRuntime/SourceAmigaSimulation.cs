namespace StuntRacer.Core.SourceRuntime;

/// <summary>
/// Optional Classic/reference player session. Integer movement is independent of
/// pacing and display interpolation; the authored default session is not changed.
/// </summary>
public sealed class SourceAmigaSimulation : IGameSession
{
    private readonly SourceAmigaPackage _package;
    private readonly SourcePlayerDynamics _dynamics;
    private SourcePlayerRawState _previous;
    private string _phase = "ready";
    private double _accumulator;
    private double _progress;
    private double _previousProgress;
    private bool _passedHalfway;
    private int _lap = 1;
    private long _lapStartTick;
    private double _bestLap;

    private SourceAmigaSimulation(SourceAmigaPackage package)
    {
        if (package.Units.PcFactor != 2 || package.Units.PositionPrecision != 16_384 ||
            package.Units.LocalYFactor != 4 || package.Units.RoadHeightDivisor != 4)
            throw new ArgumentException("The selected source kernel requires the declared PC2/Q14/Y4 unit mapping.", nameof(package));
        if (package.Clock.CallbackHertz != SourceRuntimeProfile.ConfiguredCallbackHertz ||
            package.Clock.CallbacksPerStep != SourceRuntimeProfile.CallbacksPerStep ||
            Math.Abs(package.Clock.SimulationStepSeconds - SourceRuntimeProfile.StepSeconds) > 1e-12)
            throw new ArgumentException("The selected source kernel requires the explicit 50-callback/6 reference clock.", nameof(package));
        _package = package;
        _dynamics = new(package);
        _previous = _dynamics.State;
        ObserveProgress();
        _previousProgress = _progress;
    }

    public static SourceAmigaSimulation Create(SourceAmigaPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return new(package);
    }

    public TrackDefinition Track => _package.Track;
    public SourcePlayerRawState RawState => _dynamics.State;
    public double SimulationStepSeconds => SourceRuntimeProfile.StepSeconds;
    public string ProfileId => SourceRuntimeProfile.ProfileId;
    public bool OriginalStockParity => false;
    public SourceSimulationMetadata Metadata => new(
        ProfileId, false, "ptitSeb/stuntcarremake@c56327db5685c2ae63ea3c73d64ef100183b9359",
        "three-integer-contacts-fl-fr-rear-central",
        "canonical-native-triangles-and-nearest-edges-not-stock-bilinear",
        "per-segment-geometric-heading-not-original-arc-section",
        50, 6, SimulationStepSeconds, "first-full-interval-after-start",
        "positive-axis-is-pressed-digital-buttons",
        "practice-three-lap-step-clock-not-original-championship-or-bcd-timer",
        _package.Lookups.SinQ14.Length, 64, true);

    /// <summary>Authoritative current snapshot; observing it never advances any state.</summary>
    public GameSnapshot Snapshot => CreateSnapshot(1);

    /// <summary>
    /// Runs every elapsed source tick without a discard/catch-up limit. A single call
    /// must contain 0..3600 finite seconds; larger deltas are rejected, not truncated.
    /// The initial source callback's open gate is explicitly normalized to one interval.
    /// </summary>
    public GameSnapshot Advance(double seconds, DriverInput input)
    {
        if (!double.IsFinite(seconds) || seconds is < 0 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Provide a finite elapsed interval in 0..3600 seconds.");
        SourcePlayerControl.Validate(input);
        if (_phase != "racing")
            return Snapshot;

        _accumulator += seconds;
        var available = (long)Math.Floor((_accumulator + SimulationStepSeconds * 1e-9) / SimulationStepSeconds);
        for (long index = 0; index < available && _phase == "racing"; index++)
        {
            Step(input);
            _accumulator = Math.Max(0, _accumulator - SimulationStepSeconds);
        }
        return CreateSnapshot(_phase == "finished" ? 1 : Math.Clamp(_accumulator / SimulationStepSeconds, 0, 1));
    }

    /// <summary>
    /// Advances exactly one authoritative tick while racing, without touching the
    /// wall-time accumulator. Use start first; paused/ready/finished sessions do not step.
    /// </summary>
    public void Step(DriverInput input)
    {
        SourcePlayerControl.Validate(input);
        if (_phase != "racing")
            return;
        _previous = _dynamics.State;
        _dynamics.Step(input);
        var state = _dynamics.State;
        ObserveProgress();
        if (state.Recoveries != _previous.Recoveries)
        {
            _previous = state;
            _passedHalfway = false;
        }
        else
        {
            if (_progress > Track.Length * 0.45 && _progress < Track.Length * 0.75)
                _passedHalfway = true;
            if (_passedHalfway && _previousProgress > Track.Length * 0.85 &&
                _progress < Track.Length * 0.15 && state.BodyVelocityZ > 0)
            {
                var elapsed = (state.Tick - _lapStartTick) * SimulationStepSeconds;
                _bestLap = _bestLap == 0 ? elapsed : Math.Min(_bestLap, elapsed);
                _lapStartTick = state.Tick;
                _passedHalfway = false;
                if (_lap == 3)
                    _phase = "finished";
                else
                    _lap++;
            }
        }
        _previousProgress = _progress;
    }

    public void Command(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command.Trim().ToLowerInvariant())
        {
            case "start":
                if (_phase is "ready" or "finished")
                    Restart();
                break;
            case "restart":
                Restart();
                break;
            case "garage":
                var best = _bestLap;
                Restart();
                _bestLap = best;
                _phase = "ready";
                break;
            case "pause":
                if (_phase == "racing")
                    _phase = "paused";
                break;
            case "resume":
                if (_phase == "paused")
                    _phase = "racing";
                break;
            case "recover":
                if (_phase == "racing")
                {
                    _dynamics.Recover();
                    _previous = _dynamics.State;
                    _passedHalfway = false;
                    ObserveProgress();
                    _previousProgress = _progress;
                }
                break;
            default:
                throw new ArgumentException($"Unknown simulation command: {command}", nameof(command));
        }
    }

    private void Restart()
    {
        _dynamics.ResetRun();
        _previous = _dynamics.State;
        _phase = "racing";
        _accumulator = _bestLap = 0;
        _lap = 1;
        _lapStartTick = 0;
        _passedHalfway = false;
        ObserveProgress();
        _previousProgress = _progress;
    }

    private void ObserveProgress()
    {
        var state = _dynamics.State;
        var scale = _package.Units.DisplayMetresPerPcUnit;
        _progress = Track.Sample(SourceSnapshotAdapter.ToDisplayXz(state.X, scale),
            SourceSnapshotAdapter.ToDisplayXz(state.Z, scale)).S;
    }

    private GameSnapshot CreateSnapshot(double alpha)
    {
        var state = _dynamics.State;
        var player = SourceSnapshotAdapter.Interpolate(_previous, state, _package, alpha);
        var impact = Math.Clamp(Math.Max(state.FrontLeft.Response,
            Math.Max(state.FrontRight.Response, state.Rear.Response)) / 5120.0, 0, 1);
        var message = _phase switch
        {
            "ready" => "Classic reference — start; not stock parity",
            "paused" => "Classic reference paused",
            "finished" => "Classic reference practice complete",
            _ when state.OffMap => "Classic reference — off road; recover",
            _ when state.Airborne => "Classic reference — airborne",
            _ => "Classic reference — integer three-contact player"
        };
        return new(player, null, _phase, state.Tick * SimulationStepSeconds,
            _lap, 3, (state.Tick - _lapStartTick) * SimulationStepSeconds, _bestLap,
            _progress / Track.Length, 0, impact, state.Recoveries, alpha, state.Tick, message);
    }
}
