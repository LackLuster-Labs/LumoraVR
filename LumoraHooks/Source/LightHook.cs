// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

// Light component -> Godot Light3D. Light type (Directional/Point/Spot) maps
// to different Godot subclasses so the platform node is rebuilt via
// ReplacePlatformNode when Owner.Type changes. - xlinka
[ImplementableHook(typeof(Light))]
public class LightHook : NodeBackedComponentHook<Light, Light3D>
{
    private static readonly bool Standalone = OS.HasFeature("android");
    private const float StandaloneShadowDistance = 20f;

    public static IHook<Light> Constructor() => new LightHook();

    public Light3D GodotLight => PlatformNode;

    protected override Light3D CreatePlatformNode() => BuildLight(Owner.Type.Value);

    protected override void OnAfterAttach()
    {
        base.OnAfterAttach();
        EngineSettings.Changed += OnSettingsChanged;
    }

    public override void Destroy(bool destroyingWorld)
    {
        EngineSettings.Changed -= OnSettingsChanged;
        ShadowBudget.Remove(_budget);
        base.Destroy(destroyingWorld);
    }

    // Quality settings are global; re-dirty the owner so SyncProperties runs on the main thread instead
    // of poking the Godot node from wherever the setting was written.
    private void OnSettingsChanged()
    {
        var owner = Owner;
        var world = owner?.World;
        if (owner == null || world == null || owner.IsDestroyed)
            return;
        world.RunSynchronously(() =>
        {
            if (!owner.IsDestroyed)
                owner.MarkChangeDirty();
        });
    }

    // RANGE HAS TO MEAN RANGE.
    //
    // Godot's point falloff is window(d / range) * pow(d, -attenuation), and it ships attenuation at 1,
    // so there is an inverse-DISTANCE term on top of the range window. That term is unbounded as you
    // approach the light and it has nothing to do with the range: a 0.2 m lamp is multiplied by about
    // 10 at 10 cm and 50 at 2 cm, so an 0.6-intensity pendant glow blows out a whole torso while the
    // authored numbers look completely reasonable in the log.
    //
    // A light's Range means a falloff NORMALISED over the range - 1 at the centre, 0 at the edge, no
    // unbounded term anywhere. Attenuation 0 drops pow(d, 0) to 1 and leaves exactly that window,
    // which is the falloff the content was authored for.
    //
    // This moves every light in the engine, not just imported ones, and that is the point: they were
    // all being read through a curve nothing authored against. -xlinka
    private const float NormalizedFalloff = 0f;

    // WHAT THIS HOOK LAST WROTE.
    //
    // The change pass lands here whenever ANY field of the light moves, and several of the platform's
    // setters are not free when the value is the same: a shadow toggle, or a range, angle or bias write,
    // bumps the light's version and re-renders its whole shadow map, and a projector write re-pairs it
    // with everything in range. A world that animates a lamp's colour was re-rendering that lamp's
    // shadow every frame for nothing. Everything goes through a compare against the last write, and
    // the record starts over whenever the node is rebuilt. -xlinka
    private Light3D? _appliedNode;
    private Color _appliedColor;
    private float _appliedEnergy, _appliedRange, _appliedAttenuation, _appliedSpotAngle, _appliedMaxDistance;
    private float _appliedBias, _appliedNormalBias, _appliedBlur, _appliedOpacity;
    private float _appliedFadeBegin, _appliedFadeLength, _appliedFadeShadow;
    private int _appliedFadeEnabled, _appliedVisible, _appliedShadow, _appliedSplits, _appliedOmniMode;
    private bool _appliedPunctual, _appliedSunBand;
    private Texture2D? _appliedProjector;
    private Quaternion _appliedTurn;

    // This light's place in the point and spot shadow budget.
    private readonly ShadowBudget.Entry _budget = new();

    private void ForgetApplied(Light3D light)
    {
        _appliedNode = light;
        _appliedColor = new Color(float.NaN, float.NaN, float.NaN, float.NaN);
        _appliedEnergy = _appliedRange = _appliedAttenuation = _appliedSpotAngle = _appliedMaxDistance = float.NaN;
        _appliedBias = _appliedNormalBias = _appliedBlur = _appliedOpacity = float.NaN;
        _appliedFadeBegin = _appliedFadeLength = _appliedFadeShadow = float.NaN;
        _appliedFadeEnabled = _appliedVisible = _appliedShadow = _appliedSplits = _appliedOmniMode = -1;
        _appliedPunctual = _appliedSunBand = false;
        _appliedProjector = null;
        _appliedTurn = new Quaternion(float.NaN, float.NaN, float.NaN, float.NaN);
    }

    private static bool Changed(ref float last, float value)
    {
        if (last.Equals(value))
            return false;
        last = value;
        return true;
    }

    private static bool Changed(ref int last, int value)
    {
        if (last == value)
            return false;
        last = value;
        return true;
    }

    protected override void SyncProperties()
    {
        if (Owner.Type.GetWasChangedAndClear())
            ReplacePlatformNode(BuildLight(Owner.Type.Value));

        var light = PlatformNode;
        if (light == null) return;
        if (!ReferenceEquals(_appliedNode, light))
            ForgetApplied(light);

        // A zero turn is a light saved before the offset existed: straight along the slot.
        var q = Owner.RotationOffset.Value;
        var turn = q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f ? Quaternion.Identity : new Quaternion(q.x, q.y, q.z, q.w).Normalized();
        if (_appliedTurn != turn)
        {
            _appliedTurn = turn;
            light.Quaternion = turn;
        }

        var c = Owner.LightColor.Value;
        var color = new Color(c.r, c.g, c.b, c.a);
        if (_appliedColor != color)
        {
            _appliedColor = color;
            light.LightColor = color;
        }
        // Godot fades as window(d / range) * d^-decay. With decay 1 and the energy scaled by 0.08 * range the
        // result stays within a few percent of 1 / (1 + 25 x^2) from a tenth of the range out (x = d / range);
        // only right beside the lamp does it run brighter. -xlinka
        bool inverse = Owner.Falloff.Value == LightFalloff.Inverse && light is not DirectionalLight3D;
        float energy = inverse
            ? Owner.Intensity.Value * 0.08f * System.Math.Max(Owner.Range.Value, 0f)
            : Owner.Intensity.Value;
        if (Changed(ref _appliedEnergy, energy))
            light.LightEnergy = energy;
        float falloff = inverse ? 1f : NormalizedFalloff;

        if (light is DirectionalLight3D directional)
        {
            // The user's global scale rides on top of the light's authored distance: shrinking the
            // cascade range is the cheapest real cut on the shadow pass, and it has to work on worlds
            // whose lights were authored by someone else. -xlinka
            float maxDistance = System.Math.Max(1f, Owner.ShadowMaxDistance.Value * EngineSettings.ShadowDistanceScale);
            var splits = Owner.ShadowSplits.Value switch
            {
                ShadowSplitMode.Orthogonal => DirectionalLight3D.ShadowMode.Orthogonal,
                ShadowSplitMode.Two => DirectionalLight3D.ShadowMode.Parallel2Splits,
                _ => DirectionalLight3D.ShadowMode.Parallel4Splits
            };

            // A standalone headset gets one cascade over a short range, whatever the world authored.
            // Four splits are four scene passes at 2048 each and the Pico 4 was spending most of its
            // 100 shadow draws there for six visible ones. -xlinka
            if (Standalone)
            {
                splits = DirectionalLight3D.ShadowMode.Orthogonal;
                maxDistance = System.Math.Min(maxDistance, StandaloneShadowDistance);
            }

            if (Changed(ref _appliedMaxDistance, maxDistance))
                directional.DirectionalShadowMaxDistance = maxDistance;
            if (Changed(ref _appliedSplits, (int)splits))
                directional.DirectionalShadowMode = splits;
        }
        else if (light is OmniLight3D omni)
        {
            if (Changed(ref _appliedRange, Owner.Range.Value))
                omni.OmniRange = Owner.Range.Value;
            if (Changed(ref _appliedAttenuation, falloff))
                omni.OmniAttenuation = falloff;

            // The cheapest shadow preset draws a point light's shadow as two hemispheres instead of six
            // cube faces: a third of the passes and of the caster culling, for some bending on big flat
            // casters right beside the lamp. Every other preset keeps the cube. -xlinka
            var omniMode = EngineSettings.ShadowQuality == 0
                ? OmniLight3D.ShadowMode.DualParaboloid
                : OmniLight3D.ShadowMode.Cube;
            if (Changed(ref _appliedOmniMode, (int)omniMode))
                omni.OmniShadowMode = omniMode;
        }
        else if (light is SpotLight3D spot)
        {
            if (Changed(ref _appliedRange, Owner.Range.Value))
                spot.SpotRange = Owner.Range.Value;
            if (Changed(ref _appliedAttenuation, falloff))
                spot.SpotAttenuation = falloff;

            // HALF OF IT. Godot's spot_angle is measured from the cone's axis and tops out at 90;
            // SpotAngle is the FULL cone and goes to 179. Handing the full angle straight over
            // doubles the cone, which spreads the same energy across roughly four times the area of
            // wall and reads as a light far brighter than the one that was authored.
            //
            // The default is a 60 degree full cone. A default of 30 papered over the conversion in the
            // one place it happened to be visible, while every imported and every hand-built spot
            // stayed twice as wide. -xlinka
            float angle = System.Math.Clamp(Owner.SpotAngle.Value * 0.5f, 0.01f, 90f);
            if (Changed(ref _appliedSpotAngle, angle))
                spot.SpotAngle = angle;
        }

        ApplyFades(light);
        ApplyCookie(light);

        // ShadowStrength was INVERTED, and the result was the worst of both worlds.
        //
        // The old line was ShadowOpacity = 1f - ShadowStrength. The field defaults to 1 and nothing in
        // the entire repo ever writes it, so every light in every world got opacity 0 - a shadow that
        // is fully transparent, i.e. invisible - while ShadowEnabled stayed true and the renderer kept
        // paying for the whole shadow map. Every light was buying a shadow nobody could see.
        //
        // Strength 1 means a strong shadow, so the mapping is direct. And a light whose shadow would be
        // invisible now stops rendering one at all: at opacity zero the two are pixel-identical, so the
        // pass is pure cost. -xlinka
        float shadowOpacity = System.Math.Clamp(Owner.ShadowStrength.Value, 0f, 1f);
        bool wantsShadow = Owner.Shadows.Value != ShadowType.None && shadowOpacity > 0.001f;

        // Positional shadows on standalone are six cubemap faces per omni, each a full scene pass. The
        // one directional light keeps its (single) cascade; everything else goes unshadowed. -xlinka
        if (Standalone && light is not DirectionalLight3D)
            wantsShadow = false;

        if (light is DirectionalLight3D)
        {
            ShadowBudget.Remove(_budget);
            if (Changed(ref _appliedShadow, wantsShadow ? 1 : 0))
                light.ShadowEnabled = wantsShadow;
            if (Changed(ref _appliedOpacity, shadowOpacity))
                light.ShadowOpacity = shadowOpacity;
            // A moving object's shadow stand-in comes in two bands (MovingShadowCasters); the sun takes
            // one, or it would draw that shadow twice.
            if (!_appliedSunBand)
            {
                _appliedSunBand = true;
                light.ShadowCasterMask = ~(uint)Helpers.RenderHelper.SHADOW_BAND_1;
            }
        }
        else
        {
            // Point and spot shadows go through the budget, which writes them.
            float brightness = Owner.Intensity.Value * System.Math.Max(c.r, System.Math.Max(c.g, c.b));
            ShadowBudget.Update(_budget, light, wantsShadow, Owner.Enabled.Value, shadowOpacity,
                Owner.Range.Value, brightness);
        }

        // ShadowType.Soft means PCF-filtered edges. Giving every light an invented physical size
        // enabled PCSS instead, spreading its sparse samples across a wide penumbra. With MSAA and
        // no temporal accumulation that pattern stays visible as dots on close-up avatars.
        // Keep the source punctual and use the shadow filter for softness; a hard light collapses
        // the filter radius. Written even with shadows off so mode switches clear old values.
        if (!_appliedPunctual)
        {
            _appliedPunctual = true;
            light.LightAngularDistance = 0f;
            light.LightSize = 0f;
        }
        if (Changed(ref _appliedBlur, Owner.Shadows.Value == ShadowType.Soft ? 1f : 0f))
            light.ShadowBlur = _appliedBlur;
        if (Changed(ref _appliedBias, Owner.ShadowBias.Value))
            light.ShadowBias = Owner.ShadowBias.Value;
        if (Changed(ref _appliedNormalBias, Owner.ShadowNormalBias.Value))
            light.ShadowNormalBias = Owner.ShadowNormalBias.Value;
        if (Changed(ref _appliedVisible, Owner.Enabled.Value ? 1 : 0))
            light.Visible = Owner.Enabled.Value;
    }

    // Two fades share Godot's one set of fields: the light's own (point and spot), where zero length is
    // the off switch rather than a zero-metre dissolve, and the shadow's, which drops the shadow nearer
    // than the light. A shadow fade on a light with no fade of its own pushes the light's fade out to
    // four times the shadow distance so only the shadow is affected. That push used to test the node's
    // CURRENT length, which still held the platform default of ten, so such a lamp vanished at forty
    // metres instead. -xlinka
    private void ApplyFades(Light3D light)
    {
        float length = light is DirectionalLight3D ? 0f : System.Math.Max(0f, Owner.DistanceFadeLength.Value);
        float begin = System.Math.Max(0f, Owner.DistanceFadeBegin.Value);
        float shadow = System.Math.Max(0f, Owner.ShadowFadeDistance.Value);
        bool enabled = length > 0f || shadow > 0f;
        if (shadow > 0f && length <= 0f)
        {
            begin = shadow * 4f;
            length = shadow;
        }

        if (Changed(ref _appliedFadeEnabled, enabled ? 1 : 0))
            light.DistanceFadeEnabled = enabled;
        if (!enabled)
            return;
        if (Changed(ref _appliedFadeBegin, begin))
            light.DistanceFadeBegin = begin;
        if (Changed(ref _appliedFadeLength, length))
            light.DistanceFadeLength = length;
        if (shadow > 0f && Changed(ref _appliedFadeShadow, shadow))
            light.DistanceFadeShadow = shadow;
    }

    // The cookie is Godot's light projector: multiplied into the light before it reaches anything, so a
    // window frame or a caustic pattern costs one texture and no geometry.
    //
    // Godot has NO directional projector - the property exists on the shared Light3D base but the
    // directional renderer never reads it - so a cookie on a directional light is dropped. It says so
    // once per light rather than per frame, and rather than not at all: a cookie that silently does
    // nothing is the exact class of lying inspector row this pass exists to kill. -xlinka
    private bool _warnedDirectionalCookie;

    private void ApplyCookie(Light3D light)
    {
        Texture2D? projector = null;
        if (Owner.Cookie.Target?.Asset?.Hook is IGodotTexture textureHook && textureHook.IsValid)
            projector = textureHook.GodotTexture2D;

        if (light is DirectionalLight3D)
        {
            if (projector != null && !_warnedDirectionalCookie)
            {
                _warnedDirectionalCookie = true;
                LumoraLogger.Warn(
                    $"Light '{Owner.Slot.SlotName.Value}': cookies are point/spot only, the renderer has no " +
                    "directional projector. Ignoring it.");
            }
            if (_appliedProjector != null)
            {
                _appliedProjector = null;
                light.LightProjector = null;
            }
            return;
        }

        if (!ReferenceEquals(_appliedProjector, projector))
        {
            _appliedProjector = projector;
            light.LightProjector = projector;
        }
    }

    // A light is left out by any camera whose mask misses its layers, like any other instance. The cameras that
    // film the hidden band alone (somebody's far-off avatar taken for its picture, AvatarPictures) have to see
    // the world's lights too, or they film the avatar by the sky's light alone: darker and the colour of the
    // horizon. Nothing on that band is lit by them otherwise; what lives there draws unshaded. -xlinka
    private const uint LightLayers = (uint)(Helpers.RenderHelper.PUBLIC_LAYER | Helpers.RenderHelper.HIDDEN_LAYER);

    private static Light3D BuildLight(LightType type)
    {
        return type switch
        {
            LightType.Directional => new DirectionalLight3D { Name = "DirectionalLight", Layers = LightLayers },
            LightType.Point => new OmniLight3D { Name = "PointLight", Layers = LightLayers },
            LightType.Spot => new SpotLight3D { Name = "SpotLight", Layers = LightLayers },
            _ => throw new ArgumentException($"Unknown light type: {type}")
        };
    }
}
