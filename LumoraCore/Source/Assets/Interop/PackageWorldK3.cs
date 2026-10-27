// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Components;
using Lumora.Core.Components.Media;
using Lumora.Core.Components.Utility;
using Lumora.Core.Components.Variables;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// Drivers, value plumbing and simple motion from a package, onto ours.
public static partial class PackageTypeMap
{
    static partial void AliasesK3(Dictionary<string, string> aliases)
    {
        // The same jobs under our names.
        aliases["SmoothValue"] = "ValueSmoother";
        aliases["ValueMultiplexer"] = "ValueSelector";
        aliases["ReferenceMultiplexer"] = "ReferenceSelector";
        // An asset picked from a list by index is a provider picked from a list by index.
        aliases["AssetMultiplexer"] = "ReferenceSelector";
        aliases["ValueDriver"] = "CopyValue";
        aliases["DynamicReference"] = "ReferenceFieldVariable";
        aliases["IsPlayingDriver"] = "PlaybackStateDriver";
        aliases["LinearMapper1D"] = "RangeMap1D";
        aliases["LinearMapper2D"] = "RangeMap2D";
        aliases["LinearMapper3D"] = "RangeMap3D";

        // Motion on the session clock. The spin's axis and rate, the panners' offsets and the wobble's base are
        // worked out in ApplyK3.
        aliases["Spinner"] = "Rotator";
        aliases["Panner2D"] = "UVPanner";
        aliases["Panner1D"] = "ValuePanner";
        aliases["Wobbler3D"] = "Jitter";
        aliases["Wiggler"] = "Jitter";

        aliases["BoundingBoxDriver"] = "BoundsDriver";
        aliases["Float2Driver"] = "Float2Compose";
        aliases["Float3Driver"] = "Float3Compose";
        aliases["Float3ToFloat2SwizzleDriver"] = "Float3ToFloat2Remap";
        aliases["RectTransformLerp"] = "RectLerp";
        aliases["RectSizeDriver"] = "RectSizeOutput";
        aliases["AxisMultiViewportPanner"] = "PageCarousel";

        // A switcher is an index and one comparison per target: the index is held here, and ApplyK3 puts a
        // ValueEquals beside it for each target.
        aliases["BooleanSwitcher"] = "ValueHolder<int>";
    }

    // The type this package type closes to when its colour argument is our plain color rather than colorHDR,
    // or null when that form is not one we can build. See PackageMemberMapper.PlainColourComponents.
    public static Type? PlainColourType(string encodedName)
    {
        var resolved = Resolve(PlainColourArgument.Replace(encodedName, "color"));
        if (resolved.Fate != PackageTypeFate.Mapped || resolved.Target is not { } target)
            return null;
        var valid = target.GetProperty("IsValidGenericType",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy);
        return valid?.GetValue(null) is false ? null : target;
    }

    private static readonly System.Text.RegularExpressions.Regex PlainColourArgument =
        new(@"(?<=[<,]\s*)colorX(?=\s*[,>])", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // Enums the package names differently, or that no synced member of ours declares.
    private static readonly Dictionary<string, Type> EnumArgumentsK3 = new(StringComparer.Ordinal)
    {
        ["MotionVectorMode"] = typeof(MotionVectorMode),
        ["TrailTextureMode"] = typeof(Simulation.Particles.StrandUVMode),
    };

    // An argument the shared table does not name: an enum, a nullable, a field, or one of the package's interface
    // types, which become the element our side keeps for that role. A collider is reached through its slot and an
    // audio source or stream is the component itself, the same handles the converted graphs read them as.
    private static Type? ArgumentK3(string raw, string simple)
    {
        if (simple.EndsWith('?'))
        {
            // Nothing of ours stores a nullable enum, so a nullable argument closes over its plain type.
            int mark = raw.LastIndexOf('?');
            return mark > 0 ? Argument(raw[..mark], provider: false) : null;
        }

        var inner = GenericArguments(raw, raw: true);
        switch (simple)
        {
            case "ICollider":
                return typeof(Slot);
            case "IWorldAudioDataSource" or "IAudioStream" or "IAudioSource" or "ITool" or "IComponent":
                return typeof(Component);
            case "IField" or "IValue" when inner.Count == 1:
            {
                var held = Argument(inner[0], provider: false);
                return held != null && (held.IsValueType || held == typeof(string) || held == typeof(Uri))
                    ? typeof(IField<>).MakeGenericType(held) : null;
            }
            case "IAssetProvider" when inner.Count == 1 && SimpleName(inner[0]) == "Sprite":
                return typeof(Sprite);
        }

        if (EnumArgumentsK3.TryGetValue(simple, out var aliased))
            return aliased;
        foreach (var type in GenericComponentTypes.EnumTypes)
        {
            if (type.Name == simple)
                return type;
        }
        return null;
    }
}

public static partial class PackageMemberMapper
{
    // A stored member with this name never lands; ApplyK3 takes it. The generic pass runs after the hooks and
    // would put the stored value straight back over the worked-out one.
    private const string HandledK3 = "\u0000k3";

    static partial void MemberAliasesK3(Dictionary<string, Dictionary<string, string>> table)
    {
        // A package variable adopts what is already bound "on link"; ours "on bind".
        foreach (var variable in new[] { "ValueVariable`1", "ReferenceVariable`1", "FieldVariable`1", "ReferenceFieldVariable`1", "TypeVariable" })
            MergeK3(table, variable, ("OverrideOnLink", "OverrideOnBind"));
        MergeK3(table, "ReferenceVariableDriver`1", ("DefaultTarget", "DefaultValue"));

        MergeK3(table, "CopyValue`1", ("ValueSource", "Source"), ("DriveTarget", "Target"));
        MergeK3(table, "ValueSelector`1", ("AllowWriteBack", "WriteBack"));
        // A reference picked from a list has no way back here; a write to the target holds until the next pick.
        MergeK3(table, "ReferenceSelector`1", ("Assets", "References"), ("AllowWriteBack", HandledK3));
        foreach (var map in new[] { "RangeMap1D", "RangeMap2D", "RangeMap3D" })
            MergeK3(table, map, ("AllowReverseMapping", "ReverseMapping"));

        // Who is playing: the package names the clip player's state, ours the player (ApplyIsPlaying).
        MergeK3(table, "PlaybackStateDriver", ("Playback", HandledK3), ("Targets", HandledK3));

        MergeK3(table, "Rotator", ("_target", "Rotation"), ("_offset", "BaseRotation"), ("_speed", HandledK3), ("Range", HandledK3));
        MergeK3(table, "UVPanner", ("_target", "Offset"), ("_speed", HandledK3), ("_preOffset", HandledK3), ("_offset", HandledK3),
            ("_repeat", "Repeat"));
        MergeK3(table, "ValuePanner", ("_target", "Target"), ("_speed", "Speed"), ("_preOffset", "StartOffset"), ("_offset", "Offset"),
            ("_repeat", "Repeat"));
        // The wobbler's seed and the wiggler's are both a phase here, which is also what a graph driving one writes.
        MergeK3(table, "Jitter", ("Target", "Position"), ("Offset", HandledK3), ("Magnitude", HandledK3), ("Seed", "Phase"),
            ("_target", "Rotation"), ("_offset", "BaseRotation"), ("_speed", "Speed"), ("_magnitude", HandledK3), ("_seed", "Phase"));

        MergeK3(table, "BoundsDriver", ("BoundedSource", "Source"));
        MergeK3(table, "Float3ToFloat2Remap", ("X", HandledK3), ("Y", HandledK3));
        MergeK3(table, "RectLerp", ("SourceRect", "Source"));
        MergeK3(table, "RectSizeOutput", ("TargetSize", "Target"));
        MergeK3(table, "PageCarousel", ("ViewportIndex", "Index"), ("AnimationTime", "SlideTime"), ("Viewports", "Pages"));
        MergeK3(table, "ValueHolder`1", ("ActiveIndex", "Value"), ("Targets", HandledK3), ("ActivationMode", HandledK3));

        // Lists of drives aimed at fields, joined one by one in ApplyK3.
        MergeK3(table, "ValueMultiDriver`1", ("Drives", HandledK3));
        MergeK3(table, "ReferenceMultiDriver`1", ("Drives", HandledK3));

        // A particle trail's texture layout, which a copy between the trail and ribbon modules keeps in step.
        MergeK3(table, "ParticleTrails", ("TextureMode", "UVMode"));
        MergeK3(table, "ParticleRibbons", ("TextureMode", "UVMode"));
        EnumValueAliases["TextureMode"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Stretch"] = "StretchAlongStrand",
            ["DistributePerSegment"] = "StretchAlongStrand",
            ["Tile"] = "TilePerSegment",
            ["RepeatPerSegment"] = "TilePerSegment",
        };
    }

    private static void MergeK3(Dictionary<string, Dictionary<string, string>> table, string ours, params (string Theirs, string Ours)[] pairs)
    {
        if (!table.TryGetValue(ours, out var map))
            table[ours] = map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (theirs, mine) in pairs)
            map[theirs] = mine;
    }

    static partial void ApplyK3(Component component, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList, Func<Uri, Uri?> remapUrl)
    {
        if (component.GetType() is { IsGenericType: true } generic)
        {
            var definition = generic.GetGenericTypeDefinition();
            if (definition == typeof(ValueMultiDriver<>) || definition == typeof(ReferenceMultiDriver<>))
                WorldK3.JoinDriveList(component, data, "Drives", queueReference);
            var argument = generic.GetGenericArguments()[0];
            if (argument == typeof(Uri))
                WorldK3.ApplyWebAddresses(component, data);
            else if (argument == typeof(float3x3))
                WorldK3.ApplyMatrices(component, data);
        }

        switch (component)
        {
            case PlaybackStateDriver playing when data.TryGetNode("Playback") != null:
                WorldK3.ApplyIsPlaying(playing, data, queueReference);
                break;
            case Rotator rotator when data.TryGetNode("_speed") != null:
                WorldK3.ApplySpinner(rotator, data, remapUrl);
                break;
            case UVPanner panner when data.TryGetNode("_speed") != null:
                WorldK3.ApplyPanner(panner, data, queueReference, remapUrl);
                break;
            case Jitter jitter when data.TryGetNode("Magnitude") != null:
                WorldK3.ApplyWobble(jitter, data, queueReference, remapUrl);
                break;
            case Jitter jitter when data.TryGetNode("_magnitude") != null:
                WorldK3.ApplyWiggle(jitter, data, remapUrl);
                break;
            case RangeMap3D map when data.TryGetNode("AllowReverseMapping") != null:
                WorldK3.ApplyMapper3D(map, data, queueReference);
                break;
            case Float3ToFloat2Remap remap:
                remap.X.Value = WorldK3.Channel(WorldK3.StoredK3(data, "X"), RemapChannel.X);
                remap.Y.Value = WorldK3.Channel(WorldK3.StoredK3(data, "Y"), RemapChannel.Y);
                break;
            case ValueHolder<int> holder when data.TryGetNode("ActivationMode") != null:
                WorldK3.ApplySwitcher(holder, data, queueReference);
                break;
            case InteractionElement element when data.TryGetNode("ColorDrivers") != null:
                WorldK3.ApplyColorDrivers(element, data, queueReference, remapUrl);
                break;
            case ReferenceVariable<Slot> variable:
                WorldK3.SlotOfComponent(variable.Reference, data, "Reference", queueReference);
                break;
            case ReferenceVariableDriver<Slot> driver:
                WorldK3.SlotOfComponent(driver.DefaultValue, data, "DefaultTarget", queueReference);
                break;
            case ReferenceHolder<Slot> holder:
                WorldK3.SlotOfComponent(holder.Reference, data, "Reference", queueReference);
                break;
        }
    }

    // A reference that, aimed at a member ours has no counterpart for, takes the member's owner instead.
    public interface IMemberOwnerReference : ISyncRef
    {
    }

    // The worked-out half of ApplyK3, kept apart from the other families' helpers on this class.
    private static class WorldK3
    {
        public static DataTreeNode? StoredK3(DataTreeDictionary data, string name)
            => (data.TryGetNode(name) as DataTreeDictionary)?.TryGetNode("Data");

        public static string? StoredId(DataTreeDictionary data, string name)
            => StoredK3(data, name) is DataTreeValue { Value: string id } && id.Length > 0 && !IsNullId(id) ? id : null;

        public static List<string?> StoredIds(DataTreeDictionary data, string name)
        {
            var ids = new List<string?>();
            if (StoredK3(data, name) is not DataTreeList list)
                return ids;
            foreach (var entry in list.Children)
            {
                var value = entry is DataTreeDictionary holder ? holder.TryGetNode("Data") : entry;
                ids.Add(value is DataTreeValue { Value: string id } && id.Length > 0 && !IsNullId(id) ? id : null);
            }
            return ids;
        }

        public static T StoredValue<T>(DataTreeDictionary data, string name, T fallback, Func<Uri, Uri?> remapUrl)
            => StoredK3(data, name) is { } node && TryConvert(node, typeof(T), out var value, remapUrl, name) && value is T typed
                ? typed : fallback;

        // A reference the import joins like any other, whose arrival is handed to a callback instead of stored: how
        // a hook here sees what a package id turned out to be once the whole tree exists. It holds nothing, so the
        // join always counts as made; what the callback does with it is its own business.
        private class Arrival : ISyncRef
        {
            private readonly Action<IWorldElement> _seen;

            public Arrival(Action<IWorldElement> seen) => _seen = seen;

            public RefID Value { get => RefID.Null; set { } }
            public IWorldElement Target { get => null!; set => TrySet(value); }
            public IWorldElement RawTarget => null!;
            public Type TargetType => typeof(IWorldElement);
            public ReferenceState State => default;
            public void Clear() { }

            public bool TrySet(IWorldElement target)
            {
                if (target != null)
                    _seen(target);
                return true;
            }
        }

        private sealed class OwnerArrival : Arrival, IMemberOwnerReference
        {
            public OwnerArrival(Action<IWorldElement> seen) : base(seen)
            {
            }
        }

        // The component a member belongs to, and the member's name on it.
        public static Worker? OwnerOf(IWorldElement element, out string name)
        {
            name = string.Empty;
            var parent = (element as SyncElement)?.Parent;
            while (parent != null && parent is not Worker)
                parent = (parent as SyncElement)?.Parent;
            if (parent is not Worker owner)
                return null;
            for (int i = 0; i < owner.SyncMemberCount; i++)
            {
                if (ReferenceEquals(owner.GetSyncMember(i), element))
                {
                    name = owner.GetSyncMemberName(i);
                    break;
                }
            }
            return owner;
        }

        // How a float3 written into this field crosses the mirror, by the same rule the import reads stored members
        // with: a slot's position and anything named like a position or direction flips its X, anything angular
        // flips Y and Z.
        public static float3 MirrorFor(IWorldElement field, in float3 value)
        {
            var owner = OwnerOf(field, out var name);
            if (owner is Slot slot)
                return ReferenceEquals(field, slot.LocalPosition) ? PackageHandedness.Point(value) : value;
            return owner == null ? value : PackageHandedness.Member(name, value);
        }

        // Which clip is playing: the package points at the player's playback state, ours at the player. One target
        // per driver here; any further ones get a driver of their own beside it.
        public static void ApplyIsPlaying(PlaybackStateDriver playing, DataTreeDictionary data, Action<ISyncRef, string> queueReference)
        {
            var player = StoredId(data, "Playback");
            bool first = true;
            foreach (var target in StoredIds(data, "Targets"))
            {
                if (target == null)
                    continue;
                var driver = first ? playing : playing.Slot.AttachComponent<PlaybackStateDriver>();
                first = false;
                if (player != null)
                    queueReference(new OwnerArrival(element => SetOwner(driver, element)), player);
                queueReference(driver.IsPlaying, target);
            }
            if (first && player != null)
                queueReference(new OwnerArrival(element => SetOwner(playing, element)), player);
        }

        public static void SetOwner(PlaybackStateDriver driver, IWorldElement element)
        {
            var owner = element as Component ?? OwnerOf(element, out _) as Component;
            if (!driver.IsDestroyed && owner is Media.IPlayable)
                driver.Source.Target = owner;
        }

        // The package spins by Euler rates per axis; ours spins about one axis at one rate. Every spinner seen spins
        // about one axis, which this carries exactly; rates on several axes at once become one spin about their sum,
        // close but not the tumble Euler rates make. The rate vector is angular, so it mirrors as one.
        public static void ApplySpinner(Rotator rotator, DataTreeDictionary data, Func<Uri, Uri?> remapUrl)
        {
            var speed = PackageHandedness.Axial(StoredValue(data, "_speed", float3.Zero, remapUrl));
            float rate = speed.Length;
            rotator.Axis.Value = rate > 1e-6f ? speed / rate : float3.Up;
            rotator.Speed.Value = rate;
        }

        // Speed and the offsets before and after the wrap fold into ours. Scrolling a texture is periodic, so the
        // offset after the wrap moves the scroll the same as one before it. On a material the texture rows run the
        // other way here (see FlipTextureTransforms), so the scroll is turned over once its target is known.
        public static void ApplyPanner(UVPanner panner, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
            Func<Uri, Uri?> remapUrl)
        {
            panner.Speed.Value = StoredValue(data, "_speed", float2.Zero, remapUrl);
            panner.StartOffset.Value = StoredValue(data, "_preOffset", float2.Zero, remapUrl) + StoredValue(data, "_offset", float2.Zero, remapUrl);
            if (StoredId(data, "_target") is not { } target)
                return;
            queueReference(new Arrival(element =>
            {
                if (panner.IsDestroyed || OwnerOf(element, out var name) is not MaterialProvider material || material is Projection360Material)
                    return;
                foreach (var (scaleName, offsetName) in UvTransforms)
                {
                    if (offsetName != name)
                        continue;
                    float scaleY = 1f;
                    for (int i = 0; i < material.SyncMemberCount; i++)
                    {
                        if (material.GetSyncMemberName(i) == scaleName && material.GetSyncMember(i) is Sync<float2> scale)
                            scaleY = scale.Value.y;
                    }
                    var speed = panner.Speed.Value;
                    var start = panner.StartOffset.Value;
                    panner.Speed.Value = new float2(speed.x, -speed.y);
                    panner.StartOffset.Value = new float2(start.x, 1f - scaleY - start.y);
                    return;
                }
            }), target);
        }

        // The package wobbles one float3 on noise around a base; ours wanders a position around one, its rotation half
        // left still and undriven. Every wobbler there reads the same noise and its seed only shifts it along, so ours
        // share one pattern and take the seed as their phase (the member map): wobblers seeded close together, like
        // the strips of one flag, move together. Base and swing mirror the way the field they drive does, which is
        // only known once the target is, so both are read as stored rather than by their names. Neither half takes
        // its default target in the frames before the references land: aimed at its own slot, the wobble carried a
        // flag's strips metres apart and left them there. -xlinka
        public static void ApplyWobble(Jitter jitter, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
            Func<Uri, Uri?> remapUrl)
        {
            jitter.RotationMagnitude.Value = float3.Zero;
            jitter.Rotation.ValueCameFromData = true;
            jitter.Position.ValueCameFromData = true;
            jitter.Seed.Value = 0;
            var offset = AsStored(data, "Offset", remapUrl);
            var magnitude = AsStored(data, "Magnitude", remapUrl);
            jitter.BasePosition.Value = offset;
            jitter.PositionMagnitude.Value = magnitude;
            if (StoredId(data, "Target") is { } target)
            {
                queueReference(new Arrival(element =>
                {
                    if (jitter.IsDestroyed)
                        return;
                    jitter.BasePosition.Value = MirrorFor(element, offset);
                    jitter.PositionMagnitude.Value = MirrorFor(element, magnitude);
                }), target);
            }
        }

        // The package's wiggler turns a rotation on the same noise about the parent's axes, in degrees, over a base it
        // keeps; ours is the jitter's rotation half with the position left alone, its seed a phase as above. One aimed
        // at nothing drives nothing, as there.
        public static void ApplyWiggle(Jitter jitter, DataTreeDictionary data, Func<Uri, Uri?> remapUrl)
        {
            jitter.PositionMagnitude.Value = float3.Zero;
            jitter.Position.ValueCameFromData = true;
            jitter.Rotation.ValueCameFromData = true;
            jitter.TurnAboutParent.Value = true;
            jitter.Seed.Value = 0;
            jitter.RotationMagnitude.Value = PackageHandedness.Axial(AsStored(data, "_magnitude", remapUrl));
        }

        // A float3 as the package stored it, with no mirror by name.
        private static float3 AsStored(DataTreeDictionary data, string name, Func<Uri, Uri?> remapUrl)
            => StoredK3(data, name) is { } node && TryConvert(node, typeof(float3), out var value, remapUrl, string.Empty)
                && value is float3 stored ? stored : float3.Zero;

        // A mapper onto a position or direction holds its two ends in package space; they cross the mirror the way
        // the field they drive does.
        public static void ApplyMapper3D(RangeMap3D map, DataTreeDictionary data, Action<ISyncRef, string> queueReference)
        {
            if (StoredId(data, "Target") is not { } target)
                return;
            queueReference(new Arrival(element =>
            {
                if (map.IsDestroyed)
                    return;
                map.TargetMin.Value = MirrorFor(element, map.TargetMin.Value);
                map.TargetMax.Value = MirrorFor(element, map.TargetMax.Value);
            }), target);
        }

        public static RemapChannel Channel(DataTreeNode? node, RemapChannel fallback)
        {
            if (node is not DataTreeValue { Value: IConvertible value } || value is string)
                return fallback;
            return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) switch
            {
                0 => RemapChannel.X,
                1 => RemapChannel.Y,
                2 => RemapChannel.Z,
                _ => fallback,
            };
        }

        // One comparison per target against the held index: on at the index, or everywhere but. The two modes that
        // light everything up to or from the index have no comparison of ours, and light the index alone.
        public static void ApplySwitcher(ValueHolder<int> holder, DataTreeDictionary data, Action<ISyncRef, string> queueReference)
        {
            bool allBut = StoredK3(data, "ActivationMode") is DataTreeValue { Value: string mode }
                && string.Equals(mode, "AllButIndex", StringComparison.OrdinalIgnoreCase);
            var targets = StoredIds(data, "Targets");
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] is not { } target)
                    continue;
                var equals = holder.Slot.AttachComponent<ValueEquals<int>>();
                equals.Source.Target = holder.Value;
                equals.Reference.Value = i;
                equals.Invert.Value = allBut;
                queueReference(equals.Target, target);
            }
        }

        // A package interaction element keeps its tint drivers in a list on itself; ours are ColorDriver components on
        // its slot, one per tinted field. The one ours made for itself on attach gives way to what the package kept,
        // whose targets are often a smoother's target rather than the graphic itself, so the tint eases.
        public static void ApplyColorDrivers(InteractionElement element, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
            Func<Uri, Uri?> remapUrl)
        {
            var slot = element.Slot;
            var made = new List<ColorDriver>();
            foreach (var existing in slot.GetComponents<ColorDriver>())
            {
                if (existing.Interaction.Target == null || ReferenceEquals(existing.Interaction.Target, element))
                    made.Add(existing);
            }
            foreach (var existing in made)
                existing.Destroy();

            if (StoredK3(data, "ColorDrivers") is not DataTreeList list)
                return;
            foreach (var entry in list.Children)
            {
                if (entry is not DataTreeDictionary record)
                    continue;
                if (record.TryGetNode("Data") is DataTreeDictionary inner)
                    record = inner;
                var driver = slot.AttachComponent<ColorDriver>();
                driver.Interaction.Target = element;
                if (StoredK3(record, "TintColorMode") is DataTreeValue { Value: string mode }
                    && Enum.TryParse<InteractionColorMode>(mode, ignoreCase: true, out var parsed))
                    driver.TintColorMode.Value = parsed;
                driver.NormalColor.Value = StoredValue(record, "NormalColor", driver.NormalColor.Value, remapUrl);
                driver.HighlightColor.Value = StoredValue(record, "HighlightColor", driver.HighlightColor.Value, remapUrl);
                driver.PressedColor.Value = StoredValue(record, "PressColor", driver.PressedColor.Value, remapUrl);
                driver.DisabledColor.Value = StoredValue(record, "DisabledColor", driver.DisabledColor.Value, remapUrl);
                if (StoredId(record, "ColorDrive") is { } target)
                    queueReference(driver.Target, target);
            }
        }

        // A list of drives, each aimed at a field by its id. Joined one by one as single references: a list is joined
        // against whole components, and a field is not one, so every entry came in empty.
        public static void JoinDriveList(Component component, DataTreeDictionary data, string member, Action<ISyncRef, string> queueReference)
        {
            ISyncList? list = null;
            for (int i = 0; i < component.SyncMemberCount; i++)
            {
                if (component.GetSyncMemberName(i) == member)
                    list = component.GetSyncMember(i) as ISyncList;
            }
            if (list == null)
                return;
            foreach (var id in StoredIds(data, member))
            {
                if (list.AddElement() is ISyncRef drive && id != null)
                    queueReference(drive, id);
            }
        }

        // A web address held as a value: a stream a radio plays, a page a button opens. The shared conversion keeps
        // only addresses inside the package, which it can swap for the local copy; one on the web is the value
        // itself and carries over as it is.
        public static void ApplyWebAddresses(Component component, DataTreeDictionary data)
        {
            foreach (var (name, holder) in data.Children)
            {
                if ((holder as DataTreeDictionary)?.TryGetNode("Data") is not DataTreeValue { Value: string } stored
                    || stored.ExtractUrl() is not { IsAbsoluteUri: true } address
                    || !(address.Scheme is "http" or "https" or "rtmp" or "rtsp" or "mms" or "ftp"))
                    continue;
                if (Member(component, ResolveMemberName(component, name)) is Sync<Uri> field)
                    field.Value = address;
            }
        }

        // A 3x3 matrix is stored as three rows of three; ours keeps columns.
        public static void ApplyMatrices(Component component, DataTreeDictionary data)
        {
            foreach (var (name, holder) in data.Children)
            {
                if ((holder as DataTreeDictionary)?.TryGetNode("Data") is not DataTreeList rows || rows.Count != 3
                    || Member(component, ResolveMemberName(component, name)) is not Sync<float3x3> field)
                    continue;
                var r = new float[3][];
                for (int i = 0; i < 3; i++)
                {
                    r[i] = rows[i] is DataTreeList row ? ReadNumbers(row) : Array.Empty<float>();
                    if (r[i].Length < 3)
                        return;
                }
                field.Value = new float3x3(
                    new float3(r[0][0], r[1][0], r[2][0]),
                    new float3(r[0][1], r[1][1], r[2][1]),
                    new float3(r[0][2], r[1][2], r[2][2]));
            }
        }

        public static ISyncMember? Member(Worker worker, string name)
        {
            for (int i = 0; i < worker.SyncMemberCount; i++)
            {
                if (worker.GetSyncMemberName(i) == name)
                    return worker.GetSyncMember(i);
            }
            return null;
        }

        // A package collider handle is the collider; ours is its slot.
        public static void SlotOfComponent(SyncRef<Slot> reference, DataTreeDictionary data, string member, Action<ISyncRef, string> queueReference)
        {
            if (StoredId(data, member) is not { } id)
                return;
            queueReference(new Arrival(element =>
            {
                if (element is Component { IsDestroyed: false } component && !reference.IsDisposed)
                    reference.Target = component.Slot;
            }), id);
        }
    }
}
