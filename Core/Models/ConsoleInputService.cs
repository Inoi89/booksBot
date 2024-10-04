using booksBot.Core.Interfaces;
using System;
using System.Threading.Tasks;

namespace booksBot.Core.Services
{
    public class ConsoleInputService : IInputService
    {
        public Task<string> GetInputAsync()
        {
            return Task.Run(() => Console.ReadLine());
        }
    }
}
