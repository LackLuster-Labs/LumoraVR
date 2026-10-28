// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Lumora.Nexus.Transport.LNL;

internal sealed class LNLCryptoSession : IDisposable
{
    private static readonly byte[] Magic = { (byte)'L', (byte)'N', (byte)'L', (byte)'C' };
    private static readonly byte[] KdfContext = Encoding.UTF8.GetBytes("Lumora LNL transport crypto v1");

    private const byte Version = 1;
    private const byte ClientHello = 1;
    private const byte ServerHello = 2;
    private const byte EncryptedFrame = 3;
    private const int NonceLen = 12;
    private const int TagLen = 16;
    private const int KeyLen = 32;
    private const int HandshakeNonceLen = 32;
    private const int EncryptedHeaderLen = 14;
    private const int MaxPublicKeyBytes = 512;

    private readonly bool _isClient;
    private readonly ECDiffieHellman _ecdh;
    private AesGcm? _txGcm;
    private AesGcm? _rxGcm;
    private readonly byte[] _localPublicKey;
    private readonly object _sendLock = new();

    private byte[]? _clientPublicKey;
    private byte[]? _serverPublicKey;
    private byte[]? _clientNonce;
    private byte[]? _serverNonce;
    private byte[]? _sendKey;
    private byte[]? _receiveKey;
    private byte[]? _sendNonceBase;
    private byte[]? _receiveNonceBase;
    private ulong _sendSequence;
    private bool _disposed;

    public bool IsEstablished { get; private set; }

    public LNLCryptoSession(bool isClient)
    {
        _isClient = isClient;
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _localPublicKey = _ecdh.ExportSubjectPublicKeyInfo();
    }

    public byte[] CreateClientHello()
    {
        if (!_isClient)
            throw new InvalidOperationException("Only the dialing side can start an LNL crypto handshake.");

        _clientPublicKey = _localPublicKey;
        _clientNonce = RandomNumberGenerator.GetBytes(HandshakeNonceLen);
        return BuildHello(ClientHello, _clientPublicKey, _clientNonce);
    }

    public bool TryHandleIncoming(byte[] data, int length, out byte[]? plaintext, out byte[]? response)
    {
        plaintext = null;
        response = null;

        if (data == null || length < Magic.Length + 2 || !HasMagic(data, length))
            return false;

        byte version = data[4];
        byte kind = data[5];
        if (version != Version)
            return false;

        return kind switch
        {
            ClientHello => TryHandleClientHello(data, length, out response),
            ServerHello => TryHandleServerHello(data, length),
            EncryptedFrame => TryDecrypt(data, length, out plaintext),
            _ => false,
        };
    }

    public byte[] Encrypt(byte[] data, int length)
    {
        CheckSend(data, length);
        var output = new byte[EncryptedHeaderLen + length + TagLen];
        EncryptCore(data, length, output);
        return output;
    }

    // Frames up to this size are written into the caller's own buffer.
    private const int ReusableFrameBytes = 256 * 1024;

    // The same frame written into a buffer the sender keeps, grown as frames need it, so a host sending every
    // guest every pose makes no new array per guest per message: that was the largest steady allocation with a
    // crowd in. The caller must have handed the buffer on (the transport copies it) before the next frame goes
    // in. A frame past the reusable size gets an array of its own. -xlinka
    public byte[] EncryptReusing(byte[] data, int length, ref byte[]? scratch, out int count)
    {
        CheckSend(data, length);
        count = EncryptedHeaderLen + length + TagLen;
        byte[] output;
        if (count > ReusableFrameBytes)
            output = new byte[count];
        else
        {
            if (scratch == null || scratch.Length < count)
                scratch = new byte[System.Math.Min(ReusableFrameBytes, System.Math.Max(count, (scratch?.Length ?? 1024) * 2))];
            output = scratch;
        }
        EncryptCore(data, length, output);
        return output;
    }

    private void CheckSend(byte[] data, int length)
    {
        if (!IsEstablished || _sendKey == null || _sendNonceBase == null)
            throw new InvalidOperationException("LNL crypto is not established yet.");
        if (data == null)
            throw new ArgumentNullException(nameof(data));
        if (length < 0 || length > data.Length)
            throw new ArgumentOutOfRangeException(nameof(length));
    }

    private void EncryptCore(byte[] data, int length, byte[] output)
    {
        lock (_sendLock)
        {
            ulong sequence = _sendSequence++;
            WriteEncryptedHeader(output, sequence);

            Span<byte> nonce = stackalloc byte[NonceLen];
            BuildNonce(_sendNonceBase!, sequence, nonce);
            var cipher = output.AsSpan(EncryptedHeaderLen, length);
            var tag = output.AsSpan(EncryptedHeaderLen + length, TagLen);
            _txGcm!.Encrypt(nonce, data.AsSpan(0, length), cipher, tag, output.AsSpan(0, EncryptedHeaderLen));
        }
    }

    private bool TryHandleClientHello(byte[] data, int length, out byte[]? response)
    {
        response = null;
        if (_isClient || IsEstablished)
            return false;
        if (!TryParseHello(data, length, ClientHello, out var remotePublic, out var nonce))
            return false;

        _clientPublicKey = remotePublic;
        _clientNonce = nonce;
        _serverPublicKey = _localPublicKey;
        _serverNonce = RandomNumberGenerator.GetBytes(HandshakeNonceLen);

        if (!TryDerive())
            return false;

        response = BuildHello(ServerHello, _serverPublicKey, _serverNonce);
        IsEstablished = true;
        return true;
    }

    private bool TryHandleServerHello(byte[] data, int length)
    {
        if (!_isClient || IsEstablished)
            return false;
        if (_clientPublicKey == null || _clientNonce == null)
            return false;
        if (!TryParseHello(data, length, ServerHello, out var remotePublic, out var nonce))
            return false;

        _serverPublicKey = remotePublic;
        _serverNonce = nonce;
        if (!TryDerive())
            return false;

        IsEstablished = true;
        return true;
    }

    private bool TryDerive()
    {
        if (_clientPublicKey == null || _serverPublicKey == null || _clientNonce == null || _serverNonce == null)
            return false;

        try
        {
            using var remote = ECDiffieHellman.Create();
            var remoteKey = _isClient ? _serverPublicKey : _clientPublicKey;
            remote.ImportSubjectPublicKeyInfo(remoteKey, out var read);
            if (read != remoteKey.Length)
                return false;

            var secret = _ecdh.DeriveKeyMaterial(remote.PublicKey);
            var transcript = HashTranscript(_clientPublicKey, _serverPublicKey, _clientNonce, _serverNonce);
            var c2hKey = Hkdf(secret, transcript, "client-to-host key", KeyLen);
            var h2cKey = Hkdf(secret, transcript, "host-to-client key", KeyLen);
            var c2hNonce = Hkdf(secret, transcript, "client-to-host nonce", NonceLen);
            var h2cNonce = Hkdf(secret, transcript, "host-to-client nonce", NonceLen);

            _sendKey = _isClient ? c2hKey : h2cKey;
            _receiveKey = _isClient ? h2cKey : c2hKey;
            _sendNonceBase = _isClient ? c2hNonce : h2cNonce;
            _receiveNonceBase = _isClient ? h2cNonce : c2hNonce;
            _txGcm = new AesGcm(_sendKey,TagLen);
            _rxGcm = new AesGcm(_receiveKey,TagLen);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool TryDecrypt(byte[] data, int length, out byte[]? plaintext)
    {
        plaintext = null;
        if (!IsEstablished || _receiveKey == null || _receiveNonceBase == null)
            return false;
        if (length < EncryptedHeaderLen + TagLen)
            return false;

        int cipherLen = length - EncryptedHeaderLen - TagLen;
        if (cipherLen > NetworkLimits.MaxDecompressedFrameBytes)
            return false;

        ulong sequence = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(6, 8));
        Span<byte> nonce = stackalloc byte[NonceLen];
        BuildNonce(_receiveNonceBase, sequence, nonce);
        var plain = new byte[cipherLen];

        try
        {
            _rxGcm!.Decrypt(
                nonce,
                data.AsSpan(EncryptedHeaderLen, cipherLen),
                data.AsSpan(EncryptedHeaderLen + cipherLen, TagLen),
                plain,
                data.AsSpan(0, EncryptedHeaderLen));
        }
        catch (CryptographicException)
        {
            return false;
        }

        plaintext = plain;
        return true;
    }

    private static byte[] BuildHello(byte kind, byte[] publicKey, byte[] nonce)
    {
        var output = new byte[Magic.Length + 1 + 1 + 2 + HandshakeNonceLen + publicKey.Length];
        int offset = 0;
        Buffer.BlockCopy(Magic, 0, output, offset, Magic.Length); offset += Magic.Length;
        output[offset++] = Version;
        output[offset++] = kind;
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(offset, 2), (ushort)publicKey.Length); offset += 2;
        Buffer.BlockCopy(nonce, 0, output, offset, HandshakeNonceLen); offset += HandshakeNonceLen;
        Buffer.BlockCopy(publicKey, 0, output, offset, publicKey.Length);
        return output;
    }

    private static bool TryParseHello(byte[] data, int length, byte expectedKind, out byte[] publicKey, out byte[] nonce)
    {
        publicKey = Array.Empty<byte>();
        nonce = Array.Empty<byte>();
        int offset = Magic.Length;
        if (length < offset + 1 + 1 + 2 + HandshakeNonceLen)
            return false;
        if (data[offset++] != Version)
            return false;
        if (data[offset++] != expectedKind)
            return false;

        int publicKeyLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2)); offset += 2;
        if (publicKeyLen <= 0 || publicKeyLen > MaxPublicKeyBytes)
            return false;
        if (length != offset + HandshakeNonceLen + publicKeyLen)
            return false;

        nonce = new byte[HandshakeNonceLen];
        Buffer.BlockCopy(data, offset, nonce, 0, HandshakeNonceLen); offset += HandshakeNonceLen;
        publicKey = new byte[publicKeyLen];
        Buffer.BlockCopy(data, offset, publicKey, 0, publicKeyLen);
        return true;
    }

    private static byte[] HashTranscript(byte[] clientPublic, byte[] serverPublic, byte[] clientNonce, byte[] serverNonce)
    {
        using var sha = SHA256.Create();
        sha.TransformBlock(KdfContext, 0, KdfContext.Length, null, 0);
        sha.TransformBlock(clientNonce, 0, clientNonce.Length, null, 0);
        sha.TransformBlock(serverNonce, 0, serverNonce.Length, null, 0);
        sha.TransformBlock(clientPublic, 0, clientPublic.Length, null, 0);
        sha.TransformFinalBlock(serverPublic, 0, serverPublic.Length);
        return sha.Hash!;
    }

    private static byte[] Hkdf(byte[] ikm, byte[] salt, string info, int length)
    {
        using var extract = new HMACSHA256(salt);
        var prk = extract.ComputeHash(ikm);
        var infoBytes = Encoding.UTF8.GetBytes(info);
        var output = new byte[length];
        var previous = Array.Empty<byte>();
        int written = 0;
        byte counter = 1;

        while (written < length)
        {
            using var expand = new HMACSHA256(prk);
            var input = new byte[previous.Length + infoBytes.Length + 1];
            Buffer.BlockCopy(previous, 0, input, 0, previous.Length);
            Buffer.BlockCopy(infoBytes, 0, input, previous.Length, infoBytes.Length);
            input[input.Length - 1] = counter;
            previous = expand.ComputeHash(input);
            int copy = System.Math.Min(previous.Length, length - written);
            Buffer.BlockCopy(previous, 0, output, written, copy);
            written += copy;
            counter++;
        }

        CryptographicOperations.ZeroMemory(prk);
        return output;
    }

    private static void WriteEncryptedHeader(byte[] output, ulong sequence)
    {
        Buffer.BlockCopy(Magic, 0, output, 0, Magic.Length);
        output[4] = Version;
        output[5] = EncryptedFrame;
        BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(6, 8), sequence);
    }

    private static void BuildNonce(byte[] nonceBase, ulong sequence, Span<byte> nonce)
    {
        nonceBase.AsSpan(0, NonceLen).CopyTo(nonce);
        Span<byte> sequenceBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(sequenceBytes, sequence);
        for (int i = 0; i < sequenceBytes.Length; i++)
            nonce[NonceLen - sequenceBytes.Length + i] ^= sequenceBytes[i];
    }

    private static bool HasMagic(byte[] data, int length)
    {
        if (length < Magic.Length)
            return false;
        for (int i = 0; i < Magic.Length; i++)
        {
            if (data[i] != Magic[i])
                return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ecdh.Dispose();
        if (_sendKey != null) CryptographicOperations.ZeroMemory(_sendKey);
        if (_receiveKey != null) CryptographicOperations.ZeroMemory(_receiveKey);
        if (_sendNonceBase != null) CryptographicOperations.ZeroMemory(_sendNonceBase);
        if (_receiveNonceBase != null) CryptographicOperations.ZeroMemory(_receiveNonceBase);
    }
}
