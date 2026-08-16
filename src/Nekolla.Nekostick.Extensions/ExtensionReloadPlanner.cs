using System.Collections.Immutable;

namespace Nekolla.Nekostick.Extensions;

/// <summary>Names immutable states in the extension replacement plan.</summary>
public enum ExtensionReloadState
{
    /// <summary>A reload request has been formed.</summary>
    Requested,

    /// <summary>The replacement manifest and load are validated.</summary>
    ValidateReplacement,

    /// <summary>The replacement is started before it receives traffic.</summary>
    StartReplacement,

    /// <summary>The previous instance drains existing work.</summary>
    DrainPrevious,

    /// <summary>Handler ownership is switched atomically.</summary>
    SwitchHandlers,

    /// <summary>The previous collectible context is unloaded.</summary>
    UnloadPrevious,

    /// <summary>The replacement is serving.</summary>
    Completed,

    /// <summary>The previous instance remains serving after replacement failure.</summary>
    PreservePrevious
}

/// <summary>Names an immutable intent in the reload plan.</summary>
public enum ExtensionReloadIntent
{
    /// <summary>Validate the replacement manifest and entry load.</summary>
    ValidateReplacement,

    /// <summary>Start the replacement without switching handlers.</summary>
    StartReplacement,

    /// <summary>Stop accepting new work on the previous instance and drain it.</summary>
    DrainPrevious,

    /// <summary>Switch handler ownership to the replacement.</summary>
    SwitchHandlers,

    /// <summary>Unload the previous collectible context.</summary>
    UnloadPrevious,

    /// <summary>Keep the previous instance active.</summary>
    PreservePrevious,

    /// <summary>Commit the replacement as the serving instance.</summary>
    Complete
}

/// <summary>Contains one immutable reload state and intent pair.</summary>
public sealed record ExtensionReloadStep
{
    /// <summary>Creates a reload step.</summary>
    /// <param name="state">The state represented by the step.</param>
    /// <param name="intent">The intent for the state.</param>
    public ExtensionReloadStep(ExtensionReloadState state, ExtensionReloadIntent intent)
    {
        State = state;
        Intent = intent;
    }

    /// <summary>Gets the state represented by this step.</summary>
    public ExtensionReloadState State { get; }

    /// <summary>Gets the host intent for this step.</summary>
    public ExtensionReloadIntent Intent { get; }
}

/// <summary>Describes the immutable replacement plan and its safe failure branch.</summary>
public sealed class ExtensionReloadPlan
{
    internal ExtensionReloadPlan(
        string extensionId,
        SemVersion previousVersion,
        SemVersion replacementVersion,
        ImmutableArray<ExtensionReloadStep> steps,
        ImmutableArray<ExtensionReloadStep> replacementFailureSteps)
    {
        ExtensionId = extensionId;
        PreviousVersion = previousVersion;
        ReplacementVersion = replacementVersion;
        Steps = steps;
        ReplacementFailureSteps = replacementFailureSteps;
    }

    /// <summary>Gets the stable extension identifier.</summary>
    public string ExtensionId { get; }

    /// <summary>Gets the version being replaced.</summary>
    public SemVersion PreviousVersion { get; }

    /// <summary>Gets the replacement version.</summary>
    public SemVersion ReplacementVersion { get; }

    /// <summary>Gets the success path from validation through unload.</summary>
    public ImmutableArray<ExtensionReloadStep> Steps { get; }

    /// <summary>Gets the branch used when replacement load or start fails.</summary>
    public ImmutableArray<ExtensionReloadStep> ReplacementFailureSteps { get; }
}

/// <summary>Represents the safe result of immutable reload planning.</summary>
public sealed class ExtensionReloadPlanResult
{
    private ExtensionReloadPlanResult(
        bool succeeded,
        ExtensionFailureCode failureCode,
        ExtensionReloadPlan? plan)
    {
        Succeeded = succeeded;
        FailureCode = failureCode;
        Plan = plan;
    }

    /// <summary>Gets whether a plan was formed.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the safe planning failure category.</summary>
    public ExtensionFailureCode FailureCode { get; }

    /// <summary>Gets the immutable plan on success.</summary>
    public ExtensionReloadPlan? Plan { get; }

    internal static ExtensionReloadPlanResult Success(ExtensionReloadPlan plan) =>
        new(true, ExtensionFailureCode.None, plan);

    internal static ExtensionReloadPlanResult Failure(ExtensionFailureCode code) =>
        new(false, code, null);
}

/// <summary>Builds reload intents without executing lifecycle or handler operations.</summary>
public static class ExtensionReloadPlanner
{
    /// <summary>Plans a replacement for one stable extension identifier.</summary>
    /// <param name="previous">The currently serving manifest.</param>
    /// <param name="replacement">The candidate replacement manifest.</param>
    /// <returns>An immutable success and failure branch plan.</returns>
    public static ExtensionReloadPlanResult Plan(
        ExtensionManifest? previous,
        ExtensionManifest? replacement)
    {
        if (previous is null || replacement is null)
        {
            return ExtensionReloadPlanResult.Failure(ExtensionFailureCode.InvalidArgument);
        }

        if (!string.Equals(previous.Id, replacement.Id, StringComparison.Ordinal))
        {
            return ExtensionReloadPlanResult.Failure(ExtensionFailureCode.InvalidIdentifier);
        }

        var steps = ImmutableArray.Create(
            new ExtensionReloadStep(ExtensionReloadState.ValidateReplacement, ExtensionReloadIntent.ValidateReplacement),
            new ExtensionReloadStep(ExtensionReloadState.StartReplacement, ExtensionReloadIntent.StartReplacement),
            new ExtensionReloadStep(ExtensionReloadState.DrainPrevious, ExtensionReloadIntent.DrainPrevious),
            new ExtensionReloadStep(ExtensionReloadState.SwitchHandlers, ExtensionReloadIntent.SwitchHandlers),
            new ExtensionReloadStep(ExtensionReloadState.UnloadPrevious, ExtensionReloadIntent.UnloadPrevious),
            new ExtensionReloadStep(ExtensionReloadState.Completed, ExtensionReloadIntent.Complete));
        var failureSteps = ImmutableArray.Create(
            new ExtensionReloadStep(ExtensionReloadState.PreservePrevious, ExtensionReloadIntent.PreservePrevious));

        return ExtensionReloadPlanResult.Success(new ExtensionReloadPlan(
            replacement.Id,
            previous.Version,
            replacement.Version,
            steps,
            failureSteps));
    }
}
