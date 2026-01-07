using Lumora.Core.Math;

namespace Lumora.Core.Phos;

/// <summary>
/// Bone binding data for skinned mesh vertices.
/// Stores up to 4 bone influences per vertex.
/// PhosMeshSys bone binding definition.
/// </summary>
public struct PhosBoneBinding
{
    /// <summary>Indices of bones that influence this vertex (max 4)</summary>
    public float4 boneIndices;  // Using float4 to store 4 bone indices (will cast to int when needed)

    /// <summary>Weight of each bone's influence (should sum to 1.0)</summary>
    public float4 boneWeights;

    public PhosBoneBinding(float4 indices, float4 weights)
    {
        boneIndices = indices;
        boneWeights = weights;
    }
}
