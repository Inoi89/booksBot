using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace booksBot.Infrastructure.Configuration
{
    public class AppSettings
    {
        public string WebSocketUrl { get; set; }
        public string BotToken { get; set; }
        public string InpxCollectionPath { get; set; }
        public string ArchivesPath { get; set; }
        public string LiteDbPath { get; set; } // Новый путь к файлу базы данных
    }

}
