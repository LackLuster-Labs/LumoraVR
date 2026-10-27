// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components.Utility;
using Lumora.Core.Math;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

public static partial class PackageTypeMap
{
    static partial void AliasesK2(Dictionary<string, string> aliases)
    {
        // A value per viewer. The package's override bag is our table of user entries, and the gather is the
        // same switch plus a count of who has it on.
        aliases["ValueUserOverride"] = "PerUserValue";
        aliases["BooleanUserOverrideGather"] = "PerUserBoolTally";

        // Turning to face a user. Which user, which node and the flip are set in ApplyFaceUser.
        aliases["LookAtUser"] = "FaceUser";
        aliases["AutoLookAtUser"] = "FaceUser";

        // Values that depend on where something stands: named zones, and the drivers that sample them.
        aliases["ValueSpatialVariableDriver"] = "ZoneValueDriver";
        aliases["NumericValueSpatialVariableDriver"] = "ZoneValueDriver";
        aliases["BoxConstantValueSpatialVariable"] = "BoxValueZone";
        aliases["SphereStartEndValueSpatialVariable"] = "SphereValueZone";

        // The touch that puts the toucher on an anchor (a seat here). A far touch sits straight away where the
        // package first asks in a confirm menu, and the prompt text is ours ("Sit", "Stand up").
        aliases["AvatarAnchorTouchTrigger"] = "SeatTouchTrigger";
        // A bench that takes any number of people: a seat copied from a template wherever it is touched, pressed
        // onto the plane its snap plane names (WorldK2.FinishBenches). No preview seat follows the pointer.
        aliases["MultiUserAvatarAnchor"] = "SeatSpawner";
        // That plane only ever says where along a bench a seat goes, read straight onto the spawner. It never
        // pulls anything released near it, so it is no magnet guide.
        Carried.Add("SnapPlane");

        // A clock every machine agrees on, read by scripts as a value. The package counts from its own world
        // time and ours from the session clock, so the stored offset is from another clock; scripts that
        // compare two readings of it come out the same.
        aliases["AuthorityTimeBase"] = "TimeCounter";

        // Nothing to attach, each for its own reason.
        //
        // A locale driver writes a translated string into a field, and the package saved that field holding
        // the English it last resolved. Our locale tables carry none of the package's keys, so a driver here
        // would only ever write that same English back; the field keeps it without one. -xlinka
        Carried.Add("LocaleStringDriver");
        Carried.Add("StaticLocaleProvider");
        // A fixed palette of the package platform's interface colours, read through copies. Every field it
        // fed was saved holding its colour, and a copy with no source leaves its target alone.
        Carried.Add("PlatformColorPalette");
        // Credit and export notes, and a free-text comment. Nothing in the package reads them at runtime.
        Carried.Add("License");
        Carried.Add("Comment");
        // A value kept on the package platform's servers under its own account ids. Nothing here can reach
        // it, and we have no world-writable account store to stand in; the field keeps its saved value.
        Carried.Add("CloudValueField");
        // A user's account profile fetched from the package platform's servers. A world here is never handed
        // anybody's account details beyond what the session already shows, so this is left out on purpose.
        Carried.Add("CloudUserInfo");
    }
}

public static partial class PackageMemberMapper
{
    // A stored member with this name never lands; its Apply hook takes it instead. The generic pass runs
    // after the hooks and would put the stored value straight back.
    private const string HandledK2 = "\u0000k2";

    static partial void MemberAliasesK2(Dictionary<string, Dictionary<string, string>> table)
    {
        table["PerUserValue`1"] = new(StringComparer.Ordinal)
        {
            ["CreateOverrideOnWrite"] = HandledK2,
            ["ClearOnUserLeave"] = HandledK2,
            // The entries come across in their places with their values and no user (ApplyPerUser).
            ["_overrides"] = "Overrides",
        };
        table["PerUserBoolTally"] = new(StringComparer.Ordinal)
        {
            ["ExcludeHeadless"] = "SkipServerHost",
        };
        table["FaceUser"] = new(StringComparer.Ordinal)
        {
            ["TargetUser"] = "User",
            ["TargetedUser"] = "User",
            ["_rotationDrive"] = "Rotation",
            ["_rotation"] = "Rotation",
            ["RotationOffset"] = HandledK2,
        };
        table["SeatTouchTrigger"] = new(StringComparer.Ordinal)
        {
            ["Anchor"] = "TargetSeat",
            ["Enter"] = "AllowSit",
            ["Exit"] = "AllowRelease",
            ["AcceptPhysicalTouch"] = "AcceptFingertipTouch",
            ["Vibrate"] = "ContactHaptics",
        };
        table["TimeCounter"] = new(StringComparer.Ordinal)
        {
            ["BaseSpeed"] = "Speed",
            ["_actualSpeed"] = "_countedSpeed",
            ["_actualOffset"] = "Offset",
        };
        table["SeatSpawner"] = new(StringComparer.Ordinal)
        {
            ["AnchorsRoot"] = "SeatsRoot",
            ["AcceptPhysicalTouch"] = "AcceptFingertipTouch",
            ["AnchorPointSnap"] = HandledK2,
        };
        EnumValueAliases["Vibrate"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Short"] = "Light",
            ["Long"] = "Strong",
        };
        table["ZoneValueDriver`1"] = new(StringComparer.Ordinal)
        {
            ["Drive"] = "Target",
            ["VariableName"] = "ZoneName",
        };
        table["BoxValueZone`1"] = new(StringComparer.Ordinal)
        {
            ["VariableName"] = "ZoneName",
            ["BlendDistanceMode"] = "BlendMode",
        };
        table["SphereValueZone`1"] = new(StringComparer.Ordinal)
        {
            ["VariableName"] = "ZoneName",
            ["BlendDistanceMode"] = "BlendMode",
            ["Mode"] = "DistanceMode",
        };
    }

    static partial void ApplyK2(Component component, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList, Func<Uri, Uri?> remapUrl)
    {
        if (component is FaceUser face)
            ApplyFaceUser(face, data, remapUrl);
        else if (component is PerUserValue<bool> boolOverride)
            ApplyPerUser(boolOverride.ClearOnUserLeave, boolOverride.Writes, data);
        else if (component.GetType() is { IsGenericType: true } type && type.GetGenericTypeDefinition() == typeof(PerUserValue<>)
            && type.GetField(nameof(PerUserValue<int>.ClearOnUserLeave))?.GetValue(component) is Sync<bool> clear
            && type.GetField(nameof(PerUserValue<int>.Writes))?.GetValue(component) is Sync<PerUserWrite> writes)
            ApplyPerUser(clear, writes, data);
    }

    private static DataTreeNode? StoredK2(DataTreeDictionary data, string name)
        => (data.TryGetNode(name) as DataTreeDictionary)?.TryGetNode("Data");

    private static bool StoredBool(DataTreeDictionary data, string name, bool fallback)
        => StoredK2(data, name) is DataTreeValue { Value: bool value } ? value : fallback;

    // A write to the field makes the writer's own entry, or with that off changes everyone's default.
    //
    // Entries the package does not keep (PersistentOverrides off) belong to the session that made them. A
    // user who left never comes back as the same user, so their entry is dead either way; dropping it on
    // leave keeps it out of a save, where its user reference would point at nobody.
    //
    // The stored entries come across in their places with their values and no user: the users they name belong
    // to the session the package was saved in and match nobody here. An entry is still worth its place: an author
    // keeps one that names nobody for a drive or a variable to fill in as the object runs (the user wearing it,
    // and that user's own value), and those find it by its place. -xlinka
    private static void ApplyPerUser(Sync<bool> clearOnUserLeave, Sync<PerUserWrite> writes, DataTreeDictionary data)
    {
        writes.Value = StoredBool(data, "CreateOverrideOnWrite", false) ? PerUserWrite.SetsOwn : PerUserWrite.SetsDefault;
        bool clear = StoredBool(data, "ClearOnUserLeave", false);
        bool persistent = StoredBool(data, "PersistentOverrides", true);
        clearOnUserLeave.Value = clear || !persistent;
    }

    // The package's two look-at components onto FaceUser.
    //
    // Both turn the slot's +Z toward the user, as ours does; Invert (and the auto one, which aims +Z away)
    // is a half turn about the slot's own up folded into the offset, since a look rotation of the negated
    // direction is the same rotation turned 180 degrees about its up. Only the plain one can turn about an
    // axis; turning about up is our yaw-only. The auto one's easing, swing and twist limits and retarget
    // distance have no counterpart and are left out: it snaps to the nearest user instead of easing.
    // -xlinka
    private static void ApplyFaceUser(FaceUser face, DataTreeDictionary data, Func<Uri, Uri?> remapUrl)
    {
        floatQ offset = floatQ.Identity;
        if (StoredK2(data, "RotationOffset") is { } stored && TryConvert(stored, typeof(floatQ), out var converted, remapUrl, "RotationOffset")
            && converted is floatQ q)
            offset = q;

        var halfTurn = floatQ.AxisAngle(float3.Up, MathF.PI);
        bool auto = data.TryGetNode("AutoTarget") != null;
        if (auto)
        {
            face.Mode.Value = StoredBool(data, "AutoTarget", true) ? FacingUserMode.NearestUser : FacingUserMode.SpecificUser;
            face.FaceHead.Value = true;
            face.YawOnly.Value = false;
            face.RotationOffset.Value = halfTurn * offset;
            return;
        }

        face.Mode.Value = StoredBool(data, "TargetAtLocalUser", false) ? FacingUserMode.LocalUser : FacingUserMode.SpecificUser;
        face.FaceHead.Value = !(StoredK2(data, "PositionSource") is DataTreeValue { Value: string source }
            && string.Equals(source, "Root", StringComparison.OrdinalIgnoreCase));

        bool aroundAxis = StoredBool(data, "AroundAxis", false);
        float3 axis = float3.Up;
        if (StoredK2(data, "Axis") is { } axisNode && TryConvert(axisNode, typeof(float3), out var axisValue, remapUrl, "Axis")
            && axisValue is float3 a && a.LengthSquared > 1e-8f)
            axis = a.Normalized;
        face.YawOnly.Value = aroundAxis && axis.y > 0.999f;
        face.Up.Value = aroundAxis ? axis : float3.Up;
        face.RotationOffset.Value = StoredBool(data, "Invert", false) ? halfTurn * offset : offset;
    }
}

// A bench's touch surface names the plane its seats are pressed onto, a component that does not come across
// itself. Its slot and normal go onto the spawner once every slot exists. A bench naming no plane presses onto
// its own slot.
internal static class WorldK2
{
    public static void FinishBenches(List<(Slot Slot, string Type, DataTreeDictionary Data)> parts)
    {
        if (parts.Count == 0)
            return;
        var planes = new Dictionary<string, (Slot Slot, DataTreeDictionary Data)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slot, type, data) in parts)
        {
            if (type == "SnapPlane" && (data.TryGetNode("ID") as DataTreeValue)?.Value is string { Length: > 0 } id)
                planes[id] = (slot, data);
        }
        foreach (var (slot, type, data) in parts)
        {
            if (type != "MultiUserAvatarAnchor" || slot.IsDestroyed || slot.GetComponent<Components.Avatar.SeatSpawner>() is not { } spawner)
                continue;
            if ((data.TryGetNode("AnchorPointSnap") as DataTreeDictionary)?.TryGetNode("Data") is not DataTreeValue { Value: string planeId }
                || PackageMemberMapper.IsNullId(planeId) || !planes.TryGetValue(planeId, out var plane) || plane.Slot.IsDestroyed)
                continue;
            spawner.PlaneSpace.Target = plane.Slot;
            // Unstored, the package's plane faces forward.
            var normal = PackageMemberMapper.ReadNumberArray(plane.Data, "Normal") is { Length: >= 3 } n
                ? new float3(n[0], n[1], n[2]) : float3.Forward;
            spawner.PlaneNormal.Value = PackageHandedness.Point(normal);
        }
    }
}
