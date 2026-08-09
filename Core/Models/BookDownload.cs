namespace booksBot.Core.Models;

public sealed class BookDownload : IAsyncDisposable
{
    public BookDownload(string filePath, string fileName)
    {
        FilePath = filePath;
        FileName = fileName;
    }

    public string FilePath { get; }
    public string FileName { get; }
    public long Length => new FileInfo(FilePath).Length;

    public FileStream OpenRead() => new(
        FilePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    public ValueTask DisposeAsync()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (IOException)
        {
            // The periodic temp cleanup will remove a file that is briefly locked.
        }

        return ValueTask.CompletedTask;
    }
}
