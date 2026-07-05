using FFMedia.Core.Results;
using FFMedia.Tools.YouTubeDownloader.Infrastructure;
using FFMedia.Tools.YouTubeDownloader.Models;
using YoutubeDLSharp;

namespace FFMedia.Tools.YouTubeDownloader.Services;

public sealed class YtDlpDownloadService : IDownloadService
{
    private readonly IYoutubeDlFactory _factory;

    public YtDlpDownloadService(IYoutubeDlFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task<Result<string>> DownloadAsync(
        DownloadRequest request, IProgress<DownloadUpdate> progress, CancellationToken ct)
    {
        try
        {
            var ytdl = _factory.Create();
            ytdl.OutputFolder = request.OutputFolder;

            var innerProgress = new Progress<DownloadProgress>(p => progress.Report(ProgressMapping.ToUpdate(p)));
            var options = DownloadOptions.Mp4(request.OutputFolder);

            var res = await ytdl.RunVideoDownload(request.Url, progress: innerProgress, ct: ct, overrideOptions: options);
            return res.Success
                ? Result<string>.Success(res.Data)
                : Result<string>.Failure(string.Join(Environment.NewLine, res.ErrorOutput));
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation is handled by the caller (ViewModel → "Canceled")
        }
        catch (Exception ex)
        {
            // Never let a process-launch failure crash the app — surface it as a result.
            return Result<string>.Failure(YtDlpErrors.Describe(ex));
        }
    }
}
