// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Lumora.Core.Security.Integrity;

// The signed list of every managed file a release shipped, for every platform it shipped on. The build script
// writes it beside the assemblies once both exports are done and signs it with the release key, which never
// leaves the build machine; the game checks it against the public half below. Plain text, one entry a line:
//   lumora-release 1
//   version <BuildInfo.Version>
//   file <platform> <file name> <sha-256, lower hex>
//   signature <base64 DER ECDSA P-256 over SHA-256 of every byte before this line>
// A file counts as shipped when its name and hash are listed under any platform, so a Windows build under
// Proton and a Linux build read the same list the same way. -xlinka
public sealed class ReleaseManifest
{
    public const string FileName = "release.manifest";

    private const string Header = "lumora-release 1";
    private const string SignaturePrefix = "signature ";
    private const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEhNsLkun7g8FanxMFzvbfwFrnioB4vk6I9aK9+L2DKf9+XafD2xIAi8osg9diJhEN6zW3du9CpOf1HqGH4cIubQ==";
    private const int MaxBytes = 4 * 1024 * 1024;

    private readonly Dictionary<string, HashSet<string>> _files = new(StringComparer.OrdinalIgnoreCase);

    public string Version { get; private set; } = string.Empty;

    public int FileCount { get; private set; }

    private ReleaseManifest()
    {
    }

    public bool Allows(string fileName, string sha256)
        => _files.TryGetValue(fileName, out var hashes) && hashes.Contains(sha256);

    // Null with what is wrong when the list is missing, unreadable, unsigned, signed by another key or made for
    // another release.
    public static ReleaseManifest? Load(string path, string expectedVersion, out string problem)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                problem = "missing";
                return null;
            }
            if (new FileInfo(path).Length > MaxBytes)
            {
                problem = "too large";
                return null;
            }
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            problem = "unreadable (" + ex.GetType().Name + ")";
            return null;
        }

        int split = LastLineStart(bytes, SignaturePrefix);
        if (split < 0)
        {
            problem = "unsigned";
            return null;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(Encoding.ASCII.GetString(bytes, split + SignaturePrefix.Length,
                bytes.Length - split - SignaturePrefix.Length).Trim());
        }
        catch (FormatException)
        {
            problem = "unsigned";
            return null;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
            if (!key.VerifyData(bytes.AsSpan(0, split), signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            {
                problem = "signature does not match";
                return null;
            }
        }
        catch (CryptographicException ex)
        {
            problem = "signature unreadable (" + ex.Message + ")";
            return null;
        }

        var manifest = new ReleaseManifest();
        var lines = Encoding.UTF8.GetString(bytes, 0, split).Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd('\r') != Header)
        {
            problem = "unknown format";
            return null;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("version ", StringComparison.Ordinal))
            {
                manifest.Version = line.Substring(8).Trim();
                continue;
            }
            if (!line.StartsWith("file ", StringComparison.Ordinal))
                continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4)
                continue;
            if (!manifest._files.TryGetValue(parts[2], out var hashes))
                manifest._files[parts[2]] = hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (hashes.Add(parts[3]))
                manifest.FileCount++;
        }

        if (!string.Equals(manifest.Version, expectedVersion, StringComparison.Ordinal))
        {
            problem = $"made for {(manifest.Version.Length == 0 ? "no release" : manifest.Version)}";
            return null;
        }

        problem = string.Empty;
        return manifest;
    }

    // Where the last line starting with prefix begins, or -1.
    private static int LastLineStart(byte[] bytes, string prefix)
    {
        var marker = Encoding.ASCII.GetBytes(prefix);
        for (int start = bytes.Length - marker.Length; start >= 0; start--)
        {
            if (start > 0 && bytes[start - 1] != (byte)'\n')
                continue;
            if (bytes.AsSpan(start, marker.Length).SequenceEqual(marker))
                return start;
        }
        return -1;
    }
}
