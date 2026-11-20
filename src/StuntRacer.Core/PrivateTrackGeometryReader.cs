using System.Text.Json;

namespace StuntRacer.Core;

public static class PrivateTrackGeometryReader
{
    public static TrackDefinition Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length is 0 or > 16 * 1024 * 1024)
            throw new InvalidDataException("A private track package must contain at most 16 MiB of JSON text.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 48 });
            return ReadPackage(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The private track package contains invalid JSON.", exception);
        }
    }

    private static TrackDefinition ReadPackage(JsonElement root)
    {
        if (Integer(root, "schemaVersion") != 1)
            throw new InvalidDataException("Unsupported private track package version.");
        var compatibility = Object(root, "compatibility");
        if (Text(compatibility, "profileId") != "amiga-experimental" ||
            Text(compatibility, "qualification") != "unqualified-candidate" ||
            Text(compatibility, "dynamics") != "not-implemented" ||
            Property(compatibility, "simulationStepSeconds").ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("Only experimental track geometry is supported. Original Amiga vehicle dynamics are not available.");
        _ = Object(root, "source");
        _ = Object(root, "provenance");
        var track = Object(root, "track");
        var geometry = Object(root, "geometry");
        if (Text(geometry, "kind") != "exact-source-edge-strip" ||
            Text(geometry, "roadLookup") != "geometric-triangles-not-original-integer-sampler")
            throw new InvalidDataException("Unsupported track geometry or road lookup format.");

        var pointRows = Array(track, "points", 3, 20_000);
        var edgeRows = Array(geometry, "edges", 3, 40_000);
        var segmentRows = Array(geometry, "segments", 3, 20_000);
        if (segmentRows.Length != pointRows.Length)
            throw new InvalidDataException("Track points must correspond to the ordered geometry segments.");
        var points = pointRows.Select(p => new TrackPoint(
            Number(p, "s"), Number(p, "x"), Number(p, "y"), Number(p, "z"),
            Number(p, "heading"), Number(p, "bank"), Number(p, "width"), Boolean(p, "road"))).ToArray();
        var sourceEdges = edgeRows.Select(e => new TrackSurfaceEdge(
            Vertex(Object(e, "left")), Vertex(Object(e, "right")))).ToArray();
        var starts = new int[points.Length];
        var ends = new int[points.Length];
        for (var i = 0; i < starts.Length; i++)
        {
            if (Integer(segmentRows[i], "index") != i)
                throw new InvalidDataException("Track segments must have contiguous ordered indices.");
            starts[i] = Integer(segmentRows[i], "startEdgeIndex");
            ends[i] = Integer(segmentRows[i], "endEdgeIndex");
            if ((uint)starts[i] >= (uint)sourceEdges.Length || (uint)ends[i] >= (uint)sourceEdges.Length)
                throw new InvalidDataException("A track segment references a missing edge row.");
        }
        for (var i = 0; i < starts.Length; i++)
        {
            var end = sourceEdges[ends[i]];
            var next = sourceEdges[starts[(i + 1) % starts.Length]];
            if (!Same(end.Left, next.Left) || !Same(end.Right, next.Right))
                throw new InvalidDataException("Discontinuous or gapped edge strips cannot be driven with the prototype sampler.");
        }

        var start = Integer(geometry, "startSegmentIndex");
        if ((uint)start >= (uint)points.Length)
            throw new InvalidDataException("The start segment is outside the track.");
        var length = Number(track, "length");
        for (var i = 0; i < points.Length; i++)
        {
            if (points[i].S < 0 || points[i].S >= length ||
                i == 0 && points[i].S != 0 || i > 0 && points[i].S <= points[i - 1].S)
                throw new InvalidDataException("Source display distances must be ordered from zero within the declared length.");
        }
        var orderedPoints = new TrackPoint[points.Length];
        var orderedEdges = new TrackSurfaceEdge[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var index = (i + start) % points.Length;
            var s = points[index].S - points[start].S + (index < start ? length : 0);
            orderedPoints[i] = points[index] with { S = s };
            orderedEdges[i] = sourceEdges[starts[index]];
            if (Math.Abs(orderedPoints[i].X) > 1800 || Math.Abs(orderedPoints[i].Z) > 1800 ||
                orderedPoints[i].Y is < 0 or > 1000)
                throw new InvalidDataException("Choose a display transform within the prototype's +/-1800 m ground plane and 0..1000 m elevation.");
        }
        try
        {
            return TrackDefinition.FromGeometry(Text(track, "name"), Number(track, "width"), length,
                orderedPoints, orderedEdges);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"The imported geometry is not supported: {exception.Message}", exception);
        }
    }

    private static TrackSurfaceVertex Vertex(JsonElement element) =>
        new(Number(element, "x"), Number(element, "y"), Number(element, "z"));

    private static bool Same(TrackSurfaceVertex a, TrackSurfaceVertex b) =>
        Math.Abs(a.X - b.X) <= 1e-7 && Math.Abs(a.Y - b.Y) <= 1e-7 && Math.Abs(a.Z - b.Z) <= 1e-7;

    private static JsonElement Property(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
            throw new InvalidDataException($"The private track package is missing '{name}'.");
        return value;
    }

    private static JsonElement Object(JsonElement parent, string name)
    {
        var value = Property(parent, name);
        return value.ValueKind == JsonValueKind.Object ? value
            : throw new InvalidDataException($"'{name}' must be an object.");
    }

    private static JsonElement[] Array(JsonElement parent, string name, int min, int max)
    {
        var value = Property(parent, name);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < min || value.GetArrayLength() > max)
            throw new InvalidDataException($"'{name}' must contain {min}..{max} rows.");
        return value.EnumerateArray().ToArray();
    }

    private static string Text(JsonElement parent, string name)
    {
        var value = Property(parent, name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"'{name}' must contain text.");
        return value.GetString()!;
    }

    private static double Number(JsonElement parent, string name)
    {
        var value = Property(parent, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new InvalidDataException($"'{name}' must be a finite number.");
        return number;
    }

    private static int Integer(JsonElement parent, string name)
    {
        var value = Property(parent, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new InvalidDataException($"'{name}' must be an integer.");
        return number;
    }

    private static bool Boolean(JsonElement parent, string name) =>
        Property(parent, name).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"'{name}' must be true or false.")
        };
}
