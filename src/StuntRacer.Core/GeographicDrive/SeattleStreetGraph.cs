using System.Text.Json;

namespace StuntRacer.Core.GeographicDrive;

internal sealed class SeattleStreetGraph
{
    private const int Cell = 32;
    private const double MaxShoulder = 9;
    private readonly List<DrivePoint> _points = [];
    private readonly Dictionary<DrivePoint, int> _node = [];
    private readonly List<List<int>> _adjacent = [];
    private readonly List<Link> _links = [];
    private readonly Dictionary<(int X, int Z), List<int>> _index = [];
    private readonly HashSet<int> _connected = [];

    public SeattleStreetGraph(string roadsJson, SeattleTerrain surface)
    {
        if (roadsJson.Length is 0 or > 4 * 1024 * 1024)
            throw new InvalidDataException("Seattle road data is missing or exceeds four MiB.");
        using var document = JsonDocument.Parse(roadsJson, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !StringIs(root, "license", "ODbL-1.0") ||
            !StringIs(root, "attribution", "© OpenStreetMap contributors") ||
            !StringIs(root, "licenseUrl", "https://www.openstreetmap.org/copyright") ||
            !root.TryGetProperty("roads", out var rows) || rows.ValueKind != JsonValueKind.Array ||
            rows.GetArrayLength() is < 1 or > 12000)
            throw new InvalidDataException("Seattle surface-street data or its OpenStreetMap attribution is missing or invalid.");

        var pointCount = 0;
        var seen = new HashSet<(int, int)>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 4 ||
                row[0].ValueKind != JsonValueKind.Number || !row[0].TryGetInt64(out var id) ||
                id is < 1 or > 9007199254740991 ||
                row[1].ValueKind != JsonValueKind.String || row[2].ValueKind != JsonValueKind.String ||
                row[3].ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Seattle road data has an invalid feature.");
            var kind = row[1].GetString();
            var structure = row[2].GetString();
            var polyline = row[3];
            var count = polyline.GetArrayLength();
            if (kind is not ("local" or "arterial" or "freeway") || structure is not ("surface" or "bridge") ||
                count is < 2 or > 256 || (pointCount += count) > 100_000)
                throw new InvalidDataException("Seattle road data has an invalid class, structure or point count.");

            var bridge = structure == "bridge";
            var supported = !bridge && kind != "freeway";
            var previous = Point(polyline[0], bridge);
            for (var i = 1; i < count; i++)
            {
                var location = Point(polyline[i], bridge);
                var length = previous.DistanceTo(location);
                if (length is <= 0 or > 128.03)
                    throw new InvalidDataException("Seattle road data has an invalid segment length.");
                if (supported && SeattleDriveArea.Bounds.Contains(previous) && SeattleDriveArea.Bounds.Contains(location) &&
                    surface.SegmentIsDry(previous, location, length))
                {
                    var a = Node(previous);
                    var b = Node(location);
                    if (seen.Add(a < b ? (a, b) : (b, a)))
                    {
                        _adjacent[a].Add(_links.Count);
                        _adjacent[b].Add(_links.Count);
                        _links.Add(new(a, b, length, kind == "arterial" ? MaxShoulder : 6.3));
                    }
                }
                previous = location;
            }
        }

        if (!_node.TryGetValue(SeattleDriveArea.Spawn, out var start))
            throw new InvalidDataException("This World Flight package has no supported downtown Seattle street network.");
        var visit = new Queue<int>();
        _connected.Add(start);
        visit.Enqueue(start);
        while (visit.TryDequeue(out var a))
            foreach (var edge in _adjacent[a])
            {
                var b = _links[edge].Other(a);
                if (_connected.Add(b)) visit.Enqueue(b);
            }

        Destinations = Array.AsReadOnly(SeattleDriveArea.Stops
            .Where(stop => _node.TryGetValue(stop.Point, out var node) && _connected.Contains(node)).ToArray());
        if (Destinations.Count < 3)
            throw new InvalidDataException("This World Flight package lacks connected Seattle streets for three scenic stops.");

        for (var i = 0; i < _links.Count; i++)
        {
            var link = _links[i];
            if (!_connected.Contains(link.A)) continue;
            var a = _points[link.A];
            var b = _points[link.B];
            for (var z = AtCell(Math.Min(a.Z, b.Z) - link.Shoulder); z <= AtCell(Math.Max(a.Z, b.Z) + link.Shoulder); z++)
            for (var x = AtCell(Math.Min(a.X, b.X) - link.Shoulder); x <= AtCell(Math.Max(a.X, b.X) + link.Shoulder); x++)
            {
                if (!_index.TryGetValue((x, z), out var list)) _index.Add((x, z), list = []);
                list.Add(i);
            }
        }
    }

    public IReadOnlyList<DriveDestination> Destinations { get; }
    public IEnumerable<double[]> MapStreets =>
        _links.Where(link => _connected.Contains(link.A)).Select(link => new[]
        {
            _points[link.A].X, _points[link.A].Z, _points[link.B].X, _points[link.B].Z
        });

    public bool OnStreet(DrivePoint point) => Closest(point) is not null;

    public DrivePoint[]? Route(DrivePoint start, double heading, DriveDestination destination, bool forwardOnly)
    {
        var match = Closest(start);
        if (match is not { } m) return null;
        var link = _links[m.Edge];
        var goal = _node[destination.Point];
        var queue = new PriorityQueue<(Search State, double Cost), (double Priority, int Order)>();
        var cost = new Dictionary<Search, double>();
        var cameFrom = new Dictionary<Search, Search>();
        var sequence = 0;

        void Enqueue(Search state, double distance, Search? parent = null)
        {
            if (cost.TryGetValue(state, out var existing) && distance >= existing)
                return;
            cost[state] = distance;
            if (parent is { } p) cameFrom[state] = p;
            else cameFrom.Remove(state);
            queue.Enqueue((state, distance), (distance + _points[state.Node].DistanceTo(destination.Point), sequence++));
        }

        void StartToward(int node, int other, double fraction)
        {
            var meters = fraction * link.Length;
            if (meters < 0.02)
            {
                Enqueue(new(node, -1), 0);
                return;
            }
            var angle = Bend(heading, _points[other].HeadingTo(_points[node]));
            if (forwardOnly && angle > 1.3) return;
            Enqueue(new(node, other), meters + DepartureCost(angle));
        }

        StartToward(link.A, link.B, m.Fraction);
        StartToward(link.B, link.A, 1 - m.Fraction);
        while (queue.TryDequeue(out var entry, out _))
        {
            var (current, distance) = entry;
            if (distance != cost[current]) continue;
            if (current.Node == goal)
            {
                var path = new List<DrivePoint> { _points[current.Node] };
                while (cameFrom.TryGetValue(current, out var previous))
                {
                    current = previous;
                    path.Add(_points[current.Node]);
                }
                if (m.Point.DistanceTo(path[^1]) > 0.02) path.Add(m.Point);
                path.Reverse();
                return path.ToArray();
            }
            var position = _points[current.Node];
            var isDeparture = current.From < 0;
            var incoming = isDeparture ? heading : _points[current.From].HeadingTo(position);
            foreach (var edge in _adjacent[current.Node])
            {
                var next = _links[edge];
                var b = next.Other(current.Node);
                if (b == current.From) continue;
                var turn = Bend(incoming, position.HeadingTo(_points[b]));
                if (forwardOnly && turn > (isDeparture ? 1.3 : 2.44)) continue;
                var delay = isDeparture ? DepartureCost(turn) : 18 * Math.Max(0, turn - 0.24);
                Enqueue(new(b, current.Node), distance + next.Length + delay, current);
            }
        }
        return null;
    }

    private Match? Closest(DrivePoint point)
    {
        if (!SeattleDriveArea.Bounds.Contains(point) ||
            !_index.TryGetValue((AtCell(point.X), AtCell(point.Z)), out var links))
            return null;
        Match? best = null;
        var distance = double.PositiveInfinity;
        foreach (var edge in links)
        {
            var link = _links[edge];
            var a = _points[link.A];
            var b = _points[link.B];
            var t = Fraction(point, a, b, link.Length);
            var projected = a.Toward(b, t);
            var next = point.DistanceTo(projected);
            if (next > link.Shoulder || next >= distance) continue;
            best = new Match(edge, t, projected);
            distance = next;
        }
        return best;
    }

    internal static double Fraction(DrivePoint p, DrivePoint a, DrivePoint b, double length) =>
        Math.Clamp(((p.X - a.X) * (b.X - a.X) + (p.Z - a.Z) * (b.Z - a.Z)) / (length * length), 0, 1);
    internal static double Angle(double angle) => Math.IEEERemainder(angle, Math.Tau);
    private static double Bend(double a, double b) => Math.Abs(Angle(a - b));
    private static double DepartureCost(double turn) => turn < 0.22 ? 0 : 19 * turn * turn;
    private static int AtCell(double value) => (int)Math.Floor(value / Cell);

    private int Node(DrivePoint point)
    {
        if (_node.TryGetValue(point, out var existing)) return existing;
        var id = _points.Count;
        _node.Add(point, id);
        _points.Add(point);
        _adjacent.Add([]);
        return id;
    }

    private static bool StringIs(JsonElement element, string property, string value) =>
        element.TryGetProperty(property, out var entry) && entry.ValueKind == JsonValueKind.String &&
        entry.GetString() == value;

    private static DrivePoint Point(JsonElement element, bool bridge)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != (bridge ? 3 : 2))
            throw new InvalidDataException("Seattle road data has an invalid point.");
        static double Coordinate(JsonElement value, double minimum, double maximum) =>
            value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) &&
            double.IsFinite(n) && n >= minimum && n <= maximum
                ? n : throw new InvalidDataException("Seattle road data has an invalid coordinate.");
        if (bridge) _ = Coordinate(element[2], -50, 300);
        return new(Coordinate(element[0], -40000, 40000), Coordinate(element[1], -40000, 40000));
    }

    private readonly record struct Match(int Edge, double Fraction, DrivePoint Point);
    private readonly record struct Search(int Node, int From);
    private readonly record struct Link(int A, int B, double Length, double Shoulder)
    {
        public int Other(int node) => A == node ? B : A;
    }
}
