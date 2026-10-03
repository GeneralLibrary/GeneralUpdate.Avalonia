using System.Text.Json;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Models;

namespace GeneralUpdate.Avalonia.Android.Services;

public sealed class JsonFileInstallationStore : IInstallationStore
{
    private readonly string _path;

    public JsonFileInstallationStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    internal static string DefaultPath
    {
        get
        {
            var directory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("Application data directory is unavailable. Supply an IInstallationStore.");
            return Path.Combine(directory, "update", "installation.json");
        }
    }

    public async Task<InstallationRecord?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string json;
        try
        {
            json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        var record = JsonSerializer.Deserialize<InstallationRecord>(json)
            ?? throw new InvalidDataException("The installation record is null.");
        Validate(record);
        return record;
    }

    public async Task SaveAsync(InstallationRecord record, CancellationToken cancellationToken = default)
    {
        Validate(record);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, record, cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.Delete(_path);
        return Task.CompletedTask;
    }

    private static void Validate(InstallationRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.TargetVersion) || record.RequestedAt == default ||
            record.ConfirmedAt.HasValue != !string.IsNullOrWhiteSpace(record.InstalledVersion))
            throw new InvalidDataException("The installation record is invalid.");
    }
}
