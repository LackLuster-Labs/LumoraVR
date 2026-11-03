// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Security.Cryptography;
using System.Text;

namespace Lumora.Nexus.Storage;

// Every key a sealed file needs comes from one machine-bound master key the engine holds and seals with the
// OS keystore. This assembly only ever sees the derived keys, never how the master is kept. -xlinka
public static class SealedKeys
{
    private static readonly object Gate = new();
    private static Func<byte[]>? _source;
    private static byte[]? _wrap;
    private static byte[]? _names;
    private static Aes? _hashCipher;

    private const string Base32 = "abcdefghijklmnopqrstuvwxyz234567";
    public const int HiddenHashLength = 52;

    public static Func<byte[]>? Source
    {
        get => _source;
        set
        {
            lock (Gate)
            {
                _source = value;
                _wrap = null;
                _names = null;
                _hashCipher?.Dispose();
                _hashCipher = null;
            }
        }
    }

    public static bool Available => _source != null;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(SealedFile.KeySize);

    internal static byte[] WrapKey
    {
        get
        {
            lock (Gate)
                return _wrap ??= Derive("lumora/sealed/wrap/1");
        }
    }

    // Must be called under Gate.
    private static byte[] Derive(string info)
    {
        var source = _source ?? throw new InvalidOperationException("Sealed files have no key source yet.");
        var master = source();
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, null, Encoding.ASCII.GetBytes(info));
    }

    // A file name that says nothing about what it holds: the same scope and key always give the same name, and
    // without the master key nobody can tell which key a name stands for.
    public static string NameFor(string scope, string key)
    {
        byte[] names;
        lock (Gate)
            names = _names ??= Derive("lumora/sealed/name/1");
        var mac = HMACSHA256.HashData(names, Encoding.UTF8.GetBytes(scope + "\n" + key));
        return Convert.ToHexString(mac, 0, 16).ToLowerInvariant();
    }

    // A content hash turned into a name only this machine can turn back, for a cache that has to rebuild its
    // index from file names alone. Two AES blocks over the 32 hash bytes, written as base32 so a hidden name
    // can never be mistaken for a plain hex hash.
    public static string? HideHash(string hex)
    {
        if (hex == null || hex.Length != 64)
            return null;
        byte[] raw;
        try
        {
            raw = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
        byte[] hidden;
        lock (Gate)
            hidden = HashCipher().EncryptEcb(raw, PaddingMode.None);
        return ToBase32(hidden);
    }

    public static string? RevealHash(string name)
    {
        if (name == null || name.Length != HiddenHashLength)
            return null;
        var raw = FromBase32(name);
        if (raw == null)
            return null;
        byte[] plain;
        lock (Gate)
            plain = HashCipher().DecryptEcb(raw, PaddingMode.None);
        return Convert.ToHexString(plain).ToLowerInvariant();
    }

    // Must be called under Gate.
    private static Aes HashCipher()
    {
        if (_hashCipher != null)
            return _hashCipher;
        var aes = Aes.Create();
        aes.Key = Derive("lumora/sealed/content/1");
        _hashCipher = aes;
        return aes;
    }

    private static string ToBase32(byte[] data)
    {
        var text = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                text.Append(Base32[(buffer >> bits) & 31]);
            }
        }
        if (bits > 0)
            text.Append(Base32[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }

    private static byte[]? FromBase32(string text)
    {
        var data = new byte[32];
        int buffer = 0, bits = 0, at = 0;
        foreach (var c in text)
        {
            int value = Base32.IndexOf(c);
            if (value < 0)
                return null;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                if (at >= data.Length)
                    return null;
                data[at++] = (byte)(buffer >> bits);
            }
        }
        return at == data.Length ? data : null;
    }
}
