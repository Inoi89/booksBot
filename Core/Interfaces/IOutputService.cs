using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace booksBot.Core.Interfaces
{
    public interface IOutputService
    {
        Task WriteMessageAsync(long chatId, string message);
        Task LogMessageAsync(string message);
    }
}
