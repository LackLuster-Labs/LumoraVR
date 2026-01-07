using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Core.Assets;

namespace Lumora.Core.Components.Assets;

/// <summary>
/// Component that provides shader source assets from URLs.
/// </summary>
[ComponentCategory("Assets")]
public sealed class ShaderSourceProvider : UrlAssetProvider<ShaderSourceAsset, ShaderSourceMetadata>
{
    protected override Task<ShaderSourceMetadata> LoadMetadata(Uri url, CancellationToken token)
    {
        return Task.Run(async () =>
        {
            var bytes = await LoadFileBytes(url, token);
            return new ShaderSourceMetadata { ByteLength = bytes?.Length ?? 0 };
        }, token);
    }

    protected override async Task<ShaderSourceAsset> LoadAssetData(Uri url, ShaderSourceMetadata metadata, CancellationToken token)
    {
        var bytes = await LoadFileBytes(url, token);
        if (bytes == null || bytes.Length == 0)
        {
            return null;
        }

        var asset = new ShaderSourceAsset();
        asset.SetSource(bytes);
        return asset;
    }

    private async Task<byte[]> LoadFileBytes(Uri url, CancellationToken token)
    {
        if (url.IsFile)
        {
            return await File.ReadAllBytesAsync(url.LocalPath, token);
        }

        if (url.Scheme is "http" or "https" or "lumora")
        {
            var contentCache = Engine.Current?.ContentCache;
            if (contentCache != null)
            {
                var data = await contentCache.Get(url, token);
                if (data != null)
                {
                    return data;
                }
            }

            throw new InvalidOperationException($"Failed to load content from URL: {url}");
        }

        throw new NotSupportedException($"URL scheme not supported: {url.Scheme}");
    }
}
