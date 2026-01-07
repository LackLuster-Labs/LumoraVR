using System;
using Lumora.Core.Math;

namespace Lumora.Core.Phos;

/// <summary>
/// Blend shape (morph target) data.
/// Stores position/normal/tangent deltas for animation.
/// PhosMeshSys blend shape definition.
/// </summary>
public class PhosBlendShape
{
    /// <summary>Name of this blend shape (e.g., "Smile", "Blink")</summary>
    public string Name { get; set; } = "";

    /// <summary>Frames for this blend shape (usually just one)</summary>
    public PhosBlendShapeFrame[] Frames { get; set; } = Array.Empty<PhosBlendShapeFrame>();

    public PhosBlendShape(string name, int frameCount = 1)
    {
        Name = name;
        Frames = new PhosBlendShapeFrame[frameCount];
    }
}

/// <summary>
/// Single frame of a blend shape.
/// Stores delta values that are added to base vertex data.
/// </summary>
public class PhosBlendShapeFrame
{
    /// <summary>Position deltas (added to vertex positions)</summary>
    public float3[] positions = Array.Empty<float3>();

    /// <summary>Normal deltas (added to vertex normals)</summary>
    public float3[] normals = Array.Empty<float3>();

    /// <summary>Tangent deltas (added to vertex tangents)</summary>
    public float3[] tangents = Array.Empty<float3>();

    public void SetNormalDelta(int index, float3 delta)
    {
        if (normals.Length == 0)
            normals = new float3[positions.Length];
        normals[index] = delta;
    }

    public void SetTangentDelta(int index, float3 delta)
    {
        if (tangents.Length == 0)
            tangents = new float3[positions.Length];
        tangents[index] = delta;
    }
}
