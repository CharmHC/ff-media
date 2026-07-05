using System;
using FFMedia.Tools.YouTubeDownloader.Services;
using Xunit;

namespace FFMedia.Tests.YouTubeDownloader;

public class RetryPolicyTests
{
    [Theory]
    [InlineData("Connection timed out", true)]
    [InlineData("HTTP Error 503: Service Unavailable", true)]
    [InlineData("Unable to download webpage: read timed out", true)]
    [InlineData("Video unavailable", false)]
    [InlineData("Private video. Sign in if you've been granted access", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTransient_ClassifiesByErrorText(string? error, bool expected)
    {
        Assert.Equal(expected, RetryPolicy.IsTransient(error));
    }

    [Fact]
    public void DelayFor_GrowsExponentiallyFromBase()
    {
        var p = new RetryPolicy(4, TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), p.DelayFor(1));
        Assert.Equal(TimeSpan.FromSeconds(2), p.DelayFor(2));
        Assert.Equal(TimeSpan.FromSeconds(4), p.DelayFor(3));
        Assert.Equal(TimeSpan.FromSeconds(8), p.DelayFor(4));
    }

    [Fact]
    public void Default_IsThreeAttempts()
    {
        Assert.Equal(3, RetryPolicy.Default.MaxAttempts);
    }

    [Fact]
    public void Ctor_RejectsNonPositiveAttempts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(0, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void DelayFor_RejectsNonPositiveAttempt()
    {
        var p = new RetryPolicy(3, TimeSpan.FromSeconds(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.DelayFor(0));
    }
}
