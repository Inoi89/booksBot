using System.Collections.Generic;
using booksBot.Core.Models;

namespace booksBot.Core.Interfaces
{
    public interface IBookService
    {
            Task LoadCollectionAsync();
            Task<List<BookEntry>> SearchBooksByAuthorAsync(string authorName);
            Task<List<BookEntry>> SearchBooksByTitleAsync(string title);
            Task<List<BookEntry>> SearchBooksBySeriesAsync(string seriesName);
            Task<byte[]> GetBookFileAsync(string bookId);
    }
}
