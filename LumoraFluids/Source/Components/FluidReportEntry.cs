// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Fluids;

// One entry pair on the keeper's report ring: a header and a payload. Header: x = the system's RefID low 32 bits,
// y = kind 8 | RefID bits 32..55, z = the keeper's report number, w = how many entries that report holds. The host
// judges a report whole, so it waits until it has every entry of a number before it looks at any.
//
// Payloads: Cell is a ledger cell in FluidCell's format. Drained is (drain RefID low, high, uL, 0). ContainerLitres
// is (container RefID low, high, mL, 0). PropTouch and PropSnap are (collider RefID low, high, amount bits, 0).
// PendingRemote is (person allocation byte, cell index, nL, 0). CoatProposal: see FluidCoatProposals. -xlinka
public readonly struct FluidReportEntry
{
    public readonly ulong System;
    public readonly FluidReportKind Kind;
    public readonly int Epoch;
    public readonly int Count;
    public readonly int4 Payload;

    public FluidReportEntry(ulong system, FluidReportKind kind, int epoch, int count, int4 payload)
    {
        System = system;
        Kind = kind;
        Epoch = epoch;
        Count = count;
        Payload = payload;
    }

    public static FluidReportEntry Read(int4 header, int4 payload)
    {
        ulong system = (uint)header.x | ((ulong)((uint)header.y >> 8) << 32);
        return new FluidReportEntry(system, (FluidReportKind)(header.y & 255), header.z, header.w, payload);
    }

    public void Write(out int4 header, out int4 payload)
    {
        header = new int4(FluidRings.Low(System), ((int)Kind & 255) | (int)(((System >> 32) & 0xFFFFFF) << 8), Epoch, Count);
        payload = Payload;
    }
}
