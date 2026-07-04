namespace FFMedia.Core.Tools;

/// <summary>Aggregates all registered tools for the shell to render.</summary>
public interface IToolRegistry
{
    /// <summary>Registered tools, ordered for display.</summary>
    IReadOnlyList<ITool> Tools { get; }
}
