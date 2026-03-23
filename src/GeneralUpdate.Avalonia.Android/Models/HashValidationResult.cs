namespace GeneralUpdate.Avalonia.Android.Models;

public sealed record HashValidationResult : UpdateOperationResult
{
    public string? ActualSha256 { get; init; }
    public string? ExpectedSha256 { get; init; }
}
