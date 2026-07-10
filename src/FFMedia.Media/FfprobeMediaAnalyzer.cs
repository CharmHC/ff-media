using FFMedia.Core.Binaries;
using FFMedia.Core.Processes;
using FFMedia.Core.Results;

namespace FFMedia.Media;

/// <summary>Probes media via the bundled <c>ffprobe.exe</c>, through the <see cref="IProcessRunner"/> seam.</summary>
public sealed class FfprobeMediaAnalyzer : IMediaAnalyzer
{
    private readonly IProcessRunner _runner;
    private readonly IBinaryProvider _binaries;

    public FfprobeMediaAnalyzer(IProcessRunner runner, IBinaryProvider binaries)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(binaries);
        _runner = runner;
        _binaries = binaries;
    }

    public async Task<Result<MediaInfo>> AnalyzeAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ct.ThrowIfCancellationRequested();

        if (!_binaries.Exists(ExternalBinary.Ffprobe))
        {
            return Result<MediaInfo>.Failure("ffprobe.exe is missing. Run build/fetch-binaries.ps1.");
        }

        // -v error, not -v quiet: quiet suppresses the stderr text we report back on a
        // non-zero exit, degrading "Invalid data found" to a bare "exit code 1".
        string[] arguments =
        [
            "-v", "error",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            filePath,
        ];

        ProcessResult process;
        try
        {
            process = await _runner.RunAsync(_binaries.GetPath(ExternalBinary.Ffprobe), arguments, null, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<MediaInfo>.Failure($"Could not run ffprobe: {ex.Message}");
        }

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(process.StandardError)
                ? $"exit code {process.ExitCode}"
                : process.StandardError.Trim();
            return Result<MediaInfo>.Failure($"ffprobe could not read '{Path.GetFileName(filePath)}': {detail}");
        }

        var info = FfprobeParsing.Parse(process.StandardOutput);
        return info is null
            ? Result<MediaInfo>.Failure(
                $"Could not read video streams from '{Path.GetFileName(filePath)}'. Is it a video file?")
            : Result<MediaInfo>.Success(info);
    }
}
