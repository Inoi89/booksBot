using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace booksBot.Core.Models
{
    public class CollectionMeta
    {
        public int Id { get; set; } // Уникальный ID для хранения метаданных
        public long Size { get; set; } // Размер файла .inpx
    }
}
