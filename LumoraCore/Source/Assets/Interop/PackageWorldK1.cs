// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Magnets;
using Lumora.Core.Components.Touch;
using Lumora.Core.Components.Utility;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// A world's interaction parts: buttons and what they do, text fields, snapping, things set down and handed out,
// and the markers on what may be destroyed or copied. -xlinka
public static partial class PackageTypeMap
{
    static partial void AliasesK1(Dictionary<string, string> aliases)
    {
        // Buttons that send to receivers in node graphs by tag. Joined into those graphs as the press that starts
        // them (PackageNodeTable.Buttons), so they are not attached.
        JoinsGraphs.Add("ButtonDynamicImpulseTrigger");
        JoinsGraphs.Add("ButtonDynamicImpulseTriggerWithValue");
        JoinsGraphs.Add("ButtonDynamicImpulseTriggerWithReference");

        // The editor beside a text field is our TextInput, which takes its own presses and focus.
        Carried.Add("TextField");
        // Sets the signed-in account's picture from the dash, and does nothing inside a world.
        Carried.Add("ButtonUserProfileIconSet");
        // Where a hand closes on an equipped tool; ours hold a tool where it was gripped.
        Carried.Add("GripPoseReference");

        aliases["PhysicalButton"] = "PlungerButton";
        aliases["TextEditor"] = "TextInput";
        aliases["FloatTextEditorParser"] = "TextInputFloatParser";
        aliases["ButtonEditColorX"] = "PressEditColor";
        aliases["ButtonClipboardCopyText"] = "PressCopyText";

        // Snapping is the magnet family: what snaps and what it snaps into.
        aliases["Snapper"] = "Magnet";
        aliases["SnapTarget"] = "MagnetSocket";

        aliases["GrabbableReceiverSurface"] = "DropSurface";
        aliases["GrabInstancer"] = "GrabSpawner";
        aliases["RawDataTool"] = "ScriptedTool";
        aliases["DestroyProxy"] = "DestroyAlongside";
        aliases["Joint"] = "GrabHinge";
        aliases["CharacterParenter"] = "UserCarrier";
        aliases["ChildrenSaveBlocker"] = "ChildrenSaveBlock";
        // While somebody holds the object, the ways a held thing is filed away (the held menu's save, the
        // inventory's save of what is in hand) refuse it. A world save takes it like anything else.
        aliases["GrabbableSaveBlock"] = "GrabSaveBlock";
        // A buzz zone and the hand-speed response the package keeps beside it are one zone here
        // (PackageMemberMapper.ApplyK1); the second of them on a slot goes as a zone allows one per slot.
        aliases["HapticVolume"] = "HapticZone";
        aliases["VelocityHapticFilter"] = "HapticZone";
    }
}

public static partial class PackageMemberMapper
{
    static partial void MemberAliasesK1(Dictionary<string, Dictionary<string, string>> table)
    {
        // Members listed as WorldK1.Taken are read by ApplyK1 instead: they need converting, or the name lands
        // on a different member of ours.
        WorldK1.Alias(table, "PlungerButton", ("AcceptPhysicalTouch", "AcceptFingertipTouch"), ("PressDepth", "Depth"),
            ("_buttonOffset", "RestOffset"), ("_currentPressingDepth", "CurrentDepth"));
        WorldK1.Alias(table, "TextInput", ("Text", WorldK1.Taken));
        WorldK1.Alias(table, "TextInputFloatParser", ("UpdateStringFromValue", "UpdateTextFromValue"), ("StringFormat", "Format"));
        WorldK1.Alias(table, "Magnet", ("UseBoundingBoxCenter", "UseBoundsCenter"), ("SnapCheckRadius", WorldK1.Taken),
            ("SnapTargetWhitelist", "SocketWhitelist"), ("Keywords", "Tags"), ("CheckStaticColliders", WorldK1.Taken));
        WorldK1.Alias(table, "MagnetSocket", ("SnapperWhitelist", "MagnetWhitelist"), ("SnapperKeywordWhitelist", "TagWhitelist"),
            ("MaximumSnapDistance", "MaxDistance"), ("MaximumAngleDeviation", WorldK1.Taken), ("AnimationTime", "SettleTime"),
            ("AutoSnap", "AutoAttach"), ("Filters", WorldK1.Taken), ("DirectSnapOnly", "ListedOnly"),
            ("_snapColliderRadius", WorldK1.Taken), ("proxySphere", WorldK1.Taken));
        WorldK1.Alias(table, "DropSurface", ("MaxDistance", WorldK1.Taken), ("Offset", "Lift"), ("ParentPlaced", "ParentDropped"),
            ("OverrideParent", "ParentUnder"), ("Directions", WorldK1.Taken), ("TweenTime", "GlideTime"), ("TagFilter", WorldK1.Taken));
        WorldK1.Alias(table, "GrabSpawner", ("ActivateRoot", "ActivateInstance"), ("SetInstancePersistent", WorldK1.Taken),
            ("Physical", "PhysicalOnly"));
        // A grabbable's release and its per-person gates. The package's restore bookkeeping is a moment of
        // someone else's session and is not carried.
        WorldK1.Alias(table, "Grabbable", ("ReparentOnRelease", "ReturnToParent"), ("ActiveUserFilter", WorldK1.Taken),
            ("GrabPriorityWhenGrabbed", WorldK1.Taken), ("OnlyUsers", WorldK1.Taken), ("_lastParent", WorldK1.Taken),
            ("_lastParentIsUserSpace", WorldK1.Taken), ("PreserveUserSpace", WorldK1.Taken), ("CustomCanGrabCheck", WorldK1.Taken));
        WorldK1.Alias(table, "ScriptedTool", ("UseLaser", "UsesBeam"), ("UseSecondary", "ClaimsSecondary"),
            ("AllowUseWhenHolding", "UsableWhileHolding"));
        WorldK1.Alias(table, "DestroyAlongside", ("DestroyTarget", "Target"));
        WorldK1.Alias(table, "GrabHinge", ("_grabber", "GrabberRef"), ("TwistReferenceAxis", "TwistReference"),
            ("PositionTwistThresholdAngle", "FollowPointAngle"), ("SnapIncrement", "SnapStep"), ("SnapOrientations", WorldK1.Taken),
            ("ActiveUserFilter", WorldK1.Taken), ("VibrationPreset", WorldK1.Taken), ("VibrationAngle", "HapticStep"),
            ("DontDrive", WorldK1.Taken), ("CustomGrabCheck", WorldK1.Taken), ("SnapInterpolationCurve", WorldK1.Taken),
            ("_holdSlot", WorldK1.Taken), ("_pos", WorldK1.Taken), ("_rot", WorldK1.Taken), ("_scl", WorldK1.Taken),
            ("origRotation", WorldK1.Taken), ("rotReference", WorldK1.Taken), ("dirReference", WorldK1.Taken),
            ("twistReference", WorldK1.Taken), ("usePositionTwist", WorldK1.Taken));
        WorldK1.Alias(table, "HapticZone", ("VelocitySmoothTime", WorldK1.Taken), ("StartVelocity", WorldK1.Taken),
            ("EndVelocity", WorldK1.Taken), ("StartIntensity", WorldK1.Taken), ("EndIntensity", WorldK1.Taken), ("Power", WorldK1.Taken),
            ("Intensity", WorldK1.Taken));
    }

    static partial void ApplyK1(Component component, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList, Func<Uri, Uri?> remapUrl)
    {
        switch (component)
        {
            case PlungerButton plunger when data.TryGetNode("PressDepth") != null:
                WorldK1.PhysicalButton(plunger, data);
                break;
            case Helio.UI.TextInput input when data.TryGetNode("FinishHandling") != null:
                WorldK1.TextEditor(input, data, queueReference);
                break;
            case Magnet magnet when data.TryGetNode("SnapCheckRadius") != null:
                // A package snapper only ever snaps into targets, never onto a guide shape. Its check radius
                // only finds targets; how far one reaches is the target's own distance, so ours adds none.
                // Measured from its bounds' middle or not, its own origin is what lands.
                magnet.UseGuides.Value = false;
                magnet.Radius.Value = 0f;
                magnet.BoundsCenterLands.Value = false;
                break;
            case MagnetSocket socket when data.TryGetNode("MaximumSnapDistance") != null:
                if (WorldK1.Number(data, "MaximumAngleDeviation") is { } angle)
                    socket.MaxAngle.Value = System.Math.Clamp(angle, 0f, 180f);
                // A package target settles what it takes at its own scale.
                socket.ResetItemScale.Value = true;
                break;
            case Components.Grabbable grabbable when data.TryGetNode("ReparentOnRelease") != null:
                WorldK1.Grab(grabbable, data);
                break;
            case DropSurface surface when data.TryGetNode("Directions") != null:
                WorldK1.ReceiverSurface(surface, data);
                break;
            case GrabSpawner spawner when data.TryGetNode("ActivateRoot") != null:
                WorldK1.Instancer(spawner, data);
                break;
            case GrabHinge hinge when data.TryGetNode("PositionTwistThresholdAngle") != null:
                WorldK1.Joint(hinge, data);
                break;
            case HapticZone zone:
                WorldK1.Haptic(zone, data);
                break;
            case Components.Interaction.GrabSaveBlock saveBlock:
                saveBlock.BlockInventorySave.Value = true;
                saveBlock.BlockWhileHeld.Value = false;
                saveBlock.BlockWorldSave.Value = false;
                break;
        }
    }

    private static class WorldK1
    {
        // A member name nothing of ours carries, for a package member ApplyK1 reads itself.
        public const string Taken = "\u0000";

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
            => Stored(data, member) is DataTreeValue { Value: IConvertible value } && value is not string and not bool
                ? Convert.ToSingle(value, CultureInfo.InvariantCulture) : null;

        public static string? Id(DataTreeDictionary data, string member)
            => Stored(data, member) is DataTreeValue { Value: string id } && id.Length > 0 && !IsNullId(id) ? id : null;

        // The package's press, start-of-press and hover buzzes onto the control's contact and hover feel.
        public static void PhysicalButton(PlungerButton plunger, DataTreeDictionary data)
        {
            if ((Haptic(data, "PressVibration") ?? Haptic(data, "BeginPressVibration")) is { } contact)
                plunger.ContactHaptics.Value = contact;
            if (Haptic(data, "HoverVibration") is { } hover)
                plunger.HoverHaptics.Value = hover;
        }

        private static TouchHaptics? Haptic(DataTreeDictionary data, string member) => Stored(data, member) is DataTreeValue { Value: string preset }
            ? preset switch
            {
                "None" => TouchHaptics.None,
                "Short" => TouchHaptics.Light,
                "Medium" => TouchHaptics.Medium,
                "Long" => TouchHaptics.Strong,
                _ => null,
            }
            : null;

        // The package's editor edits a Text component's content; ours keeps the value itself and shows it on that
        // text, so the value starts as what the text held and the text is driven from then on. Until the text is
        // found the field must not fall back on a child named Text, or it empties that text before it is read. -xlinka
        public static void TextEditor(Helio.UI.TextInput input, DataTreeDictionary data, Action<ISyncRef, string> queueReference)
        {
            if (Id(data, "Text") is not { } id)
                return;
            input.ContentDrive.ValueCameFromData = true;
            queueReference(new Resolved(target =>
            {
                if (target is not Helio.UI.Text text || text.IsDestroyed || input.IsDestroyed)
                    return false;
                input.Text.Value = text.Content.Value;
                input.ContentDrive.DriveTarget(text.Content);
                return true;
            }), id);
        }

        // Every direction in order, the first as Direction and the rest after it; how far it looks, unlimited
        // when the package's is; and the tag filter either way round.
        public static void ReceiverSurface(DropSurface surface, DataTreeDictionary data)
        {
            if (Number(data, "MaxDistance") is { } reach)
                surface.Reach.Value = float.IsFinite(reach) ? System.Math.Max(reach, 0f) : float.MaxValue;
            if (Stored(data, "Directions") is DataTreeList directions)
            {
                surface.ExtraDirections.Clear();
                bool first = true;
                foreach (var entry in directions)
                {
                    var value = (entry as DataTreeDictionary)?.TryGetNode("Data") ?? entry;
                    if (value is not DataTreeList vector || ReadNumbers(vector) is not { Length: >= 3 } d)
                        continue;
                    var direction = PackageHandedness.Point(new float3(d[0], d[1], d[2]));
                    if (!(direction.LengthSquared > 1e-12f))
                        continue;
                    if (first)
                        surface.Direction.Value = direction;
                    else
                        surface.ExtraDirections.Add(direction);
                    first = false;
                }
            }
            if (data.TryGetNode("TagFilter") is DataTreeDictionary filter
                && (filter.TryGetNode("List") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeList tags)
            {
                bool whitelist = (filter.TryGetNode("Mode") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: "Whitelist" };
                var list = whitelist ? surface.TagWhitelist : surface.TagBlacklist;
                list.Clear();
                foreach (var entry in tags)
                {
                    if (((entry as DataTreeDictionary)?.TryGetNode("Data") ?? entry) is DataTreeValue { Value: string tag } && tag.Length > 0)
                        list.Add(tag);
                }
            }
        }

        // Who may pick it up by whether it sits under a person, in our filter's terms.
        private static GrabUserFilter? UserFilter(DataTreeDictionary data) => Stored(data, "ActiveUserFilter") is DataTreeValue { Value: string mode }
            ? mode switch
            {
                "Disabled" => GrabUserFilter.Anyone,
                "OnlyActiveUser" => GrabUserFilter.WearerOnly,
                "ActiveUserWhenPresent" => GrabUserFilter.WearerWhenWorn,
                "ExcludeActiveUser" => GrabUserFilter.NotWearer,
                _ => null,
            }
            : null;

        // A grabbable's per-person gate and the priority it competes with once held. The people it was
        // limited to were people of another session, so a list of them is left empty here.
        public static void Grab(Components.Grabbable grabbable, DataTreeDictionary data)
        {
            if (UserFilter(data) is { } filter)
                grabbable.UserFilter.Value = filter;
            if (Number(data, "GrabPriorityWhenGrabbed") is { } held && float.IsFinite(held))
                grabbable.HeldGrabPriority.Value = (int)held;
        }

        // A package joint: its poses each with its own reach, the hand tick, and who may turn it.
        public static void Joint(GrabHinge hinge, DataTreeDictionary data)
        {
            if (Stored(data, "SnapOrientations") is DataTreeList poses)
                HingePoses(hinge, poses);
            if (UserFilter(data) is { } filter)
                hinge.UserFilter.Value = filter;
            if (Stored(data, "VibrationPreset") is DataTreeValue { Value: string preset })
            {
                hinge.TurnHaptics.Value = preset switch
                {
                    "Short" => TouchHaptics.Light,
                    "Medium" => TouchHaptics.Medium,
                    "Long" => TouchHaptics.Strong,
                    _ => TouchHaptics.None,
                };
            }
        }

        // A package hands the copy out where its template stands, beside the dispenser rather than at the world
        // root, keeps no count and takes nothing back. No saving set at all keeps the template's own.
        public static void Instancer(GrabSpawner spawner, DataTreeDictionary data)
        {
            spawner.ResetTemplateTransform.Value = false;
            spawner.DestroyOnReturn.Value = false;
            spawner.MaxInstances.Value = 0;
            if (spawner.Slot?.Parent is { IsDestroyed: false, IsRootSlot: false } parent)
                spawner.SpawnParent.Target = parent;
            if (Stored(data, "SetInstancePersistent") is DataTreeValue { Value: bool persistent })
                spawner.PersistInstances.Value = persistent;
            else
                spawner.KeepTemplatePersistence.Value = true;
        }

        // The poses a package joint settles onto, each with its own reach.
        public static void HingePoses(GrabHinge hinge, DataTreeList poses)
        {
            hinge.SnapRotations.Clear();
            hinge.SnapRanges.Clear();
            foreach (var entry in poses)
            {
                var record = (entry as DataTreeDictionary)?.TryGetNode("Data") as DataTreeDictionary ?? entry as DataTreeDictionary;
                if (record == null || Stored(record, "Orientation") is not DataTreeList orientation || ReadNumbers(orientation) is not { Length: >= 4 } q)
                    continue;
                hinge.SnapRotations.Add(PackageHandedness.Rotation(new floatQ(q[0], q[1], q[2], q[3])));
                hinge.SnapRanges.Add(System.Math.Clamp(Number(record, "MaxAngle") ?? 5f, 0f, 180f));
            }
        }

        // A buzz zone's strength, or the hand-speed response beside it, onto the slot's first zone: the one the
        // slot keeps.
        public static void Haptic(HapticZone zone, DataTreeDictionary data)
        {
            var kept = zone;
            if (zone.Slot != null)
            {
                foreach (var other in zone.Slot.GetComponents<HapticZone>())
                {
                    if (!ReferenceEquals(other, zone) && !other.IsDestroyed)
                    {
                        kept = other;
                        break;
                    }
                }
            }
            if (Number(data, "Intensity") is { } intensity)
                kept.Intensity.Value = System.Math.Clamp(intensity, 0f, 1f);
            if (data.TryGetNode("VelocitySmoothTime") == null)
                return;
            kept.UseSpeed.Value = true;
            if (Number(data, "VelocitySmoothTime") is { } response)
                kept.SpeedResponse.Value = response;
            if (Number(data, "StartVelocity") is { } startSpeed)
                kept.StartSpeed.Value = startSpeed;
            if (Number(data, "EndVelocity") is { } endSpeed)
                kept.EndSpeed.Value = endSpeed;
            if (Number(data, "StartIntensity") is { } startIntensity)
                kept.StartIntensity.Value = startIntensity;
            if (Number(data, "EndIntensity") is { } endIntensity)
                kept.EndIntensity.Value = endIntensity;
            if (Number(data, "Power") is { } power)
                kept.Power.Value = power;
        }

        // A reference the importer resolves like any other, its target handed on: for a member that lands on more
        // than one of ours, or on a part of what it names.
        public sealed class Resolved : ISyncRef
        {
            private readonly Func<IWorldElement, bool> _then;

            public Resolved(Func<IWorldElement, bool> then) => _then = then;

            public RefID Value { get => default; set { } }
            public IWorldElement Target { get => null!; set { } }
            public IWorldElement RawTarget => null!;
            public Type TargetType => typeof(IWorldElement);
            public ReferenceState State => default;
            public void Clear() { }
            public bool TrySet(IWorldElement target) => target != null && _then(target);
        }
    }
}
