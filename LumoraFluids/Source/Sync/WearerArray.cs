// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core;
using Lumora.Core.Networking;
using Lumora.Core.Networking.Sync;

namespace Lumora.Fluids;

// A fixed-length array only the person whose user root holds it may write from the wire, and only sparsely. Its full
// first state rides inside the component's creation, which the host never judges; from then on a record has to be
// sparse with every index inside the length. A full snapshot or an append is refused before anything decodes, so a
// million-entry array costs the host a header read. The one growth allowed is from empty to exactly the length, the
// wearer sizing an array it made empty.
//
// The rule reads the header the way SyncArray.Validate does and puts the reader back where it found it, because
// SyncArray.Validate reads that header again right after base.Validate without seeking. -xlinka
public class WearerArray<T> : SyncArray<T>
{
    private const byte ModeFullSnapshot = 0;
    private const byte ModeSparseValues = 1;
    private const byte ModeAppended = 2;

    // With no length given, the first sizing may be at most this long.
    public const int MaxUnsizedLength = 256;

    public WearerArray()
        : this(0)
    {
    }

    public WearerArray(int fixedLength)
    {
        FixedLength = Math.Max(0, fixedLength);
        MarkNonDrivable();
        MarkInboundRules();
    }

    // 0 = whatever the wearer first sized it to. A copy made from the wire learns nothing here and judges against its
    // own count, which its creation set.
    public int FixedLength { get; private set; }

    public bool IsSized => FixedLength > 0 ? Count == FixedLength : Count > 0;

    public bool EnsureLength(int length)
    {
        if (FixedLength == 0 && length > 0)
            FixedLength = length;
        return EnsureLength();
    }

    // Fills an empty array to its length with defaults. The wearer calls it in the frame it attaches the record, after
    // the attach: that append goes out in the creation's own batch, which the host does not judge. Not from inside
    // the member's own initialization, whose writes are never sent.
    public bool EnsureLength()
    {
        if (FixedLength <= 0 || Count != 0)
            return IsSized;
        AddRange(new T[FixedLength]);
        return IsSized;
    }

    protected override MessageValidity ValidateInboundWrite(
        BinaryMessageBatch inboundMessage, BinaryReader reader, List<ValidationGroup.Rule> rules)
    {
        var validity = base.ValidateInboundWrite(inboundMessage, reader, rules);
        if (validity != MessageValidity.Valid)
            return validity;
        var sender = inboundMessage?.SenderUser;
        validity = WearerRule.CheckSender(this, sender);
        if (validity != MessageValidity.Valid)
            return validity;
        return CheckShape(reader, sender);
    }

    private MessageValidity CheckShape(BinaryReader reader, User? sender)
    {
        var stream = reader.BaseStream;
        if (!stream.CanSeek)
            return MessageValidity.Conflict;
        long start = stream.Position;
        try
        {
            int count = Count;
            int limit = FixedLength > 0 ? FixedLength : count;
            byte mode = reader.ReadByte();
            switch (mode)
            {
                case ModeSparseValues:
                {
                    reader.Read7BitEncoded();
                    ulong changed = reader.Read7BitEncoded();
                    if (count == 0 || count != limit || changed > (ulong)limit)
                        return WearerRule.Reshaped(this, sender, "written before it was sized");
                    for (ulong i = 0; i < changed; i++)
                    {
                        ulong index = reader.Read7BitEncoded();
                        if (index >= (ulong)limit)
                            return WearerRule.Reshaped(this, sender, "index past its length");
                        SyncCoder.Decode<T>(reader);
                    }
                    return MessageValidity.Valid;
                }
                case ModeFullSnapshot:
                {
                    ulong size = reader.Read7BitEncoded();
                    return count == 0 && IsSizing(size) ? MessageValidity.Valid : WearerRule.Reshaped(this, sender, "replaced whole");
                }
                case ModeAppended:
                {
                    ulong from = reader.Read7BitEncoded();
                    ulong added = reader.Read7BitEncoded();
                    return count == 0 && from == 0 && IsSizing(added) ? MessageValidity.Valid : WearerRule.Reshaped(this, sender, "grown");
                }
                default:
                    return WearerRule.Reshaped(this, sender, "record of unknown shape");
            }
        }
        catch (Exception)
        {
            return MessageValidity.Conflict;
        }
        finally
        {
            stream.Position = start;
        }
    }

    private bool IsSizing(ulong size)
        => FixedLength > 0 ? size == (ulong)FixedLength : size > 0 && size <= MaxUnsizedLength;
}
