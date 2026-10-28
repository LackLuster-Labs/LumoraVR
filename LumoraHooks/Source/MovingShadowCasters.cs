// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;
using Lumora.Core;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

// SHADOWS OF THINGS THAT NEVER STOP MOVING.
//
// A point or spot light keeps its shadow map until something it covers changes, then renders every face
// again. A spinning fan, a turntable or a pool ball a script keeps nudging changes every frame, so every
// light around it re-rendered every frame: in an imported lobby four spinners kept four lamps drawing four
// thousand shadow draw calls a frame with the camera standing still, and a balcony light with a 120 m range
// re-rendered the whole building because a record was turning downstairs.
//
// So a caster that has kept moving hands its shadow to a shadow-only copy of itself and stops casting. The
// copy follows it at the user's moving-shadow rate, the lights around it re-render at that rate instead of
// every frame, and they sit on their cache in between. The object itself still moves every frame; only its
// shadow steps.
//
// Each copy comes twice, once per shadow band, and each shadowed light takes one band (ShadowBudget). A
// light renders its shadow again in two frames after anything in it moves, the second time in full, so
// all four lamps around a turntable used to land their two heavy frames together on every step. The
// bands step half a period apart instead: two lamps on one step, the other two half a period later, the
// same work per second in half-height peaks.
//
// Rigid meshes only (a skin or blend shapes would need the copy deformed too), never anything under a user
// (an avatar and what someone holds stay exact), and not a mover with a crowd of casters under it. SlotHook
// reports the slots it moves, the tree's process frame ticks this, main thread only. -xlinka
internal static class MovingShadowCasters
{
    private sealed class Mover
    {
        public SlotHook Hook = null!;
        public uint History;
        public ulong LastFrame;
        public ulong LastMoved;
        public bool Active;
        public bool TooBig;
        public double NextScan;
        public readonly List<Proxy> Proxies = new();
    }

    private sealed class Proxy
    {
        public MeshInstance3D Source = null!;
        public readonly MeshInstance3D[] Shadows = new MeshInstance3D[2];
        public readonly Transform3D[] Applied = new Transform3D[2];
        public GeometryInstance3D.ShadowCastingSetting Original;
        public int Users;
        public bool Visible;

        public bool Alive => GodotObject.IsInstanceValid(Shadows[0]) && GodotObject.IsInstanceValid(Shadows[1]);

        public void Free()
        {
            foreach (var shadow in Shadows)
            {
                if (GodotObject.IsInstanceValid(shadow))
                    shadow.QueueFree();
            }
        }
    }

    // Moved on at least this many of the last 24 frames: sustained motion, not a nudge.
    private const int ActivateFrames = 8;
    private const uint HistoryMask = 0xFFFFFFu;

    // Still for this many frames hands the shadow back to the object itself.
    private const ulong ReleaseFrames = 45;

    // A mover with more casters than this under it is a vehicle or a building, not a fan.
    private const int MaxCastersPerMover = 24;

    // Copies for a few fans and turntables, not for a crowd. A thousand falling props each grew two copies and
    // tripled the draw calls of the frame they were meant to save; past this many, movers cast for themselves.
    private const int MaxActiveMovers = 64;
    private static int _activeMovers;

    private const double RescanSeconds = 2.0;

    private static readonly Dictionary<SlotHook, Mover> _movers = new();
    private static readonly Dictionary<MeshInstance3D, Proxy> _proxies = new();
    private static readonly List<SlotHook> _drop = new();
    private static readonly List<MeshInstance3D> _dropProxies = new();
    private static bool _ticking;
    private static double _nextTick;
    private static int _turn;
    private static bool _countChanged;
    private static double _nextLog;

    public static int ActiveProxies => _proxies.Count;

    // No point or spot shadows on a standalone headset or a phone, so nothing to save there.
    private static readonly bool Standalone = OS.HasFeature("android");

    private static int Rate => EngineSettings.MovingShadowRate;

    // From SlotHook, when it actually wrote a new transform to the slot's node.
    public static void NoteMoved(SlotHook hook)
    {
        if (Standalone || Rate <= 0 || hook.Owner == null || hook.Owner.ActiveUserRoot != null)
            return;
        ulong frame = global::Godot.Engine.GetProcessFrames();
        if (!_movers.TryGetValue(hook, out var mover))
        {
            mover = new Mover { Hook = hook, LastFrame = frame };
            _movers[hook] = mover;
        }
        Advance(mover, frame);
        mover.History |= 1u;
        mover.LastMoved = frame;
        EnsureTicking();
    }

    public static void Forget(SlotHook hook)
    {
        if (!_movers.Remove(hook, out var mover))
            return;
        Deactivate(mover);
    }

    private static void Advance(Mover mover, ulong frame)
    {
        ulong gap = frame - mover.LastFrame;
        if (gap == 0)
            return;
        mover.History = gap >= 32 ? 0u : mover.History << (int)gap;
        mover.LastFrame = frame;
    }

    private static void EnsureTicking()
    {
        if (_ticking || global::Godot.Engine.GetMainLoop() is not SceneTree tree)
            return;
        _ticking = true;
        tree.ProcessFrame += Tick;
    }

    private static void Tick()
    {
        if (_movers.Count == 0 && _proxies.Count == 0)
            return;

        if (Rate <= 0)
        {
            foreach (var mover in _movers.Values)
                Deactivate(mover);
            _movers.Clear();
            return;
        }

        ulong frame = global::Godot.Engine.GetProcessFrames();
        double now = Lumora.Core.Engine.Current?.TotalTime ?? 0.0;

        _drop.Clear();
        foreach (var (hook, mover) in _movers)
        {
            Advance(mover, frame);
            ulong still = frame - mover.LastMoved;
            if (!mover.Active)
            {
                if (System.Numerics.BitOperations.PopCount(mover.History & HistoryMask) >= ActivateFrames
                    && _activeMovers < MaxActiveMovers)
                    Activate(mover, now);
                else if (still > 240)
                    _drop.Add(hook);
            }
            else if (still > ReleaseFrames)
            {
                Deactivate(mover);
                _drop.Add(hook);
            }
            else if (!mover.TooBig && now >= mover.NextScan)
            {
                Scan(mover, now);
            }
        }
        foreach (var hook in _drop)
            _movers.Remove(hook);

        // The bands take turns, half a period apart. Visibility follows the object every frame, so a copy
        // never casts for something switched off.
        int band = -1;
        if (now >= _nextTick)
        {
            band = _turn;
            _turn ^= 1;
            _nextTick = now + 0.5 / Rate;
        }
        _dropProxies.Clear();
        foreach (var (source, proxy) in _proxies)
        {
            if (!GodotObject.IsInstanceValid(source) || !proxy.Alive)
            {
                _dropProxies.Add(source);
                continue;
            }
            bool visible = source.IsVisibleInTree();
            if (visible != proxy.Visible)
            {
                proxy.Visible = visible;
                proxy.Shadows[0].Visible = visible;
                proxy.Shadows[1].Visible = visible;
            }
            if (band >= 0 && !Follow(proxy, band))
                _dropProxies.Add(source);
        }
        foreach (var source in _dropProxies)
        {
            if (_proxies.Remove(source, out var proxy))
            {
                if (GodotObject.IsInstanceValid(source) && source.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off)
                    source.CastShadow = proxy.Original;
                proxy.Free();
            }
            _countChanged = true;
        }

        if (_countChanged && now >= _nextLog)
        {
            _countChanged = false;
            _nextLog = now + 2.0;
            int active = 0;
            foreach (var mover in _movers.Values)
                if (mover.Active && mover.Proxies.Count > 0) active++;
            LumoraLogger.Log($"Moving shadows: {_proxies.Count} casters on {active} moving objects cast through shadow copies at {Rate} Hz");
        }
    }

    // The copy catches up with the object: where it is, what it looks like, and whether it casts at all.
    // Writes only what changed, since every write tells the lights around it to render again. False when the
    // object now casts in a way no copy can, and has to cast for itself again.
    private static bool Follow(Proxy proxy, int band)
    {
        var source = proxy.Source;
        var shadow = proxy.Shadows[band];

        // The renderer set its cast mode again: take that as the new original and keep the object quiet.
        if (source.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off)
        {
            proxy.Original = source.CastShadow;
            source.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        }
        if (!CopyCastsAlike(source, proxy.Original))
            return false;

        if (!ReferenceEquals(shadow.Mesh, source.Mesh))
            shadow.Mesh = source.Mesh;
        if (!ReferenceEquals(shadow.MaterialOverride, source.MaterialOverride))
            shadow.MaterialOverride = source.MaterialOverride;
        int surfaces = source.GetSurfaceOverrideMaterialCount();
        for (int i = 0; i < surfaces && i < shadow.GetSurfaceOverrideMaterialCount(); i++)
        {
            var material = source.GetSurfaceOverrideMaterial(i);
            if (!ReferenceEquals(shadow.GetSurfaceOverrideMaterial(i), material))
                shadow.SetSurfaceOverrideMaterial(i, material);
        }

        var xf = source.GlobalTransform;
        if (xf != proxy.Applied[band])
        {
            proxy.Applied[band] = xf;
            shadow.GlobalTransform = xf;
        }
        return true;
    }

    // A copy casts shadow-only, and the platform has no setting that is shadow-only AND double-sided, so a copy
    // casts with its material's culling. That is the same shadow for anything but a DoubleSided caster wearing
    // a material that culls: the far side of its shadow would go missing the moment it moved. -xlinka
    private static bool CopyCastsAlike(MeshInstance3D mesh, GeometryInstance3D.ShadowCastingSetting cast)
    {
        if (cast != GeometryInstance3D.ShadowCastingSetting.DoubleSided)
            return true;
        int surfaces = mesh.Mesh?.GetSurfaceCount() ?? 0;
        for (int i = 0; i < surfaces; i++)
        {
            if (!DrawsBothSides(mesh.GetActiveMaterial(i)))
                return false;
        }
        return true;
    }

    // Culling lives in the shader source as a render_mode, read once per shader.
    private static readonly ConditionalWeakTable<Shader, StrongBox<bool>> _bothSides = new();

    private static bool DrawsBothSides(Material? material)
    {
        if (material is BaseMaterial3D native)
            return native.CullMode == BaseMaterial3D.CullModeEnum.Disabled;
        if (material is not ShaderMaterial { Shader: { } shader })
            return false;
        if (!_bothSides.TryGetValue(shader, out var known))
        {
            known = new StrongBox<bool>(RendersBothSides(shader.Code));
            _bothSides.AddOrUpdate(shader, known);
        }
        return known.Value;
    }

    private static bool RendersBothSides(string? code)
    {
        if (string.IsNullOrEmpty(code))
            return false;
        for (int at = code.IndexOf("render_mode", System.StringComparison.Ordinal); at >= 0;
             at = code.IndexOf("render_mode", at + 11, System.StringComparison.Ordinal))
        {
            int line = code.LastIndexOf('\n', at) + 1;
            if (code.IndexOf("//", line, at - line, System.StringComparison.Ordinal) >= 0)
                continue;
            int end = code.IndexOf(';', at);
            if (end < 0)
                return false;
            if (code.IndexOf("cull_disabled", at, end - at, System.StringComparison.Ordinal) >= 0)
                return true;
        }
        return false;
    }

    private static void Activate(Mover mover, double now)
    {
        mover.Active = true;
        _activeMovers++;
        Scan(mover, now);
    }

    // Collects the casters under the mover and gives each one a copy. Run again now and then, so a part
    // that appears later (a mesh that finished loading, a renderer rebuilt) is picked up.
    private static void Scan(Mover mover, double now)
    {
        mover.NextScan = now + RescanSeconds;
        var node = mover.Hook.GeneratedNode3D;
        var root = mover.Hook.Owner?.World?.GodotSceneRoot as Node3D;
        if (node == null || !GodotObject.IsInstanceValid(node) || root == null || !GodotObject.IsInstanceValid(root))
            return;

        var found = node.FindChildren("*", "MeshInstance3D", true, false);
        var casters = new List<MeshInstance3D>();
        foreach (var n in found)
        {
            if (n is not MeshInstance3D mesh)
                continue;
            if (_proxies.TryGetValue(mesh, out var existing))
            {
                if (!mover.Proxies.Contains(existing))
                    casters.Add(mesh);
                continue;
            }
            if (mesh.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off || mesh.Mesh == null || mesh.Skin != null)
                continue;
            if (!CopyCastsAlike(mesh, mesh.CastShadow))
                continue;
            // Plain world geometry only: every camera sees the bands, so anything drawn for fewer keeps
            // casting itself.
            if (mesh.Layers != (uint)Helpers.RenderHelper.PUBLIC_LAYER)
                continue;
            if (mesh.Mesh is ArrayMesh arrayMesh && arrayMesh.GetBlendShapeCount() > 0)
                continue;
            casters.Add(mesh);
        }

        if (mover.Proxies.Count + casters.Count > MaxCastersPerMover)
        {
            mover.TooBig = true;
            return;
        }

        foreach (var mesh in casters)
        {
            if (!_proxies.TryGetValue(mesh, out var proxy))
            {
                proxy = CreateProxy(mesh, root);
                _proxies[mesh] = proxy;
                _countChanged = true;
            }
            proxy.Users++;
            mover.Proxies.Add(proxy);
        }
    }

    private static Proxy CreateProxy(MeshInstance3D source, Node3D root)
    {
        var xf = source.GlobalTransform;
        bool visible = source.IsVisibleInTree();
        var proxy = new Proxy
        {
            Source = source,
            Original = source.CastShadow,
            Visible = visible,
        };
        int surfaces = source.GetSurfaceOverrideMaterialCount();
        for (int band = 0; band < 2; band++)
        {
            var shadow = new MeshInstance3D
            {
                Name = "ShadowCopy",
                Mesh = source.Mesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly,
                Layers = (uint)(band == 0 ? Helpers.RenderHelper.SHADOW_BAND_0 : Helpers.RenderHelper.SHADOW_BAND_1),
                GIMode = GeometryInstance3D.GIModeEnum.Disabled,
                MaterialOverride = source.MaterialOverride,
                ExtraCullMargin = source.ExtraCullMargin,
                LodBias = source.LodBias,
                VisibilityRangeBegin = source.VisibilityRangeBegin,
                VisibilityRangeBeginMargin = source.VisibilityRangeBeginMargin,
                VisibilityRangeEnd = source.VisibilityRangeEnd,
                VisibilityRangeEndMargin = source.VisibilityRangeEndMargin,
                VisibilityRangeFadeMode = source.VisibilityRangeFadeMode,
            };
            root.AddChild(shadow);
            for (int i = 0; i < surfaces; i++)
                shadow.SetSurfaceOverrideMaterial(i, source.GetSurfaceOverrideMaterial(i));
            shadow.GlobalTransform = xf;
            shadow.Visible = visible;
            proxy.Shadows[band] = shadow;
            proxy.Applied[band] = xf;
        }
        source.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        return proxy;
    }

    private static void Deactivate(Mover mover)
    {
        if (mover.Active)
            _activeMovers--;
        mover.Active = false;
        mover.TooBig = false;
        foreach (var proxy in mover.Proxies)
        {
            if (--proxy.Users > 0)
                continue;
            // A copy already handed back (Follow) may have been replaced since; only the live one owns the
            // object's cast setting.
            if (_proxies.TryGetValue(proxy.Source, out var live) && ReferenceEquals(live, proxy))
            {
                _proxies.Remove(proxy.Source);
                _countChanged = true;
                if (GodotObject.IsInstanceValid(proxy.Source) && proxy.Source.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off)
                    proxy.Source.CastShadow = proxy.Original;
            }
            proxy.Free();
        }
        mover.Proxies.Clear();
    }
}
