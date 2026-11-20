namespace StuntRacer.Core.SourceRuntime;

/// <summary>Runtime identity and explicit candidate differences, not caller-set certification.</summary>
public sealed record SourceSimulationMetadata(
    string ProfileId,
    bool OriginalStockParity,
    string Reference,
    string ContactModel,
    string RoadSampler,
    string SteeringHeading,
    int ConfiguredCallbackHertz,
    int CallbacksPerStep,
    double SimulationStepSeconds,
    string InitialClockPhase,
    string InputAdapter,
    string RaceFlow,
    int ExternalSineEntryCount,
    int PresentationBodyOriginLiftPcUnits,
    bool DuplicateRearContactForVisualWheelsOnly);
