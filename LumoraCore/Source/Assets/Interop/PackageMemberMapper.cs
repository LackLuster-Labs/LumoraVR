// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Persistence;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;

namespace Lumora.Core.Assets.Interop;

// Copies member values from a package's saved component onto ours, by name.
//
// Worth doing generically rather than hand-coding each component: the member names line up almost
// everywhere that matters - AlbedoColor, AlbedoTexture, NormalMap, TextureScale, Metallic, Smoothness -
// so one mapper configures every material, collider and renderer at once. A hand-written table would
// cover a handful of types and silently leave the rest at defaults, which is how an import ends up
// technically successful and uniformly grey.
//
// Anything that does not line up is skipped and counted. Nothing is guessed at: a member is written
// only when the name matches AND the stored shape converts to our type. -xlinka
public static partial class PackageMemberMapper
{
    static PackageMemberMapper()
    {
        MemberAliasesK1(MemberAliases);
        MemberAliasesK2(MemberAliases);
        MemberAliasesK3(MemberAliases);
        MemberAliasesK4(MemberAliases);
        MemberAliasesK5(MemberAliases);
        MemberAliasesK6(MemberAliases);
        MemberAliasesK7(MemberAliases);
        MemberAliasesK8(MemberAliases);
        MemberAliasesRendering(MemberAliases);
        MemberAliasesUi(MemberAliases);
        MemberAliasesMats(MemberAliases);
        EnumAliasesMats(EnumValueAliases);
    }

    static partial void MemberAliasesK1(Dictionary<string, Dictionary<string, string>> table);
    static partial void MemberAliasesK2(Dictionary<string, Dictionary<string, string>> table);
    static partial void MemberAliasesK3(Dictionary<string, Dictionary<string, string>> table);
    static partial void MemberAliasesK4(Dictionary<string, Dictionary<string, string>> table);
    static partial void MemberAliasesK5(Dictionary<string, Dictionary<string, string>> table);
    static partial void MemberAliasesK6(Dictionary<string, Dictionary<string, string>> table);
    static partial void MemberAliasesK7(Dictionary<string, Dictionary<string, string>> table);
    static partial void MemberAliasesK8(Dictionary<string, Dictionary<string, string>> table);
    static partial void MemberAliasesRendering(Dictionary<string, Dictionary<string, string>> table);
    static partial void MemberAliasesUi(Dictionary<string, Dictionary<string, string>> table);
    static partial void ApplyUi(Component component, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList, Func<Uri, Uri?> remapUrl);

    static partial void ApplyK1(Component component, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList, Func<Uri, Uri?> remapUrl);
    static partial void ApplyK2(Component component, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList, Func<Uri, Uri?> remapUrl);
    static partial void ApplyK3(Component component, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList, Func<Uri, Uri?> remapUrl);
    static partial void ApplyK4(Component component, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList, Func<Uri, Uri?> remapUrl);
    static partial void ApplyK5(Component component, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList, Func<Uri, Uri?> remapUrl);
    static partial void ApplyK6(Component component, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList, Func<Uri, Uri?> remapUrl);
    static partial void ApplyK7(Component component, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList, Func<Uri, Uri?> remapUrl);
    static partial void ApplyK8(Component component, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList, Func<Uri, Uri?> remapUrl);

    // Where the same data lives under a different member name on our side. Keyed by OUR component type
    // name, then by the PACKAGE's member name.
    //
    // SkinnedMeshRenderer is the one that matters: the package type derives from the package's
    // MeshRenderer and so calls the geometry "Mesh", while ours is its own component and calls it
    // "MeshAsset". Nothing matched, the mesh was never wired, and every skinned mesh - which on an avatar
    // means the body, the head, the arms, essentially all of it - rendered nothing at all while the rigid
    // props came through fine. Ours also takes a single Material where the package holds a Materials
    // list. -xlinka
    private static readonly Dictionary<string, Dictionary<string, string>> MemberAliases =
        new(StringComparer.Ordinal)
        {
            ["SkinnedMeshRenderer"] = new(StringComparer.Ordinal)
            {
                ["Mesh"] = "MeshAsset",
            },

            // Who holds it, read by graphs through the member.
            ["Grabbable"] = new(StringComparer.Ordinal) { ["_grabber"] = "GrabberRef" },

            ["PlaceAtUser"] = new(StringComparer.Ordinal) { ["TargetPositionOffset"] = "Offset" },

            // Inline tags are read unless the text says not to.
            ["Text"] = new(StringComparer.Ordinal) { ["ParseRichText"] = "RichText" },

            // A scope's name is what every named variable under it binds by; unnamed, all of them bound nothing.
            ["VariableScope"] = new(StringComparer.Ordinal)
            {
                ["SpaceName"] = "ScopeName",
                ["OnlyDirectBinding"] = "DirectBindingOnly",
            },

            // An object standing for what it references, kept for good (see ApplyReferenceProxy).
            ["ReferenceProxy"] = new(StringComparer.Ordinal)
            {
                ["Reference"] = "Target",
                ["SpawnInstanceOnTrigger"] = "SpawnInstanceOnActivate",
            },

            // The package's probe box is a full size centred on the slot, as ours is, and its baked cubemap
            // is the one ours shows (see ApplyReflectionProbe).
            ["ReflectionProbe"] = new(StringComparer.Ordinal)
            {
                ["BoxSize"] = "Size",
                ["BakedCubemap"] = "Cubemap",
            },

            // The package's switch and gradient drivers keep their target and stops under other names.
            ["BoolToValue`1"] = new(StringComparer.Ordinal)
            {
                ["TargetField"] = "Target",
            },
            ["ValueGradient`1"] = new(StringComparer.Ordinal)
            {
                ["Points"] = "Stops",
            },

            // Press receivers and drivers under other names for the same members.
            ["PressSetValue`1"] = new(StringComparer.Ordinal) { ["SetValue"] = "Value" },
            ["PressSetReference`1"] = new(StringComparer.Ordinal) { ["SetReference"] = "Value" },
            ["PressCycleReference`1"] = new(StringComparer.Ordinal) { ["Targets"] = "References" },
            ["ValueEquals`1"] = new(StringComparer.Ordinal) { ["TargetValue"] = "Source" },
            ["AnyAllBools"] = new(StringComparer.Ordinal) { ["Conditions"] = "Inputs" },
            ["BoolToReference`1"] = new(StringComparer.Ordinal)
            {
                ["TargetReference"] = "Target",
                ["FalseTarget"] = "FalseReference",
                ["TrueTarget"] = "TrueReference",
            },
            ["ValueCaption`1"] = Caption("Value"),
            ["ReferenceCaption`1"] = Caption("Reference"),
            ["ValueCaptionCase`1"] = new(StringComparer.Ordinal) { ["ReferenceValue"] = "Match", ["Color"] = "Tint", ["Sprite"] = "Icon" },
            ["ReferenceCaptionCase`1"] = new(StringComparer.Ordinal) { ["ReferenceTarget"] = "Match", ["Color"] = "Tint", ["Sprite"] = "Icon" },

            ["AvatarScale"] = new(StringComparer.Ordinal) { ["DefaultScale"] = "Scale", ["SetOnEquip"] = "ApplyOnEquip" },
            ["BlinkLocomotion"] = new(StringComparer.Ordinal) { ["RangeExp"] = "RangeExponent", ["Drag"] = "ArcDrag" },
            ["Seat"] = new(StringComparer.Ordinal) { ["TransformRestoreMode"] = "RestoreMode" },
            ["LocomotionAnimationSuppressor"] = new(StringComparer.Ordinal) { ["SuppressFeetSimulation"] = "SuppressFeet" },
            ["SeatReleaseOnMove"] = new(StringComparer.Ordinal)
            {
                ["ReleaseOnBinaryAction"] = "ReleaseOnJump",
                ["ReleaseStrengthThreshold"] = "MoveStrengthThreshold",
            },

            // A menu entry and the pin that puts one on its owner's ring.
            ["ContextMenuEntry"] = new(StringComparer.Ordinal)
            {
                ["Color"] = "Tint",
                ["Sprite"] = "Icon",
                ["ButtonEnabled"] = "Interactable",
                ["CloseMenuOnPress"] = "CloseOnPress",
            },
            ["ContextMenuPin"] = new(StringComparer.Ordinal)
            {
                ["Item"] = "Entry",
                ["OnlyForSide"] = "Side",
                ["ExcludeOnTools"] = "NotWithTool",
                ["ExcludePrimaryHand"] = "NotOnPrimaryHand",
                ["ExcludeSecondaryHand"] = "NotOnSecondaryHand",
            },

            // A package button told to wait until the press is known not to be a drag is one that acts on
            // release here. Both act on press otherwise.
            ["Button"] = new(StringComparer.Ordinal) { ["RequireLockInToPress"] = "ActOnRelease" },

            // PARTICLE INITIALIZERS. A package's initializers name every parameter the same way whatever
            // it holds - Value, or MinValue and MaxValue - while ours name it after the quantity: MinColor,
            // MinSpeed, MinLifetime, MinSize. Not one member lined up, so not one initializer parameter
            // ever imported: every system inherited our defaults, which is white particles at whatever
            // size the component happens to start at. It reads as "the conversion does not do colour",
            // and it was really a table of names nobody had written down. -xlinka
            ["ParticleColorInitializer"] = Range("Color"),
            ["ParticleColorRangeInitializer"] = Range("MinColor", "MaxColor"),
            ["ParticleLifetimeInitializer"] = Range("Lifetime"),
            ["ParticleLifetimeRangeInitializer"] = Range("MinLifetime", "MaxLifetime"),
            ["ParticleSpeedInitializer"] = Range("Speed"),
            ["ParticleSpeedRangeInitializer"] = Range("MinSpeed", "MaxSpeed"),
            ["ParticleVelocityInitializer"] = Range("Velocity"),
            ["ParticleVelocityRangeInitializer"] = Range("MinVelocity", "MaxVelocity"),
            ["ParticleSizeInitializer"] = Range("Size"),
            ["ParticleSizeRangeInitializer"] = Range("MinSize", "MaxSize"),
            ["ParticleUniformSizeInitializer"] = Range("Size"),
            ["ParticleUniformSizeRangeInitializer"] = Range("MinSize", "MaxSize"),
            ["ParticleRotationInitializer"] = Range("Roll"),
            ["ParticleRotationRangeInitializer"] = Range("MinRoll", "MaxRoll"),
            ["ParticleRotation3DInitializer"] = Range("EulerAngles"),
            ["ParticleRotation3DRangeInitializer"] = Range("MinEulerAngles", "MaxEulerAngles"),
            ["ParticleAngularVelocityInitializer"] = Range("AngularVelocity"),
            ["ParticleAngularVelocityRangeInitializer"] = Range("MinAngularVelocity", "MaxAngularVelocity"),
            ["ParticleAngularVelocity3DInitializer"] = Range("AngularVelocity"),
            ["ParticleAngularVelocity3DRangeInitializer"] = Range("MinAngularVelocity", "MaxAngularVelocity"),
            ["ParticleTrailColorInitializer"] = Range("Color"),
            ["ParticleTrailColorRangeInitializer"] = Range("MinColor", "MaxColor"),
            ["ParticleTrailWidthInitializer"] = Range("Width"),
            ["ParticleTrailWidthRangeInitializer"] = Range("MinWidth", "MaxWidth"),
            ["ParticleTrailLifetimeInitializer"] = Range("Lifetime"),
            ["ParticleTrailLifetimeRangeInitializer"] = Range("MinLifetime", "MaxLifetime"),

            // The package's Color/MainTexture would collide with the ICommonMaterial properties of the
            // same names, so the fields carry our usual Albedo* naming and bind through here.
            ["XiexeToonMaterial"] = new(StringComparer.Ordinal)
            {
                ["Color"] = "AlbedoColor",
                ["MainTexture"] = "AlbedoTexture",
                ["BlendMode"] = "AlphaMode",
            },

            // Every other material in the engine calls this member BlendMode and spells the same six
            // values the package does, so it maps with no help. The two toon materials are the odd ones
            // out: they call it AlphaMode over a three-value enum. Nothing matched, so a fur or hair
            // shell authored as Cutout landed on our Opaque default and every alpha card rendered as a
            // solid quad - dark blotches over the body and a hatched block where the tail should be.
            // The fix belongs here rather than in a rename: AlphaMode is a live sync member and saved
            // content is keyed by member name. -xlinka
            ["ToonMaterial"] = new(StringComparer.Ordinal)
            {
                ["BlendMode"] = "AlphaMode",
            },

            // THREE OF A LIGHT'S FIVE INTERESTING MEMBERS NEVER MAPPED. Ours spell the type, the colour
            // and the shadow mode differently from the package, so every imported light came in as our
            // default: a WHITE POINT light with hard shadows, whatever the author actually built. A dim
            // coloured spot tucked behind a prop arrives as a bare bulb lighting the whole room, which
            // is why imports read as blown out rather than as slightly wrong.
            //
            // Both enums already agree value for value - Point/Directional/Spot and None/Hard/Soft - so
            // the names were the entire problem. Per-type rather than global because "Color" is already
            // spoken for: on the material families it means AlbedoColor. -xlinka
            ["Light"] = new(StringComparer.Ordinal)
            {
                ["LightType"] = "Type",
                ["Color"] = "LightColor",
                ["ShadowType"] = "Shadows",
            },
        };

    // Renames that hold whatever component they appear on.
    private static readonly Dictionary<string, string> GlobalMemberAliases = new(StringComparer.Ordinal)
    {
        // The package names the side to DRAW; ours names the side to CULL. See CullingValues - the values
        // invert, so this rename alone is not enough.
        ["Sidedness"] = "Culling",
        ["UseVertexColors"] = "UseVertexColor",

        // The particle family, outside the initializers. Both names are unambiguous - no other member in a
        // package or here carries them - so they sit here rather than in a per-type table.
        //
        // MaxParticleCount is the whole budget: unmapped, every imported system silently kept OUR default
        // instead of the author's, and EmitFromShell is the difference between emitting from a volume and
        // emitting from its surface, which is a completely different effect. -xlinka
        ["MaxParticleCount"] = "MaxParticles",
        ["EmitFromShell"] = "FromShell",
    };

    // Enum values that mean something different from what they spell, keyed by the PACKAGE's member name.
    //
    // Keying by OUR name is wrong and was a real bug: the package format has TWO different face-culling
    // members with OPPOSITE conventions, and both land on our single `Culling`.
    //
    //   Sidedness { Auto, Front, Back, Double }  - names the face to DRAW  -> inverts
    //   Culling   { Off, Front, Back }           - names the face to CULL  -> matches ours
    //
    // Inverting both (which keying by our name does) fixes the Sidedness materials and breaks every
    // Culling one - and the two are split across different material types, so half the avatar comes out
    // inside-out either way. Which member the value CAME FROM is the only thing that disambiguates. -xlinka
    private static readonly Dictionary<string, Dictionary<string, string>> EnumValueAliases =
        new(StringComparer.Ordinal)
        {
            // An anchor's restore modes under the names a seat gives them. Its body points carry over by name.
            ["TransformRestoreMode"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["RelativeTransform"] = "SeatRelative",
                ["GlobalTransform"] = "WorldTransform",
            },

            // Names the face to DRAW: invert into our cull-this-face convention.
            ["Sidedness"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Auto"] = "Back",      // the package default: draw front, so cull back
                ["Front"] = "Back",     // draw front == cull back
                ["Back"] = "Front",     // draw back  == cull front
                ["Double"] = "None",    // draw both  == cull nothing
                ["DualSided"] = "None",
                ["Both"] = "None",
            },
            // Already names the face to CULL: Front and Back carry straight across, only "Off" differs.
            // The package splits sRGB into with and without alpha; vertex colour blending only cares which space.
            ["VertexColorInterpolationSpace"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["sRGBAlpha"] = "sRGB",
            },
            ["Culling"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Off"] = "None",
            },

            // The package's BlendMode has six values; our AlphaMode has three. Only the two toon materials
            // narrow to it, and for everything else BlendMode maps onto our identical six-value
            // BlendMode and needs none of this - which is exactly why the translation below is tried
            // and not forced. See the parse-or-fall-back in TryConvert. -xlinka
            ["BlendMode"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Alpha"] = "Blend",
                ["Transparent"] = "Blend",
                ["Additive"] = "Blend",
                ["Multiply"] = "Blend",
            },

            // A collider's type. A package carries static, auto and haptic flavours of a trigger that ours
            // folds into the one Trigger. Unmapped they failed to parse and kept our default of Static, so
            // every zone in an imported world came in as a solid invisible wall that blocked people and
            // lasers. A haptic sampler goes the same way: a sensor, never a wall. -xlinka
            ["Type"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["StaticTrigger"] = "Trigger",
                ["StaticTriggerAuto"] = "Trigger",
                ["HapticTrigger"] = "Trigger",
                ["HapticStaticTrigger"] = "Trigger",
                ["HapticStaticTriggerAuto"] = "Trigger",
                ["HapticSampler"] = "Trigger",
            },
        };

    private static Dictionary<string, string> Caption(string source) => new(StringComparer.Ordinal)
    {
        [source] = "Source",
        ["ForceDeselected"] = "ShowOtherwise",
        ["Color"] = "Tint",
        ["Sprite"] = "Icon",
        ["DefaultOption"] = "Otherwise",
        ["Options"] = "Cases",
    };

    // The package's Value / MinValue / MaxValue onto whatever this initializer calls the same thing.
    private static Dictionary<string, string> Range(string value) =>
        new(StringComparer.Ordinal) { ["Value"] = value };

    private static Dictionary<string, string> Range(string min, string max) =>
        new(StringComparer.Ordinal) { ["MinValue"] = min, ["MaxValue"] = max };

    internal static string ResolveMemberName(Component component, string theirName) => ResolveMemberName(component.GetType(), theirName);

    private static string ResolveMemberName(Type? type, string theirName)
    {
        while (type != null)
        {
            if (MemberAliases.TryGetValue(type.Name, out var table)
                && table.TryGetValue(theirName, out var ours))
                return ours;
            type = type.BaseType;
        }
        return GlobalMemberAliases.TryGetValue(theirName, out var global) ? global : theirName;
    }

    // What a value looks like in the stored graph, per type:
    //   numbers  a double / int
    //   bool     a bool
    //   string   a string, also used for enum names AND for references (the target's id)
    //   vectors  an ARRAY of numbers - NOT a string. Positions, rotations, scales and colours all
    //            arrive this way, and reading them as text is why a first pass applied no transforms
    //            at all and stacked the whole object at the origin.
    //   colours  an array of numbers with a trailing colour-profile string, which is ignored here.
    // Returns how many of the STORED members found a home on ours. The caller uses it to notice a
    // component that mapped nothing, which is what a wrong alias looks like from the outside.
    public static int Apply(
        Component component,
        DataTreeDictionary data,
        Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList,
        Func<Uri, Uri?> remapUrl)
    {
        if (component is DynamicBoneChain chain)
        {
            ApplyDynamicChain(chain, data, queueReferenceList);
            RestBonesAsStored(data, queueReference, remapUrl);
        }
        if (component is LocalViewOverride view)
            ApplyViewOverride(view, data, remapUrl);
        if (component is ParticleForce force)
            ApplyGravityForce(force, data);
        if (component is PBS_Triplanar triplanar)
            ApplyTriplanar(triplanar, data);
        if (component is ReflectionProbe probe)
            ApplyReflectionProbe(probe, data);
        if (component is Components.Audio.AudioClipPlayer player)
            ApplyClipPlayback(player, data);
        if (component is Components.Utility.CopyGlobalTransform copier)
            ApplyCopyChannels(copier, data);
        // The package's reference proxy is part of the object it sits on; ours by default is a card the
        // inspector hands out, which deletes its slot once dropped or left alone.
        if (component is ReferenceProxy referenceProxy)
            referenceProxy.Temporary.Value = false;
        if (component is Components.Utility.PlaceAtUser place)
            ApplyPlaceAtUser(place, data);
        if (component is Components.Utility.AnyAllBools bools)
            ApplyNoneOf(bools, data);
        if (component is Components.Avatar.Seat seat)
            ApplyAnchor(seat, data);
        if (component is Components.Avatar.SeatReleaseOnMove release)
            ApplyAnchorRelease(release, data);
        if (component is ImageProvider picture)
            ApplyPicture(picture, data);
        ApplyK1(component, data, queueReference, queueReferenceList, remapUrl);
        ApplyK2(component, data, queueReference, queueReferenceList, remapUrl);
        ApplyK3(component, data, queueReference, queueReferenceList, remapUrl);
        ApplyK4(component, data, queueReference, queueReferenceList, remapUrl);
        ApplyK5(component, data, queueReference, queueReferenceList, remapUrl);
        ApplyK6(component, data, queueReference, queueReferenceList, remapUrl);
        ApplyK7(component, data, queueReference, queueReferenceList, remapUrl);
        ApplyK8(component, data, queueReference, queueReferenceList, remapUrl);
        ApplyRendering(component, data, queueReference, queueReferenceList, remapUrl);
        ApplyUi(component, data, queueReference, queueReferenceList, remapUrl);
        ApplyMats(component, data);
        // Index our members by name once, then walk the STORED members - that way an alias can point
        // a differently-named member at the right one, which a walk over our members alone cannot do.
        var ours = new Dictionary<string, ISyncMember>(StringComparer.Ordinal);
        for (int i = 0; i < component.SyncMemberCount; i++)
        {
            try
            {
                var memberName = component.GetSyncMemberName(i);
                var syncMember = component.GetSyncMember(i);
                if (!string.IsNullOrEmpty(memberName) && syncMember != null)
                    ours[memberName] = syncMember;
            }
            catch
            {
                // A member that will not introspect is simply not available to map onto.
            }
        }

        int matched = 0;
        foreach (var pair in data.Children)
        {
            string name = pair.Key;
            if (component is DynamicBoneChain && name == "Bones")
                continue;
            if (pair.Value is not DataTreeDictionary holder)
                continue;
            var node = holder.TryGetNode("Data");
            string ourName = ResolveMemberName(component, name);
            if (!ours.TryGetValue(ourName, out var member))
                continue;
            if (node == null)
            {
                // A record held in place keeps its own members beside its id, with no Data of its own.
                if (RecordMembers(member.GetType()).Count == 0)
                    continue;
                matched++;
                ApplyRecord(member, holder, queueReference, queueReferenceList, remapUrl);
                continue;
            }

            matched++;
            // The package named this member, so what it holds is an answer, an empty link included, as it is for a
            // member a save names. A component's own default target must not take a link over in the frames before
            // the queued references land: a jitter aimed at its slot carried a flag's strips metres off. -xlinka
            if (member is SyncElement stored)
                stored.ValueCameFromData = true;
            try
            {
                Assign(member, node, ourName, name, queueReference, queueReferenceList, remapUrl);
            }
            catch
            {
                // A member that will not take the stored value is left at its default. Never fatal:
                // one odd field must not cost the whole component.
            }
        }

        // AvatarRoot.Scale is the authored wear scale, separate from the slot's saved world size
        // and DefaultUserScale. In particular, One must not be mistaken for an unset calibration.
        if (component is AvatarForm form
            && (data.TryGetNode("Scale") as DataTreeDictionary)?.TryGetNode("Data") is { } equipScale
            && TryConvert(equipScale, typeof(float3), out var convertedScale, remapUrl, "Scale")
            && convertedScale is float3 scale
            && float.IsFinite(scale.x) && float.IsFinite(scale.y) && float.IsFinite(scale.z)
            && scale.x > 0f && scale.y > 0f && scale.z > 0f)
            form.SetEquipScale(scale);

        // A stereo texture there takes its left eye from the plain transform; ours keeps the left eye apart.
        if (ours.TryGetValue("LeftEyeTextureScale", out var leftScale) && leftScale is Sync<float2> leftScaleField
            && ours.TryGetValue("LeftEyeTextureOffset", out var leftOffset) && leftOffset is Sync<float2> leftOffsetField
            && ours.TryGetValue("TextureScale", out var plainScale) && plainScale is Sync<float2> plainScaleField
            && ours.TryGetValue("TextureOffset", out var plainOffset) && plainOffset is Sync<float2> plainOffsetField
            && (data.TryGetNode("StereoTextureTransform") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: true })
        {
            leftScaleField.Value = plainScaleField.Value;
            leftOffsetField.Value = plainOffsetField.Value;
        }

        // A projection's transform works on its own panorama coordinates, which keep the package's rows, and a
        // triplanar one on positions, which no V mirror touches.
        if (component is MaterialProvider && component is not Projection360Material and not PBS_TriplanarSurface)
            FlipTextureTransforms(ours);
        return matched;
    }

    // The package counts V up from the bottom; meshes come in with V mirrored (PackageMeshReader), so a texture
    // transform on mesh UVs has to mirror with them: v' = 1 - v gives offset' = 1 - scale - offset. Whole-number
    // tiling hides the difference, which is why it went unnoticed; a 0.5 tiling landed half a tile off.
    private static readonly (string Scale, string Offset)[] UvTransforms =
    {
        ("TextureScale", "TextureOffset"),
        ("DetailTextureScale", "DetailTextureOffset"),
        ("MaskScale", "MaskOffset"),
        ("OffsetTextureScale", "OffsetTextureOffset"),
        ("LeftEyeTextureScale", "LeftEyeTextureOffset"),
        ("RightEyeTextureScale", "RightEyeTextureOffset"),
        // Colour masks, splat maps, two-layer blends and the per-map transforms of the multi-UV surface.
        ("ColorMaskScale", "ColorMaskOffset"),
        ("ColorMapScale", "ColorMapOffset"),
        ("LerpTextureScale", "LerpTextureOffset"),
        ("Texture0Scale", "Texture0Offset"),
        ("Texture1Scale", "Texture1Offset"),
        ("AlbedoScale", "AlbedoOffset"),
        ("SecondaryAlbedoScale", "SecondaryAlbedoOffset"),
        ("EmissionMapScale", "EmissionMapOffset"),
        ("SecondaryEmissionMapScale", "SecondaryEmissionMapOffset"),
        ("NormalMapScale", "NormalMapOffset"),
        ("OcclusionMapScale", "OcclusionMapOffset"),
        ("MetallicMapScale", "MetallicMapOffset"),
        ("SpecularMapScale", "SpecularMapOffset"),
        // A pixelation's resolution mask and a depth projection's colour and depth pictures.
        ("ResolutionTextureScale", "ResolutionTextureOffset"),
        ("ColorTextureScale", "ColorTextureOffset"),
        ("DepthTextureScale", "DepthTextureOffset"),
        // Distance-lerp textures, refraction and blur normals, the blur's spread mask, and the toon material's maps.
        ("NearTextureScale", "NearTextureOffset"),
        ("FarTextureScale", "FarTextureOffset"),
        ("NormalTextureScale", "NormalTextureOffset"),
        ("SpreadTextureScale", "SpreadTextureOffset"),
        ("MainTextureScale", "MainTextureOffset"),
        ("MetallicGlossMapScale", "MetallicGlossMapOffset"),
        ("ThicknessMapScale", "ThicknessMapOffset"),
        ("ShadowRampMaskScale", "ShadowRampMaskOffset"),
        // The overlay materials' front and behind pictures.
        ("FrontTextureScale", "FrontTextureOffset"),
        ("BehindTextureScale", "BehindTextureOffset"),
        ("FrontNearTextureScale", "FrontNearTextureOffset"),
        ("FrontFarTextureScale", "FrontFarTextureOffset"),
        ("BehindNearTextureScale", "BehindNearTextureOffset"),
        ("BehindFarTextureScale", "BehindFarTextureOffset"),
    };

    // A polar mapping works its coordinates from the mesh UV into a space of its own, kept upright, so the
    // transforms that apply after it are left as stored when the material has it on.
    private static readonly Dictionary<string, string> PolarFlags = new(StringComparer.Ordinal)
    {
        ["FrontTextureScale"] = "PolarUVmapping",
        ["BehindTextureScale"] = "PolarUVmapping",
        ["FrontNearTextureScale"] = "PolarUVmapping",
        ["FrontFarTextureScale"] = "PolarUVmapping",
        ["BehindNearTextureScale"] = "PolarUVmapping",
        ["BehindFarTextureScale"] = "PolarUVmapping",
        ["NearTextureScale"] = "PolarUVmapping",
        ["FarTextureScale"] = "PolarUVmapping",
        ["LerpTextureScale"] = "LerpTexturePolarUV",
    };

    private static void FlipTextureTransforms(Dictionary<string, ISyncMember> ours)
    {
        foreach (var (scaleName, offsetName) in UvTransforms)
        {
            if (!ours.TryGetValue(scaleName, out var scaleMember) || scaleMember is not Sync<float2> scale
                || !ours.TryGetValue(offsetName, out var offsetMember) || offsetMember is not Sync<float2> offset)
                continue;
            if (PolarFlags.TryGetValue(scaleName, out var polarName) && ours.TryGetValue(polarName, out var polarMember)
                && polarMember is Sync<bool> { Value: true })
                continue;
            offset.Value = new float2(offset.Value.x, 1f - scale.Value.y - offset.Value.y);
        }
    }

    // The texture transform pair one of a material's members belongs to, by our member names, where the import
    // turned the pair over as above: a script's write or drive into either has to be turned over the same way.
    internal static bool FlippedUvPair(Component component, string member, out string scale, out string offset)
    {
        scale = offset = string.Empty;
        if (component is not MaterialProvider || component is Projection360Material or PBS_TriplanarSurface)
            return false;
        foreach (var (scaleName, offsetName) in UvTransforms)
        {
            if (member != scaleName && member != offsetName)
                continue;
            if (PolarFlags.TryGetValue(scaleName, out var polarName)
                && component.GetType().GetField(polarName)?.GetValue(component) is Sync<bool> { Value: true })
                return false;
            scale = scaleName;
            offset = offsetName;
            return true;
        }
        return false;
    }

    // A record's own members by name: a case of a caption, an input of a combiner. Each one goes through the
    // same assignment as a component's member.
    private static void ApplyRecord(object record, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList, Func<Uri, Uri?> remapUrl)
    {
        var members = RecordMembers(record.GetType());
        foreach (var (theirName, value) in data.Children)
        {
            if (value is not DataTreeDictionary holder || holder.TryGetNode("Data") is not { } node)
                continue;
            string ourName = ResolveMemberName(record.GetType(), theirName);
            if (!members.TryGetValue(ourName, out var field) || field.GetValue(record) is not ISyncMember member)
                continue;
            try
            {
                Assign(member, node, ourName, theirName, queueReference, queueReferenceList, remapUrl);
            }
            catch
            {
                // As for a component's member: left at its default.
            }
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Dictionary<string, System.Reflection.FieldInfo>> _recordMembers = new();

    // The synced members a record type declares, by name. A component's are indexed through the worker; a
    // record's are read off its fields.
    private static Dictionary<string, System.Reflection.FieldInfo> RecordMembers(Type type) =>
        _recordMembers.GetOrAdd(type, static t =>
        {
            var found = new Dictionary<string, System.Reflection.FieldInfo>(StringComparer.Ordinal);
            if (!typeof(SyncElement).IsAssignableFrom(t) || typeof(ISyncRef).IsAssignableFrom(t) || ValueProperty(t) != null)
                return found;
            foreach (var field in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (field.IsInitOnly && typeof(ISyncMember).IsAssignableFrom(field.FieldType))
                    found[field.Name] = field;
            }
            return found;
        });

    // A package anchor never lets go on its own; letting go on input is a component of its own, which comes
    // across as SeatReleaseOnMove. Only an anchor stores a restore mode under that name. It turns the user to
    // face its references' +Z, where ours face -Z: a half turn about their up.
    private static void ApplyAnchor(Components.Avatar.Seat seat, DataTreeDictionary data)
    {
        if (data.TryGetNode("TransformRestoreMode") == null)
            return;
        seat.ReleaseOnJump.Value = false;
        seat.RotationOffset.Value = floatQ.AxisAngle(float3.Up, MathF.PI);
    }

    // No strength stored means no letting go on a push of the stick, which ours writes as zero.
    private static void ApplyAnchorRelease(Components.Avatar.SeatReleaseOnMove release, DataTreeDictionary data)
    {
        if ((data.TryGetNode("ReleaseStrengthThreshold") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: null })
            release.MoveStrengthThreshold.Value = 0f;
    }

    // A package combines with All, Any or None; ours has no None, which is Any turned over.
    private static void ApplyNoneOf(Components.Utility.AnyAllBools bools, DataTreeDictionary data)
    {
        if ((data.TryGetNode("Mode") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: string mode }
            && string.Equals(mode, "None", StringComparison.OrdinalIgnoreCase))
        {
            bools.Mode.Value = Components.Utility.BoolCombineMode.Any;
            bools.Invert.Value = true;
        }
    }

    // A package's GravityForce stores one number and means "down"; ours stores the vector. The alias that
    // points GravityForce at ParticleForce is only truthful because of this - without it the component
    // attaches with a zero force and the effect falls at no speed at all.
    //
    // Only fires when the stored data actually has a Gravity member, so a real ParticleForce carrying
    // its own Force vector is untouched.
    // The package's triplanar projection takes one 2D scale and offset for all three planes, a blend power, and
    // an object-space switch. Ours: a scale and offset per axis, the same power as a sharpness, and a
    // world-space switch, which is the opposite way round. Both compute position * scale and |n|^power, so
    // the values carry over exactly for the usual even scale.
    private static void ApplyTriplanar(PBS_Triplanar material, DataTreeDictionary data)
    {
        List<float>? Numbers(string name)
        {
            if ((data.TryGetNode(name) as DataTreeDictionary)?.TryGetNode("Data") is not DataTreeList list)
                return null;
            var values = new List<float>(3);
            foreach (var node in list.Children)
            {
                if (node is DataTreeValue { Value: IConvertible number } && number is not string)
                    values.Add(Convert.ToSingle(number, System.Globalization.CultureInfo.InvariantCulture));
            }
            return values;
        }
        if (Numbers("TextureScale") is { Count: >= 2 } scale)
            material.Tiling.Value = new float3(scale[0], scale[1], scale[0]);
        if (Numbers("TextureOffset") is { Count: >= 2 } offset)
            material.Offset.Value = new float3(offset[0], offset[1], 0f);
        if ((data.TryGetNode("TriplanarBlendPower") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: IConvertible power }
            && power is not string)
            material.BlendSharpness.Value = Convert.ToSingle(power, System.Globalization.CultureInfo.InvariantCulture);
        if ((data.TryGetNode("ObjectSpace") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: bool objectSpace })
            material.WorldSpace.Value = !objectSpace;
    }

    // A baked probe shows the cubemap stored with it, rendered once in the package's own lighting, and ours
    // does the same (FromCubemap). A baked one whose cubemap is missing is captured here once instead, in
    // this world's lighting; a realtime one keeps capturing. Shadows go into a capture when the package gave
    // it a shadow distance.
    private static void ApplyReflectionProbe(ReflectionProbe probe, DataTreeDictionary data)
    {
        if ((data.TryGetNode("ProbeType") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: string type })
            probe.UpdateMode.Value = type == "Realtime" ? ProbeUpdateMode.Always
                : type == "Baked" && (data.TryGetNode("BakedCubemap") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: string cubemap }
                    && cubemap.Length > 0 && !IsNullId(cubemap) ? ProbeUpdateMode.FromCubemap
                : ProbeUpdateMode.Once;
        // The package's probes only reflect; the room's ambient comes from the world's own lighting.
        probe.AmbientMode.Value = ProbeAmbientMode.Disabled;
        if ((data.TryGetNode("ShadowDistance") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: IConvertible shadow }
            && shadow is not string)
            probe.CaptureShadows.Value = Convert.ToSingle(shadow, CultureInfo.InvariantCulture) > 0f;
    }

    // Whose node it follows and which: the local user unless a user is named (a named user from a saved
    // session is gone, so that falls back to the local one), the head unless the root is asked for, and the
    // node's turn as well when a rotation source is set.
    private static void ApplyPlaceAtUser(Components.Utility.PlaceAtUser place, DataTreeDictionary data)
    {
        object? Field(string name)
        {
            var node = data.TryGetNode(name);
            if (node is DataTreeDictionary wrapped)
                node = wrapped.TryGetNode("Data");
            return (node as DataTreeValue)?.Value;
        }
        place.Mode.Value = Components.Utility.FacingUserMode.LocalUser;
        place.UseHead.Value = Field("PositionSource") is not string source || source is "Head" or "View";
        place.FollowRotation.Value = Field("RotationSource") is string rotation && rotation is not ("" or "None");
    }

    // The package keeps play, loop, speed and where the head was in one playback member; ours keeps loop and
    // speed beside it. Without this a looping ambience saved playing comes in stopped.
    private static void ApplyClipPlayback(Components.Audio.AudioClipPlayer player, DataTreeDictionary data)
    {
        if (data.TryGetNode("playback") is not DataTreeDictionary member)
            return;
        var state = member.TryGetNode("Data") as DataTreeDictionary ?? member;
        object? Field(string name)
        {
            var node = state.TryGetNode(name);
            if (node is DataTreeDictionary wrapped)
                node = wrapped.TryGetNode("Data");
            return (node as DataTreeValue)?.Value;
        }
        bool play = Field("Play") is bool p && p;
        bool loop = Field("Loop") is bool l && l;
        float position = Field("RawPosition") is IConvertible rp and not string and not bool ? Convert.ToSingle(rp, CultureInfo.InvariantCulture) : 0f;
        float speed = Field("Speed") is IConvertible sp and not string and not bool ? Convert.ToSingle(sp, CultureInfo.InvariantCulture) : 1f;
        if (!float.IsFinite(speed))
            speed = 1f;
        player.Loop.Value = loop;
        player.Speed.Value = speed;
        player.Playback.SetLoadedState(play, loop ? PlaybackLoopMode.Loop : PlaybackLoopMode.Once, position, speed);
    }

    // A package copier holds a channel when that channel's drive has a target; ours has a switch per channel,
    // position and rotation on by default. Rotation-only copiers are how an animal's legs bend with a humanoid
    // chain, and with position left on every leg bone was dragged onto the bone it only meant to turn with:
    // a dog's hind legs stood inside its front ones. -xlinka
    private static void ApplyCopyChannels(Components.Utility.CopyGlobalTransform copier, DataTreeDictionary data)
    {
        bool? Holds(string name)
        {
            if (data.TryGetNode(name) is not DataTreeDictionary member)
                return null;
            var id = (member.TryGetNode("Data") as DataTreeValue)?.Value?.ToString();
            return !string.IsNullOrEmpty(id) && !IsNullId(id);
        }
        if (Holds("_posDrive") is bool position)
            copier.CopyPosition.Value = position;
        if (Holds("_rotDrive") is bool rotation)
            copier.CopyRotation.Value = rotation;
        if (Holds("_scaleDrive") is bool scale)
            copier.CopyScale.Value = scale;
    }

    private static void ApplyGravityForce(ParticleForce force, DataTreeDictionary data)
    {
        if (data.TryGetNode("Gravity") is not DataTreeDictionary holder)
            return;
        if (holder.TryGetNode("Data") is not DataTreeValue value || value.Value == null)
            return;

        try
        {
            float gravity = Convert.ToSingle(value.Value);
            force.Force.Value = new float3(0f, -gravity, 0f);
        }
        catch
        {
            // Not a number. Leave the force where it is.
        }
    }

    private static void ApplyDynamicChain(DynamicBoneChain chain, DataTreeDictionary data,
        Action<object, string, IReadOnlyList<string>> queueReferenceList)
    {
        DataTreeNode? Read(string name) => (data.TryGetNode(name) as DataTreeDictionary)?.TryGetNode("Data");
        if (Read("Bones") is DataTreeList bones)
        {
            var ids = new List<string>();
            foreach (var entry in bones.Children)
            {
                var record = entry as DataTreeDictionary;
                if (record?.TryGetNode("Data") is DataTreeDictionary nested)
                    record = nested;
                var boneSlot = record?.TryGetNode("BoneSlot") as DataTreeDictionary;
                var value = boneSlot?.TryGetNode("Data") ?? record?.TryGetNode("Data") ?? entry;
                if (value is DataTreeValue { Value: string id } && !IsNullId(id) && id.Length > 0)
                    ids.Add(id);
            }
            if (ids.Count > 0)
                queueReferenceList(chain.Bones, nameof(chain.Bones), ids);
        }
        if (Read("DynamicPlayerCollision") is DataTreeValue { Value: bool players })
            chain.CollideWithPlayers.Value = players;
        if (Read("IsGrabbable") is DataTreeValue { Value: bool grab })
            chain.AllowGrab.Value = grab;
        if (Read("IgnoreGrabOnFirstBone") is DataTreeValue { Value: bool ignoreFirst })
            chain.GrabFirstBone.Value = !ignoreFirst;
    }

    private static void ApplyViewOverride(LocalViewOverride view, DataTreeDictionary data, Func<Uri, Uri?> remapUrl)
    {
        // Nullable source transforms become a value and an explicit enable flag. Zero scale is a value,
        // not an absent override: it hides the wearer's head while keeping its shadow. -xlinka
        Apply("PositionOverride", typeof(float3), value => { view.PositionOverride.Value = (float3)value; view.HasPositionOverride.Value = true; });
        Apply("RotationOverride", typeof(floatQ), value => { view.RotationOverride.Value = (floatQ)value; view.HasRotationOverride.Value = true; });
        Apply("ScaleOverride", typeof(float3), value => { view.ScaleOverride.Value = (float3)value; view.HasScaleOverride.Value = true; });
        void Apply(string name, Type type, Action<object> set)
        {
            var node = (data.TryGetNode(name) as DataTreeDictionary)?.TryGetNode("Data");
            if (node != null && TryConvert(node, type, out var value, remapUrl, name) && value != null)
                set(value);
        }
    }

    private static void Assign(
        ISyncMember member,
        DataTreeNode node,
        string name,
        string theirName,
        Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList,
        Func<Uri, Uri?> remapUrl)
    {
        // References carry the target's id and can only be joined once every component exists.
        if (member is ISyncRef reference)
        {
            if (node is DataTreeValue { Value: string id } && id.Length > 0 && !IsNullId(id))
            {
                queueReference(reference, id);
                return;
            }

            // A LIST arriving at a single reference is the Materials -> Material case: the package holds a
            // list where ours holds one. Take the first entry rather than dropping the material and
            // rendering the mesh untextured.
            if (node is DataTreeList single)
            {
                foreach (var first in single.Children)
                {
                    string? firstId = first switch
                    {
                        DataTreeDictionary d => (d.TryGetNode("Data") as DataTreeValue)?.Value?.ToString(),
                        DataTreeValue v => v.Value?.ToString(),
                        _ => null,
                    };
                    if (!string.IsNullOrEmpty(firstId) && !IsNullId(firstId))
                        queueReference(reference, firstId);
                    break;
                }
            }
            return;
        }

        // A KEYED bag of members, written as a dictionary of name -> member-shaped holder rather than a
        // list. The one that matters is the humanoid bone map: a package rig stores it as bone name -> slot
        // id, ours is a SyncObjectDictionary<BodyNode, SyncRef<Slot>>, and every key a package writes is a
        // BodyNode we already have under the same spelling, eyes included. Without this the rig arrives
        // with an empty map, which is the difference between an avatar you equip and one you re-pick 47
        // bones for by hand. -xlinka
        if (node is DataTreeDictionary entries && ObjectDictionary(member.GetType()) is { } bag)
        {
            foreach (var entry in entries.Children)
            {
                if (!Enum.TryParse(bag.KeyType, entry.Key, ignoreCase: true, out var key) || key == null)
                    continue;

                var data = entry.Value is DataTreeDictionary holder ? holder.TryGetNode("Data") : entry.Value;
                if (data == null)
                    continue;

                object? slotForKey;
                try
                {
                    slotForKey = bag.GetOrAdd.Invoke(member, new[] { key });
                }
                catch
                {
                    continue;
                }

                if (slotForKey is ISyncRef keyed)
                {
                    if (data is DataTreeValue { Value: string id } && id.Length > 0 && !IsNullId(id))
                        queueReference(keyed, id);
                    continue;
                }

                // Not a reference bag - a keyed VALUE then, so it goes through the same conversion as
                // any other member.
                var property = slotForKey == null ? null : ValueProperty(slotForKey.GetType());
                if (property != null && TryConvert(data, property.PropertyType, out var keyedValue, remapUrl, theirName))
                    property.SetValue(slotForKey, keyedValue);
            }
            return;
        }

        // A list of references (materials on a renderer, for instance) is a list of member-shaped
        // holders, each carrying one id. A list of text is shaped the same and holds text: a selector's
        // labels came in as references to nothing and the labels were blank.
        if (node is DataTreeList list && LooksLikeReferenceList(list) && member is not SyncFieldList<string>)
        {
            var ids = new List<string>(list.Count);
            foreach (var entry in list.Children)
            {
                string? id = entry switch
                {
                    DataTreeDictionary d => (d.TryGetNode("Data") as DataTreeValue)?.Value?.ToString(),
                    DataTreeValue v => v.Value?.ToString(),
                    _ => null,
                };
                if (!string.IsNullOrEmpty(id) && !IsNullId(id))
                    ids.Add(id);
            }
            if (ids.Count > 0)
                queueReferenceList(member, name, ids);
            return;
        }

        var valueProperty = ValueProperty(member.GetType());
        if (valueProperty == null)
        {
            // A LIST OF VALUES, which until now fell off the end of this method entirely.
            //
            // The branch above catches a list whose entries hold reference ids; anything else landed
            // here, asked a SyncFieldList for a Value property it has never had, got null, and
            // returned having written nothing. Silently. BlendShapeWeights is the one that shows:
            // every imported avatar arrived with its shape keys back at zero, so a body or a garment
            // shaped by a shape key came in the wrong shape and nothing said why. -xlinka
            if (node is DataTreeList values)
                AssignValueList(member, values, remapUrl, theirName, queueReference, queueReferenceList);
            return;
        }

        // A null text held as the value of a value component (a switch's two values, a selector's, a field or a
        // variable of text) is a value of its own: driven into a text, null shows the text's hint
        // (Text.NullContent) where an empty string shows nothing. Left at our empty default, a search box lost
        // its "Search..." prompt. Names and other text members of those components keep the default. -xlinka
        if (node is DataTreeValue { Value: null } && valueProperty.PropertyType == typeof(string) && HoldsTextValue(member))
        {
            valueProperty.SetValue(member, null);
            return;
        }

        if (TryConvert(node, valueProperty.PropertyType, out var value, remapUrl, theirName))
            valueProperty.SetValue(member, value);
    }

    // Whether a member is the value of a component made for a type of value: declared with that type, which
    // here is text.
    private static bool HoldsTextValue(ISyncMember member)
    {
        if ((member as SyncElement)?.Parent is not { } owner || !owner.GetType().IsGenericType)
            return false;
        const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic;
        for (var type = owner.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(Flags | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (!ReferenceEquals(field.GetValue(owner), member))
                    continue;
                return type.IsGenericType
                    && type.GetGenericTypeDefinition().GetField(field.Name, Flags | System.Reflection.BindingFlags.DeclaredOnly)?.FieldType.ContainsGenericParameters == true;
            }
        }
        return false;
    }

    // Rebuild a sync list of plain values from the package's stored one. Elements arrive member-shaped
    // ({ ID, Data }), so each one is unwrapped before it is converted to whatever our element holds.
    private static void AssignValueList(ISyncMember member, DataTreeList list,
        Func<Uri, Uri?> remapUrl, string theirName, Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList)
    {
        var found = ResolveValueListShape(member.GetType());
        if (found == null)
            return;
        var shape = found.Value;

        shape.Clear.Invoke(member, null);

        foreach (var entry in list.Children)
        {
            // Add() hands back the new element; the value goes on the element's own Value property.
            var element = shape.Add.Invoke(member, null);
            if (element == null)
                continue;

            var payload = entry is DataTreeDictionary holder ? holder.TryGetNode("Data") ?? entry : entry;
            var elementValue = ValueProperty(element.GetType());
            if (elementValue == null)
            {
                // A list of records: each entry is one, its members beside its id.
                if (payload is DataTreeDictionary record && RecordMembers(element.GetType()).Count > 0)
                    ApplyRecord(element, record, queueReference, queueReferenceList, remapUrl);
                continue;
            }
            if (TryConvert(payload, elementValue.PropertyType, out var converted, remapUrl, theirName))
                elementValue.SetValue(element, converted);
        }
    }

    private readonly struct ValueListShape
    {
        public readonly System.Reflection.MethodInfo Add;
        public readonly System.Reflection.MethodInfo Clear;
        public ValueListShape(System.Reflection.MethodInfo add, System.Reflection.MethodInfo clear)
        { Add = add; Clear = clear; }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, ValueListShape?> _valueListShapes = new();

    // One reflection lookup per member TYPE, like ValueProperty above, for the same reason.
    private static ValueListShape? ResolveValueListShape(Type memberType) =>
        _valueListShapes.GetOrAdd(memberType, static t =>
        {
            // The parameterless Add() that returns the new element, not SyncFieldList's Add(T).
            //
            // WALKED BY HAND rather than asked for once. SyncFieldList<T> declares its own Add(T),
            // and reflection resolves a name at the first level of the hierarchy that declares it:
            // the derived Add(T) hides the base Add(), so GetMethod("Add", EmptyTypes) comes back
            // null on exactly the type this is for. -xlinka
            System.Reflection.MethodInfo? add = null;
            System.Reflection.MethodInfo? clear = null;
            for (var level = t; level != null && (add == null || clear == null); level = level.BaseType)
            {
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.DeclaredOnly;
                foreach (var method in level.GetMethods(flags))
                {
                    if (method.GetParameters().Length != 0)
                        continue;
                    if (add == null && method.Name == "Add" && method.ReturnType != typeof(void))
                        add = method;
                    else if (clear == null && method.Name == "Clear")
                        clear = method;
                }
            }
            if (add == null || clear == null)
                return null;
            return new ValueListShape(add, clear);
        });

    // One reflection lookup per member TYPE, not per member per component. An import touches thousands
    // of members and GetProperty is not cheap; uncached it was a measurable part of why importing an
    // avatar stalled the frame. -xlinka
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> _valueProperties = new();

    private static System.Reflection.PropertyInfo? ValueProperty(Type memberType) =>
        _valueProperties.GetOrAdd(memberType, static t =>
        {
            if (!t.IsGenericType)
                return null;
            var property = t.GetProperty("Value");
            return property != null && property.CanWrite ? property : null;
        });

    private readonly struct DictionaryShape
    {
        public readonly Type KeyType;
        public readonly System.Reflection.MethodInfo GetOrAdd;

        public DictionaryShape(Type keyType, System.Reflection.MethodInfo getOrAdd)
        {
            KeyType = keyType;
            GetOrAdd = getOrAdd;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, DictionaryShape?> _objectDictionaries = new();

    // Is this member a SyncObjectDictionary with an ENUM key? Anything else is left to the paths below:
    // a string-keyed bag has no fixed vocabulary to translate against, and guessing one is how you get
    // silently mis-assigned members.
    private static DictionaryShape? ObjectDictionary(Type memberType) =>
        _objectDictionaries.GetOrAdd(memberType, static t =>
        {
            for (var walk = t; walk != null; walk = walk.BaseType)
            {
                if (!walk.IsGenericType || walk.GetGenericTypeDefinition() != typeof(SyncObjectDictionary<,>))
                    continue;

                var keyType = walk.GetGenericArguments()[0];
                if (!keyType.IsEnum)
                    return null;

                var getOrAdd = t.GetMethod("GetOrAdd", new[] { keyType });
                return getOrAdd == null ? null : new DictionaryShape(keyType, getOrAdd);
            }
            return null;
        });

    private static bool LooksLikeReferenceList(DataTreeList list)
    {
        foreach (var entry in list.Children)
        {
            if (entry is DataTreeDictionary d && d.TryGetNode("Data") is DataTreeValue { Value: string })
                return true;
            return false;
        }
        return false;
    }

    // An all-zero id is how a cleared reference is written.
    public static bool IsNullId(string id)
    {
        foreach (char c in id)
        {
            if (c != '0' && c != '-')
                return false;
        }
        return true;
    }

    private static bool TryConvert(DataTreeNode node, Type target, out object? value, Func<Uri, Uri?> remapUrl,
        string theirMemberName)
    {
        value = null;

        // A nullable member (the rig's ForwardAxis is a float3?) stores the same shape as its plain
        // counterpart; unwrap so the conversions below see the type they know. Boxing back into the
        // nullable property is free.
        target = Nullable.GetUnderlyingType(target) ?? target;

        if (target.IsEnum)
        {
            if (node is not DataTreeValue { Value: string enumName } || enumName.Length == 0)
                return false;

            // Translate where the two enums disagree about meaning, not just spelling - but only if the
            // translation actually exists on the target. One package member name can land on two
            // different enums of ours (BlendMode narrows to AlphaMode on the toon materials and maps
            // one-to-one everywhere else), and forcing the translation would drop the value on whichever
            // of the two did not need it. Try the translated name, keep the original as the fallback.
            // -xlinka
            if (EnumValueAliases.TryGetValue(theirMemberName, out var valueMap)
                && valueMap.TryGetValue(enumName, out var translated)
                && Enum.TryParse(target, translated, ignoreCase: true, out var aliased))
            {
                value = aliased;
                return true;
            }

            if (Enum.TryParse(target, enumName, ignoreCase: true, out var parsed))
            {
                value = parsed;
                return true;
            }
            return false;
        }

        if (node is DataTreeValue scalar)
        {
            if (scalar.Value == null)
                return false;

            try
            {
                if (target == typeof(string)) { value = PackageTextMarkup.Translate(scalar.Value.ToString() ?? string.Empty); return true; }
                if (target == typeof(bool)) { value = Convert.ToBoolean(scalar.Value, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(float)) { value = Convert.ToSingle(scalar.Value, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(double)) { value = Convert.ToDouble(scalar.Value, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(int)) { value = Convert.ToInt32(scalar.Value, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(long)) { value = Convert.ToInt64(scalar.Value, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(byte)) { value = Convert.ToByte(scalar.Value, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(sbyte)) { value = Convert.ToSByte(scalar.Value, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(short)) { value = Convert.ToInt16(scalar.Value, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(ushort)) { value = Convert.ToUInt16(scalar.Value, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(uint)) { value = Convert.ToUInt32(scalar.Value, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(ulong)) { value = Convert.ToUInt64(scalar.Value, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(decimal)) { value = Convert.ToDecimal(scalar.Value, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(char)) { value = Convert.ToChar(scalar.Value, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(Uri) && scalar.Value is string)
                {
                    // A URL inside the package points at package storage, which means nothing once the
                    // file is closed. Swap it for the local asset we wrote during conversion; if there
                    // is no local copy, leave the member alone rather than storing a dead address.
                    var original = scalar.ExtractUrl();
                    value = original == null ? null : remapUrl(original);
                    return value != null;
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        if (target == typeof(SphericalHarmonicsL2))
            return TryHarmonics(node, out value);

        // A 4x4 matrix is four rows of four. It stays in the package's space: only the package's own graphs
        // keep one, and they work in that space.
        if (target == typeof(float4x4) && node is DataTreeList { Count: 4 } rows)
        {
            var m = new float[4][];
            for (int r = 0; r < 4; r++)
            {
                var row = rows[r] is DataTreeDictionary held ? held.TryGetNode("Data") : rows[r];
                m[r] = row is DataTreeList entries ? ReadNumbers(entries) : Array.Empty<float>();
                if (m[r].Length < 4)
                    return false;
            }
            value = new float4x4(new float4(m[0][0], m[1][0], m[2][0], m[3][0]), new float4(m[0][1], m[1][1], m[2][1], m[3][1]),
                new float4(m[0][2], m[1][2], m[2][2], m[3][2]), new float4(m[0][3], m[1][3], m[2][3], m[3][3]));
            return true;
        }

        // Vectors and colours: an array of numbers, with any trailing non-numeric entry ignored.
        if (node is DataTreeList numbers)
        {
            var parts = ReadNumbers(numbers);
            // Our colours are sRGB values. A package colour names its profile last; a Linear one is
            // converted to sRGB the way the package's own renderer converts it: on the curve below 1, and
            // a channel of 1 or more left as it is, which that renderer then reads as sRGB. So a sky
            // tinted 2.2 shows at 5.7 there, and a 1.69 light colour at 3.3. Taking those up the curve
            // too drew the sky, and the ambient light read off it, 2.57 times too dark (measured on the
            // package's stored ambient against the sky it came from). -xlinka
            if ((target == typeof(color) || target == typeof(colorHDR)) && parts.Length >= 3 && IsLinearProfile(numbers))
            {
                for (int i = 0; i < 3; i++)
                {
                    if (System.Math.Abs(parts[i]) < 1f)
                        parts[i] = parts[i] < 0f ? -ColorSpace.LinearToGamma(-parts[i]) : ColorSpace.LinearToGamma(parts[i]);
                }
            }
            if (!TryBuildVector(parts, target, out value))
                return false;
            // Spatial members cross the handedness boundary here, by NAME, because a package stores a
            // size, a colour and a position in the same shape. Every rotation is a rotation; a float3
            // is only reflected when its name says it is a position or a direction. See
            // PackageHandedness for the lists and the reasoning. -xlinka
            if (value is floatQ rotation)
                value = PackageHandedness.Rotation(rotation);
            else if (value is float3 vector)
                value = PackageHandedness.Member(theirMemberName, vector);
            return true;
        }

        return false;
    }

    private static bool IsLinearProfile(DataTreeList list)
    {
        foreach (var entry in list.Children)
        {
            if (entry is DataTreeValue { Value: string profile })
                return profile == "Linear";
        }
        return false;
    }

    public static float[] ReadNumbers(DataTreeList list)
    {
        var values = new List<float>(list.Count);
        foreach (var entry in list.Children)
        {
            if (entry is not DataTreeValue { Value: not null } v)
                continue;
            try
            {
                // A colour carries its profile name as a trailing string; stop rather than fail.
                if (v.Value is string)
                    break;
                values.Add(Convert.ToSingle(v.Value, CultureInfo.InvariantCulture));
            }
            catch
            {
                break;
            }
        }
        return values.ToArray();
    }

    private static bool TryBuildVector(float[] p, Type target, out object? value)
    {
        value = null;
        if (p.Length == 0)
            return false;

        if (target == typeof(float2) && p.Length >= 2) { value = new float2(p[0], p[1]); return true; }
        if (target == typeof(float3) && p.Length >= 3) { value = new float3(p[0], p[1], p[2]); return true; }
        if (target == typeof(float4) && p.Length >= 4) { value = new float4(p[0], p[1], p[2], p[3]); return true; }
        if (target == typeof(floatQ) && p.Length >= 4) { value = new floatQ(p[0], p[1], p[2], p[3]); return true; }
        if (target == typeof(color) && p.Length >= 3) { value = new color(p[0], p[1], p[2], p.Length > 3 ? p[3] : 1f); return true; }
        if (target == typeof(colorHDR) && p.Length >= 3) { value = new colorHDR(p[0], p[1], p[2], p.Length > 3 ? p[3] : 1f); return true; }
        if (target == typeof(int2) && p.Length >= 2) { value = new int2(Whole(p[0]), Whole(p[1])); return true; }
        if (target == typeof(int3) && p.Length >= 3) { value = new int3(Whole(p[0]), Whole(p[1]), Whole(p[2])); return true; }
        if (target == typeof(int4) && p.Length >= 4) { value = new int4(Whole(p[0]), Whole(p[1]), Whole(p[2]), Whole(p[3])); return true; }
        if (target == typeof(bool2) && p.Length >= 2) { value = new bool2(p[0] != 0f, p[1] != 0f); return true; }
        if (target == typeof(bool3) && p.Length >= 3) { value = new bool3(p[0] != 0f, p[1] != 0f, p[2] != 0f); return true; }
        if (target == typeof(bool4) && p.Length >= 4) { value = new bool4(p[0] != 0f, p[1] != 0f, p[2] != 0f, p[3] != 0f); return true; }

        return false;
    }

    // Slot transforms live on the slot itself rather than on a component, so they get their own reader
    // that understands the same array shape.
    public static float[]? ReadNumberArray(DataTreeDictionary node, string member)
    {
        if (node.TryGetNode(member) is not DataTreeDictionary holder)
            return null;
        if (holder.TryGetNode("Data") is not DataTreeList list)
            return null;
        var parts = ReadNumbers(list);
        return parts.Length > 0 ? parts : null;
    }
}
