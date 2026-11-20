namespace StuntRacer.Core;

public interface IGameSession
{
    TrackDefinition Track { get; }
    GameSnapshot Snapshot { get; }
    GameSnapshot Advance(double seconds, DriverInput input);
    void Command(string command);
}
