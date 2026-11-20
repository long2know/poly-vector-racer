namespace StuntRacer.Core.Stunts;

internal readonly record struct StuntVector(double X, double Y, double Z)
{
    public static readonly StuntVector Up = new(0, 1, 0);
    public static readonly StuntVector Gravity = new(0, -9.81, 0);

    public double LengthSquared => Dot(this, this);
    public double Length => Math.Sqrt(LengthSquared);
    public StuntVector Unit => Length > 1e-12 ? this / Length : default;

    public static StuntVector operator +(StuntVector a, StuntVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static StuntVector operator -(StuntVector a, StuntVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static StuntVector operator -(StuntVector a) => new(-a.X, -a.Y, -a.Z);
    public static StuntVector operator *(StuntVector a, double b) => new(a.X * b, a.Y * b, a.Z * b);
    public static StuntVector operator *(double b, StuntVector a) => a * b;
    public static StuntVector operator /(StuntVector a, double b) => a * (1 / b);
    public static double Dot(StuntVector a, StuntVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static StuntVector Cross(StuntVector a, StuntVector b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static StuntVector Lerp(StuntVector a, StuntVector b, double t) => a + (b - a) * t;
    public StuntVector OnPlane(StuntVector normal) => this - normal * Dot(this, normal);

    public StuntVector Rotate(StuntVector axis, double radians)
    {
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return this * cos + Cross(axis, this) * sin + axis * (Dot(axis, this) * (1 - cos));
    }

    public StuntVector Transport(StuntVector oldNormal, StuntVector newNormal)
    {
        var axis = Cross(oldNormal, newNormal);
        var sin = axis.Length;
        return sin < 1e-12 ? this : Rotate(axis / sin, Math.Atan2(sin, Dot(oldNormal, newNormal)));
    }

    public TrackSurfaceVertex Vertex => new(X, Y, Z);
    public static StuntVector From(TrackSurfaceVertex p) => new(p.X, p.Y, p.Z);
}
