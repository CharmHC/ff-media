using FFMedia.Core.Results;
using Xunit;

namespace FFMedia.Tests.Results;

public class ResultTests
{
    [Fact]
    public void Success_SetsValueAndIsSuccess()
    {
        var r = Result<int>.Success(42);
        Assert.True(r.IsSuccess);
        Assert.Equal(42, r.Value);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Failure_SetsErrorAndNotSuccess()
    {
        var r = Result<string>.Failure("boom");
        Assert.False(r.IsSuccess);
        Assert.Equal("boom", r.Error);
        Assert.Null(r.Value);
    }
}
