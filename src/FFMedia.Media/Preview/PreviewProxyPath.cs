using System.Security.Cryptography;
using System.Text;

namespace FFMedia.Media.Preview;

/// <summary>Where a source's proxy lives.
///
/// <para>The key folds in the source's <b>last-write time and length</b>, not just its path. Keying on
/// the path alone would serve a <b>stale proxy of a file the user has since replaced or re-encoded</b> —
/// they would scrub the OLD video and capture timestamps into the NEW one.</para></summary>
public static class PreviewProxyPath
{
    public static string For(string sourcePath, string proxyDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyDirectory);

        var file = new FileInfo(sourcePath);
        var identity = $"{file.FullName}|{file.LastWriteTimeUtc.Ticks}|{(file.Exists ? file.Length : 0)}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];

        return Path.Combine(proxyDirectory, $"preview-{hash}.mp4");
    }
}
