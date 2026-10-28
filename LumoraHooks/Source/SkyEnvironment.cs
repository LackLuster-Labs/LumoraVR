// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Godot;
using Lumora.Core;

namespace Lumora.Godot.Hooks;

// Decides which sky hook actually gets to write the scene's WorldEnvironment.
//
// There is one WorldEnvironment node for the whole tree, but any number of worlds can be open at
// once and a world can hold both a Skybox and a GradientSkybox. Without a single arbiter each hook
// stomps the node in whatever order its change pass happened to drain, which is not the same order
// twice, and a hook tearing down would restore a snapshot taken before somebody else's claim - the
// sky ends up depending on load timing.
//
// So no sky hook touches the node. They hand over a built Environment and a priority; this picks the
// winner among the claims belonging to the FOCUSED world and applies exactly that one. Worlds stay
// loaded when the user switches away, so their claims stay registered and simply stop winning, and
// the bootstrap environment comes back when nothing claims at all. -xlinka
public static class SkyEnvironment
{
    // The user's post stack, one record so every environment gets the same write. AmbientOcclusion is the
    // 0..1 strength the settings expose; 0 is off.
    public struct PostSettings
    {
        public float Bloom;
        public float AmbientOcclusion;
        public bool ScreenSpaceReflections;
        public bool ScreenSpaceIndirectLighting;
    }

    // What ApplyPostSettings last wrote, for an environment built later (a sky claim, a camera) so it
    // starts from the same stack instead of the scene file's.
    public static PostSettings CurrentPost { get; private set; } = new()
    {
        Bloom = 0.5f, AmbientOcclusion = 1f,
    };

    // loses to anything that carries a cubemap
    public const int GradientPriority = 0;

    // the core has already picked one per world
    public const int SkyboxPriority = 100;

    private sealed class Claim
    {
        public object Owner = null!;
        public World World = null!;
        public int Priority;
        public long Sequence;
        public global::Godot.Environment? Environment;
    }

    private static readonly List<Claim> _claims = new();
    private static readonly object _lock = new();

    // A world's own ambient light, laid over whichever environment wins for it. The environment's own
    // ambient is kept aside while the light is on it and put back when it goes. -xlinka
    private sealed class AmbientClaim
    {
        public object Owner = null!;
        public World World = null!;
        public Color Light;
    }

    private sealed class KeptAmbient
    {
        public global::Godot.Environment.AmbientSource Source;
        public Color Color;
        public float Energy;
        public float SkyContribution;
    }

    private static readonly List<AmbientClaim> _ambientClaims = new();
    private static global::Godot.Environment? _ambientOn;
    private static KeptAmbient? _keptAmbient;

    private static WorldEnvironment _node = null!;
    private static bool _ownsNode;
    private static global::Godot.Environment _bootstrap = null!;
    private static bool _bootstrapCaptured;
    private static FocusManager _focus = null!;
    private static long _sequence;

    // The sky the scene is currently drawing, for anything that has to render the same sky through its
    // own Environment instead of the shared WorldEnvironment - an offscreen camera clearing to skybox,
    // for one. Null before the node exists or when the winning environment is not sky-backed.
    public static Sky? CurrentSky
    {
        get
        {
            if (_node == null || !GodotObject.IsInstanceValid(_node))
                return null;
            var environment = _node.Environment;
            if (environment == null || !GodotObject.IsInstanceValid(environment))
                return null;
            return environment.Sky;
        }
    }

    // The environment the scene shows before any sky hook exists. Sky hooks duplicate it so they
    // inherit whatever fog, tonemapping and post settings the project set up, instead of starting
    // from an empty Environment and quietly dropping all of it.
    public static global::Godot.Environment Bootstrap(Node anyNodeInTree)
    {
        EnsureNode(anyNodeInTree);
        return _bootstrap;
    }

    // Push the post-processing settings onto the bootstrap environment AND every claimed one.
    //
    // Sky hooks DUPLICATE the bootstrap rather than referencing it, which is what lets a world carry
    // its own sky without stamping on everyone else's - but it also means a live settings change has
    // to be written to each copy, because a duplicate stopped tracking its source the moment it was
    // made. Applied to every claim rather than only the winner so switching worlds cannot reveal a
    // stale environment. -xlinka
    private static bool _postApplied;

    public static void ApplyPostSettings(in PostSettings settings, Node anyNodeInTree)
    {
        EnsureNode(anyNodeInTree);
        CurrentPost = settings;
        _postApplied = true;

        if (_bootstrap != null && GodotObject.IsInstanceValid(_bootstrap))
            WritePost(_bootstrap, in settings);

        lock (_lock)
        {
            foreach (var claim in _claims)
            {
                if (claim.Environment != null && GodotObject.IsInstanceValid(claim.Environment))
                    WritePost(claim.Environment, in settings);
            }
        }
    }

    // The current stack onto an environment somebody else owns (an offscreen camera's).
    public static void WritePost(global::Godot.Environment environment)
        => WritePost(environment, CurrentPost);

    private static void WritePost(global::Godot.Environment environment, in PostSettings settings)
    {
        // Linear out, no curve and no adjustment pass: both cost every pixel of both eyes, and a world's
        // grade is the world's to set. A world environment that brought its own curve is put back to
        // linear here too. -xlinka
        environment.TonemapMode = global::Godot.Environment.ToneMapper.Linear;
        environment.TonemapExposure = 1f;
        environment.TonemapWhite = 1f;
        environment.AdjustmentEnabled = false;

        // Zero turns the pass OFF rather than running it at no strength: a glow pass costs its blur
        // chain whether or not anything is bright enough to feed it.
        //
        // And on a standalone headset it is always off, whatever the world asked for. The bootstrap
        // environment had glow disabled at XR init, then this replaced it with the world's own
        // environment and glow came straight back: measured 270 ms of GPU per frame at 1504x1504 on
        // a Pico 4 with the bloom chain running. Worlds keep their bloom on desktop. -xlinka
        bool standalone = OS.HasFeature("android");
        environment.GlowEnabled = !standalone && settings.Bloom > 0.001f;
        environment.GlowIntensity = settings.Bloom;
        // Bloom here is the share of the WHOLE frame fed into the glow, not only what clears the threshold.
        // The scene's 0.05 laid a soft copy of every frame over itself, and every panel's text with it.
        environment.GlowBloom = 0f;

        // Screen-space ambient occlusion. The strength maps the 0..1 setting onto the renderer's
        // scale where 2 is its own default; the rest is shaped for a broad, soft falloff rather than a
        // tight dark crease, with the radius held at a metre, which is where contact shadow under feet
        // and in corners lives. LightAffect lets a little of it into directly lit surfaces, so the
        // occlusion darkens lit floors too, and that is what reads as grounded. Forward+ only; the
        // mobile renderer has no such pass and the property is inert there. -xlinka
        // The renderer's occlusion only darkens AMBIENT light unless LightAffect lets it into direct
        // light, and a sunlit open floor has almost no ambient to darken, so at the engine default the
        // pass is on and invisible. Occlusion that darkens lit floors as well is what reads as
        // grounded. The photoreal guides for this engine use these numbers: intensity 3, and both
        // affects at 1 "for full-scene plasticity". The slider scales all three together, so half
        // strength is half the crease and half the reach into lit surfaces, not a strong crease that
        // stops at the shadow line. -xlinka
        bool ao = !standalone && settings.AmbientOcclusion > 0.001f;
        environment.SsaoEnabled = ao;
        if (ao)
        {
            float strength = System.Math.Clamp(settings.AmbientOcclusion, 0f, 1f);
            environment.SsaoRadius = 1f;
            environment.SsaoIntensity = strength * 3f;
            environment.SsaoPower = 1.5f;
            environment.SsaoDetail = 0.5f;
            environment.SsaoHorizon = 0.06f;
            environment.SsaoSharpness = 0.98f;
            environment.SsaoLightAffect = strength;
            environment.SsaoAOChannelAffect = strength;
        }

        // Indirect lighting at the renderer's defaults (5 m radius, intensity 1): a switch, since the
        // engine's guide treats it as on-or-off alongside the occlusion.
        environment.SsilEnabled = !standalone && settings.ScreenSpaceIndirectLighting;

        // Screen-space reflections at the renderer's own defaults; the setting is a switch.
        environment.SsrEnabled = !standalone && settings.ScreenSpaceReflections;
    }

    // What the main view is actually rendering through, against what was just written. The scene's
    // WorldEnvironment hands its resource to the root viewport's World3D; if those two ever disagree
    // every setting above lands on a resource nothing draws, and the only symptom is "nothing
    // changed". One line per apply, read back from the resource, not from what we meant to write.
    public static string DescribeRendering(Node anyNodeInTree)
    {
        var root = anyNodeInTree?.GetTree()?.Root;
        var rendering = root?.World3D?.Environment;
        var installed = _node != null && GodotObject.IsInstanceValid(_node) ? _node.Environment : null;
        if (rendering == null)
            return "rendering env=none";
        string match = installed == null ? "no WorldEnvironment node"
            : ReferenceEquals(rendering, installed) ? "matches WorldEnvironment"
            : "DIFFERS from WorldEnvironment";
        return $"rendering env={rendering.GetRid().Id} ({match}) tonemap={rendering.TonemapMode} "
             + $"ssao={rendering.SsaoEnabled} (intensity {rendering.SsaoIntensity:0.##}, light {rendering.SsaoLightAffect:0.##}) "
             + $"ssil={rendering.SsilEnabled} ssr={rendering.SsrEnabled} glow={rendering.GlowEnabled} adjust={rendering.AdjustmentEnabled} "
             + $"method={RenderingServer.GetCurrentRenderingMethod()}";
    }

    // re-claiming with the same owner replaces its environment
    public static void Claimed(object owner, World world, int priority, global::Godot.Environment environment, Node anyNodeInTree)
    {
        EnsureNode(anyNodeInTree);

        // A claim built from a bootstrap duplicate taken before the settings ever ran would carry the
        // scene file's stack. Stamp the live one on the way in; a handful of property writes.
        if (_postApplied && environment != null && GodotObject.IsInstanceValid(environment))
            WritePost(environment, CurrentPost);

        lock (_lock)
        {
            // The claimer has just written its environment's ambient itself, so that is what to keep aside.
            if (environment != null && ReferenceEquals(environment, _ambientOn))
            {
                _ambientOn = null;
                _keptAmbient = null;
            }

            var existing = Find(owner);
            if (existing != null)
            {
                existing.World = world;
                existing.Priority = priority;
                existing.Environment = environment;
            }
            else
            {
                _claims.Add(new Claim
                {
                    Owner = owner,
                    World = world,
                    Priority = priority,
                    Environment = environment,
                    Sequence = ++_sequence,
                });
            }
        }

        Resolve();
    }

    // safe to call for an owner that never claimed
    public static void Released(object owner)
    {
        lock (_lock)
        {
            var existing = Find(owner);
            if (existing == null)
                return;
            _claims.Remove(existing);
        }

        Resolve();
    }

    // called after any claim change and on every focus switch
    public static void Resolve()
    {
        if (_node == null || !GodotObject.IsInstanceValid(_node))
            return;

        var focused = _focus?.FocusedWorld;
        Claim? winner = null;

        lock (_lock)
        {
            foreach (var claim in _claims)
            {
                if (claim.Environment == null || !GodotObject.IsInstanceValid(claim.Environment))
                    continue;
                // With no focus manager (the single-world boot path) there is nothing to filter on,
                // so every claim is eligible and priority alone decides.
                if (focused != null && claim.World != focused)
                    continue;
                if (winner == null || Outranks(claim, winner))
                    winner = claim;
            }
        }

        var target = winner?.Environment ?? _bootstrap;
        if (_node.Environment != target)
            _node.Environment = target;
        ApplyAmbient(target, focused);
    }

    // re-claiming with the same owner replaces its light; linear RGB
    public static void AmbientClaimed(object owner, World world, in Color linearLight, Node anyNodeInTree)
    {
        EnsureNode(anyNodeInTree);
        lock (_lock)
        {
            AmbientClaim? existing = null;
            foreach (var claim in _ambientClaims)
            {
                if (ReferenceEquals(claim.Owner, owner))
                    existing = claim;
            }
            if (existing == null)
                _ambientClaims.Add(existing = new AmbientClaim { Owner = owner });
            existing.World = world;
            existing.Light = linearLight;
        }
        Resolve();
    }

    public static void AmbientReleased(object owner)
    {
        lock (_lock)
        {
            if (_ambientClaims.RemoveAll(claim => ReferenceEquals(claim.Owner, owner)) == 0)
                return;
        }
        Resolve();
    }

    private static void ApplyAmbient(global::Godot.Environment? target, World? focused)
    {
        AmbientClaim? light = null;
        lock (_lock)
        {
            foreach (var claim in _ambientClaims)
            {
                if (focused == null || claim.World == focused)
                    light = claim;
            }
        }

        // What was laid on another environment goes back first.
        if (_ambientOn != null && (!ReferenceEquals(_ambientOn, target) || light == null))
        {
            if (GodotObject.IsInstanceValid(_ambientOn) && _keptAmbient != null)
            {
                _ambientOn.AmbientLightSource = _keptAmbient.Source;
                _ambientOn.AmbientLightColor = _keptAmbient.Color;
                _ambientOn.AmbientLightEnergy = _keptAmbient.Energy;
                _ambientOn.AmbientLightSkyContribution = _keptAmbient.SkyContribution;
            }
            _ambientOn = null;
            _keptAmbient = null;
        }

        if (light == null || target == null || !GodotObject.IsInstanceValid(target))
            return;
        if (_ambientOn == null)
        {
            _keptAmbient = new KeptAmbient
            {
                Source = target.AmbientLightSource,
                Color = target.AmbientLightColor,
                Energy = target.AmbientLightEnergy,
                SkyContribution = target.AmbientLightSkyContribution,
            };
            _ambientOn = target;
        }
        // The colour property is read as sRGB; the light arrives linear, brighter than 1 kept by energy.
        var linear = light.Light;
        float peak = System.Math.Max(1f, System.Math.Max(linear.R, System.Math.Max(linear.G, linear.B)));
        target.AmbientLightSource = global::Godot.Environment.AmbientSource.Color;
        target.AmbientLightColor = new Color(linear.R / peak, linear.G / peak, linear.B / peak).LinearToSrgb();
        target.AmbientLightEnergy = peak;
    }

    private static bool Outranks(Claim candidate, Claim current)
    {
        if (candidate.Priority != current.Priority)
            return candidate.Priority > current.Priority;
        // Equal priority means two hooks of the same kind in one world, which the core is supposed to
        // have already resolved. Newest wins so the situation at least settles instead of oscillating.
        return candidate.Sequence > current.Sequence;
    }

    private static Claim? Find(object owner)
    {
        foreach (var claim in _claims)
        {
            if (ReferenceEquals(claim.Owner, owner))
                return claim;
        }
        return null;
    }

    private static void EnsureNode(Node anyNodeInTree)
    {
        if (_node != null && GodotObject.IsInstanceValid(_node))
        {
            EnsureFocusHook();
            return;
        }

        var tree = anyNodeInTree?.GetTree();
        var root = tree?.Root;
        if (root == null)
            return;

        _node = (root.FindChild("WorldEnvironment", true, false) as WorldEnvironment)!;
        if (_node == null)
        {
            _node = new WorldEnvironment { Name = "WorldEnvironment" };
            root.AddChild(_node);
            _ownsNode = true;
        }

        if (!_bootstrapCaptured)
        {
            _bootstrapCaptured = true;
            _bootstrap = _node.Environment;
        }

        EnsureFocusHook();
    }

    private static void EnsureFocusHook()
    {
        if (_focus != null)
            return;
        _focus = Lumora.Core.Engine.Current?.FocusManager!;
        if (_focus != null)
            _focus.OnFocusedWorldChanged += (_, _) => Resolve();
    }

    // Only the hook that created the node would ever have freed it, and it has no way to know
    // whether another hook is still using it, so the decision lives here with the claim list.
    public static void ReleaseNodeIfUnused()
    {
        lock (_lock)
        {
            if (_claims.Count > 0)
                return;
        }

        if (!_ownsNode || _node == null || !GodotObject.IsInstanceValid(_node))
            return;

        _node.QueueFree();
        _node = null!;
        _ownsNode = false;
        _bootstrapCaptured = false;
        _bootstrap = null!;
    }
}
