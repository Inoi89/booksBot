using System.IO.Compression;
using System.Text;
using booksBot.Core.Models;
using booksBot.Core.Services;
using booksBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace booksBot.Tests;

public sealed class BookServiceIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"boxbot-service-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task IndexSearchDownloadAndTelegramCache_WorkTogether()
    {
        Directory.CreateDirectory(_root);
        var inpxPath = Path.Combine(_root, "collection.inpx");
        var archivePath = Path.Combine(_root, "fb2-806000-806999.zip");
        CreateInpx(inpxPath);
        CreateBookArchive(archivePath);

        var settings = Options.Create(new AppSettings
        {
            BotToken = "test-token",
            InpxCollectionPath = inpxPath,
            ArchivesPath = _root,
            LiteDbPath = Path.Combine(_root, "books.db"),
            StateDbPath = Path.Combine(_root, "state.db"),
            TempPath = Path.Combine(_root, "temp")
        });
        var service = new BookService(settings, NullLogger<BookService>.Instance);

        await service.LoadCollectionAsync();

        var byAuthor = await service.SearchAsync("Маркус Касс", BookSearchField.Author);
        Assert.Equal(2, byAuthor.Books.Count);
        Assert.All(byAuthor.Books, book => Assert.Equal("Касс Маркус", book.Authors[0].DisplayName));

        var universal = await service.SearchAsync("Касс Империя");
        var empire = Assert.Single(universal.Books);
        Assert.Equal("806581", empire.LibId);
        Assert.Equal("Святоша", empire.Series);

        var missingSeries = await service.SearchAsync("Тестов", BookSearchField.Author);
        Assert.Equal("806583", Assert.Single(missingSeries.Books).LibId);

        await using (var download = await service.PrepareBookFileAsync("806581"))
        await using (var stream = download.OpenRead())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            var content = await reader.ReadToEndAsync();
            Assert.Contains("Империя храмов", content);
        }

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "temp"), "*.fb2"));

        await service.SaveTelegramFileIdAsync("806581", "telegram-file-id");
        Assert.Equal("telegram-file-id", await service.GetTelegramFileIdAsync("806581"));

        await service.LoadCollectionAsync();
        Assert.Equal("Империя храмов", (await service.GetBookAsync("806581"))?.Title);
    }

    private static void CreateInpx(string path)
    {
        var records = new[]
        {
            CreateRecord("Касс,Маркус", "Империя храмов", "Святоша", "806581", "1"),
            CreateRecord("Касс,Маркус", "Путь защитника", "Святоша", "806582", "2"),
            CreateRecord("Тестов,Оченьдлинный", new string('А', 2_000), string.Empty, "806583", string.Empty)
        };

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("books.inp");
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (var record in records)
        {
            writer.WriteLine(record);
        }
    }

    private static string CreateRecord(string author, string title, string series, string id, string order)
    {
        var parts = new string[13];
        parts[0] = author;
        parts[1] = "sf_fantasy";
        parts[2] = title;
        parts[3] = series;
        parts[5] = id;
        parts[10] = order;
        parts[12] = "ru";
        return string.Join('\u0004', parts);
    }

    private static void CreateBookArchive(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteBook(archive, "806581", "Империя храмов");
        WriteBook(archive, "806582", "Путь защитника");
        WriteBook(archive, "806583", "Очень длинное название");
    }

    private static void WriteBook(ZipArchive archive, string id, string title)
    {
        var entry = archive.CreateEntry($"{id}.fb2");
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write($"<FictionBook><description><title-info><book-title>{title}</book-title></title-info></description></FictionBook>");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
