using System;
using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Core.Assets;

/// <summary>
/// Base class for shared material assets (Material and MaterialPropertyBlock).
/// Provides common property-setting methods and synchronization with shader uniforms.
/// </summary>
public abstract class SharedMaterialBase<C> : DynamicImplementableAsset<C>, ISharedMaterialPropertySetter
    where C : class, ISharedMaterialHook
{
    // ===== PROPERTY INITIALIZATION =====

    public void InitializeProperties(List<MaterialProperty> properties, Action onDone)
    {
        if (Hook != null)
        {
            Hook.InitializeProperties(properties, onDone);
            return;
        }

        // Fallback if no hook - initialize with default IDs
        foreach (MaterialProperty property in properties)
        {
            if (!property.Initialized)
            {
                property.Initialize(0);
            }
        }
        onDone();
    }

    // ===== FLOAT PROPERTIES =====

    public void SetFloat(int property, float value)
    {
        Hook?.SetFloat(property, value);
    }

    public void SetFloat4(int property, in float4 value)
    {
        Hook?.SetFloat4(property, in value);
    }

    public void SetFloatArray(int property, List<float> values)
    {
        Hook?.SetFloatArray(property, values);
    }

    public void SetFloat4Array(int property, List<float4> values)
    {
        Hook?.SetFloat4Array(property, values);
    }

    // ===== MATRIX PROPERTIES =====

    public void SetMatrix(int property, in float4x4 matrix)
    {
        Hook?.SetMatrix(property, in matrix);
    }

    // ===== TEXTURE PROPERTIES =====

    public void SetTexture(int property, ITexture texture)
    {
        Hook?.SetTexture(property, texture);
    }

    // ===== SCALE/OFFSET (ST) PROPERTIES =====

    public void SetST(int property, in float2 scale, in float2 offset)
    {
        float4 value = new float4(scale.x, scale.y, offset.x, offset.y);
        SetFloat4(property, in value);
    }

    // ===== DEBUG =====

    public void SetDebug(bool debug, string tag)
    {
        Hook?.SetDebug(debug, tag);
    }

    // ===== SYNC FIELD UPDATE HELPERS =====
    // NOTE: These methods are commented out because custom math types (float2, float3, float4, float4x4, color)
    // are not Godot Variant-compatible and cannot be used with Sync<T>.
    // Materials should manually call SetFloat/SetFloat4/etc when properties change via OnChanged events.

    /*
	// These methods check if a Sync<T> field changed and update the material property
	// To use these, custom math types need [GlobalClass] attribute and Variant conversion

	public void UpdateFloat(int property, Core.Sync<float> field)
	{
		if (field.IsDirty)
		{
			field.IsDirty = false;
			SetFloat(property, field.Value);
		}
	}

	public void UpdateInt(int property, Core.Sync<int> field)
	{
		if (field.IsDirty)
		{
			field.IsDirty = false;
			SetFloat(property, field.Value);
		}
	}
	*/

    // TODO: Add texture update methods when texture asset system is implemented
    /*
	public void UpdateTexture(int property, AssetRef<ITexture2D> reference, ITexture2D unloadedOverride = null)
	{
		if (reference.GetWasChangedAndClear())
		{
			ITexture2D texture = GetTexture(reference, unloadedOverride);
			SetTexture(property, texture);
		}
	}
	*/
}
