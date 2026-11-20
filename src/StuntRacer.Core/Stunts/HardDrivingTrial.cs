namespace StuntRacer.Core.Stunts;

internal sealed class HardDrivingTrial(StuntParkCourse.Course course)
{
    private double _budget = 8 + course.Gates[0].S / 24;
    private int _passed;
    private double _lastBonus;
    private bool? _success;

    public bool Expired(double elapsed) => elapsed >= _budget;

    public bool Pass(double crossingElapsed)
    {
        // Expiry wins a tie; bonuses buy the next authored sector, never revive a late gate.
        if (_success is not null || Expired(crossingElapsed) || _passed == course.Gates.Length)
            return false;
        _passed++;
        _lastBonus = _passed == course.Gates.Length ? 0 :
            4 + (course.Gates[_passed].S - course.Gates[_passed - 1].S) / 28;
        _budget += _lastBonus;
        return true;
    }

    public void Finish(bool success) => _success = success;

    public TrialSnapshot Snapshot(double elapsed) => new(
        Math.Max(0, _budget - elapsed), _passed, course.Gates.Length,
        _passed == course.Gates.Length ? 0 : course.Gates[_passed].S / course.Track.Length,
        _lastBonus, _success, elapsed);
}
