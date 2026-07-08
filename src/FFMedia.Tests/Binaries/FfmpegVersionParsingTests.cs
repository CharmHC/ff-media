using FFMedia.Core.Binaries;
using Xunit;

namespace FFMedia.Tests.Binaries;

public class FfmpegVersionParsingTests
{
    [Theory]
    [InlineData("ffmpeg version 8.1 Copyright (c) 2000-2025 the FFmpeg developers", "8.1")]
    [InlineData("ffmpeg version n8.1.2-22-g94138f6973-win64-gpl-8.1 Copyright", "n8.1.2-22-g94138f6973-win64-gpl-8.1")]
    [InlineData("ffmpeg version N-125485-ga41f543113 Copyright (c) 2000", "N-125485-ga41f543113")]
    public void Parse_ExtractsVersionToken(string firstLine, string expected)
    {
        Assert.Equal(expected, FfmpegVersionParsing.Parse(firstLine + "\nbuilt with gcc"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not ffmpeg output at all")]
    public void Parse_ReturnsNull_OnUnparseable(string input)
    {
        Assert.Null(FfmpegVersionParsing.Parse(input));
    }
}
