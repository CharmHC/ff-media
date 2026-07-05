namespace FFMedia.Core.Tools;

/// <summary>Associates a tool with the root view (page) type the shell should navigate to.
/// Uses only System.Type so Core stays UI-framework-agnostic.</summary>
public interface IToolPage
{
    string ToolId { get; }
    Type PageType { get; }
}
