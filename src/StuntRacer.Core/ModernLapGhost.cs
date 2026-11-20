namespace StuntRacer.Core;

internal sealed class ModernLapGhost
{
    private const int MaximumSamples = 18_000;
    private GhostSample[] _current = [];
    private GhostSample[] _best = [];
    private int _currentCount;
    private int _bestCount;
    private int _lapTicks;
    private double _bestDuration;
    private bool _recording;
    private bool _invalidated;
    private bool _capacityExceeded;

    public bool Enabled { get; set; } = true;

    public void ResetCurrent()
    {
        _currentCount = _lapTicks = 0;
        _recording = _invalidated = _capacityExceeded = false;
    }

    public void Clear()
    {
        ResetCurrent();
        _current = [];
        _best = [];
        _bestCount = 0;
        _bestDuration = 0;
    }

    public void BeginLap(in GhostPose pose)
    {
        ResetCurrent();
        if (_current.Length == 0)
            _current = new GhostSample[MaximumSamples];
        _recording = true;
        _current[_currentCount++] = new GhostSample(0, pose);
    }

    public void RecordTick(double elapsed, in GhostPose pose)
    {
        if (!_recording || _invalidated || _capacityExceeded)
            return;
        // The authoritative simulation is 120 Hz; neither rAF nor display interpolation records.
        if (++_lapTicks % 4 == 0)
            Append(elapsed, pose);
    }

    public void InvalidateLap()
    {
        if (_recording)
            _invalidated = true;
    }

    public void CompleteLap(double duration, in GhostPose pose)
    {
        if (!_recording)
            return;
        if (!_invalidated && !_capacityExceeded && duration > 0)
        {
            Append(duration, pose);
            if (!_capacityExceeded && (_bestCount == 0 || duration < _bestDuration))
            {
                // At most two fixed-capacity buffers; promotion swaps rather than copies a lap.
                (_best, _current) = (_current, _best);
                _bestCount = _currentCount;
                _bestDuration = duration;
                _currentCount = 0;
            }
        }
        _recording = false;
    }

    public GhostSnapshot Snapshot(double lapTime)
    {
        var status = !Enabled ? "disabled" : _capacityExceeded ? "capacity-exceeded"
            : _invalidated ? "lap-invalidated" : _bestCount > 0 ? "playing"
            : _recording ? "recording" : "waiting";
        return new GhostSnapshot(Enabled && _bestCount > 0 ? Playback(lapTime) : null,
            _bestDuration, _bestCount > 0 ? _bestCount : _currentCount, Enabled, status);
    }

    private void Append(double elapsed, in GhostPose pose)
    {
        if (_currentCount > 0 && Math.Abs(_current[_currentCount - 1].Time - elapsed) < 1e-10)
        {
            _current[_currentCount - 1] = new GhostSample(elapsed, pose);
            return;
        }
        if (_currentCount == MaximumSamples)
        {
            _capacityExceeded = true;
            return;
        }
        _current[_currentCount++] = new GhostSample(elapsed, pose);
    }

    private CarSnapshot Playback(double lapTime)
    {
        // A slower current lap holds the actual last pose; it never wraps a fabricated replay.
        var time = Math.Clamp(lapTime, 0, _bestDuration);
        var low = 0;
        var high = _bestCount - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (_best[middle].Time <= time)
                low = middle;
            else
                high = middle - 1;
        }
        var a = _best[low];
        var b = _best[Math.Min(low + 1, _bestCount - 1)];
        var alpha = b.Time > a.Time ? Math.Clamp((time - a.Time) / (b.Time - a.Time), 0, 1) : 0;
        return GhostPose.Interpolate(a.Pose, b.Pose, alpha);
    }

    private readonly record struct GhostSample(double Time, GhostPose Pose);
}

internal readonly record struct GhostPose(
    double X, double Y, double Z, double Yaw, double Pitch, double Roll, double Speed,
    double Steer, double Boost, double Damage, bool Grounded, bool Boosting,
    double FL, double FR, double RL, double RR)
{
    public static CarSnapshot Interpolate(in GhostPose a, in GhostPose b, double alpha) => new(
        Lerp(a.X, b.X, alpha), Lerp(a.Y, b.Y, alpha), Lerp(a.Z, b.Z, alpha),
        AngleLerp(a.Yaw, b.Yaw, alpha), AngleLerp(a.Pitch, b.Pitch, alpha), AngleLerp(a.Roll, b.Roll, alpha),
        Lerp(a.Speed, b.Speed, alpha), Math.Clamp(Lerp(a.Steer, b.Steer, alpha), -1, 1),
        Unit(a.Boost, b.Boost, alpha), Unit(a.Damage, b.Damage, alpha),
        alpha < 0.5 ? a.Grounded : b.Grounded, alpha < 0.5 ? a.Boosting : b.Boosting,
        [Unit(a.FL, b.FL, alpha), Unit(a.FR, b.FR, alpha), Unit(a.RL, b.RL, alpha), Unit(a.RR, b.RR, alpha)]);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double Unit(double a, double b, double t) => Math.Clamp(Lerp(a, b, t), 0, 1);
    private static double AngleLerp(double a, double b, double t) => a + Math.IEEERemainder(b - a, Math.Tau) * t;
}
