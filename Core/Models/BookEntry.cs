using LiteDB;

namespace booksBot.Core.Models;

public sealed class AuthorPart
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string MiddleName { get; set; } = string.Empty;

    [BsonIgnore]
    public string DisplayName => string.Join(
        ' ',
        new[] { LastName, FirstName, MiddleName }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed class BookEntry
{
    [BsonId]
    public string LibId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string TitleNormalized { get; set; } = string.Empty;
    public string Series { get; set; } = string.Empty;
    public string SeriesNormalized { get; set; } = string.Empty;
    public int? SeriesOrder { get; set; }
    public string Language { get; set; } = string.Empty;
    public List<AuthorPart> Authors { get; set; } = [];
    public string AuthorsNormalized { get; set; } = string.Empty;
    public string SearchTextNormalized { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
}
