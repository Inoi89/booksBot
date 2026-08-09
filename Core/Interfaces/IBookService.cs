using booksBot.Core.Models;

namespace booksBot.Core.Interfaces;

public interface IBookService
{
    Task LoadCollectionAsync(CancellationToken cancellationToken = default);
    Task<BookSearchResult> SearchAsync(
        string query,
        BookSearchField field = BookSearchField.All,
        int limit = 200,
        CancellationToken cancellationToken = default);
    Task<BookEntry?> GetBookAsync(string bookId, CancellationToken cancellationToken = default);
    Task<BookEntry?> GetRandomBookAsync(CancellationToken cancellationToken = default);
    Task<BookBlocklistBuildResult> BuildBlocklistAsync(
        string sourcePath,
        string outputPath,
        string reportPath,
        CancellationToken cancellationToken = default);
    Task<BookDownload> PrepareBookFileAsync(string bookId, CancellationToken cancellationToken = default);
    Task<BookPreview> GetBookPreviewAsync(string bookId, CancellationToken cancellationToken = default);
    Task SaveBookPreviewAsync(
        string bookId,
        string annotation,
        bool hasCover,
        string telegramCoverFileId,
        CancellationToken cancellationToken = default);
    Task<string?> GetTelegramFileIdAsync(string bookId, CancellationToken cancellationToken = default);
    Task SaveTelegramFileIdAsync(string bookId, string fileId, CancellationToken cancellationToken = default);
}
