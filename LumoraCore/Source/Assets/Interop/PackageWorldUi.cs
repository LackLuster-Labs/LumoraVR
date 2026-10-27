// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Lumora.Core.Math;
using Lumora.Core.Components;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Utility;
using Lumora.Core.Persistence;
using Logger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Assets.Interop;

// A world's panels and what its presses do, its media player, and the locomotions it hands its visitors. -xlinka
public static partial class PackageTypeMap
{
    static partial void AliasesUi(Dictionary<string, string> aliases)
    {
        // Panels. A gradient image there is our four-corner gradient fill; the fitter that keeps a ratio inside
        // its rect is our ratio lock fitting in its parent; the outlined ring is our ring segment with its radii
        // taken from the rect (PackageMemberMapper.ApplyUi).
        aliases["GradientImage"] = "GradientPanel";
        aliases["AspectRatioFitter"] = "AspectRatioConstraint";
        aliases["OutlinedArc"] = "ArcSegment";

        // Presses. A link that asks before it opens, a relay that presses another slot, and a press that calls
        // a method: the only method a world calls that way here is destroying a slot, which is our press destroy.
        aliases["Hyperlink"] = "WebLink";
        aliases["ButtonPressEventRelay"] = "PressRelay";
        aliases["ButtonActionTrigger"] = "PressDestroy";
        // Loop on, off or flipped is three more transport actions of ours.
        aliases["ButtonLoopSet"] = "ButtonPlaybackAction";

        aliases["GrabbableReparentBlock"] = "ReleaseReturnBlock";
        aliases["DestroyRoot"] = "DestroyRootMarker";
        aliases["Float2ToFloat3SwizzleDriver"] = "Float2ToFloat3Remap";
        aliases["ConvertibleFloatDriver"] = "NumberToFloat";
        aliases["UserDistanceValueDriver"] = "ProximityValue";

        // The media player's plumbing: an asset kept referenced, a reference handed on as another kind, and how
        // many people here have the video loaded.
        aliases["AssetProxy"] = "AssetHolder";
        aliases["ReferenceCast"] = "CastReference";
        aliases["UsersAssetLoadProgress"] = "AssetLoadTally";
        // A picture's size as a float2. Ours of the package's name sizes a quad from an image, a different job.
        aliases["TextureSizeDriver"] = "TextureSizeValue";

        // The world's people setup comes across as what it hands them: its locomotions. Their bodies are built by
        // the world they arrive in, here as everywhere.
        aliases["CommonAvatarBuilder"] = "WorldLocomotions";
        aliases["GrabWorldLocomotion"] = "WorldGrabLocomotion";

        // Sizes a locomotion's capsule to the wearer's head height. Ours is the person's own body, which sizes
        // itself; the capsule beside the locomotion is left out with it (WorldUi.Finish).
        Carried.Add("SingleShapeCharacterControllerManager");
        // A rigged model's list of its bones. Ours is the skeleton built from the renderers' own bone lists.
        Carried.Add("Rig");
        // How a player is told what to show when one is spawned for a video somebody imported. Ours spawns its own
        // player for that, and inside a world this does nothing at all.
        Carried.Add("VideoPlayerInterface");
        // The picture an item's card shows when saved. Ours takes that picture itself, and a picture item's card
        // is already the picture.
        Carried.Add("ItemTextureThumbnailSource");
        // Where a session's picture is taken from. Ours is the host's own view.
        Carried.Add("SessionCaptureThumbnailSource");
        // Saving a texture or a model out to a file from a menu, which has no counterpart here.
        Carried.Add("TextureExportable");
        Carried.Add("ModelExportable");
    }

    // Arguments only this family meets.
    private static Type? ArgumentUi(string raw, string simple)
    {
        switch (simple)
        {
            // What a transport control drives is anything that plays; ours keep it as the component.
            case "IPlayable":
                return typeof(Component);
            // Anything whose changes something listens for.
            case "IChangeable":
                return typeof(IWorldElement);
        }
        // An asset named bare, as a holder or a load tally takes it.
        if (AssetType(simple) is { } asset)
            return asset;
        // One of ours under the name we gave it, closed over its own arguments.
        if (Aliases.TryGetValue(simple, out var alias) && FindType(SimpleName(alias)) is { } found)
            return found.ContainsGenericParameters ? Close(found, GenericArguments(raw, raw: true), false) : found;
        return null;
    }
}

public static partial class PackageMemberMapper
{
    // A stored member with this name never lands; ApplyUi takes it, or it has nothing to land on.
    private const string TakenUi = "\u0000ui";

    static partial void MemberAliasesUi(Dictionary<string, Dictionary<string, string>> table)
    {
        // A package checkbox's own state, or the field it shows and flips in its place.
        WorldUi.Alias(table, "Checkbox", ("State", "IsChecked"), ("TargetState", "StateField"));
        WorldUi.Alias(table, "GradientPanel", ("TintTopLeft", "TopLeft"), ("TintTopRight", "TopRight"),
            ("TintBottomRight", "BottomRight"), ("TintBottomLeft", "BottomLeft"));
        WorldUi.Alias(table, "ArcSegment", ("Arc", "ArcLength"), ("Offset", TakenUi), ("OuterRadiusRatio", "OuterRadius"),
            ("InnerRadiusRatio", "InnerRadius"), ("RoundedCornerRadius", "CornerRadius"), ("FillColor", "Tint"));
        WorldUi.Alias(table, "PressDestroy", ("OnPressed", TakenUi), ("OnPressing", TakenUi), ("OnReleased", TakenUi));
        WorldUi.Alias(table, "ReleaseReturnBlock", ("DontReparent", "Blocks"), ("MaxDepth", "Depth"));
        WorldUi.Alias(table, "SphereAligner", ("AutoAddIgnoreTags", "IgnoreTags"), ("DistributionOffset", "Offset"),
            ("AlignToNormal", "FaceOutward"), ("NormalizedStart", "Start"), ("NormalizedEnd", "End"), ("Items", TakenUi));
        WorldUi.Alias(table, "Float2ToFloat3Remap", ("X", TakenUi), ("Y", TakenUi), ("Z", TakenUi));
        WorldUi.Alias(table, "ProximityValue`1", ("Node", "Point"), ("TargetField", "Target"), ("NearValue", "Near"),
            ("FarValue", "Far"));
        WorldUi.Alias(table, "AssetHolder`1", ("AssetReference", "Asset"));
        WorldUi.Alias(table, "TextureSizeValue", ("DriveMode", "Mode"));
        WorldUi.Alias(table, "AssetLoadTally`1", ("ProgressInfo", TakenUi), ("UsersNotLoaded", "NotLoaded"),
            ("UsersLoading", "Loading"), ("UsersPartiallyLoaded", "PartiallyLoaded"), ("UsersFullyLoaded", "FullyLoaded"),
            ("UsersFailedToLoad", "Failed"));
        WorldUi.Alias(table, "WorldLocomotions", ("LocomotionModules", "Modules"), ("ForceDefaultLocomotionModule", "Default"),
            ("FindUserPreferredModule", "KeepPreferred"));
        WorldUi.Alias(table, "WorldGrabLocomotion", ("_visual", "Visual"), ("Turn", TakenUi));
        WorldUi.Alias(table, "PhysicalLocomotion", ("GripVelocityMultiplier", "FlingScale"), ("Turn", TakenUi));
        WorldUi.Alias(table, "NoclipLocomotion", ("Turn", TakenUi));
        // A caption's colour lands on a high-range field or a plain one, whichever its target is (ApplyUi).
        WorldUi.Alias(table, "ValueCaption`1", ("Color", TakenUi));
        WorldUi.Alias(table, "ReferenceCaption`1", ("Color", TakenUi));
        // The playhead is read whole in ApplyUi.
        WorldUi.Alias(table, "VideoTextureProvider", ("Playback", TakenUi));
    }

    static partial void ApplyUi(Component component, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList, Func<Uri, Uri?> remapUrl)
    {
        if (component.GetType() is { IsGenericType: true } generic
            && (generic.GetGenericTypeDefinition() == typeof(ValueCaption<>) || generic.GetGenericTypeDefinition() == typeof(ReferenceCaption<>)))
            WorldUi.CaptionTint(component, data, queueReference);
        switch (component)
        {
            case Helio.UI.Layout.AspectRatioConstraint fitter when data.TryGetNode("Mode") == null:
                // The package's fitter shrinks the rect to the ratio inside it, about its pivot.
                fitter.Mode.Value = Helio.UI.Layout.AspectRatioConstraint.AspectMode.FitInParent;
                break;
            case Helio.UI.ArcSegment arc when data.TryGetNode("OuterRadiusRatio") != null:
                WorldUi.Ring(arc, data);
                break;
            case PressDestroy press when data.TryGetNode("OnPressed") != null:
                WorldUi.PressCall(press, data, queueReference);
                break;
            // A picture turned half round is ours flipped both ways: the rewind arrow drawn with the fast-forward one.
            // A quarter turn has no counterpart and stays upright.
            case Helio.UI.RawImage raw when WorldUi.Plain(data, "Orientation") is "UpsideDown180":
                raw.FlipHorizontal.Value = !raw.FlipHorizontal.Value;
                raw.FlipVertical.Value = !raw.FlipVertical.Value;
                break;
            case Float2ToFloat3Remap remap:
                remap.X.Value = WorldUi.Channel(data, "X", RemapChannel.X);
                remap.Y.Value = WorldUi.Channel(data, "Y", RemapChannel.Y);
                remap.Z.Value = WorldUi.Channel(data, "Z", RemapChannel.Zero);
                break;
            case PhysicalLocomotion walk when data.TryGetNode("Gripping") != null:
                walk.Climb.Value = WorldUi.Plain(data, "Gripping") is string grip && grip != "None";
                break;
            // A link to a page or a video service points out of the package on purpose, and is kept rather than
            // dropped like an asset that would have to be fetched. The generic pass leaves a member it cannot
            // remap alone, so this one stands.
            case VideoTextureProvider video:
                WorldUi.Address(video.URL, data, remapUrl);
                if (data.TryGetNode("Playback") is DataTreeDictionary playback)
                    WorldUi.Playhead(video, playback);
                break;
            case WebLink link:
                WorldUi.Address(link.URL, data, remapUrl);
                break;
        }
    }
}

internal static class WorldUi
{
    // THE PACKAGE'S INTERFACE PALETTE. A world's panels copy their colours from a palette component that fills
    // itself from the platform's own colour set at run time, so the package stores every entry empty and nothing
    // here can build it. The set is fixed, so every entry is a constant: a copy that reads one reads it from a
    // holder of that constant beside the copy, at the type the copy reads, and lands the same colour it did there.
    private static readonly Dictionary<string, string> PaletteHex = new(StringComparer.Ordinal)
    {
        ["Neutrals.Dark"] = "#11151d", ["Neutrals.Mid"] = "#2b2f35", ["Neutrals.MidLight"] = "#86888b", ["Neutrals.Light"] = "#e1e1e0",
        ["Hero.Yellow"] = "#f8f770", ["Hero.Green"] = "#59eb5c", ["Hero.Red"] = "#ff7676",
        ["Hero.Purple"] = "#ba64f2", ["Hero.Cyan"] = "#61d1fa", ["Hero.Orange"] = "#e69e50",
        ["Mid.Yellow"] = "#a0a14e", ["Mid.Green"] = "#3f9e44", ["Mid.Red"] = "#ae5458",
        ["Mid.Purple"] = "#824aab", ["Mid.Cyan"] = "#458fab", ["Mid.Orange"] = "#976c3d",
        ["Sub.Yellow"] = "#484a2c", ["Sub.Green"] = "#24512c", ["Sub.Red"] = "#5d323a",
        ["Sub.Purple"] = "#492f64", ["Sub.Cyan"] = "#284c5d", ["Sub.Orange"] = "#48392a",
        ["Dark.Yellow"] = "#2b2e26", ["Dark.Green"] = "#192d24", ["Dark.Red"] = "#1a1318",
        ["Dark.Purple"] = "#241e35", ["Dark.Cyan"] = "#1a2a36", ["Dark.Orange"] = "#292423",
    };

    // Per import: the palette entries met, by the id their member carried, and the holders made for them.
    private sealed class Palette
    {
        public readonly Dictionary<string, string> Entries = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<(Slot, string, Type), IWorldElement> Holders = new();
    }

    private static readonly ConditionalWeakTable<PackageNodeInput, Palette> Palettes = new();

    public static void CollectPalette(PackageNodeInput import, DataTreeDictionary data)
    {
        var palette = Palettes.GetOrCreateValue(import);
        foreach (var (group, node) in data.Children)
        {
            if (node is not DataTreeDictionary entries)
                continue;
            foreach (var (name, member) in entries.Children)
            {
                if (member is not DataTreeDictionary field || field.TryGetNode("ID") is not DataTreeValue { Value: string id } || id.Length == 0)
                    continue;
                bool hex = name.EndsWith("Hex", StringComparison.Ordinal);
                string key = group + "." + (hex ? name[..^3] : name);
                if (PaletteHex.ContainsKey(key))
                    palette.Entries[id] = hex ? key + "#" : key;
            }
        }
    }

    // The colour a palette entry holds as its hex code, and whether the entry is that code as text, for a script
    // that reads one: the same constant a copy's holder carries.
    public static bool TryPaletteEntry(PackageNodeInput import, string? id, out string code, out bool hex)
    {
        code = string.Empty;
        hex = false;
        if (id == null || !Palettes.TryGetValue(import, out var palette) || !palette.Entries.TryGetValue(id, out var entry))
            return false;
        hex = entry.EndsWith('#');
        code = PaletteHex[hex ? entry[..^1] : entry];
        return true;
    }

    // True when the link was to a palette entry and now reads a holder of its colour (or its hex code).
    public static bool TakePaletteLink(PackageNodeInput import, ISyncRef reference, string id)
    {
        if (!Palettes.TryGetValue(import, out var palette) || !palette.Entries.TryGetValue(id, out var entry))
            return false;
        bool hex = entry.EndsWith('#');
        string code = PaletteHex[hex ? entry[..^1] : entry];
        var wanted = reference.TargetType;
        var held = wanted.IsGenericType ? wanted.GetGenericArguments()[0] : null;
        IWorldElement? owner = reference as IWorldElement;
        while (owner is Networking.Sync.SyncElement element && owner is not Component)
            owner = element.Parent;
        if (owner is not Component { Slot: { IsDestroyed: false } slot } || held == null)
            return false;
        if (!palette.Holders.TryGetValue((slot, entry, held), out var field))
        {
            object? value = held == typeof(string) ? code
                : held == typeof(color) ? color.FromHex(code)
                : held == typeof(colorHDR) ? colorHDR.FromHex(code)
                : null;
            if (value == null)
                return false;
            var holder = slot.AttachComponent(typeof(ValueHolder<>).MakeGenericType(held));
            var valueField = holder.GetType().GetField("Value")!.GetValue(holder)!;
            valueField.GetType().GetProperty("Value")!.SetValue(valueField, value);
            field = (IWorldElement)valueField;
            palette.Holders[(slot, entry, held)] = field;
        }
        reference.TrySet(field);
        return true;
    }

    // The members inside the records of a list (a gradient's stops, each with its position and colour) carry ids
    // of their own, which a copy or a driver can name. Ours are the same fields on the same records, by position.
    public static void RecordListEntries(Dictionary<string, IWorldElement> byId, Component component, DataTreeDictionary data)
    {
        foreach (var (name, node) in data.Children)
        {
            if (node is not DataTreeDictionary member || member.TryGetNode("Data") is not DataTreeList stored)
                continue;
            string ours = PackageMemberMapper.ResolveMemberName(component, name);
            if (component.GetType().GetField(ours)?.GetValue(component) is not Networking.Sync.ISyncList list)
                continue;
            for (int i = 0; i < stored.Count && i < list.Count; i++)
            {
                if (stored[i] is not DataTreeDictionary item || list.GetElement(i) is not { } entry)
                    continue;
                var record = item.TryGetNode("Data") as DataTreeDictionary ?? item;
                foreach (var (fieldName, fieldNode) in record.Children)
                {
                    if (fieldNode is not DataTreeDictionary field || field.TryGetNode("ID") is not DataTreeValue { Value: string id } || id.Length == 0)
                        continue;
                    if (entry.GetType().GetField(fieldName)?.GetValue(entry) is IWorldElement found)
                        byId.TryAdd(id, found);
                }
            }
        }
    }

    public static void Alias(Dictionary<string, Dictionary<string, string>> table, string ours, params (string Theirs, string Ours)[] pairs)
    {
        if (!table.TryGetValue(ours, out var names))
            table[ours] = names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (theirs, mine) in pairs)
            names[theirs] = mine;
    }

    public static DataTreeNode? Stored(DataTreeDictionary data, string member)
        => (data.TryGetNode(member) as DataTreeDictionary)?.TryGetNode("Data");

    public static float? Number(DataTreeDictionary data, string member)
        => Plain(data, member) is IConvertible value && value is not string and not bool
            ? Convert.ToSingle(value, CultureInfo.InvariantCulture) : null;

    // A member's value whether it sits wrapped with its id or bare, as the parts of a record do.
    public static object? Plain(DataTreeDictionary data, string member)
    {
        var node = data.TryGetNode(member);
        if (node is DataTreeDictionary wrapped)
            node = wrapped.TryGetNode("Data");
        return (node as DataTreeValue)?.Value;
    }

    // A record member's own members, wrapped with its id or not.
    public static DataTreeDictionary? Record(DataTreeDictionary data, string member)
    {
        if (data.TryGetNode(member) is not DataTreeDictionary node)
            return null;
        return node.TryGetNode("Data") as DataTreeDictionary ?? node;
    }

    // A web address kept as it is; one into the package goes to the local copy, and none is no address.
    public static void Address(Sync<Uri> field, DataTreeDictionary data, Func<Uri, Uri?> remapUrl)
    {
        Uri? uri = Plain(data, "URL") switch
        {
            Uri stored => stored,
            string text when text.Length > 0 => Uri.TryCreate(text.StartsWith('@') && !text.StartsWith("@@", StringComparison.Ordinal)
                ? text[1..] : text, UriKind.Absolute, out var parsed) ? parsed : null,
            _ => null,
        };
        if (uri == null)
            return;
        if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            field.Value = uri;
        else if (remapUrl(uri) is { } local)
            field.Value = local;
    }

    // A member the package does not save carries only its id, as "<name>-ID". Something can still point at it: a
    // variable bound to a hover flag, a driver reading a count. Kept so those references find our member of the
    // same name.
    public static void RecordUnsaved(PackageNodeInput graphs, IWorldElement owner, DataTreeDictionary data)
    {
        foreach (var (name, value) in data.Children)
        {
            if (name.Length <= 3 || !name.EndsWith("-ID", StringComparison.Ordinal)
                || value is not DataTreeValue { Value: string id } || id.Length == 0 || PackageMemberMapper.IsNullId(id))
                continue;
            graphs.Members.TryAdd(id, new PackageMemberRecord(owner, name[..^3], -1));
        }
    }

    // A caption's colour drives whatever field it named: a high-range one with the caption's tint, a plain one (a
    // button's base colour, an image's tint) with the same colour as a plain one.
    public static void CaptionTint(Component caption, DataTreeDictionary data, Action<ISyncRef, string> queueReference)
    {
        if (Plain(data, "Color") is not string id || id.Length == 0 || PackageMemberMapper.IsNullId(id))
            return;
        var type = caption.GetType();
        if (type.GetField("Tint")?.GetValue(caption) is not FieldDrive<colorHDR> tint
            || type.GetField("PlainTint")?.GetValue(caption) is not FieldDrive<color> plain)
            return;
        queueReference(new Arrival(element =>
        {
            if (caption.IsDestroyed)
                return false;
            switch (element)
            {
                case IField<colorHDR> high:
                    tint.DriveTarget(high);
                    return true;
                case IField<color> low:
                    plain.DriveTarget(low);
                    return true;
                default:
                    return false;
            }
        }), id);
    }

    // The package's ring measures its radii in shares of the rect and starts its sweep a half turn round from ours.
    public static void Ring(Helio.UI.ArcSegment arc, DataTreeDictionary data)
    {
        arc.RadiiFromRect.Value = true;
        float offset = Number(data, "Offset") ?? 0f;
        float start = (offset + 180f) % 360f;
        arc.AngleStart.Value = start < 0f ? start + 360f : start;
    }

    // A press that calls a method on something. Destroying a slot is ours; anything else has no counterpart and the
    // press is left switched off rather than destroying what it was never meant to.
    public static void PressCall(PressDestroy press, DataTreeDictionary data, Action<ISyncRef, string> queueReference)
    {
        press.DestroyWholeObject.Value = false;
        var call = Record(data, "OnPressed");
        string? method = call != null && Plain(call, "Method") is string m ? m : null;
        string? target = call != null && Plain(call, "Target") is string t && t.Length > 0
            && !PackageMemberMapper.IsNullId(t) ? t : null;
        // Off until the slot it names arrives: with no target ours destroys its own slot, which is never what a
        // press that pointed elsewhere meant.
        press.Enabled.Value = false;
        if (target != null && method is "Destroy" or "DestroyPreservingAssets")
        {
            queueReference(new Arrival(element =>
            {
                if (element is not Slot slot || press.IsDestroyed)
                    return false;
                press.Target.Target = slot;
                press.Enabled.Value = true;
                return true;
            }), target);
            return;
        }
        if (method != null)
            Logger.Log($"Package import: a press on '{press.Slot?.Name.Value}' calls {method}, which a press here does not run; left off");
    }

    // A reference the importer resolves like any other, its target handed on.
    internal sealed class Arrival : ISyncRef
    {
        private readonly Func<IWorldElement, bool> _then;

        public Arrival(Func<IWorldElement, bool> then) => _then = then;

        public RefID Value { get => default; set { } }
        public IWorldElement Target { get => null!; set { } }
        public IWorldElement RawTarget => null!;
        public Type TargetType => typeof(IWorldElement);
        public ReferenceState State => default;
        public void Clear() { }
        public bool TrySet(IWorldElement target) => target != null && _then(target);
    }

    public static RemapChannel Channel(DataTreeDictionary data, string member, RemapChannel fallback)
    {
        if (Plain(data, member) is not IConvertible value || value is string)
            return fallback;
        return Convert.ToInt32(value, CultureInfo.InvariantCulture) switch
        {
            0 => RemapChannel.X,
            1 => RemapChannel.Y,
            _ => RemapChannel.Zero,
        };
    }

    // Playing or not, looping or not, where the head was and how fast it went, in one member there.
    public static void Playhead(VideoTextureProvider video, DataTreeDictionary playback)
    {
        var state = playback.TryGetNode("Data") as DataTreeDictionary ?? playback;
        playback = state;
        bool play = Plain(playback, "Play") is true;
        bool loop = Plain(playback, "Loop") is true;
        float position = Number(playback, "RawPosition") ?? 0f;
        float speed = Number(playback, "Speed") ?? 1f;
        if (!float.IsFinite(position) || position < 0f)
            position = 0f;
        if (!float.IsFinite(speed))
            speed = 1f;
        video.Playback.SetLoadedState(play, loop ? PlaybackLoopMode.Loop : PlaybackLoopMode.Once, position, speed);
    }

    // A material of its own on a panel picture (a video shown through a projection) is culled by the panel's rule.
    // The package marks which face of a world mesh draws; a panel's pictures are built here, facing the way the
    // panel reads, so that mark names the wrong face as often as the right one, and the picture vanished from
    // the side it is read from. Left alone when a mesh in the world wears the same material.
    private static void PanelMaterials(Slot root)
    {
        var onMeshes = new HashSet<IWorldElement>();
        foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>())
        {
            // A panel's own pieces are the panel's meshes, not the world's.
            if (renderer.Slot.GetComponentInParents<Helio.UI.Canvas>() != null)
                continue;
            for (int i = 0; i < renderer.Materials.Count; i++)
            {
                if (renderer.Materials[i] is IWorldElement material)
                    onMeshes.Add(material);
            }
        }
        foreach (var component in root.GetComponentsInChildren<Component>())
        {
            IAssetProvider<MaterialAsset>? material = component switch
            {
                Helio.UI.Image image => image.Material.Target,
                Helio.UI.RawImage raw => raw.Material.Target,
                _ => null,
            };
            if (material is not MaterialProvider provider || provider is UIUnlitMaterial or UITextMaterial || onMeshes.Contains(provider)
                || component.Slot.GetComponentInParents<Helio.UI.Canvas>() is not { } canvas)
                continue;
            if (provider.GetType().GetField("Culling")?.GetValue(provider) is Sync<Culling> culling && culling.Value != canvas.Culling.Value)
                culling.Value = canvas.Culling.Value;
        }
    }

    // A walk that flies is our walk flying. Anything else that is not walking on the ground has no counterpart.
    public static bool Flies(PhysicalLocomotion walk, string mode)
    {
        if (mode != "DirectionalForce")
            return false;
        walk.Fly.Value = true;
        return true;
    }

    // After the build: each locomotion's own capsule goes, since the person's body is the capsule here; and a
    // second spawner goes when the world already spawns people, or everyone arriving would get two bodies.
    public static void Finish(Slot root, IEnumerable<(Slot Slot, string Type, DataTreeDictionary Data)> locomotion)
    {
        foreach (var (slot, _, _) in locomotion)
        {
            if (slot.IsDestroyed || slot.GetComponent<LocomotionModule>() == null)
                continue;
            foreach (var capsule in new List<CapsuleCollider>(slot.GetComponents<CapsuleCollider>()))
            {
                if (capsule.Type.Value == Physics.ColliderType.CharacterController)
                    capsule.Destroy();
            }
        }

        PanelMaterials(root);

        var world = root.World;
        if (world == null)
            return;
        bool spawnsAlready = false;
        foreach (var spawn in world.RootSlot.GetComponentsInChildren<SimpleUserSpawn>())
        {
            if (!spawn.IsDestroyed && !spawn.Slot.IsDescendantOf(root))
            {
                spawnsAlready = true;
                break;
            }
        }
        if (!spawnsAlready)
            return;
        foreach (var spawn in new List<SimpleUserSpawn>(root.GetComponentsInChildren<SimpleUserSpawn>()))
        {
            Logger.Log($"Package import: '{spawn.Slot.Name.Value}' spawns people, and this world already does; left out");
            spawn.Destroy();
        }
    }
}
