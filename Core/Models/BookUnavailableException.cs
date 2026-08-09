namespace booksBot.Core.Models;

public sealed class BookUnavailableException(string bookId)
    : FileNotFoundException($"Book {bookId} is unavailable.")
{
    public string BookId { get; } = bookId;
}
