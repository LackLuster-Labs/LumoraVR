// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

﻿using System;
using System.Collections.Generic;
using Lumora.CDN;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Assets;

/// <summary>
/// Custom shader material driven by gdshader source.
/// </summary>
[ComponentCategory("Assets/Materials")]
public sealed class CustomShaderMaterial : MaterialProvider
{
    private sealed class UniformObserver
    {
        public Action<float4> ValueChanged = _ => { };
        public Action<ShaderUniformType> TypeChanged = _ => { };
        public Action<bool> IsColorChanged = _ => { };
        public Action<bool> HasRangeChanged = _ => { };
        public Action<float2> RangeChanged = _ => { };
        public ReferenceEvent<IAssetProvider<TextureAsset>> TextureChanged = _ => { };
    }

    /// <summary>
    /// Shader source provider reference.
    /// </summary>
    public readonly AssetRef<ShaderSourceAsset> Shader;

    /// <summary>
    /// Optional engine shader resource path, for built-in res:// .gdshader files.
    /// </summary>
    public readonly Sync<string> ShaderPath;

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
    private readonly Dictionary<ShaderUniformParam, UniformObserver> _uniformObservers = new();
    private bool _isUpdatingMaterial;

    protected override MaterialType MaterialType => MaterialType.Custom;

    public CustomShaderMaterial()
    {
        Shader = new AssetRef<ShaderSourceAsset>(this);
        ShaderPath = new Sync<string>(this, string.Empty);
        Parameters = new SyncList<ShaderUniformParam>();
        BlendMode = new Sync<BlendMode>(this, global::Lumora.Core.Assets.BlendMode.Opaque);
        Culling = new Sync<Culling>(this, global::Lumora.Core.Assets.Culling.Back);
        RenderQueue = new Sync<int>(this, -1);

        Parameters.ElementsAdded += OnParametersAdded;
        Parameters.ElementsRemoving += OnParametersRemoving;
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        _isUpdatingMaterial = true;
        try
        {
            asset.SetBlendMode(BlendMode.Value);
            asset.SetCulling(Culling.Value);
            asset.SetFloat("RenderQueue", RenderQueue.Value);

            var shaderPath = ShaderPath.Value;
            if (!string.IsNullOrWhiteSpace(shaderPath))
            {
                asset.SetCustomShader(shaderPath);
            }

            var shaderAsset = Shader.Asset;
            var shaderSource = shaderAsset?.Source;
            if (!string.IsNullOrWhiteSpace(shaderSource))
            {
                if (string.IsNullOrWhiteSpace(shaderPath))
                {
                    asset.SetCustomShaderSource(shaderSource);
                }
                EnsureUniforms(shaderSource);
            }

            ApplyParameters(asset);
        }
        finally
        {
            _isUpdatingMaterial = false;
        }
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

        LumoraLogger.Debug($"CustomShaderMaterial: Built {Parameters.Count} uniforms for shader");
    }

    private void OnParametersAdded(SyncElementList<ShaderUniformParam> list, int index, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var param = list[index + i];
            AttachUniformObserver(param);
        }

        NotifyUniformChanged();
    }

    private void OnParametersRemoving(SyncElementList<ShaderUniformParam> list, int index, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var param = list[index + i];
            DetachUniformObserver(param);
        }

        NotifyUniformChanged();
    }

    private void AttachUniformObserver(ShaderUniformParam param)
    {
        if (param == null || _uniformObservers.ContainsKey(param))
        {
            return;
        }

        var observer = new UniformObserver
        {
            ValueChanged = _ => NotifyUniformChanged(),
            TypeChanged = _ => NotifyUniformChanged(),
            IsColorChanged = _ => NotifyUniformChanged(),
            HasRangeChanged = _ => NotifyUniformChanged(),
            RangeChanged = _ => NotifyUniformChanged(),
            TextureChanged = _ => NotifyUniformChanged()
        };

        param.Value.OnChanged += observer.ValueChanged;
        param.Type.OnChanged += observer.TypeChanged;
        param.IsColor.OnChanged += observer.IsColorChanged;
        param.HasRange.OnChanged += observer.HasRangeChanged;
        param.Range.OnChanged += observer.RangeChanged;
        param.Texture.OnTargetChange += observer.TextureChanged;

        _uniformObservers[param] = observer;
    }

    private void DetachUniformObserver(ShaderUniformParam param)
    {
        if (param == null || !_uniformObservers.TryGetValue(param, out var observer))
        {
            return;
        }

        param.Value.OnChanged -= observer.ValueChanged;
        param.Type.OnChanged -= observer.TypeChanged;
        param.IsColor.OnChanged -= observer.IsColorChanged;
        param.HasRange.OnChanged -= observer.HasRangeChanged;
        param.Range.OnChanged -= observer.RangeChanged;
        param.Texture.OnTargetChange -= observer.TextureChanged;

        _uniformObservers.Remove(param);
    }

    private void NotifyUniformChanged()
    {
        if (_isUpdatingMaterial || IsDestroyed)
        {
            return;
        }

        MarkChangeDirty();
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

    public override void OnDestroy()
    {
        Parameters.ElementsAdded -= OnParametersAdded;
        Parameters.ElementsRemoving -= OnParametersRemoving;

        var keys = new List<ShaderUniformParam>(_uniformObservers.Keys);
        foreach (var param in keys)
        {
            DetachUniformObserver(param);
        }

        base.OnDestroy();
    }
}
