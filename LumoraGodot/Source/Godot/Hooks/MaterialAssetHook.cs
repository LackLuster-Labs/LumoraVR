using System;
using System.Collections.Generic;
using Godot;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// Godot implementation of material asset hook.
/// Creates and manages Godot StandardMaterial3D or ShaderMaterial resources.
/// </summary>
public class MaterialAssetHook : AssetHook, IMaterialAssetHook
{
    private StandardMaterial3D _standardMaterial;
    private ShaderMaterial _shaderMaterial;
    private MaterialType _materialType;
    private bool _usesShaderMaterial;
    private string _customShaderPath;

    // Pending properties to apply
    private readonly Dictionary<string, object> _pendingProperties = new();

    /// <summary>
    /// Get the underlying Godot material for assignment to renderers.
    /// </summary>
    public object GodotMaterial => _usesShaderMaterial
        ? (object)_shaderMaterial
        : (object)_standardMaterial;

    /// <summary>
    /// Whether the material is valid and ready for use.
    /// </summary>
    public bool IsValid => _shaderMaterial != null || _standardMaterial != null;

    /// <summary>
    /// Set the material type/shader.
    /// </summary>
    public void SetMaterialType(MaterialType type)
    {
        _materialType = type;

        // Dispose existing materials
        _standardMaterial?.Dispose();
        _standardMaterial = null;
        _shaderMaterial?.Dispose();
        _shaderMaterial = null;

        // Create appropriate material type
        switch (type)
        {
            case MaterialType.PBS_Metallic:
                _usesShaderMaterial = false;
                _standardMaterial = new StandardMaterial3D();
                break;

            case MaterialType.Unlit:
                _usesShaderMaterial = true;
                _shaderMaterial = CreateUnlitShaderMaterial();
                break;

            case MaterialType.Custom:
                _usesShaderMaterial = true;
                _shaderMaterial = new ShaderMaterial();
                // Custom shader will be set via SetCustomShader
                break;

            default:
                _usesShaderMaterial = false;
                _standardMaterial = new StandardMaterial3D();
                break;
        }
    }

    /// <summary>
    /// Create an unlit shader material.
    /// </summary>
    private ShaderMaterial CreateUnlitShaderMaterial()
    {
        var material = new ShaderMaterial();

        // Try to load unlit shader
        string shaderPath = "res://Shaders/Unlit.gdshader";
        if (ResourceLoader.Exists(shaderPath))
        {
            material.Shader = GD.Load<Shader>(shaderPath);
        }
        else
        {
            // Create inline unlit shader if file doesn't exist
            var shader = new Shader();
            shader.Code = @"
shader_type spatial;
render_mode unshaded;

uniform vec4 albedo_color : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap, repeat_enable;
uniform bool use_albedo_texture = false;
uniform vec2 uv_scale = vec2(1.0, 1.0);
uniform vec2 uv_offset = vec2(0.0, 0.0);
uniform float alpha_scissor_threshold = 0.5;
uniform bool use_alpha_scissor = false;

void vertex() {
    UV = UV * uv_scale + uv_offset;
}

void fragment() {
    vec4 albedo_tex = use_albedo_texture ? texture(albedo_texture, UV) : vec4(1.0);
    vec4 final_color = albedo_color * albedo_tex;
    if (use_alpha_scissor && final_color.a < alpha_scissor_threshold) {
        discard;
    }
    ALBEDO = final_color.rgb;
    ALPHA = final_color.a;
}
";
            material.Shader = shader;
        }

        return material;
    }

    /// <summary>
    /// Set a custom shader path (for Custom material type).
    /// </summary>
    public void SetCustomShader(string shaderPath)
    {
        _customShaderPath = NormalizeShaderPath(shaderPath);

        if (_shaderMaterial != null && !string.IsNullOrEmpty(_customShaderPath))
        {
            if (ResourceLoader.Exists(_customShaderPath))
            {
                _shaderMaterial.Shader = GD.Load<Shader>(_customShaderPath);
            }
            else
            {
                AquaLogger.Warn($"MaterialAssetHook: Custom shader not found: {_customShaderPath}");
            }
        }
    }

    /// <summary>
    /// Set a custom shader from source code.
    /// </summary>
    public void SetCustomShaderSource(string shaderSource)
    {
        if (string.IsNullOrWhiteSpace(shaderSource))
        {
            return;
        }

        if (_shaderMaterial == null)
        {
            _usesShaderMaterial = true;
            _shaderMaterial = new ShaderMaterial();
        }

        var shader = new Shader();
        shader.Code = shaderSource;
        _shaderMaterial.Shader = shader;
    }

    private static string NormalizeShaderPath(string shaderPath)
    {
        if (string.IsNullOrWhiteSpace(shaderPath))
        {
            return shaderPath;
        }

        const string lumresPrefix = "lumres://";
        if (shaderPath.StartsWith(lumresPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var relative = shaderPath.Substring(lumresPrefix.Length).TrimStart('/');
            return $"res://{relative}";
        }

        return shaderPath;
    }

    /// <summary>
    /// Set blend mode.
    /// </summary>
    public void SetBlendMode(BlendMode mode)
    {
        if (_standardMaterial != null)
        {
            _standardMaterial.Transparency = mode switch
            {
                BlendMode.Transparent => BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode.Cutout => BaseMaterial3D.TransparencyEnum.AlphaScissor,
                BlendMode.Additive => BaseMaterial3D.TransparencyEnum.Alpha,
                _ => BaseMaterial3D.TransparencyEnum.Disabled
            };

            // Set blend mode for additive
            if (mode == BlendMode.Additive)
            {
                _standardMaterial.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
            }
            else
            {
                _standardMaterial.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
            }
        }
        else if (_shaderMaterial != null)
        {
            _shaderMaterial.SetShaderParameter("blend_mode", (int)mode);
        }
    }

    /// <summary>
    /// Set face culling mode.
    /// </summary>
    public void SetCulling(Culling culling)
    {
        if (_standardMaterial != null)
        {
            _standardMaterial.CullMode = culling switch
            {
                Culling.Front => BaseMaterial3D.CullModeEnum.Front,
                Culling.None => BaseMaterial3D.CullModeEnum.Disabled,
                _ => BaseMaterial3D.CullModeEnum.Back
            };
        }
        else if (_shaderMaterial != null)
        {
            _shaderMaterial.SetShaderParameter("cull_mode", (int)culling);
        }
    }

    /// <summary>
    /// Set a float property.
    /// </summary>
    public void SetFloat(string property, float value)
    {
        _pendingProperties[property] = value;
    }

    /// <summary>
    /// Set an int property.
    /// </summary>
    public void SetInt(string property, int value)
    {
        _pendingProperties[property] = value;
    }

    /// <summary>
    /// Set a bool property.
    /// </summary>
    public void SetBool(string property, bool value)
    {
        _pendingProperties[property] = value;
    }

    /// <summary>
    /// Set a color property.
    /// </summary>
    public void SetColor(string property, colorHDR value)
    {
        _pendingProperties[property] = new Color(value.r, value.g, value.b, value.a);
    }

    /// <summary>
    /// Set a float2 property.
    /// </summary>
    public void SetFloat2(string property, float2 value)
    {
        _pendingProperties[property] = new Vector2(value.x, value.y);
    }

    /// <summary>
    /// Set a float3 property.
    /// </summary>
    public void SetFloat3(string property, float3 value)
    {
        _pendingProperties[property] = new Vector3(value.x, value.y, value.z);
    }

    /// <summary>
    /// Set a float4 property.
    /// </summary>
    public void SetFloat4(string property, float4 value)
    {
        _pendingProperties[property] = new Vector4(value.x, value.y, value.z, value.w);
    }

    /// <summary>
    /// Set a texture property.
    /// </summary>
    public void SetTexture(string property, TextureAsset texture)
    {
        if (texture?.Hook is TextureAssetHook textureHook && textureHook.IsValid)
        {
            var godotTex = textureHook.GodotTexture;
            var image = godotTex?.GetImage();
            AquaLogger.Log($"MaterialAssetHook.SetTexture: property={property}, texture valid, size={image?.GetWidth()}x{image?.GetHeight()}, format={image?.GetFormat()}");
            _pendingProperties[property] = godotTex;
        }
        else
        {
            AquaLogger.Log($"MaterialAssetHook.SetTexture: property={property}, texture null or invalid (Hook={texture?.Hook}, IsValid={((texture?.Hook as TextureAssetHook)?.IsValid)})");
            _pendingProperties[property] = null;
        }
    }

    /// <summary>
    /// Clear all pending properties.
    /// </summary>
    public void Clear()
    {
        _pendingProperties.Clear();
    }

    /// <summary>
    /// Apply all pending changes.
    /// </summary>
    public void ApplyChanges(Action callback)
    {
        AquaLogger.Debug($"MaterialAssetHook.ApplyChanges: Applying {_pendingProperties.Count} properties, usesShader={_usesShaderMaterial}");
        foreach (var (property, value) in _pendingProperties)
        {
            ApplyProperty(property, value);
        }

        callback?.Invoke();
    }

    /// <summary>
    /// Apply a single property to the material.
    /// </summary>
    private void ApplyProperty(string property, object value)
    {
        if (_standardMaterial != null)
        {
            ApplyStandardMaterialProperty(property, value);
        }
        else if (_shaderMaterial != null)
        {
            ApplyShaderMaterialProperty(property, value);
        }
    }

    /// <summary>
    /// Apply property to StandardMaterial3D.
    /// </summary>
    private void ApplyStandardMaterialProperty(string property, object value)
    {
        switch (property)
        {
            // Albedo
            case "AlbedoColor":
                if (value is Color c) _standardMaterial.AlbedoColor = c;
                break;
            case "AlbedoTexture":
                _standardMaterial.AlbedoTexture = value as Texture2D;
                break;

            // Metallic/Roughness
            case "Metallic":
                if (value is float m) _standardMaterial.Metallic = m;
                break;
            case "Smoothness":
                // Convert smoothness to roughness (inverse)
                if (value is float s) _standardMaterial.Roughness = 1.0f - s;
                break;
            case "MetallicMap":
                if (value is Texture2D metallicTex)
                {
                    _standardMaterial.MetallicTexture = metallicTex;
                    _standardMaterial.MetallicTextureChannel = BaseMaterial3D.TextureChannel.Red;
                }
                else
                {
                    _standardMaterial.MetallicTexture = null;
                }
                break;

            // Normal
            case "NormalMap":
                _standardMaterial.NormalEnabled = value != null;
                _standardMaterial.NormalTexture = value as Texture2D;
                break;
            case "NormalScale":
                if (value is float ns) _standardMaterial.NormalScale = ns;
                break;

            // Emission
            case "EmissiveColor":
                if (value is Color ec)
                {
                    bool hasEmission = ec.R > 0 || ec.G > 0 || ec.B > 0;
                    _standardMaterial.EmissionEnabled = hasEmission;
                    if (hasEmission)
                    {
                        _standardMaterial.Emission = ec;
                        _standardMaterial.EmissionEnergyMultiplier = 1.0f;
                    }
                }
                break;
            case "EmissiveMap":
                if (value is Texture2D emissiveTex)
                {
                    _standardMaterial.EmissionEnabled = true;
                    _standardMaterial.EmissionTexture = emissiveTex;
                }
                else if (_standardMaterial.Emission == new Color(0, 0, 0))
                {
                    _standardMaterial.EmissionEnabled = false;
                }
                break;

            // Occlusion
            case "OcclusionMap":
                _standardMaterial.AOEnabled = value != null;
                _standardMaterial.AOTexture = value as Texture2D;
                break;

            // Alpha
            case "AlphaCutoff":
                if (value is float ac) _standardMaterial.AlphaScissorThreshold = ac;
                break;

            // Texture transform
            case "TextureScale":
                if (value is Vector2 scale)
                {
                    _standardMaterial.Uv1Scale = new Vector3(scale.X, scale.Y, 1);
                }
                break;
            case "TextureOffset":
                if (value is Vector2 offset)
                {
                    _standardMaterial.Uv1Offset = new Vector3(offset.X, offset.Y, 0);
                }
                break;
        }
    }

    /// <summary>
    /// Apply property to ShaderMaterial.
    /// </summary>
    private void ApplyShaderMaterialProperty(string property, object value)
    {
        // Convert property name to shader parameter name (snake_case)
        string shaderParam = ToSnakeCase(property);

        // Map common property names to shader uniform names
        bool isUnlit = _materialType == MaterialType.Unlit;
        string mappedParam = property switch
        {
            "TintColor" => isUnlit ? "albedo_color" : "tint_color",
            "Texture" => isUnlit ? "albedo_texture" : "main_texture",
            "AlbedoColor" => "albedo_color",
            "AlbedoTexture" => "albedo_texture",
            "TextureScale" => isUnlit ? "uv_scale" : "texture_scale",
            "TextureOffset" => isUnlit ? "uv_offset" : "texture_offset",
            "AlphaCutoff" => isUnlit ? "alpha_scissor_threshold" : "alpha_cutoff",
            _ => shaderParam
        };

        if (_shaderMaterial == null)
            return;

        if (value == null)
        {
            if (property == "Texture" && isUnlit)
            {
                _shaderMaterial.SetShaderParameter("use_albedo_texture", false);
            }
            return;
        }

        // Convert value to appropriate Godot Variant based on type
        Variant variantValue = value switch
        {
            float f => Variant.From(f),
            double d => Variant.From((float)d),
            int i => Variant.From(i),
            bool b => Variant.From(b),
            string s => Variant.From(s),
            Vector2 v2 => Variant.From(v2),
            Vector3 v3 => Variant.From(v3),
            Vector4 v4 => Variant.From(v4),
            Color c => Variant.From(c),
            Texture2D tex => Variant.From(tex),
            Lumora.Core.Math.float2 f2 => Variant.From(new Vector2(f2.x, f2.y)),
            Lumora.Core.Math.float3 f3 => Variant.From(new Vector3(f3.x, f3.y, f3.z)),
            Lumora.Core.Math.colorHDR hdr => Variant.From(new Color(hdr.r, hdr.g, hdr.b, hdr.a)),
            _ => default
        };

        if (variantValue.VariantType != Variant.Type.Nil)
        {
            AquaLogger.Debug($"MaterialAssetHook.ApplyShaderMaterialProperty: Setting {mappedParam} = {value?.GetType().Name}");
            _shaderMaterial.SetShaderParameter(mappedParam, variantValue);
            if (property == "Texture" && isUnlit)
            {
                _shaderMaterial.SetShaderParameter("use_albedo_texture", value is Texture2D);
            }
        }
        else
        {
            AquaLogger.Debug($"MaterialAssetHook.ApplyShaderMaterialProperty: Skipping {mappedParam}, value is nil");
        }
    }

    /// <summary>
    /// Convert PascalCase to snake_case.
    /// </summary>
    private static string ToSnakeCase(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var result = new System.Text.StringBuilder();
        result.Append(char.ToLowerInvariant(text[0]));

        for (int i = 1; i < text.Length; i++)
        {
            if (char.IsUpper(text[i]))
            {
                result.Append('_');
                result.Append(char.ToLowerInvariant(text[i]));
            }
            else
            {
                result.Append(text[i]);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Unload and dispose the Godot material.
    /// </summary>
    public override void Unload()
    {
        _standardMaterial?.Dispose();
        _standardMaterial = null;

        _shaderMaterial?.Dispose();
        _shaderMaterial = null;

        _pendingProperties.Clear();
    }
}
