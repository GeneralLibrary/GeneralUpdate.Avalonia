using System.Security.Cryptography;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Services;

public sealed class Sha256HashValidator : IHashValidator
{
    public async Task<HashValidationResult> ValidateSha256Async(string filePath, string expectedSha256, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return new HashValidationResult
            {
                Success = false,
                State = UpdateState.Failed,
                FailureReason = UpdateFailureReason.InvalidMetadata,
                Message = "Expected SHA256 is empty.",
                FilePath = filePath,
                ExpectedSha256 = expectedSha256
            };
        }

        if (!File.Exists(filePath))
        {
            return new HashValidationResult
            {
                Success = false,
                State = UpdateState.Failed,
                FailureReason = UpdateFailureReason.FileIoError,
                Message = "Downloaded file not found.",
                FilePath = filePath,
                ExpectedSha256 = expectedSha256
            };
        }

        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
            var actual = Convert.ToHexString(hash);
            var expected = Normalize(expectedSha256);
            var success = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

            return new HashValidationResult
            {
                Success = success,
                State = success ? UpdateState.ReadyToInstall : UpdateState.Failed,
                FailureReason = success ? UpdateFailureReason.None : UpdateFailureReason.HashMismatch,
                Message = success ? "SHA256 validation succeeded." : "SHA256 validation failed.",
                FilePath = filePath,
                ActualSha256 = actual,
                ExpectedSha256 = expected
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new HashValidationResult
            {
                Success = false,
                State = UpdateState.Failed,
                FailureReason = UpdateFailureReason.FileIoError,
                Message = "Failed to validate SHA256.",
                FilePath = filePath,
                ExpectedSha256 = Normalize(expectedSha256),
                Exception = ex
            };
        }
    }

    private static string Normalize(string value) => value.Replace("-", string.Empty, StringComparison.Ordinal).Trim();
}
