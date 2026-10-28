// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System;
using Godot;
using Lumora.Core.Assets;

namespace Lumora.Godot.Hooks;

internal static class MaterialPropertyApplicator
{
    // Parameter names made once. A material update handed Godot a C# string for every write, and each
    // became a fresh StringName: a global lock and a hash on the way in, and a handle the finalizer freed
    // on that same lock later, while the frame waited on it. Built-in names were snake_cased into a new
    // string per write on top of that. -xlinka
    private static readonly ConcurrentDictionary<string, StringName> _names = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(MaterialType Type, string Property), (StringName Param, StringName Use)> _mapped = new();
    private static readonly StringName UseAlbedoTexture = Name("use_albedo_texture");
    private static readonly ConditionalWeakTable<Shader, StringName[]> _uniformNames = new();

    private static StringName Name(string name) => _names.GetOrAdd(name, static n => new StringName(n));

    private static (StringName Param, StringName Use) Mapped(MaterialType materialType, string property)
        => _mapped.GetOrAdd((materialType, property), static key =>
        {
            string param = MapShaderProperty(key.Type, key.Property);
            return (Name(param), Name("use_" + param));
        });
    public static void Apply(Material? material, MaterialType materialType, string property, object value)
    {
        if (material is StandardMaterial3D standardMaterial)
        {
            ApplyStandard(standardMaterial, property, value);
        }
        else if (material is ShaderMaterial shaderMaterial)
        {
            ApplyShader(shaderMaterial, materialType, property, value);
        }
    }

    public static Material CloneWithBlock(Material material, MaterialType materialType, IReadOnlyDictionary<string, object> properties)
    {
        if (material == null)
        {
            return null!;
        }

        if (material is ShaderMaterial shaderMaterial)
        {
            if (shaderMaterial.Shader == null)
            {
                return material;
            }

            // Build a fresh ShaderMaterial rather than Resource.Duplicate(false). Godot's
            // internal duplicate copies properties via material_set_param against the
            // clone's render-server material RID, which logs "Parameter 'material' is
            // null" when that RID hasn't been bound yet (timing window during canvas
            // material assignment, frequent on first-frame rebuilds). Copying the
            // shader + visible params explicitly avoids the renderer-side step. - xlinka
            var fresh = new ShaderMaterial
            {
                Shader = shaderMaterial.Shader,
                RenderPriority = shaderMaterial.RenderPriority,
                // Carried over by hand because this clone is built from scratch rather than
                // duplicated: without it a chained pass (the toon outline) is lost the moment a
                // property block clones the material. -xlinka
                NextPass = shaderMaterial.NextPass,
            };
            CopyShaderParameters(shaderMaterial, fresh);
            foreach (var (property, value) in properties)
            {
                Apply(fresh, materialType, property, value);
            }
            return fresh;
        }

        Material clone;
        try
        {
            clone = (material.Duplicate(false) as Material)!;
        }
        catch
        {
            return material;
        }

        if (clone == null)
        {
            return material;
        }

        foreach (var (property, value) in properties)
        {
            Apply(clone, materialType, property, value);
        }

        return clone;
    }

    private static void CopyShaderParameters(ShaderMaterial source, ShaderMaterial dest)
    {
        var shader = source.Shader;
        if (shader == null) return;

        foreach (var name in UniformNames(shader))
        {
            var value = source.GetShaderParameter(name);
            if (value.VariantType == Variant.Type.Nil) continue;
            dest.SetShaderParameter(name, value);
        }
    }

    // A shader's uniform names, read once per shader rather than once per clone.
    private static StringName[] UniformNames(Shader shader)
        => _uniformNames.GetValue(shader, static s =>
        {
            var names = new List<StringName>();
            using var uniforms = s.GetShaderUniformList();
            foreach (var entry in uniforms)
            {
                if (entry.VariantType != Variant.Type.Dictionary) continue;
                using var dict = entry.AsGodotDictionary();
                if (!dict.TryGetValue("name", out var nameVar)) continue;
                string name = nameVar.AsString();
                if (!string.IsNullOrEmpty(name))
                    names.Add(Name(name));
            }
            return names.ToArray();
        });

    public static void ApplyRenderPriority(Material material, int renderQueue)
    {
        if (material == null)
        {
            return;
        }

        // Helio assigns per-surface priorities from its own logical render queue in
        // MeshRendererHookBase. This fallback keeps direct material use reasonable.
        int priority = renderQueue < 0 ? 0 : renderQueue >= 1000 ? renderQueue - 3000 : renderQueue;
        ApplyGodotRenderPriority(material, priority);
    }

    // ONLY WHEN IT CHANGES. The platform treats every render-priority write as a change to the material,
    // same value or not, and re-files every instance drawn with it; each of those then tells every point
    // and spot light it sits in to render its shadow again. The material hook writes the priority on
    // every update of the material, so one animated material kept every lamp around everything wearing
    // it re-rendering all six shadow faces every frame. -xlinka
    public static void ApplyGodotRenderPriority(Material material, int priority)
    {
        if (material == null)
        {
            return;
        }

        int clamped = System.Math.Clamp(priority, -128, 127);
        if (material.RenderPriority != clamped)
            material.RenderPriority = clamped;
    }

    public static Variant ToVariant(object value)
    {
        return value switch
        {
            float f => Variant.From(f),
            double d => Variant.From((float)d),
            int i => Variant.From(i),
            byte byteValue => Variant.From((int)byteValue),
            bool boolValue => Variant.From(boolValue),
            string s => Variant.From(s),
            Vector2 v2 => Variant.From(v2),
            Vector3 v3 => Variant.From(v3),
            Vector4 v4 => Variant.From(v4),
            Color c => Variant.From(c),
            Texture2D tex => Variant.From(tex),
            Texture layered => Variant.From(layered),
            Lumora.Core.Math.float2 f2 => Variant.From(new Vector2(f2.x, f2.y)),
            Lumora.Core.Math.float3 f3 => Variant.From(new Vector3(f3.x, f3.y, f3.z)),
            Lumora.Core.Math.float4 f4 => Variant.From(new Vector4(f4.x, f4.y, f4.z, f4.w)),
            Lumora.Core.Math.colorHDR hdr => Variant.From(new Color(hdr.r, hdr.g, hdr.b, hdr.a)),
            _ => default
        };
    }

    private static void ApplyStandard(StandardMaterial3D material, string property, object value)
    {
        switch (property)
        {
            case "TintColor":
            case "AlbedoColor":
                if (value is Color c) material.AlbedoColor = c;
                break;
            case "Texture":
            case "AlbedoTexture":
                material.AlbedoTexture = value as Texture2D;
                break;
            case "Metallic":
                // The scalar multiplies the metallic texture's channel - so when a metallic MAP is present we let
                // the map fully drive it (see MetallicMap) and ignore the scalar; a 0 scalar would otherwise zero
                // the whole map out. Only apply the scalar when there's no map. -xlinka
                if (value is float metallic && material.MetallicTexture == null) material.Metallic = metallic;
                break;
            case "Smoothness":
                if (value is float smoothness && material.RoughnessTexture == null) material.Roughness = 1.0f - smoothness;
                break;
            case "MetallicMap":
                material.MetallicTexture = value as Texture2D;
                if (material.MetallicTexture != null)
                {
                    // glTF packs the metallic-roughness map as Occlusion(R)/Roughness(G)/Metallic(B): metallic is in
                    // BLUE (reading Red picks up occlusion ~1.0 and drives the surface fully metallic -> dark), and
                    // roughness is in GREEN. Drive BOTH off this one map and set the scalar factors to 1 so the map
                    // fully applies (Godot does scalar*channel). Blue is also right for a plain grayscale metalness
                    // map (R=G=B); a standalone metalness map without packed roughness is rare for avatars. -xlinka
                    material.MetallicTextureChannel = BaseMaterial3D.TextureChannel.Blue;
                    material.Metallic = 1.0f;
                    material.RoughnessTexture = material.MetallicTexture;
                    material.RoughnessTextureChannel = BaseMaterial3D.TextureChannel.Green;
                    material.Roughness = 1.0f;
                }
                break;
            case "NormalMap":
                material.NormalEnabled = value != null;
                material.NormalTexture = value as Texture2D;
                break;
            case "NormalScale":
                if (value is float normalScale) material.NormalScale = normalScale;
                break;
            case "EmissiveColor":
                if (value is Color emission)
                {
                    bool hasEmission = emission.R > 0 || emission.G > 0 || emission.B > 0;
                    material.EmissionEnabled = hasEmission;
                    if (hasEmission)
                    {
                        // EmissiveColor is HDR - intensity above 1 must live in the energy multiplier, not be
                        // clamped into the LDR Emission color, or glowing/neon emissives lose their punch. -xlinka
                        float maxc = System.Math.Max(emission.R, System.Math.Max(emission.G, emission.B));
                        if (maxc > 1.0f)
                        {
                            material.Emission = new Color(emission.R / maxc, emission.G / maxc, emission.B / maxc);
                            material.EmissionEnergyMultiplier = maxc;
                        }
                        else
                        {
                            material.Emission = emission;
                            material.EmissionEnergyMultiplier = 1.0f;
                        }
                    }
                }
                break;
            case "EmissiveMap":
                material.EmissionTexture = value as Texture2D;
                material.EmissionEnabled = material.EmissionTexture != null;
                break;
            case "OcclusionMap":
                material.AOTexture = value as Texture2D;
                material.AOEnabled = material.AOTexture != null;
                break;
            case "AlphaCutoff":
                if (value is float alphaCutoff) material.AlphaScissorThreshold = alphaCutoff;
                break;
            case "TextureScale":
                if (value is Vector2 scale) material.Uv1Scale = new Vector3(scale.X, scale.Y, 1);
                break;
            case "TextureOffset":
                if (value is Vector2 offset) material.Uv1Offset = new Vector3(offset.X, offset.Y, 0);
                break;
            case "RenderQueue":
                if (value is int renderQueue) ApplyRenderPriority(material, renderQueue);
                if (value is float renderQueueFloat) ApplyRenderPriority(material, (int)renderQueueFloat);
                break;
        }
    }

    private static void ApplyShader(ShaderMaterial material, MaterialType materialType, string property, object value)
    {
        if (property == "RenderQueue")
        {
            if (value is int renderQueue) ApplyRenderPriority(material, renderQueue);
            if (value is float renderQueueFloat) ApplyRenderPriority(material, (int)renderQueueFloat);
            return;
        }

        // Custom shader uniforms are parsed VERBATIM from the user's shader source - the name IS the
        // uniform. Running them through the built-in name map snake_cased anything with an uppercase
        // letter (MyColor -> my_color) into a uniform the shader doesn't have, and the write silently
        // vanished. No mapping, no use_albedo_texture sidecar; apply exactly what the shader declared.
        // (RenderQueue above stays reserved as the material's render-priority control channel.) -xlinka
        // Poiyomi's properties go by Poiyomi's own names, the shader's uniform names, the same way.
        if (materialType is MaterialType.Custom or MaterialType.Poiyomi)
        {
            // A cleared texture clears its uniform, handing it back to the shader's own default
            // (hint_default_white and the like). Skipping the write kept the last texture bound. -xlinka
            if (value == null)
            {
                material.SetShaderParameter(Name(property), default(Variant));
                return;
            }
            Variant customValue = ToVariant(value);
            if (customValue.VariantType != Variant.Type.Nil)
                material.SetShaderParameter(Name(property), customValue);
            return;
        }

        var mapped = Mapped(materialType, property);

        if (value == null)
        {
            if (property is "Texture" or "AlbedoTexture")
            {
                material.SetShaderParameter(UseAlbedoTexture, false);
            }
            // A texture member that went null has to say so, or the shader keeps sampling the last one
            // it was given.
            if (IsTextureMember(property))
                material.SetShaderParameter(mapped.Use, false);
            return;
        }

        Variant variantValue = ToVariant(value);
        if (variantValue.VariantType == Variant.Type.Nil)
        {
            return;
        }

        material.SetShaderParameter(mapped.Param, variantValue);
        if (property is "Texture" or "AlbedoTexture")
        {
            material.SetShaderParameter(UseAlbedoTexture, value is Texture2D);
        }

        // EVERY TEXTURE UNIFORM GETS ITS COMPANION FLAG, not just albedo.
        //
        // Our shader materials gate each optional map on a `use_<name>` bool, and the only one anything
        // set here was use_albedo_texture, and then only for the two albedo member names. So
        // use_normal_map, use_metallic_map, use_occlusion_map and the detail pair sat at their `false`
        // default forever: every one of those maps on every shader-based material in the engine was
        // bound to its sampler and then never read.
        //
        // This logic did exist, in ApplyShaderMaterialProperty over in MaterialAssetHook - which nothing
        // calls. Two large private methods there shadow this file and are dead, and reading them instead
        // of this is how the gap survived. Setting a uniform a shader does not declare is harmless, so
        // this needs no per-shader list.
        //
        // Driven off the VALUE being a texture rather than the member's name, because the names collide:
        // ColorMask is the mask TEXTURE on the four-way tint material and a channel WRITE MASK enum on
        // the channel-matrix filter. Only the null case above falls back to the name. -xlinka
        if (value is Texture2D)
            material.SetShaderParameter(mapped.Use, true);
    }

    // Named by what it is rather than by its type, because the value arrives as a bare object and is
    // null exactly when we most need to know it was a texture.
    private static bool IsTextureMember(string property)
        => property.EndsWith("Texture", StringComparison.Ordinal)
        || property.EndsWith("Map", StringComparison.Ordinal)
        || property.EndsWith("TextureRight", StringComparison.Ordinal)
        || property == "ColorMask";

    // The uniform a property lands on: a custom or Poiyomi shader's own name, otherwise the mapped one, worked
    // out once per pair. Every update of a material with a sampler ask came through here for each picture.
    private static readonly ConcurrentDictionary<(MaterialType Type, string Property), string> _uniforms = new();

    internal static string UniformName(MaterialType materialType, string property)
        => materialType is MaterialType.Custom or MaterialType.Poiyomi ? property
            : _uniforms.GetOrAdd((materialType, property), static key => MapShaderProperty(key.Type, key.Property));

    private static string MapShaderProperty(MaterialType materialType, string property)
    {
        bool isUnlit = materialType is MaterialType.Unlit or MaterialType.UI_Unlit or MaterialType.UI_StencilWrite or MaterialType.UI_StencilTest or MaterialType.UI_Text or MaterialType.UI_TextStencil or MaterialType.Text;
        bool isOverlay = materialType == MaterialType.OverlayUnlit;
        bool isMetaball = materialType == MaterialType.Metaball;
        bool isOverlayFresnel = materialType == MaterialType.OverlayFresnel;
        bool isFresnelLerp = materialType == MaterialType.FresnelLerp;

        return property switch
        {
            // The overlay fresnel shares the overlay unlit's uv-set naming so the two read the same
            // when you have both on a slot.
            "Texture" when isOverlayFresnel => "albedo_texture",
            "FrontTextureScale" when isOverlayFresnel => "front_uv_scale",
            "FrontTextureOffset" when isOverlayFresnel => "front_uv_offset",
            "BehindTextureScale" when isOverlayFresnel => "behind_uv_scale",
            "BehindTextureOffset" when isOverlayFresnel => "behind_uv_offset",
            // "lerp" on its own reads as the operation rather than the amount.
            "Lerp" when isFresnelLerp => "lerp_amount",
            "FrontTintColor" when isOverlay => "front_color",
            "BehindTintColor" when isOverlay => "behind_color",
            "Texture" when isOverlay => "albedo_texture",
            "AlbedoTexture" when isOverlay => "albedo_texture",
            "FrontTextureScale" when isOverlay => "front_uv_scale",
            "FrontTextureOffset" when isOverlay => "front_uv_offset",
            "BehindTextureScale" when isOverlay => "behind_uv_scale",
            "BehindTextureOffset" when isOverlay => "behind_uv_offset",
            "UseVertexColor" when isOverlay => "use_vertex_color",
            "TintColor" when isUnlit => "albedo_color",
            "Texture" when isUnlit => "albedo_texture",
            "AlbedoColor" => "albedo_color",
            "AlbedoTexture" => "albedo_texture",
            "TextureScale" when isUnlit => "uv_scale",
            "TextureOffset" when isUnlit => "uv_offset",
            "AlphaCutoff" when isUnlit => "alpha_cutoff",
            "AlphaClip" when isUnlit => "alpha_clip",
            "UseVertexColor" when isUnlit => "use_vertex_color",
            "PixelRange" => "pixel_range",
            "UseMSDF" => "use_msdf",
            "RectClip" => "rect_clip",
            "ColorMask" => "color_mask",
            "StencilComparison" => "stencil_comparison",
            "StencilOperation" => "stencil_operation",
            "StencilID" => "stencil_id",
            "StencilWriteMask" => "stencil_write_mask",
            "StencilReadMask" => "stencil_read_mask",
            "ZWrite" => "z_write",
            "ZTest" => "z_test",
            "TintA" when isMetaball => "tint_a",
            "TintB" when isMetaball => "tint_b",
            "BlobRadius" when isMetaball => "blob_radius",
            "BlobSmoothness" when isMetaball => "blob_smoothness",
            "BlobCount" when isMetaball => "blob_count",
            "RiseSpeed" when isMetaball => "rise_speed",
            "VolumeExtents" when isMetaball => "volume_extents",
            "VolumeHeight" when isMetaball => "volume_height",
            "VolumeOffset" when isMetaball => "volume_offset",
            "RimStrength" when isMetaball => "rim_strength",
            "RimFalloff" when isMetaball => "rim_falloff",
            "FresnelPower" when isMetaball => "fresnel_power",
            "AlphaScale" when isMetaball => "alpha_scale",
            "EmissionStrength" when isMetaball => "emission_strength",
            "TimeScale" when isMetaball => "time_scale",
            _ => ToSnakeCase(property)
        };
    }

    private static string ToSnakeCase(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

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
}
