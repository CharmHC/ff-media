using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FFMedia.Core.Processes;
using Microsoft.Extensions.Logging;

namespace FFMedia.Core.Binaries;

/// <summary><see cref="IBinaryUpdateService"/> over <see cref="IProcessRunner"/> (versions +
/// <c>yt-dlp -U</c>) and the GitHub API (latest-version check).</summary>
public sealed class BinaryUpdateService : IBinaryUpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";

    private readonly IProcessRunner _runner;
    private readonly IBinaryProvider _binaries;
    private readonly HttpClient _http;
    private readonly ILogger<BinaryUpdateService> _logger;

    public BinaryUpdateService(
        IProcessRunner runner, IBinaryProvider binaries, HttpClient http, ILogger<BinaryUpdateService> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(binaries);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(logger);
        _runner = runner;
        _binaries = binaries;
        _http = http;
        _logger = logger;
    }

    public async Task<string?> GetInstalledVersionAsync(ExternalBinary binary, CancellationToken ct = default)
    {
        try
        {
            var path = _binaries.GetPath(binary);
            var args = binary == ExternalBinary.YtDlp ? new[] { "--version" } : new[] { "-version" };
            var result = await _runner.RunAsync(path, args, null, ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return null;
            }

            return binary == ExternalBinary.YtDlp
                ? result.StandardOutput.Trim()
                : FfmpegVersionParsing.Parse(result.StandardOutput);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read {Binary} version", binary);
            return null;
        }
    }

    public async Task<string?> GetLatestYtDlpVersionAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            req.Headers.UserAgent.ParseAdd("FFMedia");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var payload = await resp.Content.ReadFromJsonAsync<GithubRelease>(ct).ConfigureAwait(false);
            var latest = payload?.TagName?.Trim();
            if (string.IsNullOrEmpty(latest))
            {
                return null;
            }

            var installed = await GetInstalledVersionAsync(ExternalBinary.YtDlp, ct).ConfigureAwait(false);
            return YtDlpVersion.IsNewer(latest, installed) ? latest : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "yt-dlp latest-version check failed");
            return null;
        }
    }

    public async Task<BinaryUpdateResult> UpdateYtDlpAsync(CancellationToken ct = default)
    {
        var from = await GetInstalledVersionAsync(ExternalBinary.YtDlp, ct).ConfigureAwait(false);
        var path = _binaries.GetPath(ExternalBinary.YtDlp);

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(path, new[] { "-U" }, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "yt-dlp self-update failed to launch");
            return new BinaryUpdateResult(false, from, from, "yt-dlp update failed. See logs.");
        }

        var to = await GetInstalledVersionAsync(ExternalBinary.YtDlp, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return new BinaryUpdateResult(false, from, to, "yt-dlp update failed. See logs.");
        }

        var updated = !string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
        var message = updated
            ? $"Updated yt-dlp {from} → {to}."
            : $"yt-dlp is already up to date ({to}).";
        return new BinaryUpdateResult(updated, from, to, message);
    }

    private sealed record GithubRelease([property: JsonPropertyName("tag_name")] string? TagName);
}
