// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Persistence;

// Saved worlds, inventory items and the records store, encrypted at rest with AES-256-GCM: an edited file fails
// to decrypt instead of loading altered data. One random master key encrypts the blobs, and Sealer holds that
// key at rest: bound to this machine and account by default, in the OS keystore once the platform layer sets one.
public static class LocalEncryption
{
    // "LVE1" magic so loaders can accept both encrypted and plain (legacy / local-home) blobs.
    private static readonly byte[] Magic = { (byte)'L', (byte)'V', (byte)'E', (byte)'1' };
    private const int NonceLen = 12;
    private const int TagLen = 16;
    private const int KeyLen = 32;

    private static readonly object _lock = new();
    private static byte[]? _masterKey;

    public static ISecretSealer Sealer { get; set; } = new DerivedKeySealer();

    public static bool IsEncrypted(byte[]? data)
        => data != null && data.Length >= Magic.Length
           && data[0] == Magic[0] && data[1] == Magic[1] && data[2] == Magic[2] && data[3] == Magic[3];

    // [magic][nonce][tag][ciphertext]
    public static byte[] Encrypt(byte[] plaintext)
    {
        var key = MasterKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagLen];

        using (var gcm = new AesGcm(key, TagLen))
            gcm.Encrypt(nonce, plaintext, cipher, tag);

        var output = new byte[Magic.Length + NonceLen + TagLen + cipher.Length];
        int offset = 0;
        Buffer.BlockCopy(Magic, 0, output, offset, Magic.Length); offset += Magic.Length;
        Buffer.BlockCopy(nonce, 0, output, offset, NonceLen); offset += NonceLen;
        Buffer.BlockCopy(tag, 0, output, offset, TagLen); offset += TagLen;
        Buffer.BlockCopy(cipher, 0, output, offset, cipher.Length);
        return output;
    }

    // A plain blob passes through unchanged; an edited one throws.
    public static byte[] Decrypt(byte[] data)
    {
        if (!IsEncrypted(data))
            return data;

        var key = MasterKey();
        int offset = Magic.Length;
        var nonce = new byte[NonceLen];
        Buffer.BlockCopy(data, offset, nonce, 0, NonceLen); offset += NonceLen;
        var tag = new byte[TagLen];
        Buffer.BlockCopy(data, offset, tag, 0, TagLen); offset += TagLen;
        var cipher = new byte[data.Length - offset];
        Buffer.BlockCopy(data, offset, cipher, 0, cipher.Length);

        var plain = new byte[cipher.Length];
        using (var gcm = new AesGcm(key, TagLen))
            gcm.Decrypt(nonce, cipher, tag, plain);   // AuthenticationTagMismatchException on tamper
        return plain;
    }

    // Cache files are sealed by LumoraNexus, which sits below this assembly; it gets its keys derived from this
    // master. Idempotent, called by whatever touches the caches first. -xlinka
    public static void WireSealedStorage() => Lumora.Nexus.Storage.SealedKeys.Source ??= MasterKey;

    private static byte[] MasterKey()
    {
        lock (_lock)
            return _masterKey ??= LoadOrCreateMasterKey();
    }

    private static byte[] LoadOrCreateMasterKey()
    {
        var path = KeyPath();
        try
        {
            if (File.Exists(path))
                return Sealer.Unseal(File.ReadAllBytes(path));
        }
        catch (Exception ex)
        {
            // A vault.key that won't unseal (different machine/user, corruption) is unrecoverable for
            // existing encrypted blobs; regenerate so new saves work rather than hard-failing.
            LumoraLogger.Warn($"LocalEncryption: could not unseal master key ({ex.Message}); regenerating.");
        }

        var master = RandomNumberGenerator.GetBytes(KeyLen);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Sealer.Seal(master));
            if (OperatingSystem.IsWindows())
            {
                try { File.SetAttributes(path, FileAttributes.Hidden); } catch { /* best effort */ }
            }
            else
            {
                // Owner read/write only - keep it out of reach of other users (and lean on the per-app
                // sandbox on Android).
                try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
            }
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"LocalEncryption: failed to persist master key: {ex.Message}");
        }
        return master;
    }

    private static string KeyPath()
        => Lumora.Core.Persistence.PathResolver.Resolve("vault.key");
}

public interface ISecretSealer
{
    byte[] Seal(byte[] secret);
    byte[] Unseal(byte[] sealed_);
}

// Wraps the secret with a PBKDF2-SHA256 key from this machine and account plus a per-install salt: a copied
// vault.key won't open anywhere else, but anything running as this user can work the key out again.
public sealed class DerivedKeySealer : ISecretSealer
{
    private const int SaltLen = 16;
    private const int NonceLen = 12;
    private const int TagLen = 16;
    private const int KeyLen = 32;
    private const int Iterations = 200_000;

    public byte[] Seal(byte[] secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLen);
        var wrap = DeriveWrapKey(salt);
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var cipher = new byte[secret.Length];
        var tag = new byte[TagLen];
        using (var gcm = new AesGcm(wrap, TagLen))
            gcm.Encrypt(nonce, secret, cipher, tag);

        var output = new byte[SaltLen + NonceLen + TagLen + cipher.Length];
        int offset = 0;
        Buffer.BlockCopy(salt, 0, output, offset, SaltLen); offset += SaltLen;
        Buffer.BlockCopy(nonce, 0, output, offset, NonceLen); offset += NonceLen;
        Buffer.BlockCopy(tag, 0, output, offset, TagLen); offset += TagLen;
        Buffer.BlockCopy(cipher, 0, output, offset, cipher.Length);
        return output;
    }

    public byte[] Unseal(byte[] sealed_)
    {
        int offset = 0;
        var salt = new byte[SaltLen];
        Buffer.BlockCopy(sealed_, offset, salt, 0, SaltLen); offset += SaltLen;
        var nonce = new byte[NonceLen];
        Buffer.BlockCopy(sealed_, offset, nonce, 0, NonceLen); offset += NonceLen;
        var tag = new byte[TagLen];
        Buffer.BlockCopy(sealed_, offset, tag, 0, TagLen); offset += TagLen;
        var cipher = new byte[sealed_.Length - offset];
        Buffer.BlockCopy(sealed_, offset, cipher, 0, cipher.Length);

        var wrap = DeriveWrapKey(salt);
        var secret = new byte[cipher.Length];
        using (var gcm = new AesGcm(wrap, TagLen))
            gcm.Decrypt(nonce, cipher, tag, secret);
        return secret;
    }

    private static byte[] DeriveWrapKey(byte[] salt)
    {
        var factors = Encoding.UTF8.GetBytes($"{Environment.MachineName}␟{Environment.UserName}");
        return Rfc2898DeriveBytes.Pbkdf2(factors, salt, Iterations, HashAlgorithmName.SHA256, KeyLen);
    }
}
