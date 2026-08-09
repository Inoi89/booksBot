using booksBot.Core.Services;

namespace booksBot.Tests;

public sealed class ArchiveCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"boxbot-archive-tests-{Guid.NewGuid():N}");

    public ArchiveCatalogTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void FindArchive_PrefersZipWhenBothFormatsCoverTheBook()
    {
        var sevenZip = Path.Combine(_directory, "fb2-806000-806999.7z");
        var zip = Path.Combine(_directory, "fb2-806000-806999.zip");
        File.WriteAllBytes(sevenZip, []);
        File.WriteAllBytes(zip, []);

        var catalog = new ArchiveCatalog(_directory);

        Assert.Equal(zip, catalog.FindArchive(806581));
    }

    [Theory]
    [InlineData("fb2-806000-806999.7z", 806000, 806999)]
    [InlineData("flibusta_fb2-1-999.zip", 1, 999)]
    public void TryParse_ReadsRangeFromFileName(string fileName, int start, int end)
    {
        var range = ArchiveCatalog.TryParse(Path.Combine(_directory, fileName));

        Assert.NotNull(range);
        Assert.Equal(start, range.Start);
        Assert.Equal(end, range.End);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
