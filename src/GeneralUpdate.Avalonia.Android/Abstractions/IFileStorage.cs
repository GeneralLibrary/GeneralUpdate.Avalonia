namespace GeneralUpdate.Avalonia.Android.Abstractions;

public interface IFileStorage
{
    void EnsureDirectory(string path);
    bool FileExists(string path);
    long GetFileLength(string path);
    void DeleteFile(string path);
    void MoveFile(string sourceFilePath, string destinationFilePath, bool overwrite);
    Stream OpenRead(string filePath);
    Stream OpenWrite(string filePath, bool append);
    Task<string?> ReadAllTextAsync(string filePath, CancellationToken cancellationToken = default);
    Task WriteAllTextAsync(string filePath, string content, CancellationToken cancellationToken = default);
}
