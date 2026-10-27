// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Text;

namespace Lumora.Core.Assets.Interop;

// What a component type from an imported package becomes on this side.
public enum PackageTypeFate
{
    // We have a component that does the same job; the instance can be rebuilt.
    Mapped,
    // Not attached as components. A package's visual scripting nodes become scripts instead
    // (PackageNodeGraphs), and the rest here is plumbing whose data is read elsewhere or carries nothing.
    // Not counted as conversion losses; what a graph could not carry is reported with the scripts. -xlinka
    Stripped,
    // A licensed third-party IK package. We never port that; our own solver stands in at the rig level.
    ForeignIK,
    // Materials and shaders. The parameters survive, the shader program does not.
    Shader,
    // No equivalent here yet.
    Unsupported,
}

// Translates the type names carried in a package's type table into Lumora types.
//
// Names arrive as "[AssemblyName]Namespace.TypeName", with generics written inline and nested types
// appended after a '+'. The mapping is deliberately name-driven rather than a giant hand-maintained
// table: most of what a package contains has an obvious counterpart here under the same name, and the
// alias table below only has to carry the cases where we deliberately diverged. -xlinka
public static partial class PackageTypeMap
{
    static PackageTypeMap()
    {
        AliasesK1(Aliases);
        AliasesK2(Aliases);
        AliasesK3(Aliases);
        AliasesK4(Aliases);
        AliasesK5(Aliases);
        AliasesK6(Aliases);
        AliasesK7(Aliases);
        AliasesK8(Aliases);
        AliasesRendering(Aliases);
        AliasesUi(Aliases);
        AliasesMats(Aliases);
        AliasesFace(Aliases);
    }

    static partial void AliasesK1(Dictionary<string, string> aliases);
    static partial void AliasesK2(Dictionary<string, string> aliases);
    static partial void AliasesK3(Dictionary<string, string> aliases);
    static partial void AliasesK4(Dictionary<string, string> aliases);
    static partial void AliasesK5(Dictionary<string, string> aliases);
    static partial void AliasesK6(Dictionary<string, string> aliases);
    static partial void AliasesK7(Dictionary<string, string> aliases);
    static partial void AliasesK8(Dictionary<string, string> aliases);
    static partial void AliasesRendering(Dictionary<string, string> aliases);
    static partial void AliasesUi(Dictionary<string, string> aliases);
    static partial void AliasesFace(Dictionary<string, string> aliases);

    // Package components that are not attached: another part of ours carries what they did, or they do nothing
    // in a world here; and the ones that send to node graphs, joined into those graphs instead (PackageNodeGraphs).
    // Filled by the family partials, each with its reason.
    private static readonly HashSet<string> Carried = new(StringComparer.Ordinal);
    private static readonly HashSet<string> JoinsGraphs = new(StringComparer.Ordinal);

    // One generic variant that becomes a different component of ours than the rest of its family, keyed by the
    // simple name with the package's arguments ("ButtonValueSet<int2>"): a value of a type we keep in parts.
    private static readonly Dictionary<string, Type> ClosedAliases = new(StringComparer.Ordinal);

    // Where we chose a different name for the same job. Left side is the package's simple name.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        // Anything whose name already matches resolves by the assembly scan below and needs no entry
        // here; this table is only for the places we deliberately picked a different name.
        // The plain PBS_Metallic / PBS_Specular names match ours exactly and resolve by scan. Only the
        // variants a package spells differently need an entry, and each target below is a component that
        // actually exists here - an alias pointing at a name we do not have is worse than no alias,
        // because it turns a component we CAN build into a reported loss.
        // NOT the dual-sided materials, deliberately: ours give each side textures of their own, and a package's
        // dual-sided type is one surface lit from both sides. Pointing one at the other attached a component
        // that could not receive a single texture the package sent, so the whole garment came in WHITE. The
        // PBS families with an effect of their own (two-sided, rim, intersect and the rest) map in
        // PackageMaterials. -xlinka
        // The package's user follower; ours of the same name is the nameplate's, which only follows the user
        // whose root it sits under and so stood still on a world slot.
        ["PositionAtUser"] = "PlaceAtUser",
        // PBS_ColorMaskMetallic is NOT here any more: it resolves to our own component of the same
        // shape by the assembly scan, four tint slots and all. -xlinka
        //
        // XiexeToonMaterial resolves to our own component of the same name now - it used to fall
        // back to the plain ToonMaterial, which is a different and simpler shading model.
        // PARTICLES. The package's particle modules and ours cover the same features under different
        // names; these are the ones where only the name differs, checked field by field against the
        // source type rather than matched on a hopeful-looking word. -xlinka
        ["TextureSheetAnimator"] = "ParticleFlipbook",
        ["RibbonFollowerColorRangeInitializer"] = "ParticleTrailColorRangeInitializer",
        ["RibbonFollowerWidthRangeInitializer"] = "ParticleTrailWidthRangeInitializer",
        // A gravity force IS a linear force pointing down. The float a package stores becomes our vector in
        // PackageMemberMapper, which is the only reason this alias is honest.
        ["GravityForce"] = "ParticleForce",
        ["SizeMultiplierByVelocity"] = "ParticleSizeBySpeed",
        ["SizeModifier"] = "ParticleSizeModifier",
        ["SizeOffsetByVelocity"] = "ParticleSizeOffsetBySpeed",
        // A package's pivot is two modules: one stores a per-particle offset and applies it, the other
        // fills that store from velocity. Nothing else ever writes the store, so ours folds both into
        // one pass and the storage half below is plumbing. See ParticlePivotBySpeed.
        ["PivotFromVelocityMagnitude"] = "ParticlePivotBySpeed",
        // A package's "Constant" initializers are the one-value form of the range ones, and the member
        // aliases that already turn their `Value` into our named field cover them unchanged.
        ["UniformSizeConstantInitializer"] = "ParticleUniformSizeInitializer",
        ["SpeedConstantInitializer"] = "ParticleSpeedInitializer",
        // StartSize and EndSize by those exact names, both float3.
        ["SizeOverLifetimeStartEnd"] = "ParticleSizeOverLifetime",

        ["AvatarPoseNode"] = "AvatarPoseDriver",
        // The marker that says "this slot is the avatar". Unmapped, an imported avatar arrives with no
        // root at all: the equip pass finds no body-node coverage, leaves Head empty, and the socket
        // filler drops the default grey sphere head on the user. Ours is AvatarForm, which is the same
        // idea under a different name (Node => BodyNode.Root). -xlinka
        ["AvatarRoot"] = "AvatarForm",
        ["BipedRig"] = "HumanoidRig",
        // Deliberately NO alias for a package's IK avatar component onto AvatarIK. Both package IK workers
        // live in the licensed solver's namespace, so Resolve refuses them as ForeignIK before this
        // table is consulted; the two aliases that used to sit here were never reached. Mapping them
        // would also be wrong: their proxy references point at the "Target" CHILD of each pose proxy
        // (seated on the bone), where ours point at the driven proxy slot itself and add the view
        // offset on top, so a wired _headProxy would carry the eye-to-bone offset twice. The stored
        // default pose the package solver carries is read straight out of the graph by the importer
        // instead, and equip grows a fresh AvatarIK. -xlinka
        ["RenderTransformOverride"] = "LocalViewOverride",

        // Asset providers: a package names the concrete asset, ours name the provider role.
        ["StaticTexture2D"] = "ImageProvider",
        ["StaticMesh"] = "MeshProvider",
        ["StaticFont"] = "FontProvider",
        // Levels by screen height there, by distance here; the importer converts them (FinishLodGroups).
        ["LODGroup"] = "LodGroup",
        // Screen filters under our naming.
        ["HSV_Material"] = "HSVMaterial",
        ["LUT_Material"] = "LUTMaterial",
        // A main font plus fallbacks, which is what ours is; the files are read off the fonts it names.
        ["FontChain"] = "FontProvider",
        ["StaticSprite"] = "SpriteProvider",

        // Variables. Ours drop the package's "Dynamic" prefix and name the container a scope.
        // A bare synced value and reference, the touchable button, and two value drivers: the same things here
        // under the names ours were given.
        ["ValueField"] = "ValueHolder",
        ["ReferenceField"] = "ReferenceHolder",
        ["TouchButton"] = "TouchPad",
        ["BooleanValueDriver"] = "BoolToValue",
        ["ValueGradientDriver"] = "ValueGradient",

        ["DynamicVariableSpace"] = "VariableScope",
        ["DynamicValueVariable"] = "ValueVariable",
        ["DynamicReferenceVariable"] = "ReferenceVariable",
        ["DynamicField"] = "FieldVariable",
        ["DynamicTypeVariable"] = "TypeVariable",
        ["DynamicValueVariableDriver"] = "ValueVariableDriver",
        ["DynamicReferenceVariableDriver"] = "ReferenceVariableDriver",

        // Drivers with the words the other way round.
        ["ValueCopy"] = "CopyValue",
        ["ReferenceCopy"] = "CopyReference",

        // What a button press does, from whatever button is on the slot. Ours carry a Press prefix.
        ["ButtonToggle"] = "PressToggle",
        ["ButtonValueSet"] = "PressSetValue",
        ["ButtonReferenceSet"] = "PressSetReference",
        ["ButtonValueCycle"] = "PressCycleValue",
        ["ButtonReferenceCycle"] = "PressCycleReference",

        // Drivers under the names ours were given. The asset one names the asset, ours the provider of it.
        ["ValueEqualityDriver"] = "ValueEquals",
        ["MultiBoolConditionDriver"] = "AnyAllBools",
        ["BooleanReferenceDriver"] = "BoolToReference",
        ["BooleanAssetDriver"] = "BoolToReference",
        ["ValueOptionDescriptionDriver"] = "ValueCaption",
        ["ReferenceOptionDescriptionDriver"] = "ReferenceCaption",

        // The scale an avatar is worn at.
        ["DefaultUserScale"] = "AvatarScale",

        // A teleporting locomotion is our blink.
        ["TeleportLocomotion"] = "BlinkLocomotion",

        // An anchor someone is put on is a seat here, and letting go of it on input is the seat's own release.
        ["AvatarAnchor"] = "Seat",
        ["AvatarAnchorLocomotionRelease"] = "SeatReleaseOnMove",

        // Menu entries made in the world, and the pin that puts one on its owner's root ring. Which ring an
        // entry leads to sits on a component of its own in a package; ours keeps it on the entry, filled in
        // once the build is done.
        ["ContextMenuItemSource"] = "ContextMenuEntry",
        ["RootContextMenuItem"] = "ContextMenuPin",

        // PARTICLES. We have the whole family; ours simply carries a Particle prefix. Every target here
        // was checked to exist before being written down - an alias pointing at a name we do not have
        // turns a component we CAN build into a reported loss, which is worse than no alias at all.
        ["ColorRangeInitializer"] = "ParticleColorRangeInitializer",
        ["LifetimeRangeInitializer"] = "ParticleLifetimeRangeInitializer",
        ["SpeedRangeInitializer"] = "ParticleSpeedRangeInitializer",
        ["RotationRangeInitializer"] = "ParticleRotationRangeInitializer",
        ["Rotation3DEulerRangeInitializer"] = "ParticleRotation3DRangeInitializer",
        ["AngularVelocityRangeInitializer"] = "ParticleAngularVelocityRangeInitializer",
        ["AngularVelocity3DRangeInitializer"] = "ParticleAngularVelocity3DRangeInitializer",
        ["UniformSizeRangeInitializer"] = "ParticleUniformSizeRangeInitializer",

        // Trail initializers keep the same shape, prefixed.
        ["TrailColorRangeInitializer"] = "ParticleTrailColorRangeInitializer",
        ["TrailWidthRangeInitializer"] = "ParticleTrailWidthRangeInitializer",
        ["TrailLifetimeRangeInitializer"] = "ParticleTrailLifetimeRangeInitializer",
        ["TrailLifetimeFromSizeInitializer"] = "ParticleTrailLifetimeFromSizeInitializer",

        // The package's "Module" suffix is our bare noun.
        ["ParticleLightsModule"] = "ParticleLights",
        ["ParticleTrailsModule"] = "ParticleTrails",
        ["ParticleRibbonsModule"] = "ParticleRibbons",

        ["OrientByVelocity"] = "ParticleOrientByVelocity",
        ["AlphaOverLifetimeLinearGradient"] = "ParticleAlphaOverLifetime",
        ["ColorOverLifetimeLinearGradient"] = "ParticleColorOverLifetime",

        // Same family: a "constant" initializer is our plain single-value one, as opposed to the range
        // variants above.
        ["ColorConstantInitializer"] = "ParticleColorInitializer",
        ["LifetimeConstantInitializer"] = "ParticleLifetimeInitializer",
        ["Rotation3DConstantInitializer"] = "ParticleRotation3DInitializer",
    };

    // Components that BELONG TO THE PERSON WEARING THE OBJECT, not to the object.
    //
    // An imported avatar ships its own hand laser, built out of a real component plus the
    // mesh, material and cursor slots under it. We have that component too, so it imports perfectly -
    // and now the wearer has two lasers fighting over the same hand, one of them owned by an avatar
    // rather than by the user root that is supposed to own it. There is nothing to salvage: the local
    // user already grows its own. The slot carrying one of these is dropped WITH ITS SUBTREE, because
    // the children are that laser's own parts and keeping them leaves the wreckage behind. -xlinka
    private static readonly HashSet<string> UserspaceConflicts = new(StringComparer.Ordinal)
    {
        "InteractionLaser",
    };

    public static bool ConflictsWithUserspace(string simpleName) =>
        simpleName.Length > 0 && UserspaceConflicts.Contains(simpleName);

    // Slots a component BUILT for itself, matched by the literal name that component gave them.
    //
    // Normally matching a slot on its name would be wrong, because a name is whatever the author typed.
    // These are the exception: the owning component creates the slot under itself with a hardcoded name
    // and later finds it again by that same string to destroy it, so the name is the component's own
    // contract rather than a label anyone chose. The component does not come across, which leaves its
    // scaffolding behind as an orphan nothing drives - a bare sphere on the head that follows the wearer
    // around looking like a bug in the avatar. Dropped WITH ITS SUBTREE: the parts underneath belong to
    // the thing being dropped. -xlinka
    private static readonly HashSet<string> GeneratedOrphanSlots = new(StringComparer.Ordinal)
    {
        "Voice Range Visual",
    };

    private static readonly HashSet<string> ParticlePlumbing = new(StringComparer.Ordinal)
    {
        // The style is the bag a package system's modules and renderer hang off. Read, not attached.
        "ParticleStyle",
        // Integrators. The simulation does this itself.
        "PositionSimulatorModule",
        "RotationSimulatorModule",
        // The storage half of the package's pivot pair. ParticlePivotBySpeed carries the apply, and with no
        // driver this one's buffer is all zeroes and offsets nothing, so it takes nothing with it.
        "PivotModule",
        // Renderers. WireParticleLook reads the mesh, material and texture off these directly.
        "BillboardParticleRenderer",
        "MeshParticleRenderer",
        "LegacyRendererSwitcher",
        // A renderer that draws nothing, used to keep a system simulating for its trails alone.
        // Ours needs no stand-in: a system with no mesh and no texture already draws nothing.
        "NullParticleRenderer",
    };

    private static bool IsParticlePlumbing(string simple)
    {
        if (ParticlePlumbing.Contains(simple))
            return true;
        // The package format's forward-migration shims for older particle data: LegacyGravityAdapter,
        // LegacyTrailsRibbonAdapter, LegacyDistributionAdapter and the rest, plus the nested
        // ConeEmitter+LegacyDirectionConverter.
        // The colour reinterpret adapter on trail colours is one too: the field it drives is saved holding the
        // converted colour already and nothing writes the adapter's own value, so the field reads the same here.
        // A Legacy-named type that has a counterpart here (the spawn point's glow marker) is not one of them.
        return (simple.StartsWith("Legacy", StringComparison.Ordinal) && !Aliases.ContainsKey(simple))
            || simple.EndsWith("+LegacyDirectionConverter", StringComparison.Ordinal);
    }

    public static bool IsGeneratedOrphanSlot(string slotName) =>
        slotName.Length > 0 && GeneratedOrphanSlots.Contains(slotName);

    // Things THIS ENGINE ALREADY PUTS ABOVE EVERY AVATAR, so an imported one is a second copy.
    //
    // Different reason from the set above. Those are scaffolding a component built for itself and
    // left behind when the component did not come across. These are author-built subtrees that work
    // perfectly well - and that is the problem: we draw a nameplate over every user ourselves, so
    // importing the author's gets you two, one of them scripted to follow a body this one never had.
    //
    // Matched on a CONTAINS, case-insensitive, because unlike the set above there is no hardcoded
    // contract to match exactly - the author named it. That is a blunter instrument and it can catch
    // a slot that happened to use the word, which is the trade being made deliberately: a duplicate
    // nameplate is visible on every avatar, and a wrongly-dropped slot called "Nametag" is not. -xlinka
    private static readonly string[] SuppliedByUsFragments =
    {
        "nameplate",
        "name badge",
        "nametag",
        "name tag",
    };

    public static bool IsSuppliedByUs(string slotName)
    {
        if (slotName.Length == 0)
            return false;
        foreach (var fragment in SuppliedByUsFragments)
        {
            if (slotName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Prefixes that identify a whole subsystem rather than one component.
    private const string VisualScriptingMarker = "ProtoFlux";
    private const string ForeignIkMarker = "FinalIK";

    public readonly struct Resolution
    {
        public readonly PackageTypeFate Fate;
        public readonly string SimpleName;
        public readonly Type? Target;

        public Resolution(PackageTypeFate fate, string simpleName, Type? target)
        {
            Fate = fate;
            SimpleName = simpleName;
            Target = target;
        }
    }

    // A node of the package's visual scripting, which PackageNodeGraphs converts instead of attaching.
    public static bool IsVisualScripting(string encodedName) => encodedName.Contains(VisualScriptingMarker, StringComparison.Ordinal)
        || (JoinsGraphs.Count > 0 && JoinsGraphs.Contains(SimpleName(encodedName)));

    public static Resolution Resolve(string encodedName)
    {
        if (string.IsNullOrWhiteSpace(encodedName))
            return new Resolution(PackageTypeFate.Unsupported, string.Empty, null);

        if (encodedName.Contains(VisualScriptingMarker, StringComparison.Ordinal))
            return new Resolution(PackageTypeFate.Stripped, SimpleName(encodedName), null);

        if (encodedName.Contains(ForeignIkMarker, StringComparison.Ordinal))
            return new Resolution(PackageTypeFate.ForeignIK, SimpleName(encodedName), null);

        string simple = SimpleName(encodedName);
        if (Carried.Contains(simple) || JoinsGraphs.Contains(simple))
            return new Resolution(PackageTypeFate.Stripped, simple, null);
        if (simple == "ContextMenuSubmenu")
            return new Resolution(PackageTypeFate.Stripped, simple, null);
        // A package's character settings ride a walking locomotion's slot; ours are the user's own body, so the
        // walk takes the pace from them instead (read once the build is done).
        if (simple == "CharacterController")
            return new Resolution(PackageTypeFate.Stripped, simple, null);
        // The package's head-bone helper is a relay for the desktop pose controller: it drives the view
        // override it sits beside with the unprocessed head pose when the wearer is not in VR. The
        // override itself maps to LocalViewOverride, and the importer puts the head hide on the head
        // bone by default, so there is nothing of this one left to carry. Not a loss worth listing.
        if (simple == "AvatarUserViewHeadOverride")
            return new Resolution(PackageTypeFate.Stripped, simple, null);

        // PARTICLE PLUMBING THAT CARRIES NOTHING ACROSS, so it is a strip and not a loss.
        //
        // Three different kinds of nothing. The package's simulator modules are the integrators that
        // turn velocity into position and angular velocity into rotation - single-instance, no settings,
        // and in this engine that arithmetic is the simulation itself rather than a component you can
        // leave out. The package's renderers and the style that owns them are read directly out of the
        // source data by WireParticleLook, which is where the mesh, the texture and the blend mode come
        // from, so attaching them would be a second copy of something already done. And the
        // Legacy*Adapter family exists purely to migrate older package particle data forward; there is
        // no older data on this side to migrate.
        //
        // Reported as losses these were 21 lines of noise on every avatar import, which is how the
        // ones that ARE losses got lost in the list. -xlinka
        if (IsParticlePlumbing(simple))
            return new Resolution(PackageTypeFate.Stripped, simple, null);
        if (simple.Length == 0)
            return new Resolution(PackageTypeFate.Unsupported, simple, null);
        if (ClosedAliases.Count > 0 && encodedName.Contains('<')
            && ClosedAliases.TryGetValue(simple + "<" + string.Join(",", GenericArguments(encodedName)) + ">", out var closedAlias))
            return new Resolution(PackageTypeFate.Mapped, simple, closedAlias);

        string candidate = Aliases.TryGetValue(simple, out var alias) ? alias : simple;
        var target = FindType(SimpleName(candidate));
        if (target != null)
        {
            // A generic component resolves to its OPEN definition, which cannot be instantiated. Close
            // it with the arguments the package recorded - ValueVariable<bool>, CopyValue<float3> and
            // so on. Left open, every generic component threw "ContainsGenericParameters is true" and
            // was skipped, which on a real avatar is hundreds of drivers and variables. -xlinka
            if (target.ContainsGenericParameters)
            {
                var arguments = GenericArguments(encodedName, raw: true);
                if (arguments.Count == 0)
                    arguments = GenericArguments(candidate, raw: true);
                target = Close(target, arguments, TakesProviders.Contains(simple));
                if (target == null)
                    return new Resolution(PackageTypeFate.Unsupported, simple, null);
            }
            return new Resolution(PackageTypeFate.Mapped, simple, target);
        }

        // Materials and shader providers land here when we have no counterpart. Called out separately
        // because the shader program is the one thing a conversion genuinely cannot carry across, and
        // seeing them listed is more useful than burying them among unrelated misses.
        if (LooksLikeShader(simple))
            return new Resolution(PackageTypeFate.Shader, simple, null);

        return new Resolution(PackageTypeFate.Unsupported, simple, null);
    }

    // The package's value-type names for the primitives that show up as generic arguments.
    private static readonly Dictionary<string, Type> ValueTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bool"] = typeof(bool),
        ["byte"] = typeof(byte),
        ["sbyte"] = typeof(sbyte),
        ["short"] = typeof(short),
        ["ushort"] = typeof(ushort),
        ["int"] = typeof(int),
        ["uint"] = typeof(uint),
        ["long"] = typeof(long),
        ["ulong"] = typeof(ulong),
        ["float"] = typeof(float),
        ["double"] = typeof(double),
        ["string"] = typeof(string),
        ["float2"] = typeof(Math.float2),
        ["float3"] = typeof(Math.float3),
        ["float4"] = typeof(Math.float4),
        ["floatQ"] = typeof(Math.floatQ),
        ["color"] = typeof(Math.color),
        // The package's high-dynamic-range colour is our colorHDR.
        ["colorX"] = typeof(Math.colorHDR),
        ["colorHDR"] = typeof(Math.colorHDR),
        ["ColorProfile"] = typeof(Math.ColorProfile),
        ["Uri"] = typeof(Uri),
        ["float3x3"] = typeof(Math.float3x3),
        ["float4x4"] = typeof(Math.float4x4),
        ["int2"] = typeof(Math.int2),
        ["int3"] = typeof(Math.int3),
        ["int4"] = typeof(Math.int4),
        ["bool2"] = typeof(Math.bool2),
        ["bool3"] = typeof(Math.bool3),
        ["bool4"] = typeof(Math.bool4),
        ["float2x2"] = typeof(Math.float2x2),
        ["Rect"] = typeof(Math.Rect),
        ["char"] = typeof(char),
        ["decimal"] = typeof(decimal),
        ["DateTime"] = typeof(DateTime),
        // Nine coefficients either way; theirs are colours, ours plain linear RGB (PackageMemberMapper).
        ["SphericalHarmonicsL2"] = typeof(Math.SphericalHarmonicsL2),
    };

    // Package types whose argument is an asset where ours takes the provider of one.
    private static readonly HashSet<string> TakesProviders = new(StringComparer.Ordinal) { "BooleanAssetDriver", "AssetMultiplexer" };

    private static Type? Close(Type openType, IReadOnlyList<string> arguments, bool providers)
    {
        var parameters = openType.GetGenericArguments();
        if (arguments.Count != parameters.Length)
            return null;

        var resolved = new Type[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            var argument = Argument(arguments[i], providers);
            if (argument == null)
                return null;
            resolved[i] = argument;
        }

        try
        {
            var closed = openType.MakeGenericType(resolved);
            // A constraint the arguments do not satisfy throws here rather than at construction.
            return closed.ContainsGenericParameters ? null : closed;
        }
        catch
        {
            return null;
        }
    }

    // One type argument as written in the package: a value, a component, a slot or user, or the provider of an
    // asset, which ours names by our asset type.
    private static Type? Argument(string raw, bool provider)
    {
        string simple = SimpleName(raw);
        if (provider)
            return AssetType(simple) is { } asset ? typeof(IAssetProvider<>).MakeGenericType(asset) : null;
        if (ValueTypes.TryGetValue(simple, out var valueType))
            return valueType;
        if (simple == "IAssetProvider")
            return GenericArguments(raw, raw: true) is { Count: 1 } inner && AssetType(SimpleName(inner[0])) is { } held
                ? typeof(IAssetProvider<>).MakeGenericType(held) : ArgumentK3(raw, simple);
        if (simple == "Slot")
            return typeof(Slot);
        if (simple == "User")
            return typeof(User);
        // A generic component as the argument is closed over its own arguments, or it stays open and the outer
        // one cannot close.
        if (FindType(simple) is { } found)
            return found.ContainsGenericParameters ? Close(found, GenericArguments(raw, raw: true), false) : found;
        return ArgumentK3(raw, simple) ?? ArgumentUi(raw, simple);
    }

    private static Type? AssetType(string name) => name switch
    {
        "Material" => typeof(MaterialAsset),
        "ITexture2D" or "Texture2D" or "ITexture" => typeof(TextureAsset),
        "VideoTexture" => typeof(VideoTexture),
        "AudioClip" => typeof(AudioClip),
        _ => null,
    };

    private static bool LooksLikeShader(string simple) =>
        simple.EndsWith("Material", StringComparison.Ordinal)
        || simple.StartsWith("PBS", StringComparison.Ordinal)
        || simple.Contains("Shader", StringComparison.Ordinal);

    // Index of every component this build actually has, by simple name, built once from the loaded
    // assemblies. A hardcoded namespace list was the first attempt and it quietly under-reported: it
    // named folders that are not namespaces, missed real ones, and so declared components we do have -
    // materials, meshes - as unsupported. Asking the assembly is the only version of this that cannot
    // drift as the component tree gets reorganised. -xlinka
    private static Dictionary<string, Type>? _componentsByName;
    private static readonly object _indexLock = new();

    private static Dictionary<string, Type> ComponentIndex
    {
        get
        {
            lock (_indexLock)
            {
                if (_componentsByName != null)
                    return _componentsByName;

                var index = new Dictionary<string, Type>(StringComparer.Ordinal);
                var componentBase = typeof(Component);
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var name = assembly.GetName().Name;
                    if (name == null || !name.StartsWith("Lumora", StringComparison.Ordinal))
                        continue;

                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch (System.Reflection.ReflectionTypeLoadException ex)
                    {
                        types = Array.ConvertAll(
                            Array.FindAll(ex.Types, t => t != null), t => t!);
                    }

                    foreach (var type in types)
                    {
                        if (type == null || type.IsAbstract || !componentBase.IsAssignableFrom(type))
                            continue;
                        // Generic components are indexed by their bare name: "ValueVariable`1" is the
                        // counterpart of a generic in a package's type table, and the arguments are
                        // matched separately by whatever rebuilds the instance.
                        var key = type.Name;
                        int tick = key.IndexOf('`');
                        if (tick > 0)
                            key = key[..tick];
                        index.TryAdd(key, type);
                    }
                }
                return _componentsByName = index;
            }
        }
    }

    private static Type? FindType(string simpleName) => ComponentIndex.GetValueOrDefault(simpleName);

    // "[Assembly]Namespace.Outer+Nested<...>" -> "Nested". Generic arguments are dropped: the component
    // identity is the open type, and the arguments are carried separately by whatever rebuilds it.
    public static string SimpleName(string encodedName)
    {
        var s = encodedName;

        int close = s.IndexOf(']');
        if (s.Length > 0 && s[0] == '[' && close > 0)
            s = s[(close + 1)..];

        int generic = s.IndexOf('<');
        if (generic >= 0)
            s = s[..generic];

        int nested = s.LastIndexOf('+');
        if (nested >= 0)
            s = s[(nested + 1)..];

        int dot = s.LastIndexOf('.');
        if (dot >= 0)
            s = s[(dot + 1)..];

        return s.Trim();
    }

    // The generic arguments, in source order, for reporting. "Foo<bar, [X]Y.Z>" -> ["bar", "Z"].
    public static IReadOnlyList<string> GenericArguments(string encodedName) => GenericArguments(encodedName, raw: false);

    // With raw, each argument as written, its own arguments included.
    private static IReadOnlyList<string> GenericArguments(string encodedName, bool raw)
    {
        int open = encodedName.IndexOf('<');
        if (open < 0)
            return Array.Empty<string>();

        var args = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        for (int i = open; i < encodedName.Length; i++)
        {
            char c = encodedName[i];
            if (c == '<')
            {
                depth++;
                if (depth == 1) continue;
            }
            else if (c == '>')
            {
                depth--;
                if (depth == 0)
                {
                    Flush(args, current, raw);
                    break;
                }
            }
            else if (c == ',' && depth == 1)
            {
                Flush(args, current, raw);
                continue;
            }
            current.Append(c);
        }
        return args;

        static void Flush(List<string> into, StringBuilder sb, bool raw)
        {
            var text = sb.ToString().Trim();
            sb.Clear();
            if (text.Length > 0)
                into.Add(raw ? text : SimpleName(text));
        }
    }
}
