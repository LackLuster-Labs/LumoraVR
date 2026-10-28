// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// A panel that stands in userspace drawn the way the dash is: its canvas sits on a rig of its own out of
// sight and renders into a texture, and the room sees that texture on a quad. Inside its own pass the canvas
// has the whole render-priority range to itself, so a modal, a scroll pane or a list over a list stacks on it
// exactly as it does on the dash. An overlay canvas standing in the room gets sixteen values for everything
// it draws and orders its chunks by chance as soon as two of them overlap.
//
// The canvas grows from its top-left corner: the canvas slot's origin never moves, so a point a pointer gave
// before a resize names the same spot after it, and a corner grip follows the hand exactly. The texture
// keeps a set number of pixels per metre of the panel, so one scaled up into a big monitor renders finer
// instead of stretching its pixels, up to a cap. -xlinka
[ComponentCategory("Hidden")]
public sealed class PanelSurface : Component
{
    // Canvas units to metres, on the rig and on the panel at scale one.
    public const float UnitMeters = 0.001f;
    private const float CaptureDistance = 1f;
    private const float RigSpacing = 25f;
    private const int MaxTextureSide = 4096;
    // What the dash's own capture gives a metre of its surface; a standalone headset gets half, as it does.
    private static float PixelsPerMeter => Engine.IsStandaloneHeadset ? 720f : 1440f;
    // A new density only once the scale has moved this far from the one the texture was sized for.
    private const float DensityStep = 0.15f;
    private static readonly float3 RigOrigin = new(0f, 2000f, 0f);
    private static readonly HashSet<int> TakenRigs = new();
    private static readonly Dictionary<Slot, PanelSurface> ByCanvas = new();

    private readonly List<IAssetProvider<FontSet>> _fonts = new();
    private readonly List<int> _fontGenerations = new();
    private Slot? _rig;
    private Slot? _canvasSlot;
    private Canvas? _canvas;
    private RectTransform? _canvasRect;
    private RenderTextureProvider? _texture;
    private RoundedQuadMesh? _mesh;
    private MeshRenderer? _renderer;
    private int _rigIndex = -1;
    private float2 _size;
    private float2 _topLeft;
    private float _density;
    private bool _shown = true;
    private bool _framed;

    public Slot? CanvasSlot => _canvasSlot is { IsDestroyed: false } slot ? slot : null;
    public Canvas? Canvas => _canvas is { IsDestroyed: false } canvas ? canvas : null;
    public RenderTextureProvider? Texture => _texture;
    public Slot? Rig => _rig;
    public float2 Size => _size;
    public float Density => _density;
    public bool Shown => _shown;

    // The surface whose offscreen canvas a slot is drawn on, if any.
    public static PanelSurface? Of(Slot? slot)
    {
        for (var s = slot; s != null; s = s.Parent)
        {
            if (s.GetComponent<Canvas>() == null)
                continue;
            return ByCanvas.TryGetValue(s, out var surface) && !surface.IsDestroyed ? surface : null;
        }
        return null;
    }

    // The canvas rect in its own units, y up, about the canvas slot's origin.
    public Rect CanvasRect => new(_topLeft.x, _topLeft.y - _size.y, _size.x, _size.y);

    public float2 Center => new(_topLeft.x + _size.x * 0.5f, _topLeft.y - _size.y * 0.5f);

    // Builds the surface on a child of panel, at that size, with its canvas and rig ready to take content.
    public static PanelSurface Create(Slot panel, float2 size)
    {
        var slot = panel.AddSlot("Surface");
        var surface = slot.AttachComponent<PanelSurface>();
        surface.Build(size);
        return surface;
    }

    // The faces whose atlases the canvas draws from: a glyph rasterized late shows only after one more
    // render, the same as on the dash.
    public void WatchFont(IAssetProvider<FontSet>? font)
    {
        if (font == null || _fonts.Contains(font))
            return;
        _fonts.Add(font);
        _fontGenerations.Add(-1);
    }

    private void Build(float2 size)
    {
        _size = new float2(MathF.Max(1f, size.x), MathF.Max(1f, size.y));
        _topLeft = new float2(-_size.x * 0.5f, _size.y * 0.5f);

        var holder = Templates.Userspace.LocalRoot;
        if (holder == null || holder.IsDestroyed || !ReferenceEquals(holder.World, World))
            holder = World.RootSlot;
        var rigs = holder.FindChild("Panel Rigs", recursive: false);
        if (rigs == null)
        {
            rigs = holder.AddSlot("Panel Rigs");
            rigs.Persistent.Value = false;
        }

        _rigIndex = 0;
        while (TakenRigs.Contains(_rigIndex))
            _rigIndex++;
        TakenRigs.Add(_rigIndex);

        _rig = rigs.AddSlot("Rig " + _rigIndex);
        _rig.Persistent.Value = false;
        _rig.GlobalPosition = RigOrigin + new float3(_rigIndex * RigSpacing, 0f, 0f);
        _rig.GlobalRotation = floatQ.Identity;
        _rig.AttachComponent<RenderLayerOverride>().Layer.Value = RenderLayerOverride.HiddenLayer;

        _canvasSlot = _rig.AddSlot("Canvas");
        _canvasSlot.LocalScale.Value = float3.One * UnitMeters;
        _canvasRect = _canvasSlot.AttachComponent<RectTransform>();
        _canvasRect.AnchorMin.Value = new float2(0.5f, 0.5f);
        _canvasRect.AnchorMax.Value = new float2(0.5f, 0.5f);
        _canvas = _canvasSlot.AttachComponent<Canvas>();
        ByCanvas[_canvasSlot] = this;

        _density = PickDensity();
        _texture = _rig.AttachComponent<RenderTextureProvider>();
        // Capture transparency is filled by the detached panel's opaque backing material.
        _texture.ClearColor.Value = color.Transparent;
        _texture.CullMask.Value = RenderLayerOverride.HiddenLayer;
        _texture.CameraRotation.Value = floatQ.Identity;

        _mesh = Slot.AttachComponent<RoundedQuadMesh>();
        _mesh.CornerRadius.Value = DashTheme.RadiusPanel * UnitMeters;
        // Over the world like the dash: userspace is what the viewer is looking at, wherever the room is.
        var material = DashGlass.AttachComposite(Slot, _texture, 4002);
        material.ShaderPath.Value = "res://Shaders/PanelComposite.gdshader";
        _renderer = Slot.AttachComponent<MeshRenderer>();
        _renderer.Mesh.Target = _mesh;
        _renderer.Material.Target = material;
        _renderer.SortingOrder.Value = UserspaceRenderBands.PanelSortingOrder;
        _renderer.ShadowCastMode.Value = ShadowCastMode.Off;

        Slot.AttachComponent<PanelSurfacePortal>();
        Slot.ActiveChanged += OnActiveChanged;
        Apply();
    }

    // Put away with its panel (a camera panel closed while it keeps streaming), the rig goes quiet too.
    private void OnActiveChanged(Slot slot)
    {
        bool live = Slot.IsActive;
        if (_rig != null && !_rig.IsDestroyed && _rig.ActiveSelf.Value != live)
            _rig.ActiveSelf.Value = live;
        _texture?.Asset?.SetRenderEnabled(live && _shown);
        if (!live)
            _canvas?.ReleaseAllPointers();
        if (live)
        {
            _framed = false;
            _canvas?.MarkDirty();
        }
    }

    // The canvas from the same top-left corner at a new size.
    public void SetSize(float2 size)
    {
        size = new float2(MathF.Max(1f, MathF.Round(size.x)), MathF.Max(1f, MathF.Round(size.y)));
        if (size == _size)
            return;
        _size = size;
        float density = PickDensity();
        if (MathF.Abs(density - _density) > 0.01f)
            _density = density;
        Apply();
    }

    // Shown or not: a panel waiting for the place it is restored to stays out of sight and does not render.
    public void SetShown(bool shown)
    {
        if (_shown == shown)
            return;
        _shown = shown;
        if (_renderer != null && !_renderer.IsDestroyed && _renderer.Enabled.Value != shown)
            _renderer.Enabled.Value = shown;
        _texture?.Asset?.SetRenderEnabled(shown);
        if (!shown)
            _canvas?.ReleaseAllPointers();
        if (shown)
            _canvas?.MarkDirty();
    }

    private void Apply()
    {
        if (_canvasRect == null || _canvasSlot == null || _texture == null || _mesh == null)
            return;
        SettingsUI.SetOffsets(_canvasRect, new float2(_topLeft.x, _topLeft.y - _size.y), new float2(_topLeft.x + _size.x, _topLeft.y));
        var center = Center;
        var meters = new float2(_size.x * UnitMeters, _size.y * UnitMeters);
        if (_mesh.Size.Value != meters)
            _mesh.Size.Value = meters;
        var position = new float3(center.x * UnitMeters, center.y * UnitMeters, 0f);
        if (Slot.LocalPosition.Value != position)
            Slot.LocalPosition.Value = position;

        int width = System.Math.Max(1, (int)MathF.Round(_size.x * _density));
        int height = System.Math.Max(1, (int)MathF.Round(_size.y * _density));
        if (_texture.Size.Value != new int2(width, height))
            _texture.Size.Value = new int2(width, height);
        if (_texture.OrthographicSize.Value != meters.y)
            _texture.OrthographicSize.Value = meters.y;
        var camera = _canvasSlot.LocalPointToGlobal(new float3(center.x, center.y, 0f)) + float3.Forward * CaptureDistance;
        if (_texture.CameraPosition.Value != camera)
            _texture.CameraPosition.Value = camera;
        _canvas?.MarkDirty();
        _framed = false;
    }

    // Texture pixels per canvas unit for the panel's size in the room now, kept under the texture cap.
    private float PickDensity()
    {
        float scale = MathF.Abs(Slot.GlobalScale.y);
        if (scale <= 0f || float.IsNaN(scale))
            scale = 1f;
        float density = PixelsPerMeter * UnitMeters * scale;
        float cap = MaxTextureSide / MathF.Max(1f, MathF.Max(_size.x, _size.y));
        return System.Math.Clamp(density, 1f, MathF.Max(1f, cap));
    }

    // The texture follows the panel's scale once the hand has let it settle somewhere new.
    public void RefreshDensity()
    {
        float density = PickDensity();
        if (_density > 0f && MathF.Abs(density - _density) / _density < DensityStep)
            return;
        _density = density;
        Apply();
    }

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        var canvas = Canvas;
        var asset = _texture?.Asset;
        if (canvas == null || asset == null || !_shown)
            return;

        // One render the first time the texture exists at its size, then one per finished rebuild.
        if (!_framed)
        {
            _framed = true;
            asset.SetRenderEnabled(true);
            asset.RequestRender();
        }
        if (canvas.ConsumeRenderRequested())
            asset.RequestRender();

        for (int i = 0; i < _fonts.Count; i++)
        {
            int generation = _fonts[i].Asset?.CacheGeneration ?? 0;
            if (generation == _fontGenerations[i] || canvas.IsRebuildInProgress)
                continue;
            _fontGenerations[i] = generation;
            asset.RequestRender();
        }
    }

    // A point on the canvas in its own units, from one pointer, pressed or not. reach: how far a beam went to
    // the quad, in metres.
    public void Point(UIInteractionSource source, int pointerId, in float2 canvasPoint, bool pressed, User? actor, float reach = 0f)
    {
        var canvas = Canvas;
        var slot = CanvasSlot;
        if (canvas == null || slot == null)
            return;
        var world = slot.LocalPointToGlobal(new float3(canvasPoint.x, canvasPoint.y, 0f));
        var forward = slot.Forward;
        canvas.UpdatePointer(source, pointerId, world + forward * RigReach(slot, reach), -forward, pressed, actor);
    }

    // The beam's reach to the quad as the same angle at the rig. The canvas sizes its hover hold and the chrome
    // its drag slop as angles at the pointer's distance, and the quad is scaled away from the rig (a wrist panel
    // to about a third): a fixed reach made them a fixed count of units, under a millimetre on a wrist.
    private float RigReach(Slot canvasSlot, float reach)
    {
        float quadUnit = UnitMeters * MathF.Abs(Slot.GlobalScale.y);
        float rigUnit = MathF.Abs(canvasSlot.GlobalScale.y);
        if (reach <= 0f || !float.IsFinite(reach) || quadUnit <= 1e-9f || rigUnit <= 1e-9f)
            return 0.5f;
        return MathF.Max(0.01f, reach * rigUnit / quadUnit);
    }

    public void ClearPoint(UIInteractionSource source, int pointerId, User? actor)
        => Canvas?.ClearPointer(source, pointerId, actor);

    // A spot on the quad, in the surface slot's own units, as a point on the canvas.
    public float2 CanvasPointOf(in float2 local)
    {
        var center = Center;
        return new float2(center.x + local.x / UnitMeters, center.y + local.y / UnitMeters);
    }

    public override void OnDestroy()
    {
        if (Slot != null)
            Slot.ActiveChanged -= OnActiveChanged;
        if (_canvasSlot != null)
            ByCanvas.Remove(_canvasSlot);
        if (_rig != null && !_rig.IsDestroyed)
            _rig.Destroy();
        _rig = null;
        if (_rigIndex >= 0)
            TakenRigs.Remove(_rigIndex);
        _rigIndex = -1;
        base.OnDestroy();
    }
}
