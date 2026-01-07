using System;

namespace Lumora.CDN;

public enum ServiceEnvironment
{
    Production,
    Development
}

public sealed class ServiceConfig
{
    private static ServiceConfig? _current;
    public static ServiceConfig Current => _current ?? Production;

    // production - the real deal
    public static readonly ServiceConfig Production = new()
    {
        Environment = ServiceEnvironment.Production,
        ApiBase = "https://api.lumoravr.com",
        ContentBase = "https://assets.lumoravr.com",
        CdnBase = "https://cdn.lumoravr.com",
        ThumbnailBase = "https://cdn.lumoravr.com/thumbs"
    };

    // dev - localhost bullshit
    public static readonly ServiceConfig Development = new()
    {
        Environment = ServiceEnvironment.Development,
        ApiBase = "http://localhost:5000",
        ContentBase = "http://localhost:5001",
        CdnBase = "http://localhost:5001",
        ThumbnailBase = "http://localhost:5001/thumbs"
    };

    public ServiceEnvironment Environment { get; init; }
    public required string ApiBase { get; init; }
    public required string ContentBase { get; init; }
    public required string CdnBase { get; init; }
    public required string ThumbnailBase { get; init; }

    public static void Use(ServiceConfig config) => _current = config;

    public static void UseEnvironment(ServiceEnvironment env)
    {
        _current = env switch
        {
            ServiceEnvironment.Production => Production,
            ServiceEnvironment.Development => Development,
            _ => Production
        };
    }

    // make your own config if you want
    public static ServiceConfig Custom(string apiBase, string contentBase, string? cdnBase = null, string? thumbnailBase = null)
    {
        return new ServiceConfig
        {
            Environment = ServiceEnvironment.Development,
            ApiBase = apiBase,
            ContentBase = contentBase,
            CdnBase = cdnBase ?? contentBase,
            ThumbnailBase = thumbnailBase ?? $"{cdnBase ?? contentBase}/thumbs"
        };
    }

    // get content url
    public string GetContentUrl(string hash, bool useCdn = true)
    {
        var baseUrl = useCdn ? CdnBase : ContentBase;
        return $"{baseUrl}/content/{hash}";
    }

    // get thumbnail url
    public string GetThumbnailUrl(string hash)
    {
        return $"{ThumbnailBase}/{hash}";
    }

    // convert lumora:// to http url
    public Uri ResolveUri(Uri lumoraUri, bool useCdn = true)
    {
        if (!ContentHash.IsContentUri(lumoraUri))
            throw new ArgumentException("Not a lumora:// URI", nameof(lumoraUri));

        var filename = ContentHash.GetFilename(lumoraUri);
        var baseUrl = useCdn ? CdnBase : ContentBase;
        return new Uri($"{baseUrl}/content/{filename}");
    }
}
