using System;
using System.Text;

namespace Lumora.Core.Assets;

/// <summary>
/// Asset containing shader source text.
/// </summary>
public sealed class ShaderSourceAsset : Asset
{
    private int _activeRequestCount;
    private byte[]? _rawBytes;
    private string? _source;

    /// <summary>
    /// Raw shader source bytes.
    /// </summary>
    public byte[]? RawBytes => _rawBytes;

    /// <summary>
    /// Shader source text.
    /// </summary>
    public string? Source => _source;

    public override int ActiveRequestCount => _activeRequestCount;

    /// <summary>
    /// Assign shader source bytes and decode as UTF8.
    /// </summary>
    public void SetSource(byte[] bytes)
    {
        if (bytes == null) throw new ArgumentNullException(nameof(bytes));

        _rawBytes = bytes;
        _source = Encoding.UTF8.GetString(bytes);
        Version++;
    }

    /// <summary>
    /// Add an active request for this shader.
    /// </summary>
    public void AddRequest()
    {
        _activeRequestCount++;
    }

    /// <summary>
    /// Remove an active request for this shader.
    /// </summary>
    public void RemoveRequest()
    {
        _activeRequestCount = System.Math.Max(0, _activeRequestCount - 1);
    }

    public override void Unload()
    {
        _rawBytes = null;
        _source = null;
        _activeRequestCount = 0;
    }
}
