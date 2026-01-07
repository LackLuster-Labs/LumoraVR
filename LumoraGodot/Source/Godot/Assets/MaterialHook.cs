using System;
using System.Collections.Generic;
using Godot;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Aquamarine.Godot.Extensions;
using AquaLogger = Lumora.Core.Logging.Logger;
using GodotShader = Godot.Shader;
using LumoraShader = Lumora.Core.Assets.Shader;
using LumoraMaterial = Lumora.Core.Assets.Material;

namespace Aquamarine.Source.Godot.Assets;

/// <summary>
/// Godot implementation of IMaterialHook.
/// Bridges Lumora Material to Godot ShaderMaterial.
/// </summary>
public class MaterialHook : IMaterialHook
{
    private ShaderMaterial _godotMaterial;
    private LumoraMaterial _lumoraMaterial;
    private GodotShader _currentShader;
    private Dictionary<int, string> _propertyNames = new Dictionary<int, string>();

    // ===== INITIALIZATION =====

    public void Initialize(IAsset asset)
    {
        _lumoraMaterial = asset as LumoraMaterial;
        if (_lumoraMaterial == null)
        {
            throw new ArgumentException("MaterialHook requires a Material asset");
        }

        _godotMaterial = new ShaderMaterial();
        AquaLogger.Log($"MaterialHook: Initialized for material {asset.GetHashCode()}");
    }

    public void Unload()
    {
        _godotMaterial?.Dispose();
        _godotMaterial = null;
        _propertyNames.Clear();
        AquaLogger.Log($"MaterialHook: Unloaded");
    }

    // ===== PROPERTY INITIALIZATION =====

    public void InitializeProperties(List<MaterialProperty> properties, Action onDone)
    {
        // Map property IDs to names for Godot shader parameters
        _propertyNames.Clear();
        foreach (var prop in properties)
        {
            int id = prop.GetHashCode(); // Use hash code as ID
            prop.Initialize(id);
            _propertyNames[id] = ConvertPropertyName(prop.Name);
        }
        onDone?.Invoke();
    }

    /// <summary>
    /// Convert property names to Godot shader parameter format.
    /// </summary>
    private string ConvertPropertyName(string propertyName)
    {
        // Remove leading underscore and convert to snake_case
        string name = propertyName.TrimStart('_');

        // Property name mappings for Godot
        var mappings = new Dictionary<string, string>
        {
            { "MainTex", "albedo_texture" },
            { "Color", "albedo_color" },
            { "EmissionColor", "emission" },
            { "EmissionMap", "emission_texture" },
            { "BumpMap", "normal_texture" },
            { "BumpScale", "normal_scale" },
            { "Metallic", "metallic" },
            { "Glossiness", "roughness" }, // Note: Glossiness is inverted to roughness (inverse)
			{ "OcclusionMap", "ao_texture" },
        };

        if (mappings.ContainsKey(name))
        {
            return mappings[name];
        }

        // Convert PascalCase to snake_case
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
        if (_godotMaterial == null) return;
        if (_propertyNames.TryGetValue(property, out string paramName))
        {
            _godotMaterial.SetShaderParameter(paramName, value);
        }
    }

    public void SetFloat4(int property, in float4 value)
    {
        if (_godotMaterial == null) return;
        if (_propertyNames.TryGetValue(property, out string paramName))
        {
            _godotMaterial.SetShaderParameter(paramName, new Vector4(value.x, value.y, value.z, value.w));
        }
    }

    public void SetFloatArray(int property, List<float> values)
    {
        if (_godotMaterial == null) return;
        if (_propertyNames.TryGetValue(property, out string paramName))
        {
            _godotMaterial.SetShaderParameter(paramName, values.ToArray());
        }
    }

    public void SetFloat4Array(int property, List<float4> values)
    {
        if (_godotMaterial == null) return;
        if (_propertyNames.TryGetValue(property, out string paramName))
        {
            var godotArray = new Vector4[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                godotArray[i] = new Vector4(values[i].x, values[i].y, values[i].z, values[i].w);
            }
            _godotMaterial.SetShaderParameter(paramName, godotArray);
        }
    }

    public void SetMatrix(int property, in float4x4 matrix)
    {
        if (_godotMaterial == null) return;
        if (_propertyNames.TryGetValue(property, out string paramName))
        {
            // Convert float4x4 to Godot Projection (both are column-major)
            _godotMaterial.SetShaderParameter(paramName, matrix.ToGodot());
        }
    }

    public void SetTexture(int property, ITexture texture)
    {
        if (_godotMaterial == null) return;
        if (_propertyNames.TryGetValue(property, out string paramName))
        {
            // TODO: Convert ITexture to Godot Texture2D when texture system is implemented
            // For now, set null
            _godotMaterial.SetShaderParameter(paramName, (Texture2D)null);
        }
    }

    public void SetST(int property, in float2 scale, in float2 offset)
    {
        if (_godotMaterial == null) return;
        if (_propertyNames.TryGetValue(property, out string paramName))
        {
            // In Godot, UV transform is typically a Transform2D or separate scale/offset parameters
            _godotMaterial.SetShaderParameter(paramName + "_scale", new Vector2(scale.x, scale.y));
            _godotMaterial.SetShaderParameter(paramName + "_offset", new Vector2(offset.x, offset.y));
        }
    }

    public void SetDebug(bool debug, string tag)
    {
        // Godot doesn't have direct debug mode equivalent
        // Could be used for custom debug visualization
        if (debug)
        {
            AquaLogger.Log($"MaterialHook: Debug enabled with tag '{tag}'");
        }
    }

    // ===== MATERIAL-SPECIFIC OPERATIONS =====

    public void ApplyChanges(LumoraShader shader, AssetIntegrated onDone)
    {
        if (_godotMaterial == null)
        {
            onDone?.Invoke();
            return;
        }

        // TODO: Apply shader when shader asset system is implemented
        // For now, store reference but cannot set _godotMaterial.Shader yet (type mismatch)
        // Shader needs a hook to convert Aquamarine.Shader to Godot.Shader
        // _currentShader = shader; // Cannot assign - type mismatch
        AquaLogger.Log($"MaterialHook: Shader application pending - asset '{shader?.GetType().Name ?? "null"}' (shader system not fully implemented)");
        onDone?.Invoke();
    }

    public void SetInstancing(bool enabled)
    {
        // Godot handles instancing at the MeshInstance3D level with MultiMesh
        // Material-level instancing flag is stored but applied at render time
        AquaLogger.Log($"MaterialHook: Instancing set to {enabled}");
    }

    public void SetRenderQueue(int renderQueue)
    {
        if (_godotMaterial == null) return;

        // Godot uses render_priority (default 0)
        // Render queue mapping: Background=1000, Geometry=2000, Transparent=3000, Overlay=4000
        // Convert to Godot priority: -128 to 127
        int priority = (renderQueue - 2000) / 50; // Rough conversion
        priority = System.Math.Clamp(priority, -128, 127);

        _godotMaterial.RenderPriority = priority;
        AquaLogger.Log($"MaterialHook: Render queue {renderQueue} -> priority {priority}");
    }

    public void SetTag(MaterialTag tag, string value)
    {
        // Godot material tag handling
        // Could store in metadata or handle specific tags
        AquaLogger.Log($"MaterialHook: Tag '{tag}' set to '{value}'");

        // Handle specific important tags
        if (tag == MaterialTag.RenderType)
        {
            // Could affect render mode or transparency
        }
    }

    // ===== PUBLIC ACCESS =====

    /// <summary>
    /// Get the underlying Godot ShaderMaterial.
    /// Used by MeshRendererHook to assign to MeshInstance3D.
    /// </summary>
    public ShaderMaterial GetGodotMaterial()
    {
        return _godotMaterial;
    }
}
