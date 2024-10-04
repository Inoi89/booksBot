using booksBot.Core.Interfaces;
using booksBot.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

// Не используется

namespace booksBot.Core.Services
{
    public class UserInteractionService
    {
        private readonly IBookService _bookService;
        private readonly IOutputService _outputService;
        private readonly IInputService _inputService;

        public UserInteractionService(IBookService bookService, IOutputService outputService, IInputService inputService)
        {
            _bookService = bookService;
            _outputService = outputService;
            _inputService = inputService;
        }

        public async Task StartAsync()
        {
            while (true)
            {
                // Ожидаем, пока пользователь введет запрос
                await _outputService.WriteMessageAsync("Введите запрос для поиска (или введите 'exit' для выхода):");
                var userInput = await _inputService.GetInputAsync();

                if (userInput.Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                // Спрашиваем пользователя, как он хочет выполнить поиск
                await _outputService.WriteMessageAsync("Выберите тип поиска: введите '1' для поиска по автору, '2' для поиска по названию книги, '3' для поиска по серии:");
                var searchTypeInput = await _inputService.GetInputAsync();

                List<BookEntry> searchResults = null;

                switch (searchTypeInput)
                {
                    case "1":
                        // Ищем книги по автору
                        searchResults = await _bookService.SearchBooksByAuthorAsync(userInput);
                        break;
                    case "2":
                        // Ищем книги по названию
                        searchResults = await _bookService.SearchBooksByTitleAsync(userInput);
                        break;
                    case "3":
                        // Ищем книги по серии
                        searchResults = await _bookService.SearchBooksBySeriesAsync(userInput);
                        break;
                    default:
                        await _outputService.WriteMessageAsync("Некорректный выбор. Пожалуйста, попробуйте снова.");
                        continue; // Возвращаемся к началу цикла
                }

                // Проверяем, есть ли результаты
                if (searchResults != null && searchResults.Count > 0)
                {
                    await _outputService.WriteMessageAsync($"Найдено {searchResults.Count} книг(и) по вашему запросу:");

                    // Выводим список найденных книг с деталями
                    foreach (var book in searchResults)
                    {
                        // Формируем строку авторов книги
                        var authors = string.Join("; ", book.Authors.Select(a =>
                            $"{a.LastName} {a.FirstName} {a.MiddleName}".Trim()));

                        // Добавляем серию, если она есть
                        var seriesInfo = !string.IsNullOrEmpty(book.Series) ? $"Серия: {book.Series}, " : "";

                        await _outputService.WriteMessageAsync($"ID: {book.LibId}, {seriesInfo}Автор(ы): {authors}, Название: {book.Title}");
                    }
                }
                else
                {
                    // Если книг не найдено, выводим сообщение
                    await _outputService.WriteMessageAsync("Книги не найдены.");
                }

                // Пустая строка для разделения
                await _outputService.WriteMessageAsync("");
            }
        }
    }
}
