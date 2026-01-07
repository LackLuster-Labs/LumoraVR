namespace Lumora.Core.Assets;

/// <summary>
/// Metadata for shader source assets.
/// </summary>
public sealed class ShaderSourceMetadata : IAssetMetadata
{
    /// <summary>
    /// Length of the shader source in bytes.
    /// </summary>
    public long ByteLength { get; set; }

    /// <summary>
    /// Estimated memory size equals source length.
    /// </summary>
    public long EstimatedMemorySize => ByteLength;
}
