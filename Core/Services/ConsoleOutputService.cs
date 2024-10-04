using booksBot.Core.Interfaces;
using System;
using System.Threading.Tasks;

namespace booksBot.Core.Services
{
    public class ConsoleOutputService : IOutputService
    {
        public Task WriteMessageAsync(long chatId, string message)
        {
            // Игнорируем chatId, так как он не нужен для вывода в консоль
            Console.WriteLine(message);
            return Task.CompletedTask;
        }

        public Task LogMessageAsync(string message)
        {
            Console.WriteLine(message);
            return Task.CompletedTask;
        }
    }
}
