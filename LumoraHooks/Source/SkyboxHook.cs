// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

// Turns a Skybox into the scene's environment: its sky material drawn as the sky, or else its cubemap,
// plus the ambient and reflection source flags.
//
// The hook never decides whether it is the world's sky - the component already resolved that from
// synced state - and it never writes the WorldEnvironment node directly. It builds an Environment
// and hands it to SkyEnvironment, which is the only thing allowed to install one. While this Skybox
// is not the active one it withdraws its claim, so the environment it built sits idle rather than
// fighting for the node.
//
// Each kind of sky is its own sky material on the one Sky, and a change of kind swaps which one the
// Sky wears; the Environment is built once. A gradient sky material draws through the shader a
// GradientSkybox draws through, with the same values, so an orb wearing that material and the sky
// agree. -xlinka
[ImplementableHook(typeof(Skybox))]
public sealed class SkyboxHook : ComponentHook<Skybox>
{
    private const string CubemapShaderPath = "res://Shaders/Sky_Cubemap.gdshader";
    private const string GradientShaderPath = "res://Shaders/GradientSkybox.gdshader";
    private const string PanoramaShaderPath = "res://Shaders/Sky_Panorama.gdshader";
    private const string AtmosphereShaderPath = "res://Shaders/Sky_Atmosphere.gdshader";

    private global::Godot.Environment _environment = null!;
    private Sky _sky = null!;
    private ShaderMaterial _cubemapMaterial = null!;
    private ShaderMaterial? _gradientMaterial;
    private ShaderMaterial? _panoramaMaterial;
    private ShaderMaterial? _atmosphereMaterial;
    private ShaderMaterial _showing = null!;
    private bool _claimed;
    private bool _shaderMissingLogged;

    public static IHook<Skybox> Constructor() => new SkyboxHook();

    private float RotationRadians => Owner.Rotation.Value * (System.MathF.PI / 180f);

    public override void Initialize()
    {
        base.Initialize();

        // Start from the bootstrap environment so the project's fog, tonemapping and post settings
        // survive; only the sky and the ambient/reflection sources are ours to set.
        var bootstrap = SkyEnvironment.Bootstrap(attachedNode);
        _environment = bootstrap?.Duplicate() as global::Godot.Environment ?? new global::Godot.Environment();

        _cubemapMaterial = CreateSkyMaterial(CubemapShaderPath);
        _showing = _cubemapMaterial;
        _sky = new Sky { SkyMaterial = _cubemapMaterial };

        // The radiance map is rendered ONCE, at a size a mobile GPU can afford.
        //
        // Left on Automatic with a custom ShaderMaterial, Godot cannot prove the sky is static and
        // falls to real-time: six cubemap faces plus a full mip chain, every frame. On a Pico 4 that
        // showed up as 270 ms of GPU per frame with SIX visible draw calls in the scene, unchanged by
        // turning off glow and by halving the draw count. Quality mode re-renders only when a uniform
        // changes, which is when the world sets the sky up, and 64 is plenty for the ambient term
        // since reflections from this sky are killed below anyway. Desktop keeps the defaults. -xlinka
        if (OS.HasFeature("android"))
        {
            _sky.ProcessMode = Sky.ProcessModeEnum.Quality;
            _sky.RadianceSize = Sky.RadianceSizeEnum.Size64;
        }
        _environment.BackgroundMode = global::Godot.Environment.BGMode.Sky;
        _environment.Sky = _sky;

        ApplyChanges();
    }

    public override void ApplyChanges()
    {
        if (_environment == null || _cubemapMaterial == null)
            return;

        // An edit to the sky material reaches here through Material: the material's update notifies its
        // references, and that change re-runs this hook. A 360 material with no picture set yet, or any
        // material that is not a sky, leaves the cubemap showing.
        var showing = Owner.SkyMaterial switch
        {
            GradientSkyMaterial gradient => ApplyGradient(gradient),
            AtmosphereSkyMaterial atmosphere => ApplyAtmosphere(atmosphere),
            Projection360Material picture when picture.Cubemap.Target != null
                => ApplyCubemap(picture.Cubemap.Asset, picture.Exposure.Value, picture.Tint.Value),
            Projection360Material picture when picture.Texture.Target != null => ApplyPanorama(picture),
            _ => ApplyCubemap(Owner.Cubemap.Asset, 1f, colorHDR.White),
        };
        Show(showing);

        // Sky ambient reads the sky texture itself, so it follows the cubemap for free. The flat
        // fallback is a separate source in Godot, which is why this is a switch and not a blend.
        if (Owner.AmbientFromSky.Value)
        {
            _environment.AmbientLightSource = global::Godot.Environment.AmbientSource.Sky;
        }
        else
        {
            var ambient = Owner.AmbientColor.Value;
            _environment.AmbientLightSource = global::Godot.Environment.AmbientSource.Color;
            _environment.AmbientLightColor = new Color(ambient.r, ambient.g, ambient.b, ambient.a);
        }
        _environment.AmbientLightEnergy = Owner.AmbientEnergy.Value;

        // A gradient sky is safe to reflect: its shader leaves the sun disc out of the cubemap pass.
        _environment.ReflectedLightSource = Owner.ReflectionsFromSky.Value
            ? global::Godot.Environment.ReflectionSource.Sky
            : global::Godot.Environment.ReflectionSource.Disabled;

        UpdateClaim();
    }

    private ShaderMaterial ApplyCubemap(CubemapAsset? asset, float exposure, in colorHDR tint)
    {
        var texture = (asset?.Hook as CubemapAssetHook)?.GodotCubemap;

        // A null uniform is a black sky, which is exactly right while the asset is still loading: the
        // asset reports itself loaded only after its GPU cubemap exists, and that reference change
        // brings us straight back here. A 360 material's cubemap comes back one step further round:
        // its arrival updates the material, and the material's update brings us back.
        _cubemapMaterial.SetShaderParameter("sky_cubemap", texture!);
        _cubemapMaterial.SetShaderParameter("sky_rotation", RotationRadians);
        _cubemapMaterial.SetShaderParameter("sky_exposure", Owner.Exposure.Value * exposure);
        SetColor(_cubemapMaterial, "sky_tint", tint);
        return _cubemapMaterial;
    }

    // The uniforms GradientSkyboxHook sets, from the material. Colours go over raw: this shader and the
    // material's surface shader both take them as sRGB and linearise them, so the two agree.
    // The same uniforms the material's own surface shader takes, off the same sun (ResolveSun), so an orb
    // wearing it and the sky agree.
    private ShaderMaterial ApplyAtmosphere(AtmosphereSkyMaterial sky)
    {
        var material = _atmosphereMaterial ??= CreateSkyMaterial(AtmosphereShaderPath);
        sky.ResolveSun(out var towardSun, out var sunColor, out var intensity);
        material.SetShaderParameter("atmosphere_thickness", sky.AtmosphereThickness.Value);
        SetColor(material, "sky_tint", sky.SkyTint.Value);
        SetColor(material, "ground_color", sky.GroundColor.Value);
        material.SetShaderParameter("exposure", sky.Exposure.Value);
        material.SetShaderParameter("sun_direction", new Vector3(towardSun.x, towardSun.y, towardSun.z));
        SetColor(material, "sun_color", sunColor);
        material.SetShaderParameter("sun_intensity", intensity);
        material.SetShaderParameter("sun_size", sky.SunSize.Value);
        material.SetShaderParameter("sun_disc", (int)sky.SunQuality.Value);
        return material;
    }

    private ShaderMaterial ApplyGradient(GradientSkyMaterial gradient)
    {
        var material = _gradientMaterial ??= CreateSkyMaterial(GradientShaderPath);
        SetColor(material, "top_color", gradient.TopColor.Value);
        SetColor(material, "horizon_color", gradient.HorizonColor.Value);
        SetColor(material, "bottom_color", gradient.BottomColor.Value);
        SetColor(material, "sun_color", gradient.SunColor.Value);

        var sun = gradient.SunDirection.Value;
        material.SetShaderParameter("sun_direction", new Vector3(sun.x, sun.y, sun.z));
        // A size of 0 puts both edges of the disc's smoothstep at 1, which is undefined. The material's
        // surface shader floors it the same.
        material.SetShaderParameter("sun_size", System.MathF.Max(gradient.SunSize.Value, 0.001f));
        material.SetShaderParameter("sun_intensity", gradient.SunIntensity.Value);
        material.SetShaderParameter("sun_glow_power", gradient.SunGlowPower.Value);
        return material;
    }

    // A 360 picture, placed the way Projection360 places it on a sphere around the viewer. A picture not on
    // the GPU yet draws black; the material keeps updating until it is, and each update brings us back.
    private ShaderMaterial ApplyPanorama(Projection360Material picture)
    {
        const float radians = System.MathF.PI / 180f;
        var material = _panoramaMaterial ??= CreateSkyMaterial(PanoramaShaderPath);

        var texture = picture.Texture.Asset?.Hook is IGodotTexture { IsValid: true } hook ? hook.GodotTexture2D : null;
        material.SetShaderParameter("sky_panorama", texture!);
        material.SetShaderParameter("sky_rotation", RotationRadians);
        material.SetShaderParameter("sky_exposure", Owner.Exposure.Value);
        material.SetShaderParameter("picture_exposure", picture.Exposure.Value);
        SetColor(material, "picture_tint", picture.Tint.Value);

        var fov = picture.FieldOfView.Value;
        var angle = picture.AngleOffset.Value;
        material.SetShaderParameter("field_of_view", new Vector4(fov.x, fov.y, angle.x, angle.y) * radians);
        var scale = picture.TextureScale.Value;
        var offset = picture.TextureOffset.Value;
        material.SetShaderParameter("texture_transform", new Vector4(scale.x, scale.y, offset.x, offset.y));
        material.SetShaderParameter("outside_mode", (int)picture.OutsideMode.Value);
        SetColor(material, "outside_color", picture.OutsideColor.Value);
        return material;
    }

    // The sky stepping down lets go of its picture, so a sky taken off is not kept on the GPU.
    private void Show(ShaderMaterial showing)
    {
        if (!ReferenceEquals(_showing, showing))
        {
            if (ReferenceEquals(_showing, _cubemapMaterial))
                _cubemapMaterial.SetShaderParameter("sky_cubemap", default(Variant));
            else if (_panoramaMaterial != null && ReferenceEquals(_showing, _panoramaMaterial))
                _panoramaMaterial.SetShaderParameter("sky_panorama", default(Variant));
            _showing = showing;
            _sky.SkyMaterial = showing;
        }

        if (_shaderMissingLogged || showing.Shader != null)
            return;
        _shaderMissingLogged = true;
        LumoraLogger.Warn("SkyboxHook: no sky shader loaded; the sky will not render");
    }

    private static ShaderMaterial CreateSkyMaterial(string shaderPath)
    {
        var material = new ShaderMaterial();
        if (ResourceLoader.Exists(shaderPath))
            material.Shader = GD.Load<Shader>(shaderPath);
        else
            LumoraLogger.Warn($"SkyboxHook: sky shader not found at {shaderPath}");
        return material;
    }

    private static void SetColor(ShaderMaterial material, string uniform, in colorHDR value)
    {
        material.SetShaderParameter(uniform, new Color(value.r, value.g, value.b, value.a));
    }

    private void UpdateClaim()
    {
        bool wants = Owner.IsActiveSkybox && Owner.Enabled;
        if (wants)
        {
            _claimed = true;
            SkyEnvironment.Claimed(this, Owner.World, SkyEnvironment.SkyboxPriority, _environment, attachedNode);
        }
        else if (_claimed)
        {
            _claimed = false;
            SkyEnvironment.Released(this);
        }
    }

    public override void Destroy(bool destroyingWorld)
    {
        SkyEnvironment.Released(this);
        _claimed = false;

        _environment?.Dispose();
        _sky?.Dispose();
        _cubemapMaterial?.Dispose();
        _gradientMaterial?.Dispose();
        _panoramaMaterial?.Dispose();
        _atmosphereMaterial?.Dispose();
        _environment = null!;
        _sky = null!;
        _cubemapMaterial = null!;
        _gradientMaterial = null;
        _panoramaMaterial = null;
        _atmosphereMaterial = null;
        _showing = null!;

        SkyEnvironment.ReleaseNodeIfUnused();
        base.Destroy(destroyingWorld);
    }
}
