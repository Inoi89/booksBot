using booksBot.Core.Interfaces;
using System.Threading.Tasks;

// Не используется

public class TelegramInputService : IInputService
{
    private readonly TaskCompletionSource<string> _inputCompletionSource = new TaskCompletionSource<string>();

    public Task<string> GetInputAsync()
    {
        return _inputCompletionSource.Task;
    }

    public void SetInput(string input)
    {
        _inputCompletionSource.TrySetResult(input);
    }
}