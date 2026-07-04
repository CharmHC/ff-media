namespace FFMedia.Core.Binaries;

/// <summary>Resolves the on-disk location of bundled external binaries.</summary>
public interface IBinaryProvider
{
    /// <summary>Absolute path to the given binary (whether or not it exists yet).</summary>
    string GetPath(ExternalBinary binary);

    /// <summary>True if the binary file is present on disk.</summary>
    bool Exists(ExternalBinary binary);
}
