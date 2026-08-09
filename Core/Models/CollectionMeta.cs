using LiteDB;

namespace booksBot.Core.Models;

public sealed class CollectionMeta
{
    [BsonId]
    public int Id { get; set; } = 1;
    public int SchemaVersion { get; set; }
    public long SourceSize { get; set; }
    public long SourceLastWriteUtcTicks { get; set; }
    public int BookCount { get; set; }
}
