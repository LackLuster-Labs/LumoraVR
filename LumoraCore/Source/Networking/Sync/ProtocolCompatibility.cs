// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Lumora.Core.Networking.Sync;

// Datamodel compatibility proof for the join handshake.
//
// Hashes the datamodel SCHEMA - every registered synced component type and its sync-member layout (member
// names and field types, in canonical order) plus a protocol version - rather than any binary, so peers
// built from the same source agree on the value across platforms. The per-join response salts this schema
// hash with the host's random join nonce so each join produces a distinct value. The host confirms a
// joiner reports the same schema before granting the join. -xlinka
public static class ProtocolCompatibility
{
    // Bump when the wire protocol changes in a way the component schema doesn't capture (message formats,
    // handshake steps, encoders). -xlinka
    // 2: batch headers carry a per-link delta sequence and incremental collection deltas carry the
    //    sender's pre-op element count. Both sit before data an older build expects, so a v1 peer reads
    //    every batch off by a field; the handshake has to reject it instead of letting it try.
    // 3: User lost a member. A user is not a component, so the schema hash never sees its layout and a
    //    v2 peer would read every later user field one member off.
    // 4: User gained its peak rates, versions, dash and view flags and the host's link figures.
    // 5: a full batch header carries the record count of the world it is a slice of, and Rect, float4x4
    //    and float3? fields carry their values instead of an empty marker.
    // 6: int? fields carry their values instead of an empty marker.
    // 7: User gained its chat record and typing flag.
    // 8: int2, int3, bool2-4, float2x2, spherical harmonics, char, decimal and DateTime fields carry their
    //    values instead of an empty marker.
    // 9: User gained its invite asks and the host's invite book.
    // 10: User gained its account kind.
    // 11: the join challenge carries the host's build identity and the answer proves the joiner's (release, schema
    //     and whether its game files are the ones that release shipped), not the schema alone.
    // 12: an array delta can carry only the values appended since the last one (mode 2), which a v11 peer
    //     would read as a sparse update.
    // 13: colliders gained Friction and Bounce, squishy bodies their Mass, and the fluid module's types joined
    //     the schema.
    // 14: a person's machine carries what a script on the host asks of them in a relay under their user root.
    // 15: the item seal component is AvatarProtection now.
    private const int ProtocolVersion = 15;

    private static byte[]? _baseHash;
    private static readonly object _lock = new();

    // Identical across platforms for the same source; cached after first use.
    public static byte[] BaseHash
    {
        get
        {
            if (_baseHash != null)
            {
                return _baseHash;
            }
            lock (_lock)
            {
                _baseHash ??= ComputeBaseHash();
                return _baseHash;
            }
        }
    }

    private static byte[] ComputeBaseHash()
    {
        var types = new List<Type>(ComponentTypeRegistry.GetRegisteredTypes());
        // Order-independent: sort by full name so both peers serialize the schema identically. -xlinka
        types.Sort((a, b) => string.CompareOrdinal(a.FullName ?? a.Name, b.FullName ?? b.Name));

        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(ProtocolVersion);
            writer.Write(types.Count);

            foreach (var type in types)
            {
                writer.Write(type.FullName ?? type.Name);

                WorkerInitInfo info;
                try
                {
                    info = WorkerInitializer.GetInitInfo(type);
                }
                catch
                {
                    writer.Write(-1); // unreadable layout - still part of the digest, deterministically
                    continue;
                }

                var names = info.SyncMemberNames;
                var fields = info.SyncMemberFields;
                int count = names?.Length ?? 0;
                writer.Write(count);
                for (int i = 0; i < count; i++)
                {
                    writer.Write(names![i] ?? string.Empty);
                    writer.Write(fields![i]?.FieldType.FullName ?? string.Empty);
                }
            }
        }

        ms.Position = 0;
        using var sha = SHA256.Create();
        return sha.ComputeHash(ms);
    }

    // The per-join proof: SHA-256(build identity || nonce). The identity folds in BaseHash along with the release and
    // the integrity verdict, so a joiner on the same schema but other game files answers differently. The joiner
    // computes this over the host's challenge nonce; the host checks it against its own with Verify.
    public static byte[] ComputeChallengeResponse(byte[] nonce)
    {
        var baseHash = Lumora.Core.Security.Integrity.BuildIdentity.Bytes;
        int nonceLen = nonce?.Length ?? 0;
        var buffer = new byte[baseHash.Length + nonceLen];
        Buffer.BlockCopy(baseHash, 0, buffer, 0, baseHash.Length);
        if (nonceLen > 0)
        {
            Buffer.BlockCopy(nonce!, 0, buffer, baseHash.Length, nonceLen);
        }
        using var sha = SHA256.Create();
        return sha.ComputeHash(buffer);
    }

    // Constant-time check against our own build identity for the given nonce.
    public static bool Verify(byte[] nonce, byte[]? response)
    {
        if (response == null || response.Length == 0)
        {
            return false;
        }
        var expected = ComputeChallengeResponse(nonce);
        return CryptographicOperations.FixedTimeEquals(expected, response);
    }
}
