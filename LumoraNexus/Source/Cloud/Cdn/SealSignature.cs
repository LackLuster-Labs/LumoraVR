// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Lumora.Nexus.Cloud.Cdn;

// THE SEAL TEXT. The accounts service signs exactly these bytes with the owner's P-256 key and every peer checks
// them against the owner's published public key, so one character of drift between the two sides and every seal
// reads as broken. LF only, no trailing newline, integers in invariant culture. -xlinka
public static class SealSignature
{
    public const string Version = "lumora-seal/1";

    public static string Text(string ownerId, string keyId, string itemId, int rules, long issuedAt)
        => Version
            + "\nowner:" + ownerId
            + "\nkey:" + keyId
            + "\nitem:" + itemId
            + "\nrules:" + rules.ToString(CultureInfo.InvariantCulture)
            + "\nissued:" + issuedAt.ToString(CultureInfo.InvariantCulture);

    public static byte[] Bytes(string ownerId, string keyId, string itemId, int rules, long issuedAt)
        => Encoding.UTF8.GetBytes(Text(ownerId, keyId, itemId, rules, issuedAt));

    // Lowercase hex of the first 8 bytes of SHA-256 over the key's SPKI DER.
    public static string KeyIdOf(byte[] spki)
    {
        var hash = SHA256.HashData(spki);
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    // False for anything malformed as well as for a signature that does not check out: a seal nobody can read is
    // as broken as a forged one.
    public static bool Verify(byte[] spki, string ownerId, string keyId, string itemId, int rules, long issuedAt, string signatureBase64)
    {
        if (spki == null || spki.Length == 0 || string.IsNullOrEmpty(signatureBase64))
            return false;
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException)
        {
            return false;
        }
        if (signature.Length != 64)
            return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out _);
            if (key.KeySize != 256)
                return false;
            return key.VerifyData(Bytes(ownerId, keyId, itemId, rules, issuedAt), signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    // The shapes the contract fixes, checked before any key is fetched.
    public static bool IsItemId(string? value) => IsLowerHex(value, 32);

    public static bool IsKeyId(string? value) => IsLowerHex(value, 16);

    public static bool IsRules(int rules) => rules >= 1 && rules <= ProtectionRules.All;

    private static bool IsLowerHex(string? value, int length)
    {
        if (value == null || value.Length != length)
            return false;
        foreach (var c in value)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        }
        return true;
    }
}
