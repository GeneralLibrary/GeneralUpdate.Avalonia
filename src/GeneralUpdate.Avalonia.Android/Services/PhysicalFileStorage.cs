using GeneralUpdate.Avalonia.Android.Abstractions;

namespace GeneralUpdate.Avalonia.Android.Services;

public sealed class PhysicalFileStorage : IFileStorage
{
    public void EnsureDirectory(string path) => Directory.CreateDirectory(path);

    public bool FileExists(string path) => File.Exists(path);

    public long GetFileLength(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : 0;
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void MoveFile(string sourceFilePath, string destinationFilePath, bool overwrite)
    {
        File.Move(sourceFilePath, destinationFilePath, overwrite);
    }

    public Stream OpenRead(string filePath) => new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

    public Stream OpenWrite(string filePath, bool append)
    {
        var mode = append ? FileMode.Append : FileMode.Create;
        return new FileStream(filePath, mode, FileAccess.Write, FileShare.None);
    }

    public Task<string?> ReadAllTextAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return File.Exists(filePath)
            ? File.ReadAllTextAsync(filePath, cancellationToken)
            : Task.FromResult<string?>(null);
    }

    public Task WriteAllTextAsync(string filePath, string content, CancellationToken cancellationToken = default)
        => File.WriteAllTextAsync(filePath, content, cancellationToken);
}
