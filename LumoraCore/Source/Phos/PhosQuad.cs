using Lumora.Core.Math;

namespace Lumora.Core.Phos;

/// <summary>
/// Procedural quad mesh generator.
/// Creates a quad with 4 vertices and 2 triangles.
/// Supports per-vertex colors and UV scaling/offset.
/// PhosMeshSys quad primitive.
/// </summary>
public class PhosQuad : PhosShape
{
    public const int TOTAL_VERTICES = 4;
    public const int TOTAL_TRIANGLES = 2;

    /// <summary>Parent submesh</summary>
    public readonly PhosTriangleSubmesh Submesh;

    /// <summary>Size of the quad</summary>
    public float2 Size = float2.One;

    /// <summary>Pivot offset (shifts quad origin)</summary>
    public float2 Pivot;

    /// <summary>UV scale for texture mapping</summary>
    public float2 UVScale = float2.One;

    /// <summary>UV offset for texture mapping</summary>
    public float2 UVOffset = float2.Zero;

    /// <summary>First vertex of this quad</summary>
    public PhosVertex FirstVertex;

    /// <summary>First triangle (upper-left to lower-right)</summary>
    public PhosTriangle Triangle0;

    /// <summary>Second triangle (lower-right to lower-left)</summary>
    public PhosTriangle Triangle1;

    /// <summary>Use per-vertex colors</summary>
    public bool UseColors;

    /// <summary>Color for upper-left corner</summary>
    public color UpperLeftColor;

    /// <summary>Color for upper-right corner</summary>
    public color UpperRightColor;

    /// <summary>Color for lower-left corner</summary>
    public color LowerLeftColor;

    /// <summary>Color for lower-right corner</summary>
    public color LowerRightColor;

    /// <summary>
    /// Set/get solid color (all corners same color).
    /// </summary>
    public color? Color
    {
        get
        {
            if (!UseColors)
                return null;
            return UpperLeftColor;
        }
        set
        {
            if (!value.HasValue)
            {
                UseColors = false;
                return;
            }
            UseColors = true;
            UpperLeftColor = value.Value;
            UpperRightColor = value.Value;
            LowerLeftColor = value.Value;
            LowerRightColor = value.Value;
        }
    }

    // ===== Constructors =====

    /// <summary>
    /// Create a quad from an existing first vertex.
    /// Used when recreating quad from serialized data.
    /// </summary>
    public PhosQuad(PhosVertex firstVertex) : base(firstVertex.Mesh)
    {
        FirstVertex = firstVertex;
        Submesh = null!; // Will be set later
    }

    /// <summary>
    /// Create a new quad by adding geometry to a submesh.
    /// </summary>
    public PhosQuad(PhosTriangleSubmesh submesh) : base(submesh.Mesh)
    {
        Submesh = submesh;

        // Enable required vertex attributes BEFORE adding vertices
        // This ensures arrays are properly sized when vertices are added
        Mesh.HasUV0s = true;
        Mesh.HasNormals = true;
        Mesh.HasTangents = true;

        // Add 4 vertices
        Mesh.IncreaseVertexCount(TOTAL_VERTICES);
        FirstVertex = Mesh.GetVertex(Mesh.VertexCount - TOTAL_VERTICES);
        int firstVertexIndex = FirstVertex.IndexUnsafe;

        // Add 2 triangles as a quad
        var quadTriangles = Submesh.AddQuadAsTriangles(
            firstVertexIndex,
            firstVertexIndex + 1,
            firstVertexIndex + 2,
            firstVertexIndex + 3
        );
        Triangle0 = quadTriangles.first;
        Triangle1 = quadTriangles.second;
    }

    // ===== Quad Methods =====

    /// <summary>
    /// Remove this quad from the mesh.
    /// </summary>
    public override void Remove()
    {
        // Remove triangles through submesh
        Submesh.Remove(Triangle1);
        Submesh.Remove(Triangle0);
        Mesh.RemoveVertices(FirstVertex.Index, TOTAL_VERTICES, updateSubmeshes: false);
        base.Remove();
    }

    /// <summary>
    /// Update the quad's vertex data based on current properties.
    /// </summary>
    public override void Update()
    {
        Mesh.HasColors = UseColors;
        UpdateUnsafe(FirstVertex.Index);
    }

    /// <summary>
    /// Update quad vertex data without validation.
    /// </summary>
    public void UpdateUnsafe(int index)
    {
        PhosMesh mesh = Mesh;
        float2 halfSize = Size / 2f;

        // Ensure color buffer exists when using vertex colors (IncreaseVertexCount was called before UseColors was set)
        if (UseColors)
            mesh.CheckColors();

        for (int i = 0; i < 4; i++)
        {
            // Corner position and UV
            float3 cornerPos;
            float2 cornerUV;

            switch (i)
            {
                case 0: // Upper left
                    cornerPos = new float3(-halfSize.x, halfSize.y, 0f);
                    cornerUV = new float2(0f, 1f);
                    break;
                case 1: // Upper right
                    cornerPos = new float3(halfSize.x, halfSize.y, 0f);
                    cornerUV = new float2(1f, 1f);
                    break;
                case 2: // Lower right
                    cornerPos = new float3(halfSize.x, -halfSize.y, 0f);
                    cornerUV = new float2(1f, 0f);
                    break;
                case 3: // Lower left
                    cornerPos = new float3(-halfSize.x, -halfSize.y, 0f);
                    cornerUV = new float2(0f, 0f);
                    break;
                default:
                    cornerPos = float3.Zero;
                    cornerUV = float2.Zero;
                    break;
            }

            // Apply pivot offset
            cornerPos -= new float3(Pivot.x, Pivot.y, 0f);

            // Apply UV scale and offset
            cornerUV = cornerUV * UVScale + UVOffset;

            // Apply rotation and position
            cornerPos = Rotation * cornerPos + Position;

            int vertexIndex = index + i;

            // Set vertex data
            mesh.RawPositions[vertexIndex] = cornerPos;
            mesh.RawUV0s[vertexIndex] = cornerUV;
            mesh.RawNormals[vertexIndex] = Rotation * float3.Backward;
            mesh.RawTangents[vertexIndex] = new float4(Rotation * float3.Right, -1f);

            if (UseColors)
            {
                // Safety: if color array was sized before HasColors was enabled, resize it now
                if (mesh.RawColors.Length <= vertexIndex)
                {
                    mesh.HasColors = false;
                    mesh.CheckColors();
                }
                mesh.RawColors[vertexIndex] = GetColor(i);
            }
        }
    }

    /// <summary>
    /// Get color for a specific corner.
    /// </summary>
    public color GetColor(int index)
    {
        return index switch
        {
            0 => UpperLeftColor,
            1 => UpperRightColor,
            2 => LowerRightColor,
            3 => LowerLeftColor,
            _ => color.White,
        };
    }
}
