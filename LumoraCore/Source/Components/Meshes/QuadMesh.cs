using System;
using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Lumora.Core.Components.Meshes;

/// <summary>
/// Procedural quad mesh component.
/// Generates quad geometry with support for dual-sided rendering and per-vertex colors.
/// </summary>
public class QuadMesh : ProceduralMesh
{
    // ===== Sync Fields =====

    /// <summary>Rotation of the quad</summary>
    public readonly Sync<floatQ> Rotation;

    /// <summary>Size of the quad (width, height)</summary>
    public readonly Sync<float2> Size;

    /// <summary>UV scale for texture mapping</summary>
    public readonly Sync<float2> UVScale;

    /// <summary>Scale UVs proportionally with size</summary>
    public readonly Sync<bool> ScaleUVWithSize;

    /// <summary>UV offset for texture mapping</summary>
    public readonly Sync<float2> UVOffset;

    /// <summary>Render both sides of the quad</summary>
    public readonly Sync<bool> DualSided;

    /// <summary>Use per-vertex colors</summary>
    public readonly Sync<bool> UseVertexColors;

    /// <summary>Color for upper-left corner</summary>
    public readonly Sync<color> UpperLeftColor;

    /// <summary>Color for upper-right corner</summary>
    public readonly Sync<color> UpperRightColor;

    /// <summary>Color for lower-left corner</summary>
    public readonly Sync<color> LowerLeftColor;

    /// <summary>Color for lower-right corner</summary>
    public readonly Sync<color> LowerRightColor;

    // ===== Private State =====

    private PhosQuad? quad;
    private PhosQuad? otherSide;  // For dual-sided rendering

    private floatQ _rotation;
    private float2 _size;
    private float2 _uvScale;
    private bool _scaleUVWithSize;
    private float2 _uvOffset;
    private bool _dualSided;
    private bool _useColors;
    private color _ulColor, _urColor, _llColor, _lrColor;

    // ===== Constructor =====

    public QuadMesh()
    {
        Rotation = new Sync<floatQ>(this, floatQ.Identity);
        Size = new Sync<float2>(this, float2.One);
        UVScale = new Sync<float2>(this, float2.One);
        ScaleUVWithSize = new Sync<bool>(this, false);
        UVOffset = new Sync<float2>(this, float2.Zero);
        DualSided = new Sync<bool>(this, false);
        UseVertexColors = new Sync<bool>(this, true);
        UpperLeftColor = new Sync<color>(this, color.White);
        UpperRightColor = new Sync<color>(this, color.White);
        LowerLeftColor = new Sync<color>(this, color.White);
        LowerRightColor = new Sync<color>(this, color.White);
    }

    // ===== Lifecycle =====

    public override void OnAwake()
    {
        base.OnAwake();

        // Subscribe to property changes
        SubscribeToChanges(Rotation);
        SubscribeToChanges(Size);
        SubscribeToChanges(UVScale);
        SubscribeToChanges(ScaleUVWithSize);
        SubscribeToChanges(UVOffset);
        SubscribeToChanges(DualSided);
        SubscribeToChanges(UseVertexColors);
        SubscribeToChanges(UpperLeftColor);
        SubscribeToChanges(UpperRightColor);
        SubscribeToChanges(LowerLeftColor);
        SubscribeToChanges(LowerRightColor);
    }

    // ===== Mesh Generation =====

    protected override void PrepareAssetUpdateData()
    {
        // Copy sync values to local variables (thread-safe)
        _rotation = Rotation.Value;
        _size = Size.Value;
        _uvScale = UVScale.Value;
        _scaleUVWithSize = ScaleUVWithSize.Value;
        _uvOffset = UVOffset.Value;
        _dualSided = DualSided.Value;
        _useColors = UseVertexColors.Value;
        _ulColor = UpperLeftColor.Value;
        _urColor = UpperRightColor.Value;
        _llColor = LowerLeftColor.Value;
        _lrColor = LowerRightColor.Value;
    }

    protected override void UpdateMeshData(PhosMesh mesh)
    {
        bool geometryChanged = false;

        // ===== Front Side =====

        if (quad == null)
        {
            var submesh = new PhosTriangleSubmesh(mesh);
            mesh.Submeshes.Add(submesh);
            quad = new PhosQuad(submesh);
            geometryChanged = true;
        }

        // Check if color mode changed (affects vertex data)
        if (quad.UseColors != _useColors)
        {
            quad.UseColors = _useColors;
            geometryChanged = true;
        }

        // Update quad properties
        quad.Size = _size;
        quad.Rotation = _rotation;

        // Scale UVs with size if requested
        if (_scaleUVWithSize)
        {
            quad.UVScale = _uvScale * _size;
        }
        else
        {
            quad.UVScale = _uvScale;
        }

        quad.UVOffset = _uvOffset;

        // Set colors
        quad.UpperLeftColor = _ulColor;
        quad.UpperRightColor = _urColor;
        quad.LowerLeftColor = _llColor;
        quad.LowerRightColor = _lrColor;

        // Regenerate front side vertex data
        quad.Update();

        // ===== Back Side (if dual-sided) =====

        if (_dualSided)
        {
            if (otherSide == null)
            {
                var submesh = new PhosTriangleSubmesh(mesh);
                mesh.Submeshes.Add(submesh);
                otherSide = new PhosQuad(submesh);
                geometryChanged = true;
            }

            // Check if color mode changed
            if (otherSide.UseColors != _useColors)
            {
                otherSide.UseColors = _useColors;
                geometryChanged = true;
            }

            // Update back side properties
            otherSide.Size = _size;

            // Rotate back side 180 degrees around Y axis
            otherSide.Rotation = _rotation * floatQ.AxisAngleRad(float3.Up, MathF.PI);

            if (_scaleUVWithSize)
            {
                otherSide.UVScale = _uvScale * _size;
            }
            else
            {
                otherSide.UVScale = _uvScale;
            }

            otherSide.UVOffset = _uvOffset;

            // Copy colors
            otherSide.UpperLeftColor = _ulColor;
            otherSide.UpperRightColor = _urColor;
            otherSide.LowerLeftColor = _llColor;
            otherSide.LowerRightColor = _lrColor;

            // Regenerate back side vertex data
            otherSide.Update();
        }
        else if (otherSide != null)
        {
            // Remove back side if dual-sided was disabled
            otherSide.Remove();
            otherSide = null;
            geometryChanged = true;
        }

        uploadHint[MeshUploadHint.Flag.Geometry] = geometryChanged;
    }

    protected override void ClearMeshData()
    {
        quad = null;
        otherSide = null;
    }

    // ===== Utility Properties =====

    /// <summary>
    /// Set/get solid color (all corners same color).
    /// </summary>
    public color Color
    {
        get => UpperLeftColor.Value;
        set
        {
            UpperLeftColor.Value = value;
            UpperRightColor.Value = value;
            LowerLeftColor.Value = value;
            LowerRightColor.Value = value;
        }
    }

    /// <summary>
    /// Get/set the facing direction of the quad.
    /// </summary>
    public float3 Facing
    {
        get
        {
            floatQ q = Rotation.Value;
            return q * float3.Backward;
        }
        set
        {
            Rotation.Value = floatQ.LookRotation(-value, float3.Up);
        }
    }
}
