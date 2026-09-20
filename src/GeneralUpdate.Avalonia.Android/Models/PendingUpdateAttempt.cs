using GeneralUpdate.Avalonia.Android.Enums;
using System.Text.Json.Serialization;

namespace GeneralUpdate.Avalonia.Android.Models;

/// <summary>
/// Minimal restart-safe intent. Contains no package, path, URL, credentials, or exception.
/// IntentPersisted is deliberately uncertain: a process can die during installer handoff.
/// </summary>
public sealed record PendingUpdateAttempt
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    public required Guid AttemptId { get; init; }
    public required string OriginalVersion { get; init; }
    public required string TargetVersion { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required PendingUpdatePhase Phase { get; init; }

    public void Validate()
    {
        if (SchemaVersion != 1 || AttemptId == Guid.Empty || CreatedAtUtc == default ||
            !Enum.IsDefined(Phase) || !IsVersionText(OriginalVersion) || !IsVersionText(TargetVersion))
        {
            throw new InvalidDataException("The pending update record is invalid or uses an unsupported schema.");
        }
    }

    private static bool IsVersionText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+' or '_');
}
