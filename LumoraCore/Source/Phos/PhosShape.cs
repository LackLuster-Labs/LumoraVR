using Lumora.Core.Math;
using Lumora.Core.Phos.Collections;

namespace Lumora.Core.Phos;

/// <summary>
/// Abstract base class for procedural shapes (Box, Quad, Sphere, etc.)
/// Manages position, rotation, scale transforms and vertex/triangle collections.
/// PhosMeshSys shape interface.
/// </summary>
public abstract class PhosShape
{
    /// <summary>Position offset of this shape</summary>
    public float3 Position;

    /// <summary>Rotation of this shape</summary>
    public floatQ Rotation = floatQ.Identity;

    /// <summary>Scale of this shape</summary>
    public float3 Scale = float3.One;

    /// <summary>Parent mesh this shape belongs to</summary>
    public PhosMesh Mesh { get; private set; }

    /// <summary>All vertices created by this shape</summary>
    public PhosVertexCollection AllVertices { get; protected set; }

    /// <summary>All triangles created by this shape</summary>
    public PhosTriangleCollection AllTriangles { get; protected set; }

    /// <summary>All points created by this shape</summary>
    public PhosPointCollection AllPoints { get; protected set; }

    // ===== Constructor =====

    protected PhosShape(PhosMesh mesh)
    {
        UpdateMesh(mesh);
        AllVertices = new PhosVertexCollection();
        AllTriangles = new PhosTriangleCollection();
        AllPoints = new PhosPointCollection();
    }

    protected void UpdateMesh(PhosMesh mesh)
    {
        Mesh = mesh;
    }

    // ===== Abstract Methods =====

    /// <summary>
    /// Update the shape's mesh data.
    /// Called when properties change (size, color, etc.)
    /// </summary>
    public abstract void Update();

    // ===== Removal =====

    /// <summary>
    /// Remove this shape from the mesh.
    /// </summary>
    public virtual void Remove()
    {
        RemoveGeometry();
        Mesh = null!;
    }

    /// <summary>
    /// Remove this shape's geometry (vertices and triangles).
    /// </summary>
    public virtual void RemoveGeometry()
    {
        Mesh.RemoveTriangles(AllTriangles);
        Mesh.RemovePoints(AllPoints);
        Mesh.RemoveVertices(AllVertices);
        AllTriangles.Clear();
        AllPoints.Clear();
        AllVertices.Clear();
    }

    // ===== Transform Helpers =====

    /// <summary>
    /// Apply position, rotation, and scale transforms to all vertices.
    /// Optionally transform normals and tangents as well.
    /// </summary>
    protected void TransformVertices(bool normalsAndTangents)
    {
        // Skip if identity transform
        if (Position == float3.Zero && Rotation == floatQ.Identity && Scale == float3.One)
        {
            return;
        }

        for (int i = 0; i < AllVertices.Count; i++)
        {
            PhosVertex vertex = AllVertices[i];

            // Transform position: scale, rotate, translate
            float3 pos = vertex.Position * Scale;
            pos = Rotation * pos + Position;
            vertex.Position = pos;

            if (normalsAndTangents)
            {
                // Transform normal (only rotation)
                if (Mesh.HasNormals)
                {
                    float3 normal = vertex.Normal;
                    vertex.Normal = Rotation * normal;
                }

                // Transform tangent (only rotation)
                if (Mesh.HasTangents)
                {
                    float4 tangent = vertex.Tangent4;
                    vertex.Tangent4 = new float4(Rotation * tangent.xyz, tangent.w);
                }
            }
        }
    }
}
