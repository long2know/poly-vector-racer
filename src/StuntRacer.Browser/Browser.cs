using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using StuntRacer.Core;
using StuntRacer.Core.SourceRuntime;
using StuntRacer.Core.City;
using StuntRacer.Core.Stunts;
using StuntRacer.Flight;
using StuntRacer.FlightData;
using StuntRacer.FlightOriginal;
using System.Text;

namespace StuntRacer.Browser;

[SupportedOSPlatform("browser")]
public static partial class Browser
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static IGameSession Game = new GameSimulation();
    private static bool importedGeometry;
    private static SourceAmigaPackage? sourcePackage;
    private static bool sourceTrackActive;
    private static bool classicPhysics;
    private static string? authoredTrackId = "foundry";
    private static IFlightSession? flight;
    private static FlightDiskPackage? originalFlightPackage;
    private static string experience = "racing";
    private static WorldFlightElevation worldElevation = new();

    public static void Main() { }

    [JSExport]
    public static string Initialize() => flight is not null
        ? JsonSerializer.Serialize(new { kind = "flight", flight.World.Name, world = flight.World }, Json)
        : Game is CityDrivingSession city
            ? JsonSerializer.Serialize(new { kind = "city", city.World.Name, track = Game.Track, world = city.World }, Json)
            : JsonSerializer.Serialize(Game.Track, Json);

    [JSExport]
    public static string SelectExperience(string mode)
    {
        RequireTrackChangeBoundary();
        IGameSession next = mode switch
        {
            "free-roam" or "city-blitz" or "city-circuit" => new CityDrivingSession(mode),
            "hard-driving" => StuntParkSession.CreateHardDriving(),
            "stunts" => new StuntParkSession(),
            _ => throw new ArgumentException($"Unknown driving experience '{mode}'.", nameof(mode))
        };
        Game = next;
        flight = null;
        experience = mode;
        importedGeometry = sourceTrackActive = classicPhysics = false;
        authoredTrackId = null;
        return Initialize();
    }

    [JSExport]
    public static string SelectFlight()
    {
        RequireTrackChangeBoundary();
        flight = new FlightSimulation();
        experience = "flight";
        return Initialize();
    }

    [JSExport]
    public static void AddWorldTerrainTile(int level, int x, int z, string base64)
    {
        if (base64.Length != ((WorldFlightElevation.TileLength + 2) / 3) * 4)
            throw new InvalidDataException("World Flight elevation tile has invalid encoded size.");
        worldElevation.AddTile(level, x, z, Convert.FromBase64String(base64));
    }

    [JSExport]
    public static void RemoveWorldTerrainTile(int level, int x, int z) => worldElevation.RemoveTile(level, x, z);

    [JSExport]
    public static string SelectWorldFlight(string region, string destinationJson, string? runwayJson, string seedTiles)
    {
        RequireTrackChangeBoundary();
        if (region is not ("Lower 48" or "Alaska" or "Hawaii") || destinationJson.Length > 2048 || runwayJson?.Length > 2048)
            throw new InvalidDataException("World Flight departure metadata is invalid.");
        var destination = JsonSerializer.Deserialize<WorldFlightDestination>(destinationJson, Json)
            ?? throw new InvalidDataException("World Flight has no departure point.");
        var runway = runwayJson is null ? null : JsonSerializer.Deserialize<LandingSurface>(runwayJson, Json)
            ?? throw new InvalidDataException("World Flight runway metadata is invalid.");
        const int base64Length = ((WorldFlightElevation.TileLength + 2) / 3) * 4;
        if (seedTiles.Length is 0 or > 16 * (base64Length + 1))
            throw new InvalidDataException("World Flight has an invalid initial terrain window.");
        var elevation = new WorldFlightElevation();
        foreach (var tile in seedTiles.Split(';'))
            elevation.AddTile(Convert.FromBase64String(tile));
        var world = FlightWorldDefinition.CreateGeographic(region, destination, runway, elevation);
        flight = new FlightSimulation(world);
        worldElevation = elevation;
        experience = "flight-world";
        return Initialize();
    }

    [JSExport]
    public static string SelectOriginalFlight()
    {
        RequireTrackChangeBoundary();
        var package = originalFlightPackage ?? throw new InvalidOperationException("Load the original flight disk/package first.");
        flight = new OriginalFlightSession(package);
        experience = "flight-original";
        return Initialize();
    }

    [JSExport]
    public static string LoadFlightPackage(string json)
    {
        RequireTrackChangeBoundary();
        if (Encoding.UTF8.GetByteCount(json) > FlightPackageSerializer.MaximumPackageBytes)
            throw new InvalidDataException("The original-flight package exceeds sixteen MiB.");
        return InstallOriginalFlight(FlightPackageSerializer.ReadPackage(Encoding.UTF8.GetBytes(json)));
    }

    [JSExport]
    public static string LoadFlightDisk(string base64, string fileName)
    {
        RequireTrackChangeBoundary();
        if (base64.Length > 4 * ((FlightDiskReader.MaximumInputBytes + 2) / 3))
            throw new InvalidDataException("The original-flight input exceeds two MiB.");
        return InstallOriginalFlight(FlightDiskReader.Read(Convert.FromBase64String(base64), new(fileName)));
    }

    private static string InstallOriginalFlight(FlightDiskPackage package)
    {
        var next = new OriginalFlightSession(package);
        originalFlightPackage = package;
        flight = next;
        experience = "flight-original";
        return Initialize();
    }

    [JSExport]
    public static string SelectRacing()
    {
        RequireTrackChangeBoundary();
        if (flight is null) return Initialize();
        var reset = Game.Track.IsSpatial || Game is CityDrivingSession;
        Game = new GameSimulation(reset ? null : Game.Track);
        classicPhysics = false;
        flight = null;
        experience = "racing";
        if (reset) { authoredTrackId = "foundry"; importedGeometry = sourceTrackActive = false; }
        return Initialize();
    }

    [JSExport]
    public static string AuthoredTracks() => JsonSerializer.Serialize(AuthoredTrackCatalog.Entries, Json);

    [JSExport]
    public static string SelectAuthoredTrack(string id)
    {
        RequireTrackChangeBoundary();
        var track = AuthoredTrackCatalog.Create(id);
        Game = new GameSimulation(track);
        flight = null;
        importedGeometry = false;
        sourceTrackActive = false;
        classicPhysics = false;
        authoredTrackId = id;
        experience = "racing";
        return Initialize();
    }

    [JSExport]
    public static string Frame(double seconds, double steer, double throttle, double brake, bool boost)
    {
        if (flight is not null) throw new InvalidOperationException("Use flight controls for the active aircraft.");
        return DrivingState(Game.Advance(seconds, new DriverInput(steer, throttle, brake, boost)));
    }

    [JSExport]
    public static string FlightFrame(double seconds, double roll, double pitch, double yaw,
        double throttle, bool afterburner, bool brake)
    {
        var session = flight ?? throw new InvalidOperationException("Select the Flight activity before flying.");
        return FlightState(session.Advance(seconds, new FlightInput(roll, pitch, yaw, throttle, afterburner, brake)));
    }

    [JSExport]
    public static void Command(string command)
    {
        if (flight is not null) flight.Command(command);
        else Game.Command(command);
    }

    [JSExport]
    public static string Observe() => flight is null ? DrivingState(Game.Snapshot)
        : FlightState(flight.Snapshot);

    private static string DrivingState(GameSnapshot snapshot)
    {
        if (Game is not (CityDrivingSession or StuntParkSession))
            return JsonSerializer.Serialize(snapshot, Json);
        return JsonSerializer.Serialize(new
        {
            snapshot.Player, snapshot.PaceCar, snapshot.Phase, snapshot.Time, snapshot.Lap, snapshot.Laps,
            snapshot.LapTime, snapshot.BestLap, snapshot.Progress, snapshot.Countdown, snapshot.Impact,
            snapshot.Recoveries, snapshot.Alpha, snapshot.Tick, snapshot.Message, snapshot.Race, snapshot.Trial,
            snapshot.Ghost, city = Game is CityDrivingSession city ? city.Activity : null,
            stunt = Game is StuntParkSession stunt ? stunt.Challenge : null
        }, Json);
    }

    private static string FlightState(FlightSnapshot snapshot)
    {
        var aircraft = snapshot.Aircraft;
        return JsonSerializer.Serialize(new
        {
            player = new CarSnapshot(aircraft.X, aircraft.Y, aircraft.Z, aircraft.Yaw, aircraft.Pitch,
                aircraft.Roll, aircraft.Speed, 0, aircraft.Fuel, aircraft.Crashed ? 1 : 0,
                aircraft.OnGround, aircraft.Afterburner, [0.5, 0.5, 0.5, 0.5]),
            paceCar = (CarSnapshot?)null,
            phase = snapshot.Phase switch { "flying" => "racing", "crashed" => "finished", _ => snapshot.Phase },
            snapshot.Time,
            lap = 1, laps = 1, lapTime = snapshot.Time, bestLap = 0, progress = 0,
            snapshot.Countdown, impact = aircraft.Crashed ? 1 : 0,
            snapshot.Recoveries, snapshot.Alpha, snapshot.Tick, snapshot.Message,
            race = (RaceSnapshot?)null, trial = (TrialSnapshot?)null, ghost = (GhostSnapshot?)null,
            flight = snapshot,
            terrainWaiting = flight is FlightSimulation { TerrainWaiting: true }
        }, Json);
    }

    [JSExport]
    public static string LoadPrivateGeometry(string json)
    {
        RequireTrackChangeBoundary();
        var track = PrivateTrackGeometryReader.Read(json);
        Game = new GameSimulation(track);
        flight = null;
        importedGeometry = true;
        sourceTrackActive = false;
        classicPhysics = false;
        authoredTrackId = null;
        experience = "racing";
        return Initialize();
    }

    [JSExport]
    public static string ResetAuthoredTrack()
    {
        return SelectAuthoredTrack("foundry");
    }

    [JSExport]
    public static string LoadSourcePackage(string json, bool classic)
    {
        RequireTrackChangeBoundary();
        var package = SourceAmigaPackageReader.Read(json);
        IGameSession next = classic ? SourceAmigaSimulation.Create(package) : new GameSimulation(package.Track);
        sourcePackage = package;
        Game = next;
        flight = null;
        sourceTrackActive = true;
        classicPhysics = classic;
        importedGeometry = false;
        authoredTrackId = null;
        experience = "racing";
        return Initialize();
    }

    [JSExport]
    public static string SetSourcePhysics(bool classic)
    {
        RequireTrackChangeBoundary();
        var package = sourcePackage ?? throw new InvalidOperationException("Load a source-data package before selecting Classic physics.");
        IGameSession next = classic ? SourceAmigaSimulation.Create(package) : new GameSimulation(package.Track);
        Game = next;
        flight = null;
        sourceTrackActive = true;
        classicPhysics = classic;
        importedGeometry = false;
        authoredTrackId = null;
        experience = "racing";
        return Initialize();
    }

    [JSExport]
    public static string DescribeCompatibility() => JsonSerializer.Serialize(new
    {
        experience,
        vehicleMode = flight is null ? "racing" : "flight",
        physicsProfile = flight is OriginalFlightSession ? "amiga-flight-source-compatible" :
            flight?.World.Geography is not null ? "authored-flight-world" :
            flight is not null ? "authored-flight" : Game is CityDrivingSession ? "authored-city-driving" :
            Game is StuntParkSession ? "authored-spatial-stunt-contacts" :
            classicPhysics ? "amiga-derived-source-compatible" : "authored",
        originalPhysicsAvailable = false,
        originalStockParity = false,
        sourcePackageLoaded = sourcePackage is not null,
        sourcePhysicsActive = flight is null && classicPhysics,
        originalFlightDataAvailable = originalFlightPackage is not null,
        flightSourceActive = flight is OriginalFlightSession,
        originalFlightIdentity = originalFlightPackage is null ? null : new
        {
            executableSha256 = originalFlightPackage.Source.Executable.Sha256,
            profile = originalFlightPackage.Compatibility.Profile,
            originalFlightPackage.Compatibility.RequiredTablesValidated,
            originalFlightPackage.Compatibility.ContactGeometryValidated,
            originalFlightPackage.Compatibility.CleanExecutableIdentityVerified,
            contactProfile = flight is OriginalFlightSession original ? original.ContactProfile : null
        },
        authoredTrackId = flight is null ? authoredTrackId : null,
        simulationStepSeconds = flight is OriginalFlightSession ? OriginalFlightSession.StepSeconds :
            flight is null && classicPhysics ? sourcePackage!.SimulationStepSeconds : 1.0 / 120,
        contactSpanMetres = flight is not null ? 0 : classicPhysics ? 128 * sourcePackage!.DisplayMetresPerSourceUnit : 2.12,
        contactAxleDistanceMetres = flight is not null ? 0 : classicPhysics ? 256 * sourcePackage!.DisplayMetresPerSourceUnit : 2.52,
        trackData = flight is OriginalFlightSession ? "amiga-flight-user-disk" :
            flight?.World.Geography is not null ? "streamed-elevation" :
            flight is not null ? "authored-flight-world" : Game is CityDrivingSession ? "authored-city-world" :
            Game is StuntParkSession ? "authored-spatial-stunt-park" : sourceTrackActive ? "amiga-derived-reference-data" :
            importedGeometry ? "private-source-format-geometry" : "authored",
        qualification = flight is OriginalFlightSession ? "verified-user-disk-original-flight-rules-enhanced-renderer" :
            flight?.World.Geography is not null ? "authored-flight-with-geographic-terrain" :
            flight is not null ? "authored-flight-prototype-not-original-port" :
            Game is CityDrivingSession ? "authored-open-world-city-not-midtown-game-code" :
            Game is StuntParkSession ? "authored-stunt-contacts-not-original-stunts-engine" :
            sourceTrackActive || importedGeometry ? "unqualified-candidate" : "authored-approximation"
    }, Json);

    private static void RequireTrackChangeBoundary()
    {
        var phase = flight?.Snapshot.Phase ?? Game.Snapshot.Phase;
        if (phase is not ("ready" or "paused" or "finished" or "crashed"))
            throw new InvalidOperationException("Pause the run before changing track data.");
    }
}
