// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Simulation.SoftBody;

[StructLayout(LayoutKind.Sequential)]
public struct ClothPoint
{
    public float3 Position;
    public float Pinned;
    public float3 Previous;
    public float Pull;
    public float3 Goal;
    public float Area;
    public int Pin;
    public float Distance;
    public int FirstTriangle, TriangleCount;
}

[StructLayout(LayoutKind.Sequential)]
public struct ClothEdge
{
    public int A, B;
    public float Rest, Ka, Kb;
    public float Reserved0, Reserved1, Reserved2;
}

public sealed class ClothComputeData
{
    public ClothPoint[] Points = Array.Empty<ClothPoint>();
    public ClothEdge[] Edges = Array.Empty<ClothEdge>();
    public int[] Triangles = Array.Empty<int>();
    public int[] Adjacent = Array.Empty<int>();
    public int[] GroupStarts = Array.Empty<int>();
    public int StretchGroups;
}

public struct ClothComputeStep
{
    public float Dt, PreviousDt, Damping, Ratio, MaxMove, Stretch, Bend;
    public float3 Gravity, Wind;
    public int Iterations;
    public bool Moving;
}

public interface IClothCompute : IDisposable
{
    long AllocatedBytes { get; }
    string Status { get; }
    bool Step(ClothComputeData data, in ClothComputeStep step);
}

public static class ClothCompute
{
    public static Func<IClothCompute>? Create { get; set; }
}
