using System;
using System.Collections.Generic;
using Godot;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Aquamarine.Godot.Extensions;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Source.Godot.Assets;

/// <summary>
/// Godot implementation of IMaterialPropertyBlockHook.
/// Bridges Lumora MaterialPropertyBlock to Godot per-instance shader parameters.
///
/// Architecture:
/// - Stores per-instance shader parameter overrides
/// - Applied to GeometryInstance3D (MeshInstance3D) for per-object variations
/// - More lightweight than full materials (no shader, just parameter overrides)
/// </summary>
public class MaterialPropertyBlockHook : IMaterialPropertyBlockHook
{
    private MaterialPropertyBlock _lumoraPropertyBlock;
    private Dictionary<int, string> _propertyNames = new Dictionary<int, string>();
    private Dictionary<string, Variant> _propertyValues = new Dictionary<string, Variant>();

    // ===== INITIALIZATION =====

    public void Initialize(IAsset asset)
    {
        _lumoraPropertyBlock = asset as MaterialPropertyBlock;
        if (_lumoraPropertyBlock == null)
        {
            throw new ArgumentException("MaterialPropertyBlockHook requires a MaterialPropertyBlock asset");
        }

        AquaLogger.Log($"MaterialPropertyBlockHook: Initialized for property block {asset.GetHashCode()}");
    }

    public void Unload()
    {
        _propertyNames.Clear();
        _propertyValues.Clear();
        AquaLogger.Log($"MaterialPropertyBlockHook: Unloaded");
    }

    // ===== PROPERTY INITIALIZATION =====

    public void InitializeProperties(List<MaterialProperty> properties, Action onDone)
    {
        _propertyNames.Clear();
        foreach (var prop in properties)
        {
            int id = prop.GetHashCode();
            prop.Initialize(id);
            _propertyNames[id] = ConvertPropertyName(prop.Name);
        }
        onDone?.Invoke();
    }

    private string ConvertPropertyName(string propertyName)
    {
        // Same conversion as MaterialHook
        string name = propertyName.TrimStart('_');
        return ToSnakeCase(name);
    }

    private string ToSnakeCase(string str)
    {
        if (string.IsNullOrEmpty(str)) return str;

        var result = new System.Text.StringBuilder();
        result.Append(char.ToLower(str[0]));

        for (int i = 1; i < str.Length; i++)
        {
            if (char.IsUpper(str[i]))
            {
                result.Append('_');
                result.Append(char.ToLower(str[i]));
            }
            else
            {
                result.Append(str[i]);
            }
        }

        return result.ToString();
    }

    // ===== PROPERTY SETTERS =====

    public void SetFloat(int property, float value)
    {
        if (_propertyNames.TryGetValue(property, out string paramName))
        {
            _propertyValues[paramName] = value;
        }
    }

    public void SetFloat4(int property, in float4 value)
    {
        if (_propertyNames.TryGetValue(property, out string paramName))
        {
            _propertyValues[paramName] = new Vector4(value.x, value.y, value.z, value.w);
        }
    }

    public void SetFloatArray(int property, List<float> values)
    {
        if (_propertyNames.TryGetValue(property, out string paramName))
        {
            _propertyValues[paramName] = values.ToArray();
        }
    }

    public void SetFloat4Array(int property, List<float4> values)
    {
        if (_propertyNames.TryGetValue(property, out string paramName))
        {
            var godotArray = new Vector4[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                godotArray[i] = new Vector4(values[i].x, values[i].y, values[i].z, values[i].w);
            }
            _propertyValues[paramName] = godotArray;
        }
    }

    public void SetMatrix(int property, in float4x4 matrix)
    {
        if (_propertyNames.TryGetValue(property, out string paramName))
        {
            // Convert float4x4 to Godot Projection for Variant compatibility
            _propertyValues[paramName] = matrix.ToGodot();
        }
    }

    public void SetTexture(int property, ITexture texture)
    {
        if (_propertyNames.TryGetValue(property, out string paramName))
        {
            // TODO: Convert ITexture to Godot Texture2D when texture system is implemented
            _propertyValues[paramName] = Variant.CreateFrom((Texture2D)null);
        }
    }

    public void SetST(int property, in float2 scale, in float2 offset)
    {
        if (_propertyNames.TryGetValue(property, out string paramName))
        {
            _propertyValues[paramName + "_scale"] = new Vector2(scale.x, scale.y);
            _propertyValues[paramName + "_offset"] = new Vector2(offset.x, offset.y);
        }
    }

    public void SetDebug(bool debug, string tag)
    {
        if (debug)
        {
            AquaLogger.Log($"MaterialPropertyBlockHook: Debug enabled with tag '{tag}'");
        }
    }

    // ===== APPLY CHANGES =====

    public void ApplyChanges(AssetIntegrated onDone)
    {
        // Property values are stored and will be applied to GeometryInstance3D
        // when ApplyToInstance() is called
        AquaLogger.Log($"MaterialPropertyBlockHook: Changes applied ({_propertyValues.Count} properties)");
        onDone?.Invoke();
    }

    // ===== PUBLIC ACCESS =====

    /// <summary>
    /// Apply this property block's overrides to a Godot GeometryInstance3D.
    /// Called by MeshRendererHook to apply per-instance parameters.
    /// </summary>
    public void ApplyToInstance(GeometryInstance3D instance)
    {
        if (instance == null) return;

        foreach (var kvp in _propertyValues)
        {
            instance.SetInstanceShaderParameter(kvp.Key, kvp.Value);
        }

        AquaLogger.Log($"MaterialPropertyBlockHook: Applied {_propertyValues.Count} parameters to instance");
    }

    /// <summary>
    /// Get all property overrides as a dictionary.
    /// </summary>
    public Dictionary<string, Variant> GetPropertyOverrides()
    {
        return new Dictionary<string, Variant>(_propertyValues);
    }
}
