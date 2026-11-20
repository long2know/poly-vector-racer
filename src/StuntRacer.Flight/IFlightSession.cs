namespace StuntRacer.Flight;

public interface IFlightSession
{
    FlightWorldDefinition World { get; }
    FlightSnapshot Snapshot { get; }
    FlightSnapshot Advance(double seconds, FlightInput input);
    void Command(string command);
}
