// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// Packs and unpacks a ledger cell: x = ix, y = iz, z = iy (signed 16) and fill (16), w = origin 8, substance 4,
// flags 4 (Moving, OnSurface, Fading, Curing), anchor 8, age in 16 s steps 8. Fill is a fraction of the cell's
// volume, so a cell means the same litres on every machine whatever spacing it simulates at. Coordinates are in
// the system slot's local space, or the anchor slot's when the anchor index is set. -xlinka
public static class FluidCell
{
    public const int FlagMoving = 1;
    public const int FlagOnSurface = 2;
    public const int FlagFading = 4;
    public const int FlagCuring = 8;

    public const int FillMax = 65535;

    // Anchor index 0 is the system's own space; 1..255 are AnchorSlots entries (index + 1).
    public const int NoAnchor = 0;

    public static int4 Pack(int ix, int iy, int iz, int fill, int origin, int substance, int flags, int anchor, int age)
    {
        int f = Math.Clamp(fill, 0, FillMax);
        int y = Math.Clamp(iy, short.MinValue, short.MaxValue);
        int w = (origin & 255) | ((substance & 15) << 8) | ((flags & 15) << 12) | ((anchor & 255) << 16) | ((age & 255) << 24);
        return new int4(ix, iz, (y << 16) | f, w);
    }

    public static int Ix(int4 cell) => cell.x;

    public static int Iz(int4 cell) => cell.y;

    // arithmetic shift keeps the sign of the upper half
    public static int Iy(int4 cell) => cell.z >> 16;

    public static int Fill(int4 cell) => cell.z & 0xFFFF;

    public static int Origin(int4 cell) => cell.w & 255;

    public static int Substance(int4 cell) => (cell.w >> 8) & 15;

    public static int Flags(int4 cell) => (cell.w >> 12) & 15;

    public static int Anchor(int4 cell) => (cell.w >> 16) & 255;

    public static int Age(int4 cell) => (int)((uint)cell.w >> 24);

    public static bool IsMoving(int4 cell) => (Flags(cell) & FlagMoving) != 0;

    public static int4 WithFill(int4 cell, int fill)
        => new(cell.x, cell.y, (cell.z & unchecked((int)0xFFFF0000)) | Math.Clamp(fill, 0, FillMax), cell.w);

    public static int4 WithOrigin(int4 cell, int origin) => new(cell.x, cell.y, cell.z, (cell.w & ~255) | (origin & 255));

    public static int4 WithFlags(int4 cell, int flags) => new(cell.x, cell.y, cell.z, (cell.w & ~(15 << 12)) | ((flags & 15) << 12));

    public static int4 WithAge(int4 cell, int age) => new(cell.x, cell.y, cell.z, (cell.w & 0x00FFFFFF) | ((age & 255) << 24));

    // What makes two entries the same cell: its coordinates and the space they are in.
    public static (int X, int Y, int Z, int Anchor) Key(int4 cell) => (cell.x, Iy(cell), cell.y, Anchor(cell));

    public static bool SameKey(int4 a, int4 b) => a.x == b.x && a.y == b.y && Iy(a) == Iy(b) && Anchor(a) == Anchor(b);

    // Everything but fill and age: a change here is written whatever the fill did.
    public static bool SameKind(int4 a, int4 b) => (a.w & 0x0000FFFF) == (b.w & 0x0000FFFF);

    public static double CellLitres(float cellSize) => (double)cellSize * cellSize * cellSize * 1000.0;

    public static double Litres(int4 cell, float cellSize) => Fill(cell) / (double)FillMax * CellLitres(cellSize);

    public static int FillOf(double litres, float cellSize)
    {
        double whole = CellLitres(cellSize);
        if (whole <= 0)
            return 0;
        return (int)Math.Clamp(Math.Round(litres / whole * FillMax), 0, FillMax);
    }

    public static float3 Centre(int4 cell, float cellSize)
        => new((cell.x + 0.5f) * cellSize, (Iy(cell) + 0.5f) * cellSize, (cell.y + 0.5f) * cellSize);

    public static void CellOf(float3 local, float cellSize, out int ix, out int iy, out int iz)
    {
        ix = (int)MathF.Floor(local.x / cellSize);
        iy = (int)MathF.Floor(local.y / cellSize);
        iz = (int)MathF.Floor(local.z / cellSize);
    }

    public static bool IsEmpty(int4 cell) => Fill(cell) == 0;
}
