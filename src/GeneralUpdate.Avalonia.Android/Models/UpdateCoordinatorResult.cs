using GeneralUpdate.Avalonia.Android.Enums;

namespace GeneralUpdate.Avalonia.Android.Models;

/// <summary>InstallerLaunched acknowledges only handoff; only reconciliation can report Updated.</summary>
public sealed record UpdateCoordinatorResult
{
    public required Guid OperationId { get; init; }
    public required UpdateCoordinatorStage Stage { get; init; }
    public required UpdateCoordinatorOutcome Outcome { get; init; }
    public UpdateFailureReason FailureReason { get; init; }
    public string? Message { get; init; }
    public PendingUpdateAttempt? PendingUpdate { get; init; }
}
