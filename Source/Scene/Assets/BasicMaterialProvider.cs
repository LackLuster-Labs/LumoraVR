using System;
using LumoraVR.Source.Helpers;
using Godot;
using Godot.Collections;

namespace LumoraVR.Source.Scene.Assets;

public class BasicMaterialProvider : IMaterialProvider
{
    #region Enums

    public enum AlphaMode
    {
        Opaque,
        AlphaScissor,
        AlphaMix,
        AlphaAdd,
        AlphaSub,
        AlphaMul,
    }

    public enum ColorChannel
    {
        R,
        G,
        B,
        A,
    }

    #endregion

    private StandardMaterial3D _mat = new();

    #region AlbedoAndAlpha

    public ITextureProvider AlbedoTexture
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (value == null) _mat.AlbedoTexture = null;
            else
                value.Set(m =>
                {
                    if (field != value) return;
                    _mat.AlbedoTexture = m;
                });
        }
    }
    public Color AlbedoColor
    {
        get => _mat.AlbedoColor;
        set => _mat.AlbedoColor = value;
    }

    public bool VertexColors
    {
        get => _mat.VertexColorUseAsAlbedo;
        set => _mat.VertexColorUseAsAlbedo = value;
    }

    public AlphaMode Alpha
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            _mat.Transparency = value switch
            {
                AlphaMode.Opaque => BaseMaterial3D.TransparencyEnum.Disabled,
                AlphaMode.AlphaScissor => BaseMaterial3D.TransparencyEnum.AlphaScissor,
                AlphaMode.AlphaMix => BaseMaterial3D.TransparencyEnum.Alpha,
                AlphaMode.AlphaAdd => BaseMaterial3D.TransparencyEnum.Alpha,
                AlphaMode.AlphaSub => BaseMaterial3D.TransparencyEnum.Alpha,
                AlphaMode.AlphaMul => BaseMaterial3D.TransparencyEnum.Alpha,
                _ => BaseMaterial3D.TransparencyEnum.Disabled,
            };
            _mat.BlendMode = value switch
            {
                AlphaMode.AlphaAdd => BaseMaterial3D.BlendModeEnum.Add,
                AlphaMode.AlphaSub => BaseMaterial3D.BlendModeEnum.Sub,
                AlphaMode.AlphaMul => BaseMaterial3D.BlendModeEnum.Mul,
                _ => BaseMaterial3D.BlendModeEnum.Mix,
            };
        }
    }
    public float AlphaScissorThreshold
    {
        get => _mat.AlphaScissorThreshold;
        set => _mat.AlphaScissorThreshold = value;
    }

    #endregion

    #region Emission

    public ITextureProvider EmissionTexture
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (value == null) _mat.EmissionTexture = null;
            else
                value.Set(m =>
                {
                    if (field != value) return;
                    _mat.EmissionTexture = m;
                });
        }
    }
    public Color EmissionColor
    {
        get => _mat.Emission;
        set
        {
            _mat.Emission = value;
            UpdateEmissionStatus();
        }
    }
    public float EmissionStrength
    {
        get => _mat.EmissionEnergyMultiplier;
        set
        {
            _mat.EmissionEnergyMultiplier = value;
            UpdateEmissionStatus();
        }
    }

    #endregion

    #region Normals

    public ITextureProvider NormalMap
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (value == null)
            {
                _mat.NormalTexture = null;
                _mat.NormalEnabled = false;
            }
            else
                value.Set(m =>
                {
                    if (field != value) return;
                    _mat.NormalTexture = m;
                    _mat.NormalEnabled = true;
                });
        }
    }
    public float NormalScale
    {
        get => _mat.NormalScale;
        set => _mat.NormalScale = value;
    }

    #endregion

    #region Metallic

    public ITextureProvider MetallicMap
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (value == null)
            {
                _mat.MetallicTexture = null;
            }
            else
                value.Set(m =>
                {
                    if (field != value) return;
                    _mat.MetallicTexture = m;
                });
        }
    }
    public float MetallicScale
    {
        get => _mat.Metallic;
        set => _mat.Metallic = value;
    }
    public ColorChannel MetallicChannel
    {
        get => _mat.MetallicTextureChannel switch
        {
            BaseMaterial3D.TextureChannel.Red => ColorChannel.R,
            BaseMaterial3D.TextureChannel.Green => ColorChannel.G,
            BaseMaterial3D.TextureChannel.Blue => ColorChannel.B,
            BaseMaterial3D.TextureChannel.Alpha => ColorChannel.A,
            _ => ColorChannel.R,
        };
        set => _mat.MetallicTextureChannel = value switch
        {
            ColorChannel.R => BaseMaterial3D.TextureChannel.Red,
            ColorChannel.G => BaseMaterial3D.TextureChannel.Green,
            ColorChannel.B => BaseMaterial3D.TextureChannel.Blue,
            ColorChannel.A => BaseMaterial3D.TextureChannel.Alpha,
            _ => BaseMaterial3D.TextureChannel.Red,
        };
    }

    #endregion

    #region Roughness

    public ITextureProvider RoughnessMap
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (value == null)
            {
                _mat.RoughnessTexture = null;
            }
            else
                value.Set(m =>
                {
                    if (field != value) return;
                    _mat.RoughnessTexture = m;
                });
        }
    }
    public float RoughnessScale
    {
        get => _mat.Roughness;
        set => _mat.Roughness = value;
    }
    public ColorChannel RoughnessChannel
    {
        get => _mat.RoughnessTextureChannel switch
        {
            BaseMaterial3D.TextureChannel.Red => ColorChannel.R,
            BaseMaterial3D.TextureChannel.Green => ColorChannel.G,
            BaseMaterial3D.TextureChannel.Blue => ColorChannel.B,
            BaseMaterial3D.TextureChannel.Alpha => ColorChannel.A,
            _ => ColorChannel.R,
        };
        set => _mat.RoughnessTextureChannel = value switch
        {
            ColorChannel.R => BaseMaterial3D.TextureChannel.Red,
            ColorChannel.G => BaseMaterial3D.TextureChannel.Green,
            ColorChannel.B => BaseMaterial3D.TextureChannel.Blue,
            ColorChannel.A => BaseMaterial3D.TextureChannel.Alpha,
            _ => BaseMaterial3D.TextureChannel.Red,
        };
    }

    #endregion

    public void Set(Action<Material> setAction) => setAction(_mat);
    public bool AssetReady => true;

    private void UpdateEmissionStatus()
    {
        _mat.EmissionEnabled = !(EmissionColor.IsEqualApprox(Colors.Black) || EmissionStrength <= 0);
    }

    public void Initialize(IRootObject owner, Dictionary<string, Variant> data)
    {
        //reuse these so we don't have a giant mess of local variables
        AlphaMode a;
        Variant v;
        float f;
        bool b;
        Color c;
        ITextureProvider tex;
        ColorChannel cc;

        //assets
        if (data.TryGetAsset("albedoTexture", owner, out tex)) AlbedoTexture = tex;
        if (data.TryGetAsset("emissionTexture", owner, out tex)) EmissionTexture = tex;
        if (data.TryGetAsset("normalMap", owner, out tex)) NormalMap = tex;
        if (data.TryGetAsset("roughnessMap", owner, out tex)) RoughnessMap = tex;
        if (data.TryGetAsset("metallicMap", owner, out tex)) MetallicMap = tex;

        //values
        //albedo & alpha
        if (data.TryGetValue("albedoColor", out v) && v.TryGetColor(out c)) AlbedoColor = c;
        if (data.TryGetValue("alphaMode", out v) && v.TryGetEnum(out a)) Alpha = a;
        if (data.TryGetValue("alphaScissorThreshold", out v) && v.TryGetSingle(out f)) AlphaScissorThreshold = f;
        if (data.TryGetValue("vertexColors", out v) && v.TryGetBool(out b)) VertexColors = b;
        //emissions
        if (data.TryGetValue("emissionColor", out v) && v.TryGetColor(out c)) EmissionColor = c;
        if (data.TryGetValue("emissionStrength", out v) && v.TryGetSingle(out f)) EmissionStrength = f;
        //normals
        if (data.TryGetValue("normalScale", out v) && v.TryGetSingle(out f)) NormalScale = f;
        //metallic
        if (data.TryGetValue("metallicScale", out v) && v.TryGetSingle(out f)) MetallicScale = f;
        if (data.TryGetValue("metallicChannel", out v) && v.TryGetEnum(out cc)) MetallicChannel = cc;
        //roughness
        if (data.TryGetValue("roughnessScale", out v) && v.TryGetSingle(out f)) RoughnessScale = f;
        if (data.TryGetValue("roughnessChannel", out v) && v.TryGetEnum(out cc)) RoughnessChannel = cc;
    }
}
