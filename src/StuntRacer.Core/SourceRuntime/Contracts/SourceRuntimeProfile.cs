namespace StuntRacer.Core.SourceRuntime;

public static class SourceRuntimeProfile
{
    public const string ProfileId = "amiga-derived-source-compatible";
    public const bool OriginalStockParity = false;

    // Explicit remake-host configuration, not measured original hardware timing.
    public const double ConfiguredCallbackHertz = 50;
    public const int CallbacksPerStep = 6;
    public const double CallbackSeconds = 1 / ConfiguredCallbackHertz;
    public const double StepSeconds = CallbacksPerStep / ConfiguredCallbackHertz;
}
