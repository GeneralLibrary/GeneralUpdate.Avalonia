namespace GeneralUpdate.Avalonia.Android.Abstractions;

/// <summary>
/// Optional workflow-wide exclusion for coordinators sharing a store. The lease must remain
/// held across read, discovery, download, persistence, and installer handoff, not only store IO.
/// </summary>
public interface IPendingUpdateStoreLeaseProvider
{
    ValueTask<IAsyncDisposable> AcquireLeaseAsync(CancellationToken cancellationToken = default);
}
