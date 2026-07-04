using FFMedia.Core.Tools;
using Xunit;

namespace FFMedia.Tests.Tools;

public class ToolRegistryTests
{
    private sealed record FakeTool(string Id, string DisplayName, int SortOrder) : ITool
    {
        public string Description => $"{DisplayName} description";
        public string IconGlyph => "";
    }

    [Fact]
    public void Tools_AreOrderedBySortOrderThenDisplayName()
    {
        var a = new FakeTool("b", "Beta", 10);
        var b = new FakeTool("a", "Alpha", 10);
        var c = new FakeTool("c", "Gamma", 5);

        var registry = new ToolRegistry(new ITool[] { a, b, c });

        Assert.Equal(new[] { "Gamma", "Alpha", "Beta" }, registry.Tools.Select(t => t.DisplayName));
    }

    [Fact]
    public void Tools_IsEmpty_WhenNoToolsRegistered()
    {
        var registry = new ToolRegistry(Array.Empty<ITool>());
        Assert.Empty(registry.Tools);
    }
}
