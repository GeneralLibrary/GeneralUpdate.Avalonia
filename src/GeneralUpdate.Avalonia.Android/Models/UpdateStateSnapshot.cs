namespace GeneralUpdate.Avalonia.Android.Models;

public sealed record UpdateStateSnapshot(UpdateState State, UpdateFailureReason FailureReason, string? Message);
