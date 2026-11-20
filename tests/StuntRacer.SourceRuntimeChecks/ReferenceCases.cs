namespace StuntRacer.SourceRuntimeChecks;

internal static class ReferenceCases
{
    public static readonly (int Velocity, int DeltaXZ, int DeltaY)[] ActivePosition =
    [
        (1, 476, 119),
        (-1, -476, -119),
        (0, 0, 0)
    ];

    public static readonly (int Activation, int Reserve, int Subunit, int EnginePower, int BoostFlag)[] Boost =
    [
        (1, 1, 16, 480, 128),
        (2, 1, 15, 480, 128),
        (17, 1, 0, 480, 128),
        (18, 0, 16, 480, 128),
        (19, 0, 16, 240, 0)
    ];

    public static readonly (int Difference, int PreviousDifference, int Response)[] Contact =
    [
        (-1, 0, 0),
        (100, 0, 207),
        (100, 100, 100)
    ];

    public static readonly (int StoredHeight, long StorageHeightQ14, int RoadHeight, int PlayerYAtRoad, double RenderPCY)[] Heights =
    [
        (1, 16384, 4, 1024, 0.25),
        (4, 65536, 16, 4096, 1),
        (12, 196608, 48, 12288, 3)
    ];
}
