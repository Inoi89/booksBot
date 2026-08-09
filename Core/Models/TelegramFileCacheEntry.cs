using LiteDB;

namespace booksBot.Core.Models;

public sealed class TelegramFileCacheEntry
{
    [BsonId]
    public string BookId { get; set; } = string.Empty;
    public string FileId { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
}
