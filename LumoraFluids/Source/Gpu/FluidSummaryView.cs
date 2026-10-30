// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Lumora.Fluids;

// A summary as it came back from the GPU, two frames after the frame it describes (Header.Frame says which). A
// view over a pooled buffer: read it in the update that took it, then hand it back. Each section is clamped to
// its capacity and to the bytes that arrived, so a short or garbled read yields fewer records, never an
// exception. -xlinka
public readonly struct FluidSummaryView
{
    public FluidSummaryView(byte[] buffer, int length)
    {
        Buffer = buffer;
        Length = Math.Min(length, buffer?.Length ?? 0);
    }

    public byte[]? Buffer { get; }

    public int Length { get; }

    public bool IsEmpty => Buffer == null || Length < FluidSummaryHeader.Size;

    public FluidSummaryHeader Header => IsEmpty ? default : MemoryMarshal.Read<FluidSummaryHeader>(Buffer!.AsSpan(0, FluidSummaryHeader.Size));

    public ReadOnlySpan<FluidContactRecord> Contacts
        => Section<FluidContactRecord>(FluidLayout.SummaryContactsOffset, FluidLayout.SummaryContactsCapacity, Header.Contacts);

    public ReadOnlySpan<FluidLimbRecord> Limbs
        => Section<FluidLimbRecord>(FluidLayout.SummaryLimbsOffset, FluidLayout.SummaryLimbsCapacity, Header.Limbs);

    public ReadOnlySpan<FluidBakeCandidate> Bakes
        => Section<FluidBakeCandidate>(FluidLayout.SummaryBakesOffset, FluidLayout.SummaryBakesCapacity, Header.Bakes);

    public ReadOnlySpan<FluidPendingRemote> Pending
        => Section<FluidPendingRemote>(FluidLayout.SummaryPendingOffset, FluidLayout.SummaryPendingCapacity, Header.Pending);

    public ReadOnlySpan<FluidImpulseRecord> Impulses
        => Section<FluidImpulseRecord>(FluidLayout.SummaryImpulsesOffset, FluidLayout.SummaryImpulsesCapacity, Header.Impulses);

    public ReadOnlySpan<FluidVolumeRecord> Volumes
        => Section<FluidVolumeRecord>(FluidLayout.SummaryVolumesOffset, FluidLayout.SummaryVolumesCapacity, Header.Volumes);

    public ReadOnlySpan<FluidSystemStatus> Systems
        => Section<FluidSystemStatus>(FluidLayout.SummarySystemsOffset, FluidLayout.SummarySystemsCapacity, Header.Systems);

    public ReadOnlySpan<FluidQueryResult> Queries
        => Section<FluidQueryResult>(FluidLayout.SummaryQueriesOffset, FluidLayout.SummaryQueriesCapacity, Header.Queries);

    public ReadOnlySpan<FluidPropEvent> Props
        => Section<FluidPropEvent>(FluidLayout.SummaryPropsOffset, FluidLayout.SummaryPropsCapacity, Header.Props);

    public ReadOnlySpan<FluidLedgerEntry> Bins
        => Section<FluidLedgerEntry>(FluidLayout.SummaryBinsOffset, FluidLayout.SummaryBinsCapacity, Header.Bins);

    private ReadOnlySpan<T> Section<T>(int offset, int capacity, uint count) where T : struct
    {
        if (IsEmpty || offset >= Length)
            return default;
        int size = Unsafe.SizeOf<T>();
        int n = (int)Math.Min(count, (uint)capacity);
        n = Math.Min(n, (Length - offset) / size);
        return n <= 0 ? default : MemoryMarshal.Cast<byte, T>(Buffer!.AsSpan(offset, n * size));
    }
}
