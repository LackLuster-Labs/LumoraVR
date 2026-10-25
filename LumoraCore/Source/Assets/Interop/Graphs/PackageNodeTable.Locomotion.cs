// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets.Interop;

// Locomotion and the body, and asking what kind of thing something is. The package asks the kind by comparing a
// class against a class; the two things its graphs ask it of here, a user's locomotion and a desktop user's
// camera, have a kind of ours to compare instead. -xlinka
internal static partial class PackageNodeTable
{
    private static void Locomotion()
    {
        On("SwitchLocomotionModule", s =>
        {
            string swap = s.Add("SwitchLocomotion");
            UserOrMachine(s, s.Node, "TargetUser", swap, "user");
            if (s.Feeder("ModuleName") is { Class: "ValueObjectInput" } named && named.Value("Value") is string name)
                s.Set(swap, "name", StockLocomotion(name));
            else
                s.In("ModuleName", swap, "name");
            s.In("ExactMatch", swap, "exact", false);
            string which = s.Add("If");
            s.Wire(swap, "switched", which, "condition");
            s.Op("", swap);
            s.Flow(swap, "next", which);
            s.Exit("OnSwitched", which, "true");
            s.Exit("OnNotFound", which, "false");
        });

        // Goes on only when something came in, as the package's does.
        On("InstallLocomotionModules", s =>
        {
            string install = s.Add("InstallLocomotions");
            s.In("ModulesRoot", install, "from");
            UserOrMachine(s, s.Node, "TargetUser", install, "user");
            s.In("ClearExisting", install, "clear", false);
            string worked = s.Add("If");
            s.Wire(install, "installed", worked, "condition");
            s.Op("", install);
            s.Flow(install, "next", worked);
            s.Exit("Next", worked, "true");
        });

        // Gravity pulls only while walking, with the user's own scale.
        On("CharacterGravity", s =>
        {
            string mode = s.Add("LocomotionMode");
            UserOrMachine(s, s.Node, "Character", mode, "user");
            string walking = s.Add("IsSame", "locomotionKind");
            s.Wire(mode, "kind", walking, "a");
            s.Set(walking, "b", "Walk");
            string pull = s.Add("Conditional", "float3");
            s.Wire(walking, "same", pull, "condition");
            s.Set(pull, "whenTrue", new[] { 0d, -9.81d, 0d });
            s.Set(pull, "whenFalse", new[] { 0d, 0d, 0d });
            s.Out("Gravity", pull, "result");
            if (!s.IsRead("ActualGravity"))
                return;
            string root = s.Add("UserBodyRoot");
            UserOrMachine(s, s.Node, "Character", root, "user");
            string size = s.Add("SlotScale");
            s.Wire(root, "slot", size, "slot");
            string split = s.Add("SplitVector", "float3");
            s.Wire(size, "scale", split, "vector");
            string actual = s.Add("Multiply", "float3,float");
            s.Wire(pull, "result", actual, "a");
            s.Wire(split, "x", actual, "b");
            s.Out("ActualGravity", actual, "result");
        });

        On("SetHipsPosition", s =>
        {
            string place = s.Add("PlaceHips");
            UserOrMachine(s, s.Node, "UserRoot", place, "user");
            s.In("Position", place, "position");
            s.Op("", place);
            s.Exit("Next", place, "next");
        });
        On("SetHipsRotation", s =>
        {
            string turn = s.Add("TurnHips");
            UserOrMachine(s, s.Node, "UserRoot", turn, "user");
            s.Wire(FacingFromPackage(s, "Rotation"), "rotation", turn, "rotation");
            s.Op("", turn);
            s.Exit("Next", turn, "next");
        });
        // A character is its user here, as the package finds one from the other.
        On("IsCharacterOnGround", s =>
        {
            string ground = s.Add("UserOnGround");
            s.In("Character", ground, "user");
            s.Out("", ground, "grounded");
        });
        On("SetCharacterVelocity", s =>
        {
            string set = s.Add("SetUserVelocity");
            s.In("Character", set, "user");
            s.In("Velocity", set, "velocity");
            s.Op("", set);
            s.Exit("Next", set, "next");
        });
        // An impulse with mass is a change of speed of a tenth of it, the package's stock character weighing 10.
        On("ApplyCharacterImpulse", s =>
        {
            string light = s.Add("Multiply", "float3,float");
            s.In("Impulse", light, "a");
            s.Set(light, "b", 0.1d);
            string change = s.Add("Conditional", "float3");
            s.In("IgnoreMass", change, "condition", false);
            s.In("Impulse", change, "whenTrue");
            s.Wire(light, "result", change, "whenFalse");
            string push = s.Add("PushUser");
            s.In("Character", push, "user");
            s.Wire(change, "result", push, "velocity");
            s.Op("", push);
            s.Exit("Next", push, "next");
        });

        On("UserPrimaryHand", s =>
        {
            string hand = s.Add("PrimaryHand");
            UserOrMachine(s, s.Node, "User", hand, "user");
            s.Out("", hand, "hand");
        });

        // Read only through the kind checks below, which look through them.
        Same(_ => { }, "GetType", "TypeObjectInput", "UserActiveViewTargettingController");
        foreach (var cls in new[] { "ObjectEquals", "ObjectNotEquals" })
        {
            TryGet(cls, out var plain);
            bool equal = cls == "ObjectEquals";
            On(cls, s =>
            {
                if (s.Node.Args.Count == 1 && s.Node.Args[0] == "Type")
                    KindCheck(s, equal);
                else
                    plain(s);
            });
        }
    }

    // The package's names for the stock locomotions, as ours names the same ones.
    private static string StockLocomotion(string name) => name switch
    {
        "Locomotion.Noclip.Name" => "Noclip",
        "Locomotion.WalkRun.Name" => "Walk",
        "Locomotion.Teleport.Name" => "Blink",
        _ => name,
    };

    private static void KindCheck(PackageNodeScope s, bool equal)
    {
        var a = s.Feeder("A");
        var (asked, named) = a is { Class: "GetType" } ? (a, s.Feeder("B")) : (s.Feeder("B"), a);
        string? type = named is { Class: "TypeObjectInput" } ? s.Index.TypeName(named.Value("Type")) : null;
        var subject = asked is { Class: "GetType" } ? Underneath(s, asked.Ref("Object")) : null;
        string? node = null, port = null;
        switch (subject?.Class)
        {
            case "GetActiveLocomotionModule" when type != null:
            {
                string mode = s.Add("LocomotionMode");
                UserOrMachine(s, subject, "User", mode, "user");
                node = s.Add("IsSame", "locomotionKind");
                s.Wire(mode, "kind", node, "a");
                s.Set(node, "b", type switch
                {
                    "PhysicalLocomotion" => "Walk",
                    "NoclipLocomotion" => "Noclip",
                    "TeleportLocomotion" => "Teleport",
                    _ => "Other",
                });
                port = "same";
                break;
            }
            // Asked of a desktop camera; in a headset there is none, so it is never that kind there.
            case "UserActiveViewTargettingController" when type != null:
            {
                string? view = type switch
                {
                    "FirstPersonTargettingController" => "FirstPerson",
                    "ThirdPersonTargettingController" => "ThirdPerson",
                    "FreeformTargettingController" => "FreeCam",
                    _ => null,
                };
                if (view == null)
                    break;
                string device = s.Add("UserDevice");
                UserOrMachine(s, subject, "User", device, "user");
                string presence = s.Add("UserPresence");
                UserOrMachine(s, subject, "User", presence, "user");
                string same = s.Add("IsSame", "desktopViewMode");
                s.Wire(device, "viewMode", same, "a");
                s.Set(same, "b", view);
                string flat = s.Add("Not", "bool");
                s.Wire(presence, "vr", flat, "value");
                node = s.Add("And", "bool,bool");
                s.Wire(same, "same", node, "a");
                s.Wire(flat, "result", node, "b");
                port = "result";
                break;
            }
        }
        if (node == null || port == null)
        {
            s.Fail($"asks whether something is a {type ?? "kind of thing"}, which has no counterpart here");
            return;
        }
        if (equal)
        {
            s.Out("", node, port);
            return;
        }
        string not = s.Add("Not", "bool");
        s.Wire(node, port, not, "value");
        s.Out("", not, "result");
    }

    // What a value really is, through the casts and relays in front of it.
    private static PackageNode? Underneath(PackageNodeScope s, string? id)
    {
        for (int depth = 0; depth < 32 && id != null; depth++)
        {
            if (!s.Index.TryNode(id, out var node, out _))
                return null;
            if (!node.Class.StartsWith("ObjectCast", System.StringComparison.Ordinal) && !node.Class.StartsWith("ObjectRelay", System.StringComparison.Ordinal))
                return node;
            id = node.Ref("Input");
        }
        return null;
    }

    // A user input of a package node, or the user at this machine where it is left empty, as the package's does.
    private static void UserOrMachine(PackageNodeScope s, PackageNode from, string theirInput, string ourNode, string ourPort)
    {
        if (from.Ref(theirInput) != null)
        {
            s.InFrom(from, theirInput, ourNode, ourPort);
            return;
        }
        string me = s.Add("MachineUser");
        s.Wire(me, "user", ourNode, ourPort);
    }
}
