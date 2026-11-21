using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using StuntRacer.Core.GeographicDrive;
using StuntRacer.Flight;

namespace StuntRacer.Browser;

public static partial class Browser
{
    [JSExport]
    public static string SeattleDriveAreaInfo() => JsonSerializer.Serialize(new
    {
        SeattleDriveArea.Spawn, SeattleDriveArea.Bounds, SeattleDriveArea.Tiles
    }, Json);

    [JSExport]
    public static string SelectSeattleDrive(string roadJson, string terrainSeed)
    {
        RequireTrackChangeBoundary();
        const int base64Length = ((WorldFlightElevation.TileLength + 2) / 3) * 4;
        var required = SeattleDriveArea.Tiles;
        if (terrainSeed.Length != required.Count * (base64Length + 1) - 1)
            throw new InvalidDataException("Seattle Drive requires all ten detailed elevation tiles.");
        var seeds = terrainSeed.Split(';');
        if (seeds.Length != required.Count)
            throw new InvalidDataException("Seattle Drive terrain has the wrong tile count.");
        var terrain = new WorldFlightElevation();
        for (var i = 0; i < required.Count; i++)
        {
            var tile = required[i];
            terrain.AddTile(tile.Level, tile.X, tile.Z, Convert.FromBase64String(seeds[i]));
        }
        var next = new SeattleDrivingSession(roadJson, terrain.Sample);
        Game = next;
        flight = null;
        experience = SeattleDriveArea.Id;
        worldElevation = new WorldFlightElevation();
        importedGeometry = sourceTrackActive = classicPhysics = false;
        authoredTrackId = null;
        return Initialize();
    }

    [JSExport]
    public static string SeattleDrivePlan() =>
        JsonSerializer.Serialize((Game as SeattleDrivingSession)?.Plan, Json);

    private static string SeattleDriveDefinition(SeattleDrivingSession drive) => JsonSerializer.Serialize(new
    {
        kind = SeattleDriveArea.Id,
        name = SeattleDriveArea.Name,
        world = new
        {
            name = SeattleDriveArea.Name, coverage = SeattleDriveArea.Coverage, SeattleDriveArea.Spawn, SeattleDriveArea.Bounds,
            drive.Destinations, streets = drive.MapStreets
        }
    }, Json);
}
