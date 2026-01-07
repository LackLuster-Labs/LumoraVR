// hashing and uri stuff for content

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Lumora.CDN;

public static class ContentHash
{
    public const string Scheme = "lumora";

    // hash a file
    public static string FromFile(string path)
    {
        using var stream = File.OpenRead(path);
        return FromStream(stream);
    }

    // hash a stream
    public static string FromStream(Stream stream)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // hash bytes
    public static string FromBytes(byte[] data)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // hash a string
    public static string FromString(string text)
    {
        return FromBytes(Encoding.UTF8.GetBytes(text));
    }

    // make a lumora:// uri
    public static Uri ToUri(string hash, string? extension = null)
    {
        var filename = string.IsNullOrEmpty(extension)
            ? hash
            : $"{hash}{(extension.StartsWith('.') ? extension : $".{extension}")}";
        return new Uri($"{Scheme}:///{filename}");
    }

    // get the hash from a uri
    public static string ParseHash(Uri uri)
    {
        var path = uri.AbsolutePath.TrimStart('/');
        var dot = path.LastIndexOf('.');
        return dot > 0 ? path[..dot] : path;
    }

    // parse full uri info
    public static (string Hash, string? Extension) Parse(Uri uri)
    {
        var path = uri.AbsolutePath.TrimStart('/');
        var dot = path.LastIndexOf('.');

        if (dot > 0)
            return (path[..dot], path[dot..]);

        return (path, null);
    }

    // get just the filename part
    public static string GetFilename(Uri uri)
    {
        return uri.AbsolutePath.TrimStart('/');
    }

    // is this a lumora:// uri?
    public static bool IsContentUri(Uri uri)
    {
        return uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase);
    }

    // is this a local file?
    public static bool IsLocalUri(Uri uri)
    {
        return uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase);
    }
}

// mime type mapping
public static class MimeTypes
{
    public static string FromExtension(string extension)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "bmp" => "image/bmp",
            "svg" => "image/svg+xml",
            "ico" => "image/x-icon",
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "flac" => "audio/flac",
            "mp4" => "video/mp4",
            "webm" => "video/webm",
            "avi" => "video/x-msvideo",
            "mov" => "video/quicktime",
            "glb" => "model/gltf-binary",
            "gltf" => "model/gltf+json",
            "obj" => "model/obj",
            "fbx" => "application/octet-stream",
            "json" => "application/json",
            "xml" => "application/xml",
            "txt" => "text/plain",
            "html" or "htm" => "text/html",
            "css" => "text/css",
            "js" => "application/javascript",
            "wasm" => "application/wasm",
            "zip" => "application/zip",
            "7z" => "application/x-7z-compressed",
            "ttf" => "font/ttf",
            "otf" => "font/otf",
            "woff" => "font/woff",
            "woff2" => "font/woff2",
            _ => "application/octet-stream"
        };
    }

    public static string ToExtension(string mimeType)
    {
        return mimeType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/svg+xml" => ".svg",
            "audio/mpeg" => ".mp3",
            "audio/wav" => ".wav",
            "audio/ogg" => ".ogg",
            "audio/flac" => ".flac",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "model/gltf-binary" => ".glb",
            "model/gltf+json" => ".gltf",
            "application/json" => ".json",
            "text/plain" => ".txt",
            "font/ttf" => ".ttf",
            "font/otf" => ".otf",
            "font/woff" => ".woff",
            "font/woff2" => ".woff2",
            _ => ".bin"
        };
    }
}
