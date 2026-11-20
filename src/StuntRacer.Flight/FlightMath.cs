namespace StuntRacer.Flight;

// Double precision keeps the authoritative attitude independent of rendering math/float fields.
internal readonly record struct FlightVec(double X, double Y, double Z)
{
    public static FlightVec Up => new(0, 1, 0);
    public static FlightVec Forward => new(0, 0, 1);
    public double LengthSquared => Dot(this, this);
    public double Length => Math.Sqrt(LengthSquared);
    public FlightVector Snapshot => new(X, Y, Z);

    public FlightVec Unit(FlightVec fallback)
    {
        var length = Length;
        return length > 1e-12 && double.IsFinite(length) ? this / length : fallback;
    }

    public FlightVec Limit(double maximum)
    {
        var length = Length;
        return length > maximum ? this * (maximum / length) : this;
    }

    public static FlightVec operator +(FlightVec a, FlightVec b) =>
        new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static FlightVec operator -(FlightVec a, FlightVec b) =>
        new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static FlightVec operator -(FlightVec a) => new(-a.X, -a.Y, -a.Z);
    public static FlightVec operator *(FlightVec a, double b) => new(a.X * b, a.Y * b, a.Z * b);
    public static FlightVec operator /(FlightVec a, double b) => new(a.X / b, a.Y / b, a.Z / b);
    public static double Dot(FlightVec a, FlightVec b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static FlightVec Cross(FlightVec a, FlightVec b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    public static FlightVec Rotate(FlightVec vector, FlightVec axis, double radians)
    {
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return vector * cos + Cross(axis, vector) * sin +
               axis * (Dot(axis, vector) * (1 - cos));
    }
}
