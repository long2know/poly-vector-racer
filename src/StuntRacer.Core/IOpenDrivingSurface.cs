namespace StuntRacer.Core;

internal interface IOpenDrivingSurface
{
    RoadSample Sample(double x, double z, double carY);
}
