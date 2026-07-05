using System.Linq;

namespace FFMedia.Tools.YouTubeDownloader.Services;

/// <summary>
/// Decides whether a failed download should be retried and how long to wait between attempts.
/// Pure and deterministic — the tested core of the queue's resilience.
/// </summary>
public sealed class RetryPolicy
{
    // Case-insensitive substrings that indicate a transient (worth-retrying) network error.
    private static readonly string[] TransientSignatures =
    {
        "timed out", "timeout", "connection reset", "temporary failure",
        "network is unreachable", "unable to download", "http error 5",
        "getaddrinfo", "read timed out",
    };

    private readonly TimeSpan _baseDelay;

    public RetryPolicy(int maxAttempts, TimeSpan baseDelay)
    {
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        MaxAttempts = maxAttempts;
        _baseDelay = baseDelay;
    }

    public int MaxAttempts { get; }

    /// <summary>Exponential backoff: baseDelay · 2^(attempt-1). attempt is 1-based.</summary>
    public TimeSpan DelayFor(int attempt)
    {
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt));
        return TimeSpan.FromTicks(_baseDelay.Ticks * (long)Math.Pow(2, attempt - 1));
    }

    /// <summary>True when the error text looks like a transient network failure worth retrying.</summary>
    public static bool IsTransient(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        var lowered = error.ToLowerInvariant();
        return TransientSignatures.Any(lowered.Contains);
    }

    /// <summary>App default: 3 attempts, 1s exponential base.</summary>
    public static RetryPolicy Default { get; } = new(3, TimeSpan.FromSeconds(1));
}
