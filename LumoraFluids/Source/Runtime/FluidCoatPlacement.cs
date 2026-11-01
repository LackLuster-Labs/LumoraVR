// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// The people side's own coat proposal kinds, beside the ledger's Bake, Contact and Limb (FluidCoatProposals), in the
// same CoatProposal report entries and the same header (person allocation 8 | body node 8 | substance 4 | kind 4). Each
// carries where on the part it goes, so the view-only wearer's coat lands where the keeper's simulation saw the fluid
// on them, and each is one entry: a keeper's report bytes are judged, and coats share them with the ledger's cells.
//   Placed bake (3): settled goo on a part. x bits 24-31 = cell index bits 16-23; y = local x, local y as halves;
//                    z = local z as a half | uL 16; w = cell index bits 0-15 | tint RGB565 16.
//   Placed wet (4):  fluid that ran over a patch of skin and left it wet (a substance that never anchors, like water).
//                    y = local x, local y as halves; z = local z as a half | particle frames 16; w = nanolitres.
// Local points are in the part bone's space (the space spots use). The cell is the ledger cell the goo counts in, so
// the wearer's Pickup claims exactly that cell. -xlinka
public static class FluidCoatPlacement
{
    public const int KindPlacedBake = 3;
    public const int KindPlacedWet = 4;
    // The largest cell index a placed bake carries.
    public const int MaxCell = (1 << 24) - 1;
    public const float MaxMicrolitres = 65535f;

    public static int4 Bake(User person, BodyNode part, int substance, float3 local, float microlitres, int cell, uint tint)
    {
        int c = Math.Clamp(cell, 0, MaxCell);
        int x = Header(person, part, substance, KindPlacedBake) | ((c >> 16) << 24);
        int ul = (int)Math.Clamp(MathF.Round(microlitres), 1f, MaxMicrolitres);
        return new int4(x, Halves(local.x, local.y), (int)ToHalf(local.z) | (ul << 16), (c & 0xFFFF) | ((int)Rgb565(tint) << 16));
    }

    public static int4 Wet(User person, BodyNode part, int substance, float3 local, int contacts, float volume)
    {
        int n = Math.Clamp(contacts, 0, 65535);
        int nl = (int)Math.Clamp(Math.Round(volume * 1e12), 0, int.MaxValue);
        return new int4(Header(person, part, substance, KindPlacedWet), Halves(local.x, local.y), (int)ToHalf(local.z) | (n << 16), nl);
    }

    public static float3 LocalOf(int4 payload)
        => new(FromHalf(payload.y), FromHalf(payload.y >> 16), FromHalf(payload.z));

    public static float MicrolitresOf(int4 payload) => (payload.z >> 16) & 0xFFFF;

    public static int CellOf(int4 payload) => (payload.w & 0xFFFF) | (((payload.x >> 24) & 0xFF) << 16);

    public static uint TintOf(int4 payload) => FromRgb565((uint)(payload.w >> 16) & 0xFFFF);

    public static int ContactsOf(int4 payload) => (payload.z >> 16) & 0xFFFF;

    // m3
    public static float WetVolumeOf(int4 payload) => payload.w * 1e-12f;

    // The ledger cell goo at a world point counts in, as an index into the system's Cells, for the Pickup that claims
    // it: the cell holding the point, else the fullest one within reach cells of it (goo riding a body is binned where
    // it was a moment ago). -1 when the ledger holds nothing that near.
    public static int LedgerCellAt(FluidSystem? system, float3 world, int reach = 1)
    {
        var slot = system?.Slot;
        if (system == null || slot == null || !float.IsFinite(world.x + world.y + world.z))
            return -1;
        var cells = system.Cells;
        if (cells.Count == 0)
            return -1;
        float size = FluidLedger.CellSizeOf(system);
        FluidCell.CellOf(slot.GlobalPointToLocal(world), size, out int ix, out int iy, out int iz);
        int best = -1, bestRing = int.MaxValue, bestFill = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (FluidCell.Anchor(cell) != FluidCell.NoAnchor || FluidCell.IsEmpty(cell))
                continue;
            int ring = Math.Max(Math.Abs(FluidCell.Ix(cell) - ix), Math.Max(Math.Abs(FluidCell.Iy(cell) - iy), Math.Abs(FluidCell.Iz(cell) - iz)));
            if (ring > reach)
                continue;
            int fill = FluidCell.Fill(cell);
            if (ring < bestRing || (ring == bestRing && fill > bestFill))
            {
                best = i;
                bestRing = ring;
                bestFill = fill;
            }
        }
        return best;
    }

    private static int Header(User person, BodyNode part, int substance, int kind)
        => person.AllocationID.Value | (((int)part & 255) << 8) | ((substance & 15) << 16) | ((kind & 15) << 20);

    private static int Halves(float a, float b) => (int)ToHalf(a) | ((int)ToHalf(b) << 16);

    private static uint ToHalf(float v) => BitConverter.HalfToUInt16Bits((System.Half)(float.IsFinite(v) ? Math.Clamp(v, -65000f, 65000f) : 0f));

    private static float FromHalf(int bits) => (float)BitConverter.UInt16BitsToHalf((ushort)(bits & 0xFFFF));

    private static uint Rgb565(uint rgba)
    {
        uint r = rgba & 255, g = (rgba >> 8) & 255, b = (rgba >> 16) & 255;
        return (r >> 3) | ((g >> 2) << 5) | ((b >> 3) << 11);
    }

    // Opaque: a coat's alpha comes from its substance.
    private static uint FromRgb565(uint c)
    {
        uint r = c & 31, g = (c >> 5) & 63, b = (c >> 11) & 31;
        return ((r << 3) | (r >> 2)) | (((g << 2) | (g >> 4)) << 8) | (((b << 3) | (b >> 2)) << 16) | (255u << 24);
    }
}
