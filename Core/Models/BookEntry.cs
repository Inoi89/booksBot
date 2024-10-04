using LiteDB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace booksBot.Core.Models
{
    public class AuthorPart
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string MiddleName { get; set; }
    }

    public class BookEntry
    {
        public string LibId { get; set; }
        public string Title { get; set; }
        public string TitleNormalized { get; set; }
        public string Series { get; set; }
        public int? SeriesOrder { get; set; } 
        public string Language { get; set; } 

        [BsonIgnore]
        public List<AuthorPart> Authors { get; set; } = new List<AuthorPart>();

        public string Genre { get; set; }
    }
}

