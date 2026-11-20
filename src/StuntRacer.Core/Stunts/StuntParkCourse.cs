namespace StuntRacer.Core.Stunts;

/// <summary>Original XYZ road edges; no original Stunts track data or vehicle code.</summary>
public static class StuntParkCourse
{
    /// <summary>Standalone spatial catalog geometry; use StuntParkSession, not the Modern heightfield model.</summary>
    public static TrackDefinition CreateTrack() => Build().Track;

    internal static Course Build()
    {
        const double y = 12;
        const double width = 11;
        var b = new Builder();
        b.Add(new(-70, y, -185), new(1, 0, 0), 0, width);
        b.Feature("Loop approach");
        b.Line(new(-70, y, 20), 0, width);
        var loopStart = b.Distance;
        b.Feature("Vertical loop");
        b.Samples(640, t =>
        {
            var angle = t * Math.Tau;
            return (new StuntVector(-70, y + 18 * (1 - Math.Cos(angle)),
                20 + 18 * Math.Sin(angle) + 18 * t), new StuntVector(1, 0, 0), 0d, width, true);
        });
        var loopEnd = b.Distance;
        b.Feature("Loop runout");
        b.Line(new(-70, y, 53), 0, width);
        b.Gate("Loop exit", b.Distance - 3);
        b.Line(new(-70, y, 140), 0, width);
        b.Feature("Banked turn");
        b.Turn(-70, 140, 55, 0, y, width);
        b.Gate("Banked turn", b.Distance - 2);
        b.Feature("Bridge climb");
        b.Samples(80, t => (new StuntVector(40, y + 9 * Smooth(t), 140 - 50 * t),
            new StuntVector(-1, 0, 0), Math.PI, width + (5.4 - width) * Smooth(t), true));
        b.Feature("Precision bridge");
        b.Line(new(40, 21, 25), Math.PI, 5.4);
        b.Gate("Precision bridge", b.Distance - 8);
        b.Feature("Bridge descent");
        b.Samples(90, t => (new StuntVector(40, 21 - 9 * Smooth(t), 25 - 60 * t),
            new StuntVector(-1, 0, 0), Math.PI, 5.4 + (width - 5.4) * Smooth(t), true));
        b.Feature("Ramp approach");
        b.Line(new(40, y, -85), Math.PI, width);
        b.Feature("Gap ramp");
        b.Samples(64, t =>
        {
            var d = t * 26;
            var rise = d < 12 ? 0.25 * d * d / 24 : 1.5 + 0.25 * (d - 12);
            return (new StuntVector(40, y + rise, -85 - d),
                new StuntVector(-1, 0, 0), Math.PI, width, true);
        });
        var rampEnd = b.Distance;
        b.Feature("Open gap");
        b.Samples(24, t => (new StuntVector(40, 17, -111 - 16 * t),
            new StuntVector(-1, 0, 0), Math.PI, width, t == 1));
        var landingStart = b.Distance;
        b.Feature("Landing runway");
        b.Line(new(40, 17, -182), Math.PI, width);
        b.Samples(76, t => (new StuntVector(40, 17 - 5 * Smooth(t), -182 - 50 * t),
            new StuntVector(-1, 0, 0), Math.PI, width, true));
        var landingEnd = b.Distance;
        b.Gate("Gap landing", b.Distance - 10);
        b.Feature("Return banking");
        b.Turn(40, -232, 55, Math.PI, y, width);
        b.Gate("Return bend", b.Distance - 2);
        b.Feature("Finish straight");
        b.Line(new(-70, y, -185), Math.Tau, width);
        return b.Finish(loopStart, loopEnd, rampEnd, landingStart, landingEnd);
    }

    private static double Smooth(double t) => t * t * (3 - 2 * t);

    internal sealed record Course(TrackDefinition Track, Feature[] Features, Gate[] Gates,
        double LoopStart, double LoopEnd, double RampEnd, double LandingStart, double LandingEnd)
    {
        public string FeatureAt(double s)
        {
            s = (s % Track.Length + Track.Length) % Track.Length;
            for (var i = Features.Length - 1; i >= 0; i--)
                if (s >= Features[i].S - 1e-8)
                    return Features[i].Name;
            return Features[0].Name;
        }
    }

    internal readonly record struct Feature(string Name, double S);
    internal readonly record struct Gate(string Name, double S, double RecoveryS);

    internal sealed class Builder
    {
        private readonly List<TrackPoint> _points = [];
        private readonly List<TrackSurfaceEdge> _edges = [];
        private readonly List<Feature> _features = [];
        private readonly List<Gate> _gates = [];
        private StuntVector _last;
        public double Distance { get; private set; }

        public void Add(StuntVector center, StuntVector right, double heading, double width, bool road = true)
        {
            if (_points.Count > 0)
                Distance += (center - _last).Length;
            _last = center;
            _points.Add(new(Distance, center.X, center.Y, center.Z, heading,
                Math.Asin(Math.Clamp(right.Y, -1, 1)), width, road));
            _edges.Add(new((center - right * (width / 2)).Vertex, (center + right * (width / 2)).Vertex));
        }

        public void Feature(string name) => _features.Add(new(name, Distance));
        public void Gate(string name, double recoveryS) => _gates.Add(new(name, Distance, recoveryS));

        public void Samples(int count,
            Func<double, (StuntVector Center, StuntVector Right, double Heading, double Width, bool Road)> sample)
        {
            for (var i = 1; i <= count; i++)
            {
                var p = sample((double)i / count);
                Add(p.Center, p.Right, p.Heading, p.Width, p.Road);
            }
        }

        public void Line(StuntVector end, double heading, double width)
        {
            var start = _last;
            var initialWidth = _points[^1].Width;
            Samples((int)Math.Ceiling((end - start).Length / 0.7), t =>
                (StuntVector.Lerp(start, end, t), new StuntVector(Math.Cos(heading), 0, -Math.Sin(heading)),
                    heading, initialWidth + (width - initialWidth) * t, true));
        }

        public void Turn(double x, double z, double radius, double heading, double height, double width,
            double arc = Math.PI, double banking = 0.34)
        {
            var initial = new StuntVector(x, height, z);
            var right = new StuntVector(Math.Cos(heading), 0, -Math.Sin(heading));
            var forward = new StuntVector(Math.Sin(heading), 0, Math.Cos(heading));
            Samples(256, t =>
            {
                var a = arc * t;
                var h = heading + a;
                var signedRadius = radius * Math.Sign(arc);
                var bank = -Math.Sign(arc) * banking * Math.Pow(Math.Sin(Math.PI * t), 2);
                return (initial + right * (signedRadius * (1 - Math.Cos(a))) + forward * (signedRadius * Math.Sin(a)),
                    new StuntVector(Math.Cos(h) * Math.Cos(bank), Math.Sin(bank), -Math.Sin(h) * Math.Cos(bank)),
                    h, width, true);
            });
        }

        public Course Finish(double loopStart, double loopEnd, double rampEnd, double landingStart, double landingEnd,
            string name = "Skyline Loop Park — authored Stunts-inspired challenge", double width = 11)
        {
            // The final sample duplicates the start; the strip closes to row zero, not a zero-area row.
            _points.RemoveAt(_points.Count - 1);
            _edges.RemoveAt(_edges.Count - 1);
            _gates.Add(new("Finish", Distance, 0));
            var track = TrackDefinition.FromSpatialGeometry(name, width, Distance,
                _points.ToArray(), _edges.ToArray());
            return new(track, _features.ToArray(), _gates.ToArray(), loopStart, loopEnd,
                rampEnd, landingStart, landingEnd);
        }
    }
}
