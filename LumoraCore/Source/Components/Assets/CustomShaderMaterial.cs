using System.Collections.Generic;
using Lumora.CDN;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Assets;

/// <summary>
/// Custom shader material driven by gdshader source.
/// </summary>
[ComponentCategory("Assets/Materials")]
public sealed class CustomShaderMaterial : MaterialProvider
{
    /// <summary>
    /// Shader source provider reference.
    /// </summary>
    public readonly AssetRef<ShaderSourceAsset> Shader;

    /// <summary>
    /// Shader uniform parameters (synced).
    /// </summary>
    public readonly SyncList<ShaderUniformParam> Parameters;

    /// <summary>
    /// Blend mode (Opaque, Cutout, Transparent, Additive).
    /// </summary>
    public readonly Sync<BlendMode> BlendMode;

    /// <summary>
    /// Face culling mode.
    /// </summary>
    public readonly Sync<Culling> Culling;

    /// <summary>
    /// Render queue priority (-1 = default).
    /// </summary>
    public readonly Sync<int> RenderQueue;

    private string _lastShaderHash;

    protected override MaterialType MaterialType => MaterialType.Custom;

    public CustomShaderMaterial()
    {
        Shader = new AssetRef<ShaderSourceAsset>(this);
        Parameters = new SyncList<ShaderUniformParam>();
        BlendMode = new Sync<BlendMode>(this, global::Lumora.Core.Assets.BlendMode.Opaque);
        Culling = new Sync<Culling>(this, global::Lumora.Core.Assets.Culling.Back);
        RenderQueue = new Sync<int>(this, -1);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        asset.SetBlendMode(BlendMode.Value);
        asset.SetCulling(Culling.Value);
        asset.SetFloat("RenderQueue", RenderQueue.Value);

        var shaderAsset = Shader.Asset;
        var shaderSource = shaderAsset?.Source;
        if (!string.IsNullOrWhiteSpace(shaderSource))
        {
            asset.SetCustomShaderSource(shaderSource);
            EnsureUniforms(shaderSource);
        }

        ApplyParameters(asset);
    }

    private void EnsureUniforms(string shaderSource)
    {
        if (World == null || !World.IsAuthority)
        {
            return;
        }

        var hash = ContentHash.FromString(shaderSource);
        if (hash == _lastShaderHash)
        {
            return;
        }
        _lastShaderHash = hash;

        var existing = new Dictionary<string, ShaderUniformParamSnapshot>();
        foreach (var param in Parameters)
        {
            if (string.IsNullOrEmpty(param.Name.Value))
            {
                continue;
            }
            existing[param.Name.Value] = new ShaderUniformParamSnapshot(param);
        }

        Parameters.Clear();

        var defs = ShaderUniformParser.Parse(shaderSource);
        foreach (var def in defs)
        {
            var param = Parameters.Add();
            param.Name.Value = def.Name;
            param.Type.Value = def.Type;
            param.IsColor.Value = def.IsColor;
            param.HasRange.Value = def.HasRange;
            param.Range.Value = def.Range;

            if (existing.TryGetValue(def.Name, out var snapshot))
            {
                snapshot.Apply(param);
                continue;
            }

            if (def.HasDefault)
            {
                param.Value.Value = def.DefaultValue;
            }
        }

        AquaLogger.Debug($"CustomShaderMaterial: Built {Parameters.Count} uniforms for shader");
    }

    private void ApplyParameters(MaterialAsset asset)
    {
        foreach (var param in Parameters)
        {
            var name = param.Name.Value;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            switch (param.Type.Value)
            {
                case ShaderUniformType.Float:
                    asset.SetFloat(name, param.Value.Value.x);
                    break;
                case ShaderUniformType.Vec2:
                    asset.SetFloat2(name, new float2(param.Value.Value.x, param.Value.Value.y));
                    break;
                case ShaderUniformType.Vec3:
                    asset.SetFloat3(name, new float3(param.Value.Value.x, param.Value.Value.y, param.Value.Value.z));
                    break;
                case ShaderUniformType.Vec4:
                    if (param.IsColor.Value)
                    {
                        asset.SetColor(name, new colorHDR(param.Value.Value.x, param.Value.Value.y, param.Value.Value.z, param.Value.Value.w));
                    }
                    else
                    {
                        asset.SetFloat4(name, new float4(param.Value.Value.x, param.Value.Value.y, param.Value.Value.z, param.Value.Value.w));
                    }
                    break;
                case ShaderUniformType.Int:
                    asset.SetInt(name, (int)param.Value.Value.x);
                    break;
                case ShaderUniformType.Bool:
                    asset.SetBool(name, param.Value.Value.x >= 0.5f);
                    break;
                case ShaderUniformType.Texture2D:
                    asset.SetTexture(name, param.Texture.Asset);
                    break;
            }
        }
    }

    private readonly struct ShaderUniformParamSnapshot
    {
        private readonly ShaderUniformType _type;
        private readonly float4 _value;
        private readonly bool _isColor;
        private readonly bool _hasRange;
        private readonly float2 _range;
        private readonly IAssetProvider<TextureAsset>? _textureTarget;

        public ShaderUniformParamSnapshot(ShaderUniformParam param)
        {
            _type = param.Type.Value;
            _value = param.Value.Value;
            _isColor = param.IsColor.Value;
            _hasRange = param.HasRange.Value;
            _range = param.Range.Value;
            _textureTarget = param.Texture.Target;
        }

        public void Apply(ShaderUniformParam param)
        {
            param.Type.Value = _type;
            param.Value.Value = _value;
            param.IsColor.Value = _isColor;
            param.HasRange.Value = _hasRange;
            param.Range.Value = _range;
            if (_textureTarget != null)
            {
                param.Texture.Target = _textureTarget;
            }
        }
    }
}
