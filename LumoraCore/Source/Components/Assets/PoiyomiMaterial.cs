// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Assets.Poiyomi;
using Lumora.Core.Components;
using Lumora.Core.Components.Assets;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Assets;

// Poiyomi Toon, ported. The material keeps Poiyomi's properties under Poiyomi's own names (_Color, _MainTex,
// _ShadingEnabled, _LightingMode, _EnableEmission...), one entry per property it sets; whatever it leaves alone
// keeps Poiyomi's default. The shader is written from them: feature switches pick the code that compiles,
// assigned textures their samples, render state the render modes, and the values reach the shader under the
// same names, so an imported material and an animation that drives "material._EmissionStrength" need no
// mapping. -xlinka
[ComponentCategory("Assets/Materials")]
public class PoiyomiMaterial : MaterialProvider, ICommonMaterial, ICustomInspectorUI
{
    private sealed class PropertyObserver
    {
        public Action<float4> ValueChanged = _ => { };
        public Action<string> NameChanged = _ => { };
        public Action<ShaderUniformType> TypeChanged = _ => { };
        public ReferenceEvent<IAssetProvider<TextureAsset>> TextureChanged = _ => { };
        public ReferenceEvent<IAssetProvider<CubemapAsset>> CubemapChanged = _ => { };
        public Action<IChangeable> AssetArrived = _ => { };
    }

    // Edited through Poiyomi's own layout (PoiyomiMaterialInspector), not as a raw list.
    [HideInInspector]
    public readonly SyncList<ShaderUniformParam> Properties;

    // -1 = the queue the render state implies.
    public readonly Sync<int> RenderQueue;

    private readonly Dictionary<ShaderUniformParam, PropertyObserver> _observers = new();
    // Name lookup over Properties, first entry of a name wins; rebuilt after any add, remove or rename.
    private readonly Dictionary<string, ShaderUniformParam> _byName = new();
    private bool _indexDirty = true;
    private bool _isUpdatingMaterial;

    // The property names the shader text was last written from. Build reads them through ReadForSource; a value it
    // did not read cannot change the text. Swapped whole, since a change can arrive on the sync thread. -xlinka
    private HashSet<string>? _sourceReads;
    private HashSet<string>? _readsBuilding;
    private Func<string, float>? _readForSource;

    // Values changed since the last push, and the batch being pushed now.
    private readonly object _valuesGate = new();
    private List<ShaderUniformParam> _changedValues = new();
    private List<ShaderUniformParam> _pushing = new();
    private bool _valuesQueued;
    private Action? _pushValues;

    protected override MaterialType MaterialType => MaterialType.Poiyomi;

    public colorHDR Color
    {
        get => ToColor(GetVector("_Color"));
        set => SetVector("_Color", new float4(value.r, value.g, value.b, value.a));
    }

    public IAssetProvider<TextureAsset> MainTexture
    {
        get => GetTexture("_MainTex")!;
        set => SetTexture("_MainTex", value);
    }

    public IAssetProvider<TextureAsset> NormalMap
    {
        get => GetTexture("_BumpMap")!;
        set => SetTexture("_BumpMap", value);
    }

    public float NormalScale
    {
        get => GetFloat("_BumpScale");
        set => SetFloat("_BumpScale", value);
    }

    public float2 TextureScale
    {
        get { var st = GetVector("_MainTex_ST"); return new float2(st.x, st.y); }
        set { var st = GetVector("_MainTex_ST"); SetVector("_MainTex_ST", new float4(value.x, value.y, st.z, st.w)); }
    }

    public float2 TextureOffset
    {
        get { var st = GetVector("_MainTex_ST"); return new float2(st.z, st.w); }
        set { var st = GetVector("_MainTex_ST"); SetVector("_MainTex_ST", new float4(st.x, st.y, value.x, value.y)); }
    }

    // Set by the platform once a renderer on this machine has shown this material through its simple stand-in. The
    // stand-in reads the values again only when the material says it changed, so value pushes say so from then on.
    public bool ShownSimplified { get; set; }

    public PoiyomiMaterial()
    {
        Properties = new SyncList<ShaderUniformParam>();
        RenderQueue = new Sync<int>(this, -1);
        Properties.ElementsAdded += OnPropertiesAdded;
        Properties.ElementsRemoving += OnPropertiesRemoving;
        Properties.ElementsRemoved += OnPropertiesRemoved;
    }

    public ShaderUniformParam? Find(string name)
    {
        if (_indexDirty)
        {
            _byName.Clear();
            foreach (var param in Properties)
            {
                if (!string.IsNullOrEmpty(param.Name.Value))
                    _byName.TryAdd(param.Name.Value, param);
            }
            _indexDirty = false;
        }
        return _byName.TryGetValue(name, out var found) && !found.IsDestroyed ? found : null;
    }

    // The property's entry, made with Poiyomi's type and default when the material has none yet. Null for a
    // name Poiyomi does not have.
    public ShaderUniformParam? Ensure(string name)
    {
        if (Find(name) is { } existing)
            return existing;
        if (!PoiyomiProperties.TryGet(name, out var property))
            return null;
        var param = Properties.Add();
        param.Name.Value = name;
        _indexDirty = true;
        param.Type.Value = property.Type;
        param.IsColor.Value = property.IsColor;
        param.Value.Value = property.Default;
        return param;
    }

    public float4 GetVector(string name)
    {
        if (Find(name) is { } param)
            return param.Value.Value;
        return PoiyomiProperties.TryGet(name, out var property) ? property.Default : float4.Zero;
    }

    public float GetFloat(string name) => GetVector(name).x;

    public void SetVector(string name, float4 value)
    {
        if (Ensure(name) is { } param)
            param.Value.Value = value;
    }

    public void SetFloat(string name, float value) => SetVector(name, new float4(value, 0f, 0f, 0f));

    public void SetColor(string name, colorHDR value) => SetVector(name, new float4(value.r, value.g, value.b, value.a));

    public IAssetProvider<TextureAsset>? GetTexture(string name) => Find(name)?.Texture.Target;

    public void SetTexture(string name, IAssetProvider<TextureAsset>? texture)
    {
        if (texture == null && Find(name) == null)
            return;
        if (Ensure(name) is { } param)
            param.Texture.Target = texture!;
    }

    public IAssetProvider<CubemapAsset>? GetCubemap(string name) => Find(name)?.Cubemap.Target;

    public void SetCubemap(string name, IAssetProvider<CubemapAsset>? cubemap)
    {
        if (cubemap == null && Find(name) == null)
            return;
        if (Ensure(name) is { } param)
            param.Cubemap.Target = cubemap!;
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        _isUpdatingMaterial = true;
        try
        {
            var textures = new List<string>();
            foreach (var param in Properties)
            {
                if ((param.Type.Value == ShaderUniformType.Texture2D && param.Texture.Target != null)
                    || (param.Type.Value == ShaderUniformType.TextureCube && param.Cubemap.Target != null))
                    textures.Add(param.Name.Value);
            }
            var reads = _readsBuilding = new HashSet<string>(StringComparer.Ordinal);
            var (main, outline) = PoiyomiShaderSource.Build(_readForSource ??= ReadForSource, textures);
            _sourceReads = reads;
            asset.SetCustomShaderSource(main);
            asset.SetNextPassShaderSource(outline ?? string.Empty);
            asset.SetFloat("RenderQueue", RenderQueue.Value);

            // Everything goes out below, so values waiting for a push are already in.
            lock (_valuesGate)
                _changedValues.Clear();
            foreach (var param in Properties)
            {
                var name = param.Name.Value;
                if (string.IsNullOrEmpty(name))
                    continue;
                Stage(asset, name, param);
            }
        }
        finally
        {
            _isUpdatingMaterial = false;
        }
    }

    private float ReadForSource(string name)
    {
        _readsBuilding?.Add(name);
        return GetFloat(name);
    }

    private static void Stage(MaterialAsset asset, string name, ShaderUniformParam param)
    {
        var v = param.Value.Value;
        switch (param.Type.Value)
        {
            case ShaderUniformType.Float:
                asset.SetFloat(name, v.x);
                break;
            case ShaderUniformType.Int:
            case ShaderUniformType.Bool:
                asset.SetInt(name, (int)MathF.Round(v.x));
                break;
            case ShaderUniformType.Vec2:
                asset.SetFloat2(name, new float2(v.x, v.y));
                break;
            case ShaderUniformType.Vec3:
                // A color goes as a color: Godot converts a source_color uniform from sRGB only then.
                if (param.IsColor.Value)
                    asset.SetColor(name, new colorHDR(v.x, v.y, v.z, 1f));
                else
                    asset.SetFloat3(name, new float3(v.x, v.y, v.z));
                break;
            case ShaderUniformType.Vec4:
                if (param.IsColor.Value)
                    asset.SetColor(name, ToColor(v));
                else
                    asset.SetFloat4(name, v);
                break;
            case ShaderUniformType.Texture2D:
                asset.SetTexture(name, param.Texture.Asset);
                break;
            case ShaderUniformType.TextureCube:
                asset.SetCubemap(name, param.Cubemap.Asset);
                break;
        }
    }

    // A value the shader text was not written from goes to the live material by itself. Mayu's heart rate counter
    // eases its decal scale a little every frame, and each step rebuilt the whole material: the text written again,
    // every one of its 185 properties staged again, every renderer wearing it told to re-apply it. A value the text
    // reads (a feature switch, the blend or cull mode) still rebuilds. -xlinka
    private void ValueChanged(ShaderUniformParam param)
    {
        if (_isUpdatingMaterial || IsDestroyed)
            return;
        var reads = _sourceReads;
        if (reads == null || Asset == null || reads.Contains(param.Name.Value ?? string.Empty)
            || param.Type.Value is ShaderUniformType.Texture2D or ShaderUniformType.TextureCube)
        {
            MarkChangeDirty();
            return;
        }
        if (AssetReferenceCount == 0)
            return;
        lock (_valuesGate)
        {
            if (!_changedValues.Contains(param))
                _changedValues.Add(param);
            if (_valuesQueued)
                return;
            _valuesQueued = true;
        }
        var world = World;
        if (world != null)
            world.RunSynchronously(_pushValues ??= PushValues);
        else
            PushValues();
    }

    private void PushValues()
    {
        lock (_valuesGate)
        {
            _valuesQueued = false;
            (_pushing, _changedValues) = (_changedValues, _pushing);
        }
        var asset = Asset;
        if (IsDestroyed || asset == null)
        {
            _pushing.Clear();
            return;
        }
        foreach (var param in _pushing)
        {
            var name = param.Name.Value;
            if (param.IsDestroyed || string.IsNullOrEmpty(name))
                continue;
            Stage(asset, name, param);
            asset.ApplyStagedNow(name);
        }
        _pushing.Clear();
        if (ShownSimplified)
            AssetUpdated();
    }

    private static colorHDR ToColor(float4 v) => new colorHDR(v.x, v.y, v.z, v.w);

    public void BuildInspectorBody(UIBuilder ui)
    {
        var host = ui.Root.AddSlot("Poiyomi");
        host.AttachComponent<RectTransform>();
        var layout = host.AttachComponent<VerticalLayout>();
        layout.Spacing.Value = 2f;
        layout.ForceExpandWidth.Value = true;
        layout.ForceExpandHeight.Value = false;
        host.AttachComponent<PoiyomiMaterialInspector>().Setup(this);
    }

    protected override IEnumerable<IAssetRef> NestedAssetRefs()
    {
        foreach (var param in Properties)
        {
            if (param.Texture.Target != null)
                yield return param.Texture;
            if (param.Cubemap.Target != null)
                yield return param.Cubemap;
        }
    }

    private void OnPropertiesAdded(SyncElementList<ShaderUniformParam> list, int index, int count)
    {
        _indexDirty = true;
        for (int i = 0; i < count; i++)
            Observe(list[index + i]);
        PropertyChanged();
    }

    private void OnPropertiesRemoving(SyncElementList<ShaderUniformParam> list, int index, int count)
    {
        _indexDirty = true;
        for (int i = 0; i < count; i++)
            Unobserve(list[index + i]);
        PropertyChanged();
    }

    private void OnPropertiesRemoved(SyncElementList<ShaderUniformParam> list, int index, int count) => _indexDirty = true;

    private void Observe(ShaderUniformParam param)
    {
        if (param == null || _observers.ContainsKey(param))
            return;
        var observer = new PropertyObserver
        {
            ValueChanged = _ => ValueChanged(param),
            NameChanged = _ =>
            {
                _indexDirty = true;
                PropertyChanged();
            },
            TypeChanged = _ => PropertyChanged(),
            TextureChanged = _ => PropertyChanged(),
            CubemapChanged = _ => PropertyChanged(),
            AssetArrived = _ => PropertyChanged(),
        };
        param.Value.OnChanged += observer.ValueChanged;
        param.Name.OnChanged += observer.NameChanged;
        param.Type.OnChanged += observer.TypeChanged;
        param.Texture.OnTargetChange += observer.TextureChanged;
        param.Cubemap.OnTargetChange += observer.CubemapChanged;
        // A texture's asset landing raises the reference's Changed, not its target change: without this the
        // material kept the shader's default textures it was first applied with. -xlinka
        ((IChangeable)param.Texture).Changed += observer.AssetArrived;
        ((IChangeable)param.Cubemap).Changed += observer.AssetArrived;
        _observers[param] = observer;
    }

    private void Unobserve(ShaderUniformParam param)
    {
        if (param == null || !_observers.TryGetValue(param, out var observer))
            return;
        param.Value.OnChanged -= observer.ValueChanged;
        param.Name.OnChanged -= observer.NameChanged;
        param.Type.OnChanged -= observer.TypeChanged;
        param.Texture.OnTargetChange -= observer.TextureChanged;
        param.Cubemap.OnTargetChange -= observer.CubemapChanged;
        ((IChangeable)param.Texture).Changed -= observer.AssetArrived;
        ((IChangeable)param.Cubemap).Changed -= observer.AssetArrived;
        _observers.Remove(param);
    }

    private void PropertyChanged()
    {
        if (_isUpdatingMaterial || IsDestroyed)
            return;
        MarkChangeDirty();
    }

    public override void OnDestroy()
    {
        Properties.ElementsAdded -= OnPropertiesAdded;
        Properties.ElementsRemoving -= OnPropertiesRemoving;
        Properties.ElementsRemoved -= OnPropertiesRemoved;
        foreach (var param in new List<ShaderUniformParam>(_observers.Keys))
            Unobserve(param);
        base.OnDestroy();
    }
}
