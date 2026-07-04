using FFMedia.Core;
using FFMedia.Core.Binaries;
using FFMedia.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FFMedia.Tests;

public class CoreServiceCollectionExtensionsTests
{
    private sealed record FakeTool(string Id, string DisplayName, int SortOrder) : ITool
    {
        public string Description => "";
        public string IconGlyph => "";
    }

    [Fact]
    public void AddFFMediaCore_ResolvesRegistry_WithRegisteredToolsOrdered()
    {
        var provider = new ServiceCollection()
            .AddSingleton<ITool>(new FakeTool("z", "Zeta", 20))
            .AddSingleton<ITool>(new FakeTool("a", "Alpha", 10))
            .AddFFMediaCore(binariesDirectory: Path.GetTempPath())
            .BuildServiceProvider();

        var registry = provider.GetRequiredService<IToolRegistry>();

        Assert.Equal(new[] { "Alpha", "Zeta" }, registry.Tools.Select(t => t.DisplayName));
    }

    [Fact]
    public void AddFFMediaCore_ResolvesBinaryProvider_UsingGivenDirectory()
    {
        var dir = Path.GetTempPath();
        var provider = new ServiceCollection()
            .AddFFMediaCore(binariesDirectory: dir)
            .BuildServiceProvider();

        var binaries = provider.GetRequiredService<IBinaryProvider>();

        Assert.Equal(Path.Combine(dir, "yt-dlp.exe"), binaries.GetPath(ExternalBinary.YtDlp));
    }
}
