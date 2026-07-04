namespace FFMedia.Core.Tools;

internal sealed class ToolRegistry : IToolRegistry
{
    public ToolRegistry(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        Tools = tools
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.DisplayName, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<ITool> Tools { get; }
}
