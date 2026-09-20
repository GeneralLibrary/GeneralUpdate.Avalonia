using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Abstractions;

/// <summary>
/// App-private durable tracking for one coordinator. Read returns null only when no record exists;
/// corrupt or inaccessible state must throw. Write must atomically replace a complete record,
/// and a failed write/clear must preserve the previous record.
/// Implement IPendingUpdateStoreLeaseProvider to support multiple coordinators/processes;
/// otherwise the host must use exactly one coordinator for this store.
/// </summary>
public interface IPendingUpdateStore
{
    Task<PendingUpdateAttempt?> ReadAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(PendingUpdateAttempt attempt, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
