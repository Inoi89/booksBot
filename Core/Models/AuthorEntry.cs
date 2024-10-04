using System.Collections.Generic;

namespace booksBot.Core.Models
{
    public class AuthorEntry
    {
        public int Id { get; set; }
        public string BookLibId { get; set; } // Ссылка на книгу
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string MiddleName { get; set; }
    }


}
