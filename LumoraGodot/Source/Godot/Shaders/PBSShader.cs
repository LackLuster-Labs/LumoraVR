using System;
using Godot;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Source.Godot.Shaders;

/// <summary>
/// Physically Based Shading shader wrapper for Godot.
/// </summary>
public class PBSShader
{
    private static Shader _shaderResource;
    private ShaderMaterial _material;

    private const string SHADER_PATH = "res://Shaders/PBS.gdshader";

    /// <summary>
    /// Initialize and load shader resource.
    /// </summary>
    public PBSShader()
    {
        LoadShader();
        CreateMaterial();
    }

    /// <summary>
    /// Load shader file (cached).
    /// </summary>
    private static void LoadShader()
    {
        if (_shaderResource != null)
            return;

        if (!ResourceLoader.Exists(SHADER_PATH))
        {
            AquaLogger.Error($"PBSShader: Shader file not found at {SHADER_PATH}");
            return;
        }

        _shaderResource = GD.Load<Shader>(SHADER_PATH);

        if (_shaderResource == null)
        {
            AquaLogger.Error($"PBSShader: Failed to load shader from {SHADER_PATH}");
        }
        else
        {
            AquaLogger.Debug($"PBSShader: Loaded shader from {SHADER_PATH}");
        }
    }

    /// <summary>
    /// Create shader material instance.
    /// </summary>
    private void CreateMaterial()
    {
        if (_shaderResource == null)
        {
            AquaLogger.Error("PBSShader: Cannot create material - shader not loaded");
            return;
        }

        _material = new ShaderMaterial();
        _material.Shader = _shaderResource;

        SetAlbedoColor(new Color(1.0f, 1.0f, 1.0f, 1.0f));
        SetMetallic(0.0f);
        SetSmoothness(0.5f);
        SetEmissionColor(new Color(0.0f, 0.0f, 0.0f, 1.0f));
        SetEmissionEnergy(1.0f);

        AquaLogger.Debug("PBSShader: Created material with default properties");
    }

    /// <summary>
    /// Get shader material.
    /// </summary>
    public ShaderMaterial GetMaterial()
    {
        return _material;
    }

    /// <summary>
    /// Set albedo color.
    /// </summary>
    public void SetAlbedoColor(Color color)
    {
        _material?.SetShaderParameter("albedo_color", color);
    }

    /// <summary>
    /// Get albedo color.
    /// </summary>
    public Color GetAlbedoColor()
    {
        return _material?.GetShaderParameter("albedo_color").AsColor() ?? Colors.White;
    }

    /// <summary>
    /// Set albedo texture.
    /// </summary>
    public void SetAlbedoTexture(Texture2D texture)
    {
        _material?.SetShaderParameter("albedo_texture", texture);
        _material?.SetShaderParameter("use_albedo_texture", texture != null);
    }

    /// <summary>
    /// Get albedo texture.
    /// </summary>
    public Texture2D GetAlbedoTexture()
    {
        return _material?.GetShaderParameter("albedo_texture").As<Texture2D>();
    }

    /// <summary>
    /// Set metallic value.
    /// </summary>
    public void SetMetallic(float metallic)
    {
        _material?.SetShaderParameter("metallic", Mathf.Clamp(metallic, 0.0f, 1.0f));
    }

    /// <summary>
    /// Get metallic value.
    /// </summary>
    public float GetMetallic()
    {
        return _material?.GetShaderParameter("metallic").AsSingle() ?? 0.0f;
    }

    /// <summary>
    /// Set metallic texture.
    /// </summary>
    public void SetMetallicTexture(Texture2D texture, bool useAlphaForSmoothness = false)
    {
        _material?.SetShaderParameter("metallic_texture", texture);
        _material?.SetShaderParameter("use_metallic_texture", texture != null);
        _material?.SetShaderParameter("use_metallic_texture_smoothness", useAlphaForSmoothness);
    }

    /// <summary>
    /// Get metallic texture.
    /// </summary>
    public Texture2D GetMetallicTexture()
    {
        return _material?.GetShaderParameter("metallic_texture").As<Texture2D>();
    }

    /// <summary>
    /// Set smoothness value.
    /// </summary>
    public void SetSmoothness(float smoothness)
    {
        _material?.SetShaderParameter("smoothness", Mathf.Clamp(smoothness, 0.0f, 1.0f));
    }

    /// <summary>
    /// Get smoothness value.
    /// </summary>
    public float GetSmoothness()
    {
        return _material?.GetShaderParameter("smoothness").AsSingle() ?? 0.5f;
    }

    /// <summary>
    /// Set roughness value (converted to smoothness).
    /// </summary>
    public void SetRoughness(float roughness)
    {
        SetSmoothness(1.0f - Mathf.Clamp(roughness, 0.0f, 1.0f));
    }

    /// <summary>
    /// Get roughness value.
    /// </summary>
    public float GetRoughness()
    {
        return 1.0f - GetSmoothness();
    }

    /// <summary>
    /// Set normal map texture.
    /// </summary>
    public void SetNormalMap(Texture2D texture, float scale = 1.0f)
    {
        _material?.SetShaderParameter("normal_texture", texture);
        _material?.SetShaderParameter("use_normal_map", texture != null);
        _material?.SetShaderParameter("normal_scale", scale);
    }

    /// <summary>
    /// Get normal map texture.
    /// </summary>
    public Texture2D GetNormalMap()
    {
        return _material?.GetShaderParameter("normal_texture").As<Texture2D>();
    }

    /// <summary>
    /// Set normal map scale.
    /// </summary>
    public void SetNormalScale(float scale)
    {
        _material?.SetShaderParameter("normal_scale", scale);
    }

    /// <summary>
    /// Get normal map scale.
    /// </summary>
    public float GetNormalScale()
    {
        return _material?.GetShaderParameter("normal_scale").AsSingle() ?? 1.0f;
    }

    /// <summary>
    /// Set emission color.
    /// </summary>
    public void SetEmissionColor(Color color)
    {
        _material?.SetShaderParameter("emission_color", color);
    }

    /// <summary>
    /// Get emission color.
    /// </summary>
    public Color GetEmissionColor()
    {
        return _material?.GetShaderParameter("emission_color").AsColor() ?? Colors.Black;
    }

    /// <summary>
    /// Set emission texture.
    /// </summary>
    public void SetEmissionTexture(Texture2D texture)
    {
        _material?.SetShaderParameter("emission_texture", texture);
        _material?.SetShaderParameter("use_emission_texture", texture != null);
    }

    /// <summary>
    /// Get emission texture.
    /// </summary>
    public Texture2D GetEmissionTexture()
    {
        return _material?.GetShaderParameter("emission_texture").As<Texture2D>();
    }

    /// <summary>
    /// Set emission energy/intensity.
    /// </summary>
    public void SetEmissionEnergy(float energy)
    {
        _material?.SetShaderParameter("emission_energy", Mathf.Max(0.0f, energy));
    }

    /// <summary>
    /// Get emission energy.
    /// </summary>
    public float GetEmissionEnergy()
    {
        return _material?.GetShaderParameter("emission_energy").AsSingle() ?? 1.0f;
    }

    /// <summary>
    /// Set ambient occlusion texture.
    /// </summary>
    public void SetOcclusionMap(Texture2D texture, float lightAffect = 0.0f)
    {
        _material?.SetShaderParameter("ao_texture", texture);
        _material?.SetShaderParameter("use_ao_texture", texture != null);
        _material?.SetShaderParameter("ao_light_affect", Mathf.Clamp(lightAffect, 0.0f, 1.0f));
    }

    /// <summary>
    /// Get ambient occlusion texture.
    /// </summary>
    public Texture2D GetOcclusionMap()
    {
        return _material?.GetShaderParameter("ao_texture").As<Texture2D>();
    }

    /// <summary>
    /// Set alpha scissor threshold for alpha clipping.
    /// </summary>
    public void SetAlphaScissor(float threshold, bool enable = true)
    {
        _material?.SetShaderParameter("alpha_scissor_threshold", Mathf.Clamp(threshold, 0.0f, 1.0f));
        _material?.SetShaderParameter("use_alpha_scissor", enable);
    }

    /// <summary>
    /// Get alpha scissor threshold.
    /// </summary>
    public float GetAlphaScissor()
    {
        return _material?.GetShaderParameter("alpha_scissor_threshold").AsSingle() ?? 0.5f;
    }

    /// <summary>
    /// Set UV scale and offset for texture tiling.
    /// </summary>
    public void SetUVTransform(Vector2 scale, Vector2 offset)
    {
        _material?.SetShaderParameter("uv_scale", scale);
        _material?.SetShaderParameter("uv_offset", offset);
    }

    /// <summary>
    /// Get UV scale.
    /// </summary>
    public Vector2 GetUVScale()
    {
        return _material?.GetShaderParameter("uv_scale").AsVector2() ?? Vector2.One;
    }

    /// <summary>
    /// Get UV offset.
    /// </summary>
    public Vector2 GetUVOffset()
    {
        return _material?.GetShaderParameter("uv_offset").AsVector2() ?? Vector2.Zero;
    }

    /// <summary>
    /// Enable/disable vertex color for albedo.
    /// </summary>
    public void SetVertexColorAlbedo(bool enable)
    {
        _material?.SetShaderParameter("use_vertex_color_albedo", enable);
    }

    /// <summary>
    /// Enable/disable vertex color for emission.
    /// </summary>
    public void SetVertexColorEmission(bool enable)
    {
        _material?.SetShaderParameter("use_vertex_color_emission", enable);
    }

    /// <summary>
    /// Create new PBS material instance.
    /// </summary>
    public static PBSShader CreateInstance()
    {
        return new PBSShader();
    }

    /// <summary>
    /// Clone material with all properties.
    /// </summary>
    public PBSShader Clone()
    {
        var clone = new PBSShader();

        clone.SetAlbedoColor(GetAlbedoColor());
        clone.SetAlbedoTexture(GetAlbedoTexture());
        clone.SetMetallic(GetMetallic());
        clone.SetSmoothness(GetSmoothness());
        clone.SetNormalMap(GetNormalMap(), GetNormalScale());
        clone.SetEmissionColor(GetEmissionColor());
        clone.SetEmissionTexture(GetEmissionTexture());
        clone.SetEmissionEnergy(GetEmissionEnergy());
        clone.SetOcclusionMap(GetOcclusionMap());
        clone.SetUVTransform(GetUVScale(), GetUVOffset());

        return clone;
    }

    /// <summary>
    /// Dispose material.
    /// </summary>
    public void Dispose()
    {
        _material?.Dispose();
        _material = null;
    }
}
