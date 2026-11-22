using StuntRacer.Engine;

namespace StuntRacer.Flight;

public sealed record FlightDuelRival(string Name, FlightVector Position, double Heading, double Distance);

public sealed record FlightDuelState(int Score, int ScoreGoal, double TimeRemaining, double ScanRange,
    double CooldownSeconds, bool InRange, bool NextPass, bool? Success, string Status, FlightDuelRival Rival);

public sealed class FlightTagDuel : IFlightSession
{
    private const int TicksPerSecond = 120;
    private const int RunTicks = 32 * TicksPerSecond;
    private const int CooldownTicks = 102;
    private const int Goal = 3;
    private const double ScanRange = 135;
    private const double PassTravel = 480;
    private const double PracticeFloor = 140;
    private const string Briefing = "Keep level for the first pass. Follow the amber glider and tag it three times.";
    private static readonly FlightWorldDefinition DuelWorld = new(
        "Amberline Range", 0,
        Array.AsReadOnly(new FlightIsland[]
        {
            new("Amberline", 160, -2150, 2200, 4550, 16, 0),
            new("Parchment Shoal", -1590, -3180, 440, 980, 8, -0.30),
            new("Sundial Keys", 1870, -1260, 710, 1360, 20, 0.18)
        }),
        Array.AsReadOnly(new LandingSurface[] { new("Amberline Strip", -630, -3790, 72, 980, 16, 0) }),
        new(-630, 18.4, -4210), 0, 2.4);

    private readonly FlightSimulation _flight = new(DuelWorld);
    private long _nextScanTick;
    private FlightVector? _lastTag;
    private int _score;
    private bool? _success;
    private string _status = Briefing;

    public FlightWorldDefinition World => DuelWorld;
    public FlightSnapshot Snapshot => WithDuel(_flight.Snapshot);

    public FlightSnapshot Advance(double seconds, FlightInput input)
    {
        FixedStepClock.ValidateDelta(seconds);
        var ticksLeft = RunTicks - _flight.Snapshot.Tick;
        var snapshot = _flight.Advance(Math.Min(seconds, (double)ticksLeft / TicksPerSecond), input);
        if (_success is null)
        {
            if (snapshot.Tick == RunTicks)
                Finish(false, "Time is up. Launch again to try another route.");
            else if (snapshot.Phase == "crashed" || snapshot.Phase == "flying" && snapshot.Aircraft.Y < PracticeFloor)
                Finish(false, "Below the practice floor. Launch again to stay with the glider.");
        }
        return WithDuel(snapshot);
    }

    public void Command(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command.Trim().ToLowerInvariant())
        {
            case "garage":
            case "recover":
                Reset();
                break;
            case "restart":
                Reset();
                Launch();
                break;
            case "start":
            case "air-start":
                if (_flight.Snapshot.Phase == "ready") Launch();
                break;
            case "scan":
                Scan();
                break;
            default:
                if (_success is null) _flight.Command(command);
                break;
        }
    }

    private void Launch()
    {
        _flight.Command("air-start");
        _flight.Command("gear-toggle");
    }

    private void Reset()
    {
        _flight.Command("garage");
        _nextScanTick = 0;
        _lastTag = null;
        _score = 0;
        _success = null;
        _status = Briefing;
    }

    private void Finish(bool success, string status)
    {
        _success = success;
        _status = status;
        _flight.Command("pause");
    }

    private void Scan()
    {
        var snapshot = _flight.Snapshot;
        if (_success is not null || snapshot.Phase != "flying" || snapshot.Tick < _nextScanTick) return;
        _nextScanTick = snapshot.Tick + CooldownTicks;
        var rival = RivalAt(snapshot);
        if (rival.Distance > ScanRange)
            _status = "Too far away. Close the gap and scan again.";
        else if (!OnNextPass(rival))
            _status = "Pass already tagged. Stay with the glider for the next one.";
        else
        {
            _lastTag = rival.Position;
            _score++;
            if (_score == Goal) Finish(true, "Duel clear. Three clean tags.");
            else _status = $"Tag {_score} recorded. Follow the glider for the next pass.";
        }
    }

    private FlightSnapshot WithDuel(FlightSnapshot snapshot)
    {
        var rival = RivalAt(snapshot);
        var status = _success is null && snapshot.Phase == "paused" ? "Duel paused." : _status;
        return snapshot with
        {
            Phase = _success is null ? snapshot.Phase : "finished",
            Message = status,
            Duel = new(_score, Goal, (double)(RunTicks - snapshot.Tick) / TicksPerSecond, ScanRange,
                Math.Max(0, _nextScanTick - snapshot.Tick) / (double)TicksPerSecond,
                rival.Distance <= ScanRange, OnNextPass(rival), _success, status, rival)
        };
    }

    private bool OnNextPass(FlightDuelRival rival) => _lastTag is not { } last ||
        Distance(last.X - rival.Position.X, last.Y - rival.Position.Y, last.Z - rival.Position.Z) >= PassTravel;

    private static FlightDuelRival RivalAt(FlightSnapshot snapshot)
    {
        // A scripted weave through the fictional range, independent of the player or score.
        const double sway = 185, bendRate = .30, pace = 108, leadInTime = 3.1;
        var time = (double)snapshot.Tick / TicksPerSecond;
        var bend = Math.Max(0, time - 2) * bendRate;
        var leadIn = Math.Exp(-time / leadInTime);
        var position = new FlightVector(18 + sway * (1 - Math.Cos(bend)),
            255 + 1.65 * time + 8 * Math.Sin(time * .40), -3835 + pace * time - 210 * (1 - leadIn));
        var aircraft = snapshot.Aircraft;
        return new("Kite glider", position, Math.Atan2(sway * bendRate * Math.Sin(bend), pace - 210 / leadInTime * leadIn),
            Distance(aircraft.X - position.X, aircraft.Y - position.Y, aircraft.Z - position.Z));
    }

    private static double Distance(double x, double y, double z) => Math.Sqrt(x * x + y * y + z * z);
}
