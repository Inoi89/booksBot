using booksBot.Core.Interfaces;
using System.Threading.Tasks;
using Telegram.Bot;

public class TelegramOutputService : IOutputService
{
    private readonly ITelegramBotClient _botClient;

    public TelegramOutputService(ITelegramBotClient botClient)
    {
        _botClient = botClient;
    }

    public async Task WriteMessageAsync(long chatId, string message)
    {
        await _botClient.SendTextMessageAsync(chatId, message);
    }

    public Task LogMessageAsync(string message)
    {
        throw new NotImplementedException("Этот метод предназначен для консольного вывода.");
    }
}
