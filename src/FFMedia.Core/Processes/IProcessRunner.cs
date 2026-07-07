namespace FFMedia.Core.Processes;

/// <summary>Launches child processes, capturing output and honoring cancellation. The seam
/// that makes binary orchestration testable without real exes (SDD §6).</summary>
public interface IProcessRunner
{
    /// <summary>Runs <paramref name="fileName"/> with <paramref name="arguments"/>, capturing
    /// stdout/stderr and optionally streaming stdout lines via <paramref name="onOutputLine"/>.
    /// A non-zero exit is returned in the result (not thrown). Cancellation kills the process
    /// tree and throws <see cref="OperationCanceledException"/>. A launch failure (e.g. missing
    /// file) throws.</summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IProgress<string>? onOutputLine = null,
        CancellationToken ct = default);
}
