using System.Text.Json;
using System.Text.Json.Serialization;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Services;

/// <summary>
/// Stores an intent in a host-selected app-private file (not a cache or shared downloads directory).
/// Atomic same-directory replacement protects the previous record if writing fails.
/// Cooperating coordinators hold an exclusive file lease for the entire workflow.
/// Flush and rename protect against process interruption; directory fsync/power-loss durability
/// is not guaranteed. Do not remove the persistent .lock file while this store is in use.
/// </summary>
public sealed class JsonPendingUpdateStore : IPendingUpdateStore, IPendingUpdateStoreLeaseProvider
{
    private const int MaximumRecordBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };
    private readonly string _filePath;

    public JsonPendingUpdateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public async ValueTask<IAsyncDisposable> AcquireLeaseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Keep the lock inode stable: deleting it could let another process lock a new file.
                return new FileStream(_filePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<PendingUpdateAttempt?> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var file = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaximumRecordBytes)
                throw new InvalidDataException("The pending update record is too large.");
            var attempt = await JsonSerializer.DeserializeAsync<PendingUpdateAttempt>(file, JsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("The pending update record is empty.");
            attempt.Validate();
            return attempt;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The pending update record is corrupt.", ex);
        }
    }

    public async Task WriteAsync(PendingUpdateAttempt attempt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        attempt.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, attempt, JsonOptions, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(_filePath);
        return Task.CompletedTask;
    }
}
