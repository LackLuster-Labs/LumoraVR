// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;

namespace Lumora.Core.Assets.Interop;

// People arriving, other worlds, and the slots a session graph tags and finds. A package addresses other worlds
// by its own records, which name no world here, so a graph looking for one never finds it. -xlinka
internal static partial class PackageNodeTable
{
    private static void Session()
    {
        On("UserJoined", s => UserArrives(s, "OnUserJoined", "OnJoined", "JoinedUser"));
        On("UserSpawn", s => UserArrives(s, "OnUserSpawned", "OnSpawn", "SpawnedUser"));
        Same(_ => { }, "UserJoined+Proxy", "UserSpawn+Proxy");

        On("FocusWorld", s =>
        {
            s.Problem("focuses a world by the package's own address, which names no world here; it goes on as not found");
            s.OpThrough("", "OnNotFound");
        });
        On("OpenWorld", s =>
        {
            s.Problem("opens a world by the package's own address, which names no world here; it goes on as having failed");
            s.OutConstant("SessionID", null);
            s.OutConstant("SessionURL", null);
            s.OpThrough("", "OnOpenFail");
        });

        // A node of the graph is there for as long as its script is.
        if (TryGet("ElementExists", out var exists))
        {
            On("ElementExists", s =>
            {
                if (NodeNamedBy(s, "Element") != null)
                    s.OutConstant("", true);
                else
                    exists(s);
            });
        }

        Map("IndexOfChild", "SlotChildIndex", null, ins: "Instance=slot", outs: "=index");
        Map("SetTag", "SetSlotTag", null, ins: "Instance=slot Tag=tag", ops: "=in", exits: "Next=next");

        // No depth given searches every level, as a negative one does; ours counts the first level as one.
        On("FindChildByTag", s =>
        {
            string find = s.Add("FindChildByTag");
            s.In("Instance", find, "slot");
            s.In("Tag", find, "tag");
            if (!s.Wired("SearchDepth"))
                s.Set(find, "maxDepth", 0d);
            else if (s.Feeder("SearchDepth") is { Class: "ValueInput" } depth && depth.Value("Value") is IConvertible d)
            {
                int levels = Convert.ToInt32(d, CultureInfo.InvariantCulture);
                s.Set(find, "maxDepth", (double)(levels < 0 ? 0 : levels + 1));
            }
            else
            {
                string add = s.Add("Add", "int,int");
                s.In("SearchDepth", add, "a");
                s.Set(add, "b", 1d);
                string floor = s.Add("Max", "int,int");
                s.Wire(add, "result", floor, "a");
                s.Set(floor, "b", 0d);
                s.Wire(floor, "result", find, "maxDepth");
            }
            s.Out("", find, "child");
        });

        if (TryGet("FireOnValueChange", out var changed))
            On("FireOnObjectValueChange", changed);

        On("VerifyJoinRequest", VerifyJoin);
        Same(_ => { }, "VerifyJoinRequest+Proxy");
        Map("AllowJoin", "AllowJoin", null, ins: "Handle=request", ops: "=in", exits: "Next=next");
        Map("DenyJoin", "RefuseJoin", null, ins: "Handle=request DenyReason=reason", ops: "=in", exits: "Next=next");
        Map("AssignRole", "SetJoinRole", null, ins: "Handle=request RoleName=role", ops: "=in", exits: "Next=next");
    }

    // A join check here only hears someone once they have passed the session's own rules, so its flow begins
    // there. What it reads of them is kept as it starts: the package's check reads them after waiting on the
    // web, and an event's own outputs are gone once its flow has waited. What no join here carries reads as
    // its plainest value: nobody is invited by the world or on a kiosk, and a platform mute stands for both of
    // the package's bans, as both arrive silenced here.
    private static void VerifyJoin(PackageNodeScope s)
    {
        if (!s.Wired("Verify"))
            return;
        string heard = s.Add("OnJoinRequest");
        foreach (var (theirs, value) in new (string, object?)[]
                 {
                     ("UserSessionId", string.Empty), ("IsInvited", false), ("IsInKioskMode", false), ("HeadOutputDevice", null),
                     ("Platform", null),
                 })
            s.OutConstant(theirs, value);
        string from = heard;
        foreach (var (theirs, ours, type) in new[]
                 {
                     ("Handle", "request", "joinrequest"), ("UserId", "userId", "string"), ("MachineId", "machineId", "string"),
                     ("Username", "name", "string"), ("IsContact", "friend", "bool"), ("IsSpectatorBanned", "silenced", "bool"),
                     ("IsMuteBanned", "silenced", "bool"),
                 })
        {
            if (!s.IsRead(theirs))
                continue;
            string kept = s.Add("StoredValue", type);
            string keep = s.Add("WriteValue", type);
            s.Set(keep, "var", kept);
            s.Wire(heard, ours, keep, "value");
            s.Flow(from, "next", keep);
            from = keep;
            s.Out(theirs, kept, "value");
        }
        s.Exit("Verify", from, "next");
    }

    // Who arrived reaches the host only, unless asked otherwise. They are kept while the flow runs and cleared
    // after it, so a flow from elsewhere reading them reads nobody, as in the package.
    private static void UserArrives(PackageNodeScope s, string ours, string call, string output)
    {
        if (!s.Wired(call))
        {
            s.OutConstant(output, null);
            return;
        }
        string heard = s.Add(ours);
        string after = heard;
        string afterExit = "next";
        bool? onlyHost = ConstantFlag(s, "OnlyHost", true);
        if (onlyHost != false)
        {
            string host = s.Add("IsSessionHost");
            s.Wire(s.Add("MachineUser"), "user", host, "user");
            string gate = s.Add("If");
            s.Flow(heard, "next", gate);
            if (onlyHost == true)
                s.Wire(host, "host", gate, "condition");
            else
            {
                string anyone = s.Add("Not", "bool");
                s.In("OnlyHost", anyone, "value");
                string either = s.Add("Or", "bool,bool");
                s.Wire(host, "host", either, "a");
                s.Wire(anyone, "result", either, "b");
                s.Wire(either, "result", gate, "condition");
            }
            after = gate;
            afterExit = "true";
        }
        if (!s.IsRead(output))
        {
            s.Exit(call, after, afterExit);
            return;
        }
        string kept = s.Add("StoredValue", "user");
        string keep = s.Add("WriteValue", "user");
        s.Set(keep, "var", kept);
        s.Wire(heard, "user", keep, "value");
        s.Flow(after, afterExit, keep);
        string both = s.Add("Sequence");
        s.Flow(keep, "next", both);
        s.Exit(call, both, "out0");
        string clear = s.Add("WriteValue", "user");
        s.Set(clear, "var", kept);
        s.Flow(both, "out1", clear);
        s.Out(output, kept, "value");
    }

    // A flag input as a constant: what it means unwired, its value when a constant feeds it, else null.
    private static bool? ConstantFlag(PackageNodeScope s, string input, bool unwired)
    {
        if (!s.Wired(input))
            return unwired;
        if (s.Feeder(input) is { Class: "ValueInput" or "GlobalValue" } constant)
            return constant.Value("Value") is IConvertible value && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        return null;
    }

    // The package node an input names as an object, through casts, relays, sources and globals.
    private static PackageNode? NodeNamedBy(PackageNodeScope s, string input)
    {
        string? id = s.Node.Ref(input);
        for (int depth = 0; depth < 16 && s.Index.TryNode(id, out var node, out var member); depth++)
        {
            if (member.Length > 0)
                return null;
            switch (node.Class)
            {
                case "ObjectCast" or "ObjectRelay":
                    id = node.Ref("Input");
                    break;
                case "ElementSource":
                    id = node.Ref("Source");
                    break;
                case "GlobalReference":
                    id = node.Ref("Reference");
                    if (s.Index.Nodes.TryGetValue(id ?? string.Empty, out var named))
                        return named;
                    break;
                default:
                    return null;
            }
        }
        return null;
    }
}
