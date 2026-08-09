using LiteDB;

namespace booksBot.Core.Models;

public sealed class BookPreviewCacheEntry
{
    [BsonId]
    public string BookId { get; set; } = string.Empty;
    public string Annotation { get; set; } = string.Empty;
    public bool HasCover { get; set; }
    public string TelegramCoverFileId { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
}
