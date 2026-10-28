// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using Lumora.Core.Networking;
using Lumora.Nexus.Protocol;
using Lumora.Nexus.Transport;

namespace Lumora.Core.Networking.Messages;

public enum ControlMessageType : byte
{
    JoinRequest = 0,

    // Carries UserID, MaxUsers, WorldTime, StateVersion.
    JoinGrant = 1,

    JoinStartDelta = 2,

    Leave = 3,

    Kick = 4,

    Ping = 5,

    Pong = 6
}

public class ControlMessage
{
    public ControlMessageType SubType;
    public byte[] Data = null!;

    public MessageType Type => MessageType.Control;
    public bool Reliable => true;

    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)Type);
        writer.Write((byte)SubType);

        if (Data != null && Data.Length > 0)
        {
            writer.Write(Data.Length);
            writer.Write(Data);
        }
        else
        {
            writer.Write(0);
        }

        return ms.ToArray();
    }

    public static ControlMessage Decode(BinaryReader reader)
    {
        var message = new ControlMessage
        {
            SubType = (ControlMessageType)reader.ReadByte()
        };

        // Bounded read: a peer cannot make us allocate >MaxControlMessagePayload
        // by declaring a huge length. Empty payloads are allowed (length == 0).
        message.Data = reader.ReadBoundedBytesInt32(NetworkLimits.MaxControlMessagePayload);
        return message;
    }
}

public struct JoinRequestData
{
    public string UserName;
    public string MachineID;
    public string UserID;
    public byte HeadDevice;

    // DER SubjectPublicKeyInfo for this client's machine key. MachineID must be the hash of this, and
    // the joiner has to sign the host's challenge nonce with the matching private key to get in. This
    // is what makes MachineID actually mean something instead of being free text. -xlinka
    public byte[] MachinePublicKey;

    // Optional account identity. When the joiner is signed into an account, these name the
    // account and the login session whose public key it published to the backend. The host fetches that
    // key from the cloud and verifies the account signature (in JoinAuthenticate). Empty = guest, machine
    // key only. We do NOT send the account public key itself, the host gets the trusted one from the
    // cloud so a joiner can't present a key of their choosing. -xlinka
    public string AccountUserId;
    public string AccountSessionId;

    // A random nonce the joiner generates so the HOST can prove ITS identity back: the host signs this in
    // JoinChallenge and the joiner verifies it before trusting the host. -xlinka
    public byte[] HostVerificationToken;

    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(UserName ?? "");
        writer.Write(MachineID ?? "");
        writer.Write(UserID ?? "");
        writer.Write(HeadDevice);
        writer.Write(MachinePublicKey?.Length ?? 0);
        if (MachinePublicKey != null && MachinePublicKey.Length > 0)
            writer.Write(MachinePublicKey);
        writer.Write(AccountUserId ?? "");
        writer.Write(AccountSessionId ?? "");
        writer.Write(HostVerificationToken?.Length ?? 0);
        if (HostVerificationToken != null && HostVerificationToken.Length > 0)
            writer.Write(HostVerificationToken);

        return ms.ToArray();
    }

    public static JoinRequestData Decode(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var result = new JoinRequestData
        {
            UserName = reader.ReadString(),
            MachineID = reader.ReadString(),
            UserID = reader.ReadString(),
            HeadDevice = reader.ReadByte(),
            MachinePublicKey = Array.Empty<byte>(),
            AccountUserId = "",
            AccountSessionId = "",
            HostVerificationToken = Array.Empty<byte>()
        };

        // Everything past HeadDevice is appended and read tolerantly so a short/legacy payload still
        // decodes. Lengths are bounded so a hostile peer can't make us allocate off a declared size. -xlinka
        if (ms.Position < ms.Length)
        {
            int keyLen = reader.ReadInt32();
            if (keyLen > 0 && keyLen <= 8192)
                result.MachinePublicKey = reader.ReadBytes(keyLen);
        }
        if (ms.Position < ms.Length)
            result.AccountUserId = reader.ReadString();
        if (ms.Position < ms.Length)
            result.AccountSessionId = reader.ReadString();
        if (ms.Position < ms.Length)
        {
            int tokLen = reader.ReadInt32();
            if (tokLen > 0 && tokLen <= 1024)
                result.HostVerificationToken = reader.ReadBytes(tokLen);
        }

        return result;
    }
}

public struct JoinGrantData
{
    public ulong AssignedUserID;
    public ulong AllocationIDStart;
    public ulong AllocationIDEnd;
    public int MaxUsers;
    public double WorldTime;
    public ulong StateVersion;

    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(AssignedUserID);
        writer.Write(AllocationIDStart);
        writer.Write(AllocationIDEnd);
        writer.Write(MaxUsers);
        writer.Write(WorldTime);
        writer.Write(StateVersion);

        return ms.ToArray();
    }

    public static JoinGrantData Decode(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        return new JoinGrantData
        {
            AssignedUserID = reader.ReadUInt64(),
            AllocationIDStart = reader.ReadUInt64(),
            AllocationIDEnd = reader.ReadUInt64(),
            MaxUsers = reader.ReadInt32(),
            WorldTime = reader.ReadDouble(),
            StateVersion = reader.ReadUInt64()
        };
    }
}

public struct JoinRejectData
{
    public string Reason;

    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(Reason ?? "Join rejected");

        return ms.ToArray();
    }

    public static JoinRejectData Decode(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        return new JoinRejectData
        {
            Reason = reader.ReadString()
        };
    }
}

// Host -> joiner: a random nonce the joiner must sign with its machine private key to prove it holds
// the key behind the MachineID it claimed. Fresh per join, so a captured signature can't be replayed
// to another join (different nonce). -xlinka
public struct JoinChallengeData
{
    public byte[] Nonce;

    // Host's own identity, so the joiner can verify the host before answering. HostMachineId
    // must hash to HostMachinePublicKey, and HostMachineSignature is the host signing the joiner's
    // HostVerificationToken with its machine key. -xlinka
    public string HostMachineId;
    public byte[] HostMachinePublicKey;
    public byte[] HostMachineSignature;

    // The host's build identity and whether its own game files are modified, so a joiner on other files can stop
    // here and say which side differs instead of answering into a refusal. -xlinka
    public string HostBuild;
    public bool HostModified;

    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(Nonce?.Length ?? 0);
        if (Nonce != null && Nonce.Length > 0)
            writer.Write(Nonce);
        writer.Write(HostMachineId ?? "");
        writer.Write(HostMachinePublicKey?.Length ?? 0);
        if (HostMachinePublicKey != null && HostMachinePublicKey.Length > 0)
            writer.Write(HostMachinePublicKey);
        writer.Write(HostMachineSignature?.Length ?? 0);
        if (HostMachineSignature != null && HostMachineSignature.Length > 0)
            writer.Write(HostMachineSignature);
        writer.Write(HostBuild ?? "");
        writer.Write(HostModified);

        return ms.ToArray();
    }

    public static JoinChallengeData Decode(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var result = new JoinChallengeData
        {
            Nonce = Array.Empty<byte>(),
            HostMachineId = "",
            HostMachinePublicKey = Array.Empty<byte>(),
            HostMachineSignature = Array.Empty<byte>(),
            HostBuild = ""
        };
        int len = reader.ReadInt32();
        if (len > 0 && len <= 1024)
            result.Nonce = reader.ReadBytes(len);
        // Host identity fields are appended; tolerate their absence for a short/legacy payload. -xlinka
        if (ms.Position < ms.Length)
            result.HostMachineId = reader.ReadString();
        if (ms.Position < ms.Length)
        {
            int keyLen = reader.ReadInt32();
            if (keyLen > 0 && keyLen <= 8192)
                result.HostMachinePublicKey = reader.ReadBytes(keyLen);
        }
        if (ms.Position < ms.Length)
        {
            int sigLen = reader.ReadInt32();
            if (sigLen > 0 && sigLen <= 4096)
                result.HostMachineSignature = reader.ReadBytes(sigLen);
        }
        if (ms.Position < ms.Length)
            result.HostBuild = reader.ReadString();
        if (ms.Position < ms.Length)
            result.HostModified = reader.ReadBoolean();
        return result;
    }
}

// Joiner -> host: the joiner's signature over the challenge nonce, made with its machine private key.
// The host verifies it against the public key from the JoinRequest. -xlinka
public struct JoinAuthenticateData
{
    public byte[] Signature;          // machine-key signature over the challenge nonce
    public byte[] AccountSignature;   // optional account-key signature over the same nonce, when signed in
    public byte[] CompatHash;         // salted build proof: hash(build identity || challenge nonce)
    public bool Modified;             // the joiner's own game files are modified, so a refusal can say which side

    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(Signature?.Length ?? 0);
        if (Signature != null && Signature.Length > 0)
            writer.Write(Signature);
        writer.Write(AccountSignature?.Length ?? 0);
        if (AccountSignature != null && AccountSignature.Length > 0)
            writer.Write(AccountSignature);
        writer.Write(CompatHash?.Length ?? 0);
        if (CompatHash != null && CompatHash.Length > 0)
            writer.Write(CompatHash);
        writer.Write(Modified);

        return ms.ToArray();
    }

    public static JoinAuthenticateData Decode(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var result = new JoinAuthenticateData
        {
            Signature = Array.Empty<byte>(),
            AccountSignature = Array.Empty<byte>(),
            CompatHash = Array.Empty<byte>()
        };
        int len = reader.ReadInt32();
        if (len > 0 && len <= 4096)
            result.Signature = reader.ReadBytes(len);
        // Account signature is appended; tolerate its absence (guest / legacy). -xlinka
        if (ms.Position < ms.Length)
        {
            int accLen = reader.ReadInt32();
            if (accLen > 0 && accLen <= 4096)
                result.AccountSignature = reader.ReadBytes(accLen);
        }
        // Compat hash is appended last; tolerate its absence so the host can fail-closed on its own terms. -xlinka
        if (ms.Position < ms.Length)
        {
            int hashLen = reader.ReadInt32();
            if (hashLen > 0 && hashLen <= 4096)
                result.CompatHash = reader.ReadBytes(hashLen);
        }
        if (ms.Position < ms.Length)
            result.Modified = reader.ReadBoolean();
        return result;
    }
}
