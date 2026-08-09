namespace booksBot.Core.Models;

public enum BookSearchField
{
    All,
    Title,
    Author,
    Series
}

public sealed record BookSearchResult(
    string Query,
    BookSearchField Field,
    IReadOnlyList<BookEntry> Books,
    int MatchedCount,
    bool IsTruncated);
