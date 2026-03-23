using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Abstractions;

public interface IHashValidator
{
    Task<HashValidationResult> ValidateSha256Async(
        string filePath,
        string expectedSha256,
        CancellationToken cancellationToken = default);
}
