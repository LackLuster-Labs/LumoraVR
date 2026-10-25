// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;

namespace Lumora.Core.Assets.Interop;

// The world, people and devices, where a package node is several of ours or reads a little differently. A
// controller or headset read here is this machine's own, so a read of someone's controller is kept to their
// own machine and reads nothing elsewhere. -xlinka
internal static partial class PackageNodeTable
{
    private static void World()
    {
        // A transform read is one node of ours per part that is read.
        Same(s =>
        {
            bool local = s.Node.Class == "LocalTransform";
            foreach (var (theirs, ours, port) in new[]
                     {
                         (local ? "LocalPosition" : "GlobalPosition", local ? "SlotLocalPosition" : "SlotPosition", "position"),
                         (local ? "LocalRotation" : "GlobalRotation", local ? "SlotLocalRotation" : "SlotRotation", "rotation"),
                         (local ? "LocalScale" : "GlobalScale", local ? "SlotLocalScale" : "SlotScale", "scale"),
                     })
            {
                string read = s.Add(ours);
                s.In("Instance", read, "slot");
                s.Out(theirs, read, port);
            }
        }, "GlobalTransform", "LocalTransform");

        // Writes of several parts in a row, each part what it always writes when unwired; a missing slot stops.
        On("SetLocalTransform", s => SetterOnSlot(s,
            ("SetSlotLocalScale", "Scale", "scale", new[] { 1d, 1d, 1d }),
            ("SetSlotLocalRotation", "Rotation", "rotation", new[] { 0d, 0d, 0d, 1d }),
            ("SetSlotLocalPosition", "Position", "position", new[] { 0d, 0d, 0d })));
        On("SetLocalPositionRotation", s => SetterOnSlot(s,
            ("SetSlotLocalPosition", "Position", "position", new[] { 0d, 0d, 0d }),
            ("SetSlotLocalRotation", "Rotation", "rotation", new[] { 0d, 0d, 0d, 1d })));

        Map("GetUp", "SlotDirection", null, ins: "Instance=slot", outs: "=direction", set: "axis=Up");
        Map("SetUp", "SetSlotDirection", null, ins: "Instance=slot Up=direction", ops: "=in", exits: "Next=next", set: "axis=Up");
        Map("GlobalRotationToLocal", "ConvertRotation", null, ins: "Instance=to GlobalRotation=rotation", outs: "=converted",
            values: "GlobalRotation=[0,0,0,1]");
        Map("TransformRotation", "ConvertRotation", null, ins: "FromSpace=from ToSpace=to SourceRotation=rotation", outs: "=converted",
            values: "SourceRotation=[0,0,0,1]");

        // Out of a slot's own space into the world's; no slot leaves the value as it is, as world space does here.
        Map("LocalPointToGlobal", "ConvertPoint", null, ins: "Instance=from LocalPoint=point", outs: "=converted");
        Map("LocalVectorToGlobal", "ConvertVector", null, ins: "Instance=from LocalVector=vector", outs: "=converted");
        Map("LocalDirectionToGlobal", "ConvertDirection", null, ins: "Instance=from LocalDirection=direction", outs: "=converted");
        Map("LocalRotationToGlobal", "ConvertRotation", null, ins: "Instance=from LocalRotation=rotation", outs: "=converted",
            values: "LocalRotation=[0,0,0,1]");
        Map("LocalScaleToGlobal", "ConvertScale", null, ins: "Instance=from LocalScale=scale", outs: "=converted");
        Map("GlobalDirectionToLocal", "ConvertDirection", null, ins: "Instance=to GlobalDirection=direction", outs: "=converted");
        Map("TransformDirection", "ConvertDirection", null, ins: "FromSpace=from ToSpace=to SourceDirection=direction", outs: "=converted");
        Map("TransformScale", "ConvertScale", null, ins: "FromSpace=from ToSpace=to SourceScale=scale", outs: "=converted");
        Map("GetDown", "SlotDirection", null, ins: "Instance=slot", outs: "=direction", set: "axis=Down");

        // The named zones a point stands in, the highest priority answering, else the default.
        Map("SampleValueSpatialVariable", "SampleZone", "{T}", ins: "Point=point Name=zone DefaultValue=defaultValue", outs: "=value");
        Map("CreateTransformUndoStep", "RecordTransform", null, ins: "Target=slot", ops: "=in", exits: "Next=next");

        // The package counts depth past the first level, and a negative one has no limit; ours counts the first.
        Same(s =>
        {
            bool child = s.Node.Class == "FindChildByName";
            string find = s.Add(child ? "FindChild" : "FindParent");
            s.In("Instance", find, "slot");
            s.In("Name", find, "name");
            s.In("MatchSubstring", find, "partial", true);
            s.In("IgnoreCase", find, "ignoreCase");
            if (s.Feeder("SearchDepth") is { Class: "ValueInput" } depth && depth.Value("Value") is IConvertible d)
            {
                int levels = Convert.ToInt32(d, CultureInfo.InvariantCulture);
                s.Set(find, "maxDepth", (double)(levels < 0 ? 0 : levels + 1));
            }
            else if (s.Wired("SearchDepth"))
            {
                string add = s.Add("Add", "int,int");
                s.In("SearchDepth", add, "a");
                s.Set(add, "b", 1d);
                string floor = s.Add("Max", "int,int");
                s.Wire(add, "result", floor, "a");
                s.Set(floor, "b", 0d);
                s.Wire(floor, "result", find, "maxDepth");
            }
            else
            {
                s.Set(find, "maxDepth", 1d);
            }
            s.Out("", find, child ? "child" : "parent");
        }, "FindChildByName", "FindParentByName");

        // The package's set does nothing at all without a value: flow goes straight on.
        On("SetSlotActiveSelf", s =>
        {
            if (!s.Wired("Active"))
            {
                s.OpThrough("", "Next");
                return;
            }
            string set = s.Add("SetSlotActive");
            s.In("Instance", set, "slot");
            s.In("Active", set, "active");
            s.Op("", set);
            s.Exit("Next", set, "next");
        });

        On("DuplicateSlot", s =>
        {
            string copy = s.Add("DuplicateSlot");
            s.In("Template", copy, "slot");
            s.Op("", copy);
            s.Out("Duplicate", copy, "copy");
            if (!s.Wired("OverrideParent"))
            {
                s.Exit("Next", copy, "next");
                return;
            }
            string move = s.Add("SetSlotParent");
            s.Wire(copy, "copy", move, "slot");
            s.In("OverrideParent", move, "parent");
            s.Set(move, "inPlace", true);
            s.Flow(copy, "next", move);
            s.Exit("Next", move, "next");
        });

        // People: a package user root is a user here, and a character controller is the user it moves.
        Same(s => s.OutThrough("", "User"), "UserUserRoot");
        Same(s => s.OutThrough("", "User"), "FindCharacterControllerFromUser");
        On("LocalUserSlot", s =>
        {
            string user = s.Add("MachineUser");
            string root = s.Add("UserBodyRoot");
            s.Wire(user, "user", root, "user");
            s.Out("", root, "slot");
        });
        // The user's scale eased over its time: on at once, and on again once it has arrived.
        On("SetUserScale", s =>
        {
            string scale = s.Add("ScaleUser");
            if (s.Wired("UserRoot"))
                s.In("UserRoot", scale, "user");
            else
            {
                string me = s.Add("MachineUser");
                s.Wire(me, "user", scale, "user");
            }
            s.In("Scale", scale, "scale", 1d);
            s.In("AnimationTime", scale, "seconds", 0d);
            s.Op("", scale);
            if (!s.Wired("OnAnimationFinished"))
            {
                s.Exit("OnScaleChangeStart", scale, "next");
                return;
            }
            string wait = s.Add("Delay");
            s.In("AnimationTime", wait, "seconds", 0d);
            if (s.Wired("OnScaleChangeStart"))
            {
                string both = s.Add("Sequence");
                s.Flow(scale, "next", both);
                s.Exit("OnScaleChangeStart", both, "out0");
                s.Flow(both, "out1", wait);
            }
            else
                s.Flow(scale, "next", wait);
            s.Exit("OnAnimationFinished", wait, "then");
        });
        // No user root reads as 1, not as the 0 our scale gives for no slot.
        On("UserRootGlobalScale", s =>
        {
            string root = s.Add("UserBodyRoot");
            s.In("UserRoot", root, "user");
            string scale = s.Add("SlotScale");
            s.Wire(root, "slot", scale, "slot");
            string split = s.Add("SplitVector", "float3");
            s.Wire(scale, "scale", split, "vector");
            string none = s.Add("IsNull", "slot");
            s.Wire(root, "slot", none, "value");
            string pick = s.Add("Conditional", "float");
            s.Wire(none, "null", pick, "condition");
            s.Set(pick, "whenTrue", 1d);
            s.Wire(split, "x", pick, "whenFalse");
            s.Out("", pick, "result");
        });
        On("GetActiveLocomotionModule", s =>
        {
            string mode = s.Add("LocomotionMode");
            if (s.Wired("User"))
                s.In("User", mode, "user");
            else
                s.Wire(s.Add("MachineUser"), "user", mode, "user");
            s.Out("", mode, "module");
        });
        On("IsGrabbableGrabbed", s =>
        {
            string grabbed = s.Add("SlotGrabbed");
            if (s.WorldOf("Grabbable") is Component component)
                s.Bind(grabbed, "slot", component.Slot, "slot");
            else
                s.In("Grabbable", grabbed, "slot");
            s.Out("", grabbed, "grabbed");
        });

        // Nearest people to a slot: ours takes a point, the slot's position, or the script's own.
        Same(s =>
        {
            string kind = s.Node.Class switch { "NearestUserFoot" => "NearestFoot", "NearestUserHand" => "NearestHand", _ => "NearestHead" };
            string near = s.Add(kind);
            string at = s.Add("SlotPosition");
            if (s.Wired("Reference"))
                s.In("Reference", at, "slot");
            else
                s.Wire(s.Add("SelfSlot"), "slot", at, "slot");
            s.Wire(at, "position", near, "point");
            if (kind != "NearestHead")
            {
                s.In("GetLeft", near, "left", true);
                s.In("GetRight", near, "right", true);
                s.Out("Chirality", near, "side");
            }
            s.In("IgnoreUser", near, "ignore");
            s.In("IgnoreAFK", near, "presentOnly");
            s.Out("User", near, "user");
            s.Out("Slot", near, "slot");
            s.Out("Distance", near, "distance");
        }, "NearestUserFoot", "NearestUserHand", "NearestUserHead");

        On("GeneralHeadset", s =>
        {
            string presence = s.Add("UserPresence");
            s.In("User", presence, "user");
            s.Out("IsActive", presence, "vr");
            if (s.IsRead("BatteryLevel"))
            {
                string headset = s.Add("HeadsetState");
                string? mine = null;
                s.Out("BatteryLevel", OnlyTheirs(s, "User", headset, "battery", "float", -1d, ref mine), "result");
            }
            s.OutConstant("IsBatteryCharging", false);
        });

        Same(Controller, "IndexController", "TouchController", "ViveController", "StandardController");

        On("LocalScreenPointToWorld", s =>
        {
            string screen = s.Add("ScreenPointToWorld");
            if (s.Wired("NormalizedScreenPoint"))
            {
                string centred = s.Add("Subtract", "float2,float2");
                s.In("NormalizedScreenPoint", centred, "a");
                s.Set(centred, "b", new[] { 0.5, 0.5 });
                string scaled = s.Add("Multiply", "float2,float2");
                s.Wire(centred, "result", scaled, "a");
                s.Set(scaled, "b", new[] { 2d, -2d });
                s.Wire(scaled, "result", screen, "point");
            }
            s.In("Distance", screen, "distance", 1d);
            s.Out("", screen, "position");
        });

        // Seats: taken and left on the seat's slot.
        On("AnchorEvents", s =>
        {
            var seat = s.WorldOf("Anchor") switch { Slot slot => slot, Component c => c.Slot, _ => null };
            if (seat == null)
            {
                s.Fail("listens to a seat that did not come across");
                return;
            }
            foreach (var (call, ours) in new[] { ("OnAnchored", "OnSeatTaken"), ("OnReleased", "OnSeatLeft") })
            {
                if (!s.Wired(call))
                    continue;
                string heard = s.Add(ours);
                s.Bind(heard, "source", seat, "slot");
                s.Exit(call, heard, "next");
                if (call == "OnAnchored" || !s.Wired("OnAnchored"))
                    s.Out("User", heard, "user");
            }
        });

        // Media through a method by name.
        On("SyncMethodProxy", s =>
        {
            var global = s.Index.TryNode(s.Node.Ref("Method"), out var found, out _) ? found : null;
            var delegated = global?.Stored("Delegate") as Persistence.DataTreeDictionary;
            string? method = (delegated?.TryGetNode("Method") as Persistence.DataTreeValue)?.Value as string;
            var target = s.Index.World((delegated?.TryGetNode("Target") as Persistence.DataTreeValue)?.Value as string);
            // A call through a delegate nobody set, or one whose target the package had already lost, calls nothing,
            // and flow goes on.
            string? targetId = (delegated?.TryGetNode("Target") as Persistence.DataTreeValue)?.Value as string;
            if ((method == null && string.IsNullOrEmpty(targetId)) || (!string.IsNullOrEmpty(targetId) && !s.Index.Holds(targetId)))
            {
                if (!string.IsNullOrEmpty(targetId))
                    s.Problem($"calls {method ?? "a method"} on something the package had already lost, so it calls nothing");
                s.OpThrough("", "Next");
                return;
            }
            string? ours = method switch { "Play" => "PlayMedia", "Stop" => "StopMedia", "Pause" => "PauseMedia", "Resume" => "ResumeMedia", _ => null };
            var slot = target switch { Component c => c.Slot, Slot direct => direct, _ => null };
            if (ours == null || slot == null)
            {
                s.Fail($"calls {method ?? "a method"} on {s.Index.Describe((delegated?.TryGetNode("Target") as Persistence.DataTreeValue)?.Value as string)}");
                return;
            }
            string call = s.Add(ours);
            s.Bind(call, "slot", slot, "slot");
            s.Op("", call);
            s.Exit("Next", call, "next");
        });
        Same(_ => { }, "GlobalDelegate");
    }

    // A controller read: this machine's controller for the hand asked, kept to the user asked when there is one.
    private static void Controller(PackageNodeScope s)
    {
        string state = s.Add("ControllerState");
        string info = s.Add("ControllerInfo");
        if (s.Feeder("Node") is { Class: "ValueInput" } side && side.Value("Value") is string chirality)
        {
            s.Set(state, "right", chirality == "Right");
            s.Set(info, "right", chirality == "Right");
        }
        else if (s.Wired("Node"))
        {
            string isRight = s.Add("IsSame", "chirality");
            s.In("Node", isRight, "a");
            s.Set(isRight, "b", "Right");
            s.Wire(isRight, "same", state, "right");
            s.Wire(isRight, "same", info, "right");
        }
        else
        {
            s.Set(state, "right", false);
            s.Set(info, "right", false);
        }

        var outputs = s.Node.Class switch
        {
            "IndexController" => new[]
            {
                ("ButtonA", "primary", "bool"), ("ButtonB", "secondary", "bool"), ("ButtonA_Touch", "primaryTouched", "bool"),
                ("ButtonB_Touch", "secondaryTouched", "bool"), ("Grip", "grip", "float"), ("GripClick", "gripPressed", "bool"),
                ("Joystick", "stick", "float2"), ("JoystickTouch", "stickTouched", "bool"), ("JoystickClick", "stickPressed", "bool"),
                ("Trigger", "trigger", "float"), ("TriggerTouch", "triggerTouched", "bool"), ("TriggerClick", "triggerPressed", "bool"),
                ("Touchpad", "touchpad", "float2"), ("TouchpadTouch", "touchpadTouched", "bool"), ("TouchpadPress", "touchpadPressed", "bool"),
            },
            "TouchController" => new[]
            {
                ("ButtonYB", "secondary", "bool"), ("ButtonXA", "primary", "bool"), ("ButtonYB_Touch", "secondaryTouched", "bool"),
                ("ButtonXA_Touch", "primaryTouched", "bool"), ("Grip", "grip", "float"), ("GripClick", "gripPressed", "bool"),
                ("Joystick", "stick", "float2"), ("JoystickTouch", "stickTouched", "bool"), ("JoystickClick", "stickPressed", "bool"),
                ("Trigger", "trigger", "float"), ("TriggerTouch", "triggerTouched", "bool"), ("TriggerClick", "triggerPressed", "bool"),
            },
            "ViveController" => new[]
            {
                ("Grip", "gripPressed", "bool"), ("App", "menu", "bool"), ("Trigger", "trigger", "float"),
                ("TriggerClick", "triggerPressed", "bool"), ("Touchpad", "touchpad", "float2"), ("TouchpadTouch", "touchpadTouched", "bool"),
                ("TouchpadClick", "touchpadPressed", "bool"),
            },
            _ => new[]
            {
                ("Primary", "primary", "bool"), ("Secondary", "secondary", "bool"), ("Grab", "gripPressed", "bool"), ("Menu", "menu", "bool"),
                ("Strength", "trigger", "float"), ("Axis", "stick", "float2"),
            },
        };
        string? mine = null;
        foreach (var (theirs, ours, type) in outputs)
        {
            if (s.IsRead(theirs))
                s.Out(theirs, OnlyTheirs(s, "User", state, ours, type, null, ref mine), "result");
        }
        foreach (var (theirs, ours, type, fallback) in new[]
                 {
                     ("IsActive", "present", "bool", (object?)null), ("Type", "profile", "string", null), ("BatteryLevel", "battery", "float", -1d),
                 })
        {
            if (s.IsRead(theirs))
                s.Out(theirs, OnlyTheirs(s, "User", info, ours, type, fallback, ref mine), "result");
        }
        s.OutConstant("IsBatteryCharging", false);
        foreach (var unread in new[] { "GripTouch", "TouchpadForce", "ThumbRestTouch", "TriggerHair" })
            s.OutConstant(unread, null);
    }

    // A device read of this machine, given only where the user asked is this machine's own; elsewhere the
    // fallback. Answers the node whose result is the gated value.
    private static string OnlyTheirs(PackageNodeScope s, string userInput, string node, string port, string type, object? fallback,
        ref string? mine)
    {
        if (mine == null)
        {
            mine = s.Add("IsMachineUser");
            if (s.Wired(userInput))
                s.In(userInput, mine, "user");
            else
                s.Wire(s.Add("MachineUser"), "user", mine, "user");
        }
        string pick = s.Add("Conditional", type);
        s.Wire(mine, "local", pick, "condition");
        s.Wire(node, port, pick, "whenTrue");
        if (fallback != null)
            s.Set(pick, "whenFalse", fallback);
        return pick;
    }
}
