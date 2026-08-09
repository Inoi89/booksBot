using booksBot.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace booksBot.Tests;

public sealed class BlockedBookRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bookbot-blocklist-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ReloadsExactIdsWithoutSubstringMatching()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "blocked-book-ids.txt");
        var registry = new BlockedBookRegistry(path, NullLogger.Instance, TimeSpan.Zero);

        Assert.False(registry.IsBlocked("806581"));

        File.WriteAllLines(path, ["# comment", "806581 # RKN 10"]);
        Assert.True(registry.IsBlocked("806581"));
        Assert.False(registry.IsBlocked("80658"));
        Assert.False(registry.IsBlocked("8065810"));

        File.WriteAllLines(path, ["806582"]);
        Assert.False(registry.IsBlocked("806581"));
        Assert.True(registry.IsBlocked("806582"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
