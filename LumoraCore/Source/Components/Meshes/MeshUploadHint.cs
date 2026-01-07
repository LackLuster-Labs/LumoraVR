using System;
using Lumora.Core.Phos;

namespace Lumora.Core.Components.Meshes;

/// <summary>
/// Tracks which mesh data channels have changed.
/// Allows selective GPU upload for bandwidth optimization.
/// </summary>
public struct MeshUploadHint
{
    [Flags]
    public enum Flag
    {
        /// <summary>Vertex positions changed</summary>
        Geometry = 1 << 0,

        /// <summary>Vertex normals changed</summary>
        Normals = 1 << 1,

        /// <summary>Vertex tangents changed</summary>
        Tangents = 1 << 2,

        /// <summary>Vertex colors changed</summary>
        Colors = 1 << 3,

        /// <summary>UV channel 0 changed</summary>
        UV0 = 1 << 4,

        /// <summary>UV channel 1 changed</summary>
        UV1 = 1 << 5,

        /// <summary>UV channel 2 changed</summary>
        UV2 = 1 << 6,

        /// <summary>UV channel 3 changed</summary>
        UV3 = 1 << 7,

        /// <summary>Bone bindings changed</summary>
        BoneBindings = 1 << 8,

        /// <summary>Mesh updates frequently (optimize for dynamic)</summary>
        Dynamic = 1 << 9,

        /// <summary>Keep CPU-side copy readable</summary>
        Readable = 1 << 10
    }

    private Flag flags;

    // ===== Indexer =====

    /// <summary>
    /// Get or set whether a specific flag is enabled.
    /// </summary>
    public bool this[Flag flag]
    {
        get => (flags & flag) != 0;
        set
        {
            if (value)
                flags |= flag;
            else
                flags &= ~flag;
        }
    }

    // ===== Methods =====

    /// <summary>
    /// Set all flags.
    /// </summary>
    public void SetAll()
    {
        flags = (Flag)0xFFFF;
    }

    /// <summary>
    /// Clear all flags.
    /// </summary>
    public void Clear()
    {
        flags = 0;
    }

    /// <summary>
    /// Reset flags for channels that don't exist in the mesh.
    /// Prevents uploading unused data.
    /// </summary>
    public void ResetUnusedChannels(PhosMesh mesh)
    {
        if (!mesh.HasNormals)
            this[Flag.Normals] = false;

        if (!mesh.HasTangents)
            this[Flag.Tangents] = false;

        if (!mesh.HasColors)
            this[Flag.Colors] = false;

        if (!mesh.HasUV0s)
            this[Flag.UV0] = false;

        if (mesh.UVChannelCount < 2)
            this[Flag.UV1] = false;

        if (mesh.UVChannelCount < 3)
            this[Flag.UV2] = false;

        if (mesh.UVChannelCount < 4)
            this[Flag.UV3] = false;

        if (!mesh.HasBoneBindings)
            this[Flag.BoneBindings] = false;
    }

    /// <summary>
    /// Check if any flag is set.
    /// </summary>
    public bool HasAnyFlag()
    {
        return flags != 0;
    }

    /// <summary>
    /// Get all enabled flags.
    /// </summary>
    public Flag GetFlags()
    {
        return flags;
    }
}
