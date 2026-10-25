// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Lumora.Core.Components.Utility;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Persistence;
using Lumora.Script;

namespace Lumora.Core.Assets.Interop;

// Package nodes first seen in worlds: flow, impulses, async, variables and stored values.
//
// A change node watching a field in the world, a named variable or a collider user tracker hears it change
// rather than looking every frame; only one watching something worked out as it runs looks every frame. A demultiplexer keeps which way flow came
// in a Stored Value; one that hands straight to a multiplexer reading that index sends each way on directly.
// Collider handles are the slots that carry them, as a collision names them here. -xlinka
internal static partial class PackageNodeTable
{
    private static void WorldC()
    {
        On("Sequence", FamilyC.Sequence);
        On("ImpulseMultiplexer", FamilyC.Multiplex);
        On("ImpulseDemultiplexer", FamilyC.Demultiplex);
        On("ImpulseDisplay", s => s.OpEnds(""));
        Same(FamilyC.WriteOrCreate, "WriteOrCreateDynamicValueVariable", "WriteOrCreateDynamicObjectVariable");
        On("WriteDynamicObjectVariable", FamilyC.WriteObjectVariable);
        Same(FamilyC.FireOnLocalBool, "FireOnLocalFalse", "FireOnLocalTrue");
        On("FireOnLocalValueChange", FamilyC.FireOnLocalChange);
        On("LocalLeakyImpulseBucket", FamilyC.LeakyBucket);
        On("OnePerFrame", FamilyC.OnePerFrame);
        On("AsyncWhile", FamilyC.AsyncWhile);
        if (TryGet("DelaySecondsFloat", out var delay))
            On("DelaySecondsInt", delay);
        On("DebugLine", FamilyC.DebugLine);
        On("DebugBox", FamilyC.DebugBox);
        On("PlayOneShotAndWait", FamilyC.PlayOneShotAndWait);
        On("PlaybackDrive", FamilyC.PlaybackDrive);
        Same(_ => { }, "PlaybackDrive+Proxy", "DataModelUserRefStore+Store", "DynamicImpulseReceiverWithValue+Proxy",
            "ReferenceInterfaceAsVariable");
        On("DynamicImpulseTriggerWithValue", FamilyC.SendWithValue);
        On("DynamicImpulseReceiverWithValue", FamilyC.ReceiveWithValue);
        On("LocalValue", FamilyC.LocalValue);
        On("DataModelUserRefStore", FamilyC.UserStore);
        On("ObjectMultiplex", FamilyC.ObjectMultiplex);
        On("ValueMultiplex", FamilyC.ValueMultiplex);
        On("MultiNullCoalesce", FamilyC.NullCoalesce);
        On("AllocatingUser", FamilyC.AllocatingUser);
        On("CreateFieldUndoStep", FamilyC.FieldUndoStep);
        On("TryEnumToInt", FamilyC.EnumToInt);
        // A step along an enum's values, wrapping at the ends; two names for one number are one step on both sides.
        Map("NextValue", "NextEnumValue", "{T}", ins: "Value=value", outs: "=next");
        Map("PreviousValue", "PreviousEnumValue", "{T}", ins: "Value=value", outs: "=previous");

        // Shapes the earlier handlers do not take, ahead of them.
        foreach (var cls in new[] { "ValueWrite", "ObjectWrite", "ValueIndirectWrite", "ObjectIndirectWrite" })
        {
            if (!TryGet(cls, out var plain))
                continue;
            On(cls, s =>
            {
                if (!FamilyC.WriteOwn(s))
                    plain(s);
            });
        }
        foreach (var (cls, equal) in new[] { ("ValueEquals", true), ("ValueNotEquals", false) })
        {
            if (!TryGet(cls, out var plain))
                continue;
            On(cls, s =>
            {
                if (FamilyC.EnumOf(s.Node.Args.Count > 0 ? s.Node.Args[0] : null) is { } type)
                    FamilyC.CompareEnums(s, type, equal);
                else
                    plain(s);
            });
        }
        foreach (var cls in new[] { "DynamicVariableObjectInput", "ReadDynamicObjectVariable" })
        {
            if (!TryGet(cls, out var plain))
                continue;
            On(cls, s =>
            {
                if (!FamilyC.ReadOwn(s))
                    plain(s);
            });
        }
        if (TryGet("DataModelObjectRefStore", out var objectStore))
        {
            On("DataModelObjectRefStore", s =>
            {
                if (!FamilyC.ObjectStoreOwn(s))
                    objectStore(s);
            });
        }
        if (TryGet("ReferenceDrive", out var referenceDrive))
        {
            On("ReferenceDrive", s =>
            {
                if (!FamilyC.ReferenceDriveOwn(s))
                    referenceDrive(s);
            });
        }
        if (TryGet("FireOnValueChange", out var fireOnChange))
        {
            On("FireOnValueChange", s =>
            {
                if (!FamilyC.FireOnChangeOwn(s))
                    fireOnChange(s);
            });
        }
        if (TryGet("IsNull", out var isNull))
        {
            On("IsNull", s =>
            {
                if (!FamilyC.IsNullOwn(s))
                    isNull(s);
            });
        }
    }

    private static class FamilyC
    {
        private static readonly ConditionalWeakTable<PackageGraphWriter, Dictionary<PackageNode, string>> LocalStores = new();
        private static readonly ConditionalWeakTable<PackageNodeIndex, Dictionary<string, List<PackageNode>>> Callers = new();

        // Our handle type for an object type of the package's, including the ones only a collision or an
        // audio source names.
        public static string? Handle(string? their) => their switch
        {
            null => null,
            "ICollider" => "slot",
            "IComponent" or "IWorldAudioDataSource" or "IAudioStream" => "component",
            _ => ObjectType(their),
        };

        private static string? Raw(PackageNodeScope s, int i) => i < s.Node.Args.Count ? s.Node.Args[i] : null;

        // Calls in order, each run to its end before the next.
        public static void Sequence(PackageNodeScope s)
        {
            var steps = new List<Action<string, string>>();
            foreach (var id in s.Node.Refs("Calls"))
            {
                if (id != null)
                    steps.Add((node, exit) => s.Writer.Exit(node, exit, s.Node, id));
            }
            if (steps.Count == 0)
            {
                s.OpEnds("");
                return;
            }
            Fan(s, null, null, steps);
        }

        // Steps one after another from the node's own flow in, or from one of our exits: Sequences of four, the
        // last way out of each leading to the next.
        private static void Fan(PackageNodeScope s, string? fromNode, string? fromExit, IReadOnlyList<Action<string, string>> steps)
        {
            if (steps.Count == 1)
            {
                string relay = s.Add("FlowRelay");
                Enter(s, fromNode, fromExit, relay);
                steps[0](relay, "next");
                return;
            }
            int at = 0;
            while (at < steps.Count)
            {
                string sequence = s.Add("Sequence");
                Enter(s, fromNode, fromExit, sequence);
                int here = steps.Count - at <= 4 ? steps.Count - at : 3;
                for (int k = 0; k < here; k++)
                    steps[at + k](sequence, "out" + k.ToString(CultureInfo.InvariantCulture));
                at += here;
                fromNode = sequence;
                fromExit = "out3";
            }
        }

        private static void Enter(PackageNodeScope s, string? fromNode, string? fromExit, string to)
        {
            if (fromNode == null)
                s.Op("", to);
            else
                s.Flow(fromNode, fromExit!, to);
        }

        // Flow goes on by the call at the index, and nowhere for an index past the list.
        public static void Multiplex(PackageNodeScope s)
        {
            if (OnlyBypassed(s))
                return;
            var targets = s.Node.Refs("Impulses");
            int? fixedIndex = null;
            if (!s.Wired("Index"))
                fixedIndex = 0;
            else if (Constant(s, "Index") is IConvertible n and not string)
                fixedIndex = Convert.ToInt32(n, CultureInfo.InvariantCulture);
            if (fixedIndex is { } at)
            {
                if (at >= 0 && at < targets.Count && targets[at] is { } only)
                {
                    string relay = s.Add("FlowRelay");
                    s.Op("", relay);
                    s.Writer.Exit(relay, "next", s.Node, only);
                }
                else
                {
                    s.OpEnds("");
                }
                return;
            }
            string? previous = null;
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] is not { } target)
                    continue;
                string same = s.Add("IsEqual", "int,int");
                s.In("Index", same, "a");
                s.Set(same, "b", (double)i);
                string pick = s.Add("If");
                s.Wire(same, "result", pick, "condition");
                Enter(s, previous, "false", pick);
                s.Writer.Exit(pick, "true", s.Node, target);
                previous = pick;
            }
            if (previous == null)
                s.OpEnds("");
        }

        // Which way flow came in, kept for whatever reads the index, then on.
        public static void Demultiplex(PackageNodeScope s)
        {
            var mux = BypassOf(s.Index, s.Node);
            var muxTargets = mux?.Refs("Impulses");
            string? kept = null;
            if (s.IsRead("Index"))
            {
                kept = s.Add("StoredValue", "int");
                s.Out("Index", kept, "value");
            }
            if (s.Node.Stored("Operations") is not DataTreeList entries)
                return;
            for (int i = 0; i < entries.Count; i++)
            {
                if (((entries[i] as DataTreeDictionary)?.TryGetNode("ID") as DataTreeValue)?.Value is not string entry
                    || !s.Index.Read.Contains(entry))
                    continue;
                string step;
                if (kept != null)
                {
                    step = s.Add("WriteValue", "int");
                    s.Set(step, "var", kept);
                    s.Set(step, "value", (double)i);
                }
                else
                {
                    step = s.Add("FlowRelay");
                }
                s.Op("Operations[" + i.ToString(CultureInfo.InvariantCulture) + "]", step);
                if (mux == null)
                    s.Exit("OnTriggered", step, "next");
                else if (i < muxTargets!.Count && muxTargets[i] is { } target)
                    s.Writer.Exit(step, "next", mux, target);
            }
        }

        // The multiplexer a demultiplexer hands to, when it picks by that demultiplexer's own index.
        private static PackageNode? BypassOf(PackageNodeIndex index, PackageNode demux)
        {
            if (!index.TryNode(demux.Ref("OnTriggered"), out var mux, out var member) || member.Length != 0 || mux.Class != "ImpulseMultiplexer")
                return null;
            string? id = mux.Ref("Index");
            for (int depth = 0; depth < 16 && index.TryNode(id, out var node, out var output); depth++)
            {
                if (ReferenceEquals(node, demux))
                    return output == "Index" ? mux : null;
                if (!node.Class.StartsWith("ValueRelay", StringComparison.Ordinal))
                    return null;
                id = node.Ref("Input");
            }
            return null;
        }

        // Every flow into the multiplexer comes from a demultiplexer that sends on past it.
        private static bool OnlyBypassed(PackageNodeScope s)
        {
            var map = Callers.GetValue(s.Index, Referrers);
            if (!map.TryGetValue(s.Node.Id, out var callers))
                return true;
            foreach (var caller in callers)
            {
                if (caller.Class != "ImpulseDemultiplexer" || !ReferenceEquals(BypassOf(s.Index, caller), s.Node))
                    return false;
            }
            return true;
        }

        private static Dictionary<string, List<PackageNode>> Referrers(PackageNodeIndex index)
        {
            var map = new Dictionary<string, List<PackageNode>>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in index.Nodes.Values)
            {
                foreach (var id in PackageNodeIndex.References(node))
                {
                    if (!map.TryGetValue(id, out var list))
                        map[id] = list = new List<PackageNode>();
                    list.Add(node);
                }
            }
            return map;
        }

        // What a constant node feeding the input holds, or null.
        private static object? Constant(PackageNodeScope s, string input)
            => s.Feeder(input) is { Class: "ValueInput" or "GlobalValue" or "ValueObjectInput" } constant ? constant.Value("Value") : null;

        // The variable's own slot, or the script's when the package node names none.
        private static void Target(PackageNodeScope s, string node)
        {
            if (s.Wired("Target"))
                s.In("Target", node, "slot");
            else
                s.Wire(s.Add("SelfSlot"), "slot", node, "slot");
        }

        // Written when it is there, made when it is not. A write the variable refuses and a variable that could
        // not be made both leave written and created false here, so both go on by not found when that is
        // wired and by failed otherwise.
        public static void WriteOrCreate(PackageNodeScope s)
        {
            string? type = s.Node.Class.Contains("Object", StringComparison.Ordinal) ? Handle(Raw(s, 0)) : s.Arg(0);
            if (type == null)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            string write = s.Add("WriteOrCreateScopedVariable", type);
            Target(s, write);
            s.In("Path", write, "path");
            s.In("Value", write, "value");
            s.In("CreateDirectlyOnTarget", write, "onSlot", false);
            if (Constant(s, "CreateNonPersistent") is bool fleeting)
                s.Set(write, "saved", !fleeting);
            else if (s.Wired("CreateNonPersistent"))
            {
                string kept = s.Add("Not", "bool");
                s.In("CreateNonPersistent", kept, "value");
                s.Wire(kept, "result", write, "saved");
            }
            s.Op("", write);
            bool written = s.Wired("OnWritten"), failed = s.Wired("OnFailed") || s.Wired("OnNotFound");
            if (!s.Wired("OnCreated") && !written && !failed)
                return;
            string made = s.Add("If");
            s.Flow(write, "next", made);
            s.Wire(write, "created", made, "condition");
            s.Exit("OnCreated", made, "true");
            if (!written && !failed)
                return;
            string took = s.Add("If");
            s.Flow(made, "false", took);
            s.Wire(write, "written", took, "condition");
            s.Exit("OnWritten", took, "true");
            s.Exit(s.Wired("OnNotFound") ? "OnNotFound" : "OnFailed", took, "false");
        }

        public static void WriteObjectVariable(PackageNodeScope s)
        {
            if (Handle(Raw(s, 0)) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            string write = s.Add("WriteScopedVariable", type);
            Target(s, write);
            s.In("Path", write, "path");
            s.In("Value", write, "value");
            s.Op("", write);
            string done = s.Add("If");
            s.Flow(write, "next", done);
            s.Wire(write, "written", done, "condition");
            s.Exit("OnSuccess", done, "true");
            string found = s.Add("If");
            s.Flow(done, "false", found);
            s.Wire(write, "found", found, "condition");
            s.Exit("OnFailed", found, "true");
            s.Exit("OnNotFound", found, "false");
        }

        // The field a change node watches when its input reads one straight; why it never fires when what it
        // reads did not come across. Neither for something worked out as it runs.
        private static string? Watched(PackageNodeScope s, string input, out IWorldElement? field)
        {
            field = null;
            if (s.Feeder(input) is not { Class: "ValueSource" } source)
                return null;
            field = WorldOf(s.Index, source, "Source") as IField;
            if (field != null)
                return null;
            string? id = source.Ref("Source");
            if (s.Index.TryNode(id, out var global, out _) && global.Class.StartsWith("GlobalReference", StringComparison.Ordinal))
                id = global.Ref("Reference");
            return $"watches {s.Index.Describe(id)}, so it never fires";
        }

        // A collider user tracker's flag for the user at this machine, which ours keeps in no field: the tracker's
        // slot, whose people events tell the same turn. Ours counts a person by their body touching its colliders
        // where the package counted their character collider, which comes to the same for a person walking in.
        private static Slot? Tracker(PackageNodeScope s, string input)
        {
            if (s.Feeder(input) is not { Class: "ValueSource" } source)
                return null;
            return s.Index.OwnerOf(SourceIdOf(s.Index, source), out var member) is Components.ColliderUserTracker tracker
                && member == "IsLocalUserInside" ? tracker.Slot : null;
        }

        // At start the watch learns the value; after that the field changing, or failing one, every frame.
        // A named variable read from the script's own slot is heard through that slot's variables changing.
        private static void Feed(PackageNodeScope s, string watch, IWorldElement? field, string input)
        {
            bool variable = s.Feeder(input)?.Class is "DynamicVariableValueInput" or "DynamicVariableObjectInput";
            if (field == null && !variable)
            {
                string tick = s.Add("OnUpdate");
                s.Flow(tick, "next", watch);
                return;
            }
            string start = s.Add("OnStart");
            s.Flow(start, "next", watch);
            string heard = s.Add(field != null ? "OnValueChanged" : "OnScopedVariableChanged");
            if (field != null)
                s.Bind(heard, "source", field, "anyfield");
            s.Flow(heard, "next", watch);
        }

        // A value change heard rather than looked for, where an event says it; the rest as before.
        public static bool FireOnChangeOwn(PackageNodeScope s)
        {
            if (s.Arg(0) is not { } type || !s.Wired("Value") || Watched(s, "Value", out var field) != null)
                return false;
            if (field == null && s.Feeder("Value")?.Class is not ("DynamicVariableValueInput" or "DynamicVariableObjectInput"))
                return false;
            string watch = s.Add("Watch", type);
            Feed(s, watch, field, "Value");
            s.In("Value", watch, "value");
            s.Exit("OnChanged", watch, "changed");
            return true;
        }

        // The package node runs on every machine for its own value; this one runs where the script runs.
        public static void FireOnLocalBool(PackageNodeScope s)
        {
            bool onTrue = s.Node.Class == "FireOnLocalTrue";
            if (!s.Wired("Condition") || Constant(s, "Condition") != null)
                return;
            if (Tracker(s, "Condition") is { } area)
            {
                string heard = s.Add(onTrue ? "OnPersonEnter" : "OnPersonExit");
                s.Bind(heard, "source", area, "slot");
                string mine = s.Add("IsMachineUser");
                s.Wire(heard, "user", mine, "user");
                string only = s.Add("If");
                s.Wire(mine, "local", only, "condition");
                s.Flow(heard, "next", only);
                s.Exit("OnChange", only, "true");
                return;
            }
            if (Watched(s, "Condition", out var field) is { } never)
            {
                s.Problem(never);
                return;
            }
            string watch = s.Add("Watch", "bool");
            Feed(s, watch, field, "Condition");
            s.In("Condition", watch, "value");
            string which = s.Add("If");
            s.Flow(watch, "changed", which);
            s.In("Condition", which, "condition");
            s.Exit("OnChange", which, onTrue ? "true" : "false");
        }

        public static void FireOnLocalChange(PackageNodeScope s)
        {
            if (s.Arg(0) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            if (!s.Wired("Value") || Constant(s, "Value") != null)
                return;
            if (Watched(s, "Value", out var field) is { } never)
            {
                s.Problem(never);
                return;
            }
            string watch = s.Add("Watch", type);
            Feed(s, watch, field, "Value");
            s.In("Value", watch, "value");
            s.Exit("OnChange", watch, "changed");
        }

        // Pulses at most once an interval: a trigger in time pulses at once, a sooner one waits its turn in the
        // bucket, and one past its capacity overflows. A reset empties the bucket; a wait already under way
        // that a fresh trigger re-arms before it ends pulses early, where the package's would be cancelled.
        public static void LeakyBucket(PackageNodeScope s)
        {
            string count = s.Add("StoredValue", "int");
            string last = s.Add("StoredValue", "double");
            s.Set(last, "initial", -1e12);
            string running = s.Add("StoredValue", "bool");
            s.Out("CurrentCapacity", count, "value");
            string clock = s.Add("WorldTime");

            string full = s.Add("GreaterOrEqual", "int,int");
            s.Wire(count, "value", full, "a");
            s.In("MaximumCapacity", full, "b", (double)int.MaxValue);
            string overflow = s.Add("If");
            s.Wire(full, "result", overflow, "condition");
            s.Op("Trigger", overflow);
            s.Exit("Overflow", overflow, "true");
            string busy = s.Add("If");
            s.Wire(running, "value", busy, "condition");
            s.Flow(overflow, "false", busy);
            string queue = s.Add("IncrementValue", "int");
            s.Set(queue, "var", count);
            s.Flow(busy, "true", queue);

            string since = s.Add("Subtract", "double,double");
            s.Wire(clock, "seconds", since, "a");
            s.Wire(last, "value", since, "b");
            string sinceFloat = s.Add("ToFloat", "double");
            s.Wire(since, "result", sinceFloat, "value");
            string due = s.Add("GreaterOrEqual", "float,float");
            s.Wire(sinceFloat, "result", due, "a");
            s.In("Interval", due, "b", 0d);
            string now = s.Add("If");
            s.Wire(due, "result", now, "condition");
            s.Flow(busy, "false", now);
            string stamp = s.Add("WriteValue", "double");
            s.Set(stamp, "var", last);
            s.Wire(clock, "seconds", stamp, "value");
            s.Flow(now, "true", stamp);
            s.Exit("Pulse", stamp, "next");

            string arm = s.Add("WriteValue", "bool");
            s.Set(arm, "var", running);
            s.Set(arm, "value", true);
            s.Flow(now, "false", arm);
            string add = s.Add("IncrementValue", "int");
            s.Set(add, "var", count);
            s.Flow(arm, "next", add);
            string rest = s.Add("Subtract", "float,float");
            s.In("Interval", rest, "a", 0d);
            s.Wire(sinceFloat, "result", rest, "b");
            string first = s.Add("Delay");
            s.Wire(rest, "result", first, "seconds");
            s.Flow(add, "next", first);

            // One leaks out each interval until the bucket is empty.
            string live = s.Add("If");
            s.Wire(running, "value", live, "condition");
            s.Flow(first, "then", live);
            string drop = s.Add("DecrementValue", "int");
            s.Set(drop, "var", count);
            s.Flow(live, "true", drop);
            string mark = s.Add("WriteValue", "double");
            s.Set(mark, "var", last);
            s.Wire(clock, "seconds", mark, "value");
            s.Flow(drop, "next", mark);
            string both = s.Add("Sequence");
            s.Flow(mark, "next", both);
            s.Exit("Pulse", both, "out0");
            string more = s.Add("GreaterThan", "int,int");
            s.Wire(count, "value", more, "a");
            s.Set(more, "b", 0d);
            string again = s.Add("If");
            s.Wire(more, "result", again, "condition");
            s.Flow(both, "out1", again);
            string next = s.Add("Delay");
            s.In("Interval", next, "seconds", 0d);
            s.Flow(again, "true", next);
            s.Flow(next, "then", live);
            string idle = s.Add("WriteValue", "bool");
            s.Set(idle, "var", running);
            s.Set(idle, "value", false);
            s.Flow(again, "false", idle);

            string empty = s.Add("WriteValue", "int");
            s.Set(empty, "var", count);
            s.Set(empty, "value", 0d);
            s.Op("Reset", empty);
            string forget = s.Add("WriteValue", "double");
            s.Set(forget, "var", last);
            s.Set(forget, "value", -1e12);
            s.Flow(empty, "next", forget);
            string stop = s.Add("WriteValue", "bool");
            s.Set(stop, "var", running);
            s.Set(stop, "value", false);
            s.Flow(forget, "next", stop);
        }

        // Through once a frame: the world's time is that frame's.
        public static void OnePerFrame(PackageNodeScope s)
        {
            string clock = s.Add("WorldTime");
            string last = s.Add("StoredValue", "double");
            s.Set(last, "initial", -1e12);
            string other = s.Add("NotEqual", "double,double");
            s.Wire(clock, "seconds", other, "a");
            s.Wire(last, "value", other, "b");
            string gate = s.Add("If");
            s.Wire(other, "result", gate, "condition");
            s.Op("", gate);
            string mark = s.Add("WriteValue", "double");
            s.Set(mark, "var", last);
            s.Wire(clock, "seconds", mark, "value");
            s.Flow(gate, "true", mark);
            s.Exit("Next", mark, "next");
        }

        // The package's loop waits for each turn to end. Here a turn's chain ends at its first wait, so the
        // next turn starts a frame after the last one began: a turn that waits a frame keeps step, one that waits
        // longer overlaps the next, and one that waits for nothing takes a frame each where the package ran them
        // all at once.
        public static void AsyncWhile(PackageNodeScope s)
        {
            string check = s.Add("If");
            s.In("Condition", check, "condition");
            if (s.Wired("LoopStart"))
            {
                string first = s.Add("Sequence");
                s.Op("", first);
                s.Exit("LoopStart", first, "out0");
                s.Flow(first, "out1", check);
            }
            else
            {
                s.Op("", check);
            }
            string turn = s.Add("Sequence");
            s.Flow(check, "true", turn);
            s.Exit("LoopIteration", turn, "out0");
            string wait = s.Add("Delay");
            s.Set(wait, "seconds", 0d);
            s.Flow(turn, "out1", wait);
            s.Flow(wait, "then", check);
            s.Exit("LoopEnd", check, "false");
        }

        // Drawn on the machine the script runs on, which is where the package drew a local one too.
        public static void DebugLine(PackageNodeScope s)
        {
            string draw = s.Add("DrawLine");
            s.In("Point0", draw, "from");
            s.In("Point1", draw, "to");
            s.In("Color", draw, "color");
            s.In("Radius", draw, "radius", 0.005);
            s.In("Duration", draw, "seconds", 0d);
            s.Op("", draw);
            s.Exit("Next", draw, "next");
        }

        // An unwired point is the script's own slot.
        public static void DebugBox(PackageNodeScope s)
        {
            string draw = s.Add("DrawBox");
            if (s.Wired("Point"))
                s.In("Point", draw, "center");
            else
            {
                string at = s.Add("SlotPosition");
                s.Wire(s.Add("SelfSlot"), "slot", at, "slot");
                s.Wire(at, "position", draw, "center");
            }
            s.In("Size", draw, "size");
            s.In("Orientation", draw, "rotation", new[] { 0d, 0d, 0d, 1d });
            s.In("Color", draw, "color");
            s.In("Duration", draw, "seconds", 0d);
            s.Op("", draw);
            s.Exit("Next", draw, "next");
        }

        // Played like Play One Shot, then the started call, then the finished call once the sound has stopped.
        // A sound taken away before its end counts as finished, where the package's chain would end there.
        public static void PlayOneShotAndWait(PackageNodeScope s)
        {
            const string signature = "audioclip,float3,float,float,float,float,float,float,int,audioRolloffCurve,audioTypeGroup,audioDistanceSpace,float,float,bool,bool,slot";
            string play = s.Add("PlayOneShot", signature);
            if (s.WorldOf("Clip") is { } clip)
                s.Bind(play, "clip", clip, "audioclip");
            else
                s.In("Clip", play, "clip");
            s.In("Point", play, "point");
            s.In("Volume", play, "volume", 1d);
            s.In("Speed", play, "speed", 1d);
            s.In("SpatialBlend", play, "spatialBlend", 1d);
            s.In("Doppler", play, "doppler", 1d);
            s.In("MinDistance", play, "minDistance", 1d);
            s.In("MaxDistance", play, "maxDistance", 500d);
            s.In("Priority", play, "priority", 128d);
            if (Constant(s, "Rolloff") is string curve)
                s.Set(play, "rolloff", curve == "LogarithmicClamped" ? "Logarithmic" : curve);
            else
                s.In("Rolloff", play, "rolloff");
            s.In("Group", play, "group");
            s.In("DistanceSpace", play, "distanceSpace", "Global");
            s.In("MinScale", play, "minScale");
            s.In("MaxScale", play, "maxScale");
            s.In("IgnoreAudioEffects", play, "ignoreEffects");
            if (s.Wired("Root"))
                s.In("Root", play, "parent");
            else
                s.Wire(s.Add("SelfSlot"), "slot", play, "parent");
            s.Op("", play);
            s.Out("Audio", play, "sound");

            string wait = s.Add("AwaitMediaStop");
            s.Wire(play, "sound", wait, "slot");
            if (s.Wired("OnStartedPlaying"))
            {
                string both = s.Add("Sequence");
                s.Flow(play, "next", both);
                s.Exit("OnStartedPlaying", both, "out0");
                s.Flow(both, "out1", wait);
            }
            else
            {
                s.Flow(play, "next", wait);
            }
            s.Exit("OnFinishedPlaying", wait, "ended");
            s.Exit("OnFinishedPlaying", wait, "stopped");
        }

        // The player the drive held, kept to the inputs while it drives: on start, or every frame when they are
        // worked out as it runs. Its position and the drift allowed are fractions of the clip's length, and a
        // forced resync allows none on the next pass. Start drive drives the player it held again rather than
        // another one fed in.
        public static void PlaybackDrive(PackageNodeScope s)
        {
            string? driven = s.Helper("PlaybackDrive+Proxy")?.Ref("Drive");
            var slot = OwnerSlot(s, driven) ?? OwnerSlot(s, SourceId(s, "Target"));
            if (slot == null)
            {
                s.Fail($"drives {s.Index.Describe(driven)}");
                return;
            }
            string driving = s.Add("StoredValue", "bool");
            s.Set(driving, "initial", driven != null);
            string force = s.Add("StoredValue", "bool");
            s.Out("IsDriving", driving, "value");

            bool computed = false;
            foreach (var input in new[] { "NormalizedPosition", "MaximumPositionError", "Speed", "Play", "Loop" })
                computed |= s.Wired(input) && Constant(s, input) == null;
            string tick = s.Add(computed ? "OnUpdate" : "OnStart");
            string on = s.Add("If");
            s.Wire(driving, "value", on, "condition");
            s.Flow(tick, "next", on);

            string follow = s.Add("FollowPlayback");
            s.Bind(follow, "slot", slot, "slot");
            s.In("Play", follow, "playing", false);
            s.In("Loop", follow, "loop", false);
            s.In("Speed", follow, "speed", 1d);
            string length = s.Add("MediaLength");
            s.Bind(length, "slot", slot, "slot");
            if (s.Wired("NormalizedPosition"))
            {
                string at = s.Add("Multiply", "float,float");
                s.In("NormalizedPosition", at, "a");
                s.Wire(length, "seconds", at, "b");
                s.Wire(at, "result", follow, "position");
            }
            else
            {
                s.Set(follow, "position", 0d);
            }
            string allowed = s.Add("Multiply", "float,float");
            s.In("MaximumPositionError", allowed, "a", 0d);
            s.Wire(length, "seconds", allowed, "b");
            string tolerance = s.Add("Conditional", "float");
            s.Wire(force, "value", tolerance, "condition");
            s.Set(tolerance, "whenTrue", 0d);
            s.Wire(allowed, "result", tolerance, "whenFalse");
            s.Wire(tolerance, "result", follow, "tolerance");
            s.Flow(on, "true", follow);
            string settled = s.Add("WriteValue", "bool");
            s.Set(settled, "var", force);
            s.Set(settled, "value", false);
            s.Flow(follow, "next", settled);

            foreach (var (operation, after, store, value) in new[]
                     {
                         ("StartDrive", "OnStartDrive", driving, true), ("StopDrive", "OnStopDrive", driving, false),
                         ("ForceResync", "OnResync", force, true),
                     })
            {
                string write = s.Add("WriteValue", "bool");
                s.Set(write, "var", store);
                s.Set(write, "value", value);
                s.Op(operation, write);
                s.Exit(after, write, "next");
            }
        }

        // The id a source input finally names, through its source node and a global reference.
        private static string? SourceId(PackageNodeScope s, string input)
        {
            string? id = s.Node.Ref(input);
            for (int depth = 0; depth < 16 && s.Index.TryNode(id, out var node, out _); depth++)
            {
                id = node.Class switch
                {
                    "GlobalReference" => node.Ref("Reference"),
                    "ElementSource" or "SlotSource" or "ReferenceSource" or "ObjectValueSource" or "ValueSource" => node.Ref("Source"),
                    "ObjectRelay" or "ValueRelay" or "ObjectCast" => node.Ref("Input"),
                    _ => null,
                };
            }
            return id;
        }

        // The slot of whatever an id names: a slot, a component, or a member of one, ours or not.
        private static Slot? OwnerSlot(PackageNodeScope s, string? id)
        {
            var element = s.Index.World(id) ?? s.Index.OwnerOf(id, out _);
            return element switch
            {
                Slot slot => slot,
                Component component => component.Slot,
                SyncElement { Parent: Component owner } => owner.Slot,
                _ => null,
            };
        }

        // A value sent by tag goes as every send does (PackageNodeTable.Impulses.cs): kept, then each receiver
        // under the target listening for that tag and type reads it as its own.
        public static void SendWithValue(PackageNodeScope s) => SendImpulse(s);

        // Reached through its senders. With none, what reads its value reads nothing; it never runs anyway.
        public static void ReceiveWithValue(PackageNodeScope s) => ReceiveImpulse(s);

        // A value kept on this machine for as long as the script runs.
        public static void LocalValue(PackageNodeScope s)
        {
            if (s.Arg(0) == null)
            {
                s.Fail("its value's type has no counterpart here");
                return;
            }
            s.Out("", Local(s, s.Node), "value");
        }

        // The Stored Value for a local value, made by whichever of it and its writes comes first.
        private static string Local(PackageNodeScope s, PackageNode node)
        {
            var made = LocalStores.GetOrCreateValue(s.Writer);
            if (!made.TryGetValue(node, out var id))
                made[node] = id = s.Add("StoredValue", node.Arg(0)!);
            return id;
        }

        public static void UserStore(PackageNodeScope s)
        {
            if (RefHolder(s, s.Node, "user") is not { } reference)
            {
                s.Fail("its reference could not be kept");
                return;
            }
            string read = s.Add("ReferenceTarget", "user");
            s.Bind(read, "reference", reference, "reference");
            s.Out("", read, "target");
        }

        public static bool ObjectStoreOwn(PackageNodeScope s)
        {
            string? raw = ReferencedRaw(s.Node);
            if (ObjectType(raw) != null || Handle(raw) is not { } type)
                return false;
            if (RefHolder(s, s.Node, type) is not { } reference)
            {
                s.Fail("its reference's type cannot be kept here");
                return true;
            }
            string read = s.Add("ReferenceTarget", type);
            s.Bind(read, "reference", reference, "reference");
            s.Out("", read, "target");
            return true;
        }

        private static string? ReferencedRaw(PackageNode node)
        {
            if (node.Args.Count == 0)
                return null;
            string arg = node.Args[^1];
            return arg.StartsWith("SyncRef<", StringComparison.Ordinal) ? arg[8..^1] : arg;
        }

        // The synced reference a store keeps on the script's slot, seeded with what the package stored: for a
        // collider its slot. Made once per store.
        private static IWorldElement? RefHolder(PackageNodeScope s, PackageNode store, string type)
        {
            if (s.Writer.Holders.TryGetValue(store, out var made))
                return made;
            if (!ScriptTypes.TryGet(type, out var scriptType))
                return null;
            Component holder;
            try
            {
                holder = s.Home.AttachComponent(typeof(ReferenceHolder<>).MakeGenericType(scriptType.ClrType));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return null;
            }
            var member = PackageNodeIndex.MemberOf(holder, "Reference", -1);
            if (member is ISyncRef reference && s.Index.Helper(store, "+Store")?.Ref("Target") is { } seed
                && s.Index.World(seed) is { } target)
                reference.TrySet(type == "slot" && target is Component component ? component.Slot : target);
            if (member != null)
                s.Writer.Holders[store] = member;
            return member;
        }

        // Writes into a local value, a user store, a collider store, or a reference that is one of those.
        public static bool WriteOwn(PackageNodeScope s)
        {
            // A write into nothing fails there too.
            if (!s.Wired("Variable"))
            {
                s.OpThrough("", "OnFail");
                return true;
            }
            if (!s.Index.TryNode(s.Node.Ref("Variable"), out var target, out _))
                return false;
            Variable? variable = null;
            switch (target.Class)
            {
                case "LocalValue":
                    if (target.Arg(0) != null)
                        variable = new StoredVar(Local(s, target));
                    break;
                case "ReferenceInterfaceAsVariable" or "ReferenceAsVariable":
                    if (StoreBehind(s.Index, target) is { } store)
                        variable = StoreVariable(s, store);
                    if (variable == null)
                        return false;
                    break;
                case "ValueSource" or "ObjectValueSource" or "ReferenceSource":
                {
                    // A source naming nothing fails the write there too. One naming what did not come across
                    // writes nothing here, and flow goes on as if it had, the way such a drive is left out.
                    var element = WorldOf(s.Index, target, "Source");
                    if (element == null)
                    {
                        string? id = SourceIdOf(s.Index, target);
                        if (s.Index.TryNode(id, out var inGraph, out _))
                        {
                            if (!inGraph.Class.EndsWith("+Store", StringComparison.Ordinal)
                                || !s.Index.Nodes.TryGetValue(inGraph.Ref("Node") ?? string.Empty, out var served))
                                return false;
                            variable = StoreVariable(s, served)
                                ?? (Holder(s, served) is { } kept ? kept is ISyncRef ? new ReferenceVar(kept) : new FieldVar(kept) : null);
                            if (variable == null)
                                return false;
                            break;
                        }
                        if (id != null && !s.Index.Holds(id))
                            return false;
                        if (id == null)
                        {
                            s.OpThrough("", "OnFail");
                            return true;
                        }
                        s.Problem($"writes into {s.Index.Describe(id)}; the write is left out");
                        s.OpThrough("", "OnWritten");
                        return true;
                    }
                    if (WrittenType(s) != null)
                        return false;
                    variable = element is ISyncRef ? new ReferenceVar(element) : new FieldVar(element);
                    break;
                }
                default:
                    variable = StoreVariable(s, target);
                    if (variable == null)
                        return false;
                    break;
            }
            string? raw = s.Node.Args.Count > 0 ? s.Node.Args[^1] : null;
            var type = WrittenType(s) ?? Handle(raw);
            if (variable == null || type == null)
            {
                s.Fail("writes into something that did not come across");
                return true;
            }
            var (write, reports) = Write(s, variable, "Value", type);
            s.Op("", write);
            AfterWrite(s, write, reports, "OnWritten", "OnFail");
            return true;
        }

        private static Variable? StoreVariable(PackageNodeScope s, PackageNode store)
        {
            if (store.Class == "DataModelUserRefStore")
                return RefHolder(s, store, "user") is { } user ? new ReferenceVar(user) : null;
            if (store.Class == "DataModelObjectRefStore" && ObjectType(ReferencedRaw(store)) == null
                && Handle(ReferencedRaw(store)) is { } type)
                return RefHolder(s, store, type) is { } held ? new ReferenceVar(held) : null;
            return null;
        }

        // The store a reference variable reaches through sources and globals: the one whose kept reference it names.
        private static PackageNode? StoreBehind(PackageNodeIndex index, PackageNode variable)
        {
            string? id = variable.Ref("Reference");
            for (int depth = 0; depth < 16 && index.TryNode(id, out var node, out _); depth++)
            {
                if (node.Class.EndsWith("+Store", StringComparison.Ordinal))
                    return index.Nodes.TryGetValue(node.Ref("Node") ?? string.Empty, out var store) ? store : null;
                id = node.Class switch
                {
                    "GlobalReference" => node.Ref("Reference"),
                    "ElementSource" or "ReferenceSource" => node.Ref("Source"),
                    "ObjectRelay" => node.Ref("Input"),
                    _ => null,
                };
            }
            return null;
        }

        // A scoped variable of a collider or an audio source, read as the handle that stands for one here.
        public static bool ReadOwn(PackageNodeScope s)
        {
            string? raw = Raw(s, 0);
            if (ObjectType(raw) != null || Handle(raw) is not { } type)
                return false;
            string read = s.Add("ReadScopedVariable", type);
            bool named = s.Node.Class.StartsWith("DynamicVariable", StringComparison.Ordinal);
            if (!named && s.Wired("Source"))
                s.In("Source", read, "slot");
            else
                s.Wire(s.Add("SelfSlot"), "slot", read, "slot");
            s.In(named ? "VariableName" : "Path", read, "path");
            s.Out("Value", read, "value");
            s.Out(named ? "HasValue" : "FoundValue", read, "found");
            return true;
        }

        // A drive aimed at the world for a handle type only this family names, and a drive whose target the
        // package had already lost.
        public static bool ReferenceDriveOwn(PackageNodeScope s)
        {
            string? target = s.Helper("ReferenceDrive+Proxy")?.Ref("Drive");
            if (target == null || s.Index.TryNode(target, out _, out _))
                return false;
            if (!s.Index.Holds(target))
            {
                s.Problem("drives a reference the package had already lost, so it drives nothing");
                return true;
            }
            var reference = s.Index.World(target);
            if (reference == null)
            {
                s.Problem($"drives {s.Index.Describe(target)}; the drive is left out");
                return true;
            }
            string? raw = ReferencedRaw(s.Node);
            if (ObjectType(raw) != null || Handle(raw) is not { } type)
                return false;
            string drive = s.Add("DriveReference", type);
            s.Bind(drive, "reference", reference, "reference");
            s.In("Target", drive, "target");
            return true;
        }

        // A grabbable's holder is none when nothing holds it. Other handles only this family names test as they are.
        public static bool IsNullOwn(PackageNodeScope s)
        {
            string? raw = Raw(s, 0);
            if (raw == "Grabber")
            {
                var grabbable = s.Feeder("Instance") is { Class: "ReferenceSource" } source
                    ? s.Index.OwnerOf(SourceIdOf(s.Index, source), out var member) is Component owner && member == "_grabber" ? owner : null
                    : null;
                // A holder that did not come across reads as nothing, as its source does.
                if (grabbable == null)
                {
                    s.Problem($"tests the holder of {s.Index.Describe(s.Feeder("Instance") is { } f ? SourceIdOf(s.Index, f) : null)}; it holds nothing here");
                    s.OutConstant("", true);
                    return true;
                }
                string held = s.Add("SlotGrabbed");
                s.Bind(held, "slot", grabbable.Slot, "slot");
                string free = s.Add("Not", "bool");
                s.Wire(held, "grabbed", free, "value");
                s.Out("", free, "result");
                return true;
            }
            if (ObjectType(raw) != null || Handle(raw) is not { } type)
                return false;
            string none = s.Add("IsNull", type);
            s.In("Instance", none, "value");
            s.Out("", none, "null");
            return true;
        }

        private static string? SourceIdOf(PackageNodeIndex index, PackageNode source)
        {
            string? id = source.Ref("Source");
            if (index.TryNode(id, out var global, out _) && global.Class.StartsWith("GlobalReference", StringComparison.Ordinal))
                id = global.Ref("Reference");
            return id;
        }

        // The entry at the index, nothing past the list: Selects of four, the last of each the rest.
        public static void ObjectMultiplex(PackageNodeScope s)
        {
            int count = s.Node.Refs("Inputs").Count;
            s.OutConstant("InputCount", (double)count);
            if (Handle(Raw(s, 0)) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            if (count == 0)
            {
                s.OutConstant("Output", null);
                return;
            }
            s.Out("Output", Pick(s, type, 0, count), "result");
        }

        // The same for a value, its type's empty value past the list.
        public static void ValueMultiplex(PackageNodeScope s)
        {
            int count = s.Node.Refs("Inputs").Count;
            s.OutConstant("InputCount", (double)count);
            if (s.Arg(0) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            if (count == 0)
            {
                s.OutConstant("Output", null);
                return;
            }
            s.Out("Output", Pick(s, type, 0, count), "result");
        }

        // A Select gives nothing past its four, so where the rest go on through d, an index past its three is
        // held at d and the rest pick by it for themselves.
        private static string Pick(PackageNodeScope s, string type, int start, int count)
        {
            string select = s.Add("Select", type);
            int here = count - start <= 4 ? count - start : 3;
            bool more = start + here < count;
            string? index = null;
            if (start != 0)
            {
                index = s.Add("Subtract", "int,int");
                s.In("Index", index, "a");
                s.Set(index, "b", (double)start);
            }
            if (more)
            {
                string held = s.Add("Min", "int,int");
                if (index == null)
                    s.In("Index", held, "a");
                else
                    s.Wire(index, "result", held, "a");
                s.Set(held, "b", 3d);
                index = held;
            }
            if (index == null)
                s.In("Index", select, "index");
            else
                s.Wire(index, "result", select, "index");
            for (int k = 0; k < here; k++)
                s.InAt("Inputs", start + k, select, Lettered(k));
            if (more)
                s.Wire(Pick(s, type, start + here, count), "result", select, "d");
            return select;
        }

        // The first operand that holds something.
        public static void NullCoalesce(PackageNodeScope s)
        {
            int count = s.Node.Refs("Operands").Count;
            if (Handle(Raw(s, 0)) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            if (count == 0)
            {
                s.OutConstant("", null);
                return;
            }
            string? rest = null;
            for (int i = count - 1; i >= 0; i--)
            {
                string pick = s.Add("Fallback", type);
                s.InAt("Operands", i, pick, "value");
                if (rest != null)
                    s.Wire(rest, "result", pick, "fallback");
                rest = pick;
            }
            s.Out("", rest!, "result");
        }

        public static void AllocatingUser(PackageNodeScope s)
        {
            bool component = s.WorldOf("Element") is Component;
            string made = s.Add("MadeBy", component ? "component" : "slot");
            s.In("Element", made, component ? "component" : "slot");
            s.Out("", made, "user");
        }

        // An undo point for a field's value; ours keeps one per record, so forcing a new one changes nothing.
        public static void FieldUndoStep(PackageNodeScope s)
        {
            var field = s.WorldOf("Target");
            if (field == null && s.Feeder("Target") is { Class: "RefObjectInput" } named)
                field = s.Index.World(named.Ref("Target"));
            string? type = field != null ? FieldType(field) : null;
            if (type == null && s.Feeder("Target") is { } feeder)
            {
                foreach (var arg in feeder.Args)
                {
                    if (PackageNode.OurType(arg) is { } held && held.StartsWith("field<", StringComparison.Ordinal))
                    {
                        type = held[6..^1];
                        break;
                    }
                }
            }
            if (type == null)
            {
                s.Fail("records a field whose type has no counterpart here");
                return;
            }
            string record = s.Add("RecordField", type);
            if (field != null)
                s.Bind(record, "field", field, $"field<{type}>");
            else
                s.In("Target", record, "field");
            s.Op("", record);
            s.Exit("Next", record, "next");
        }

        // Ours for an enum of the package's, where the names line up or are translated below.
        public static string? EnumOf(string? their)
        {
            string? ours = their switch
            {
                "HeadOutputDevice" => "headOutputDevice",
                "VoiceMode" => "voiceMode",
                "SessionAccessLevel" => "worldAccessLevel",
                _ => null,
            };
            return ours != null && ScriptTypes.TryGet(ours, out _) ? ours : null;
        }

        private static string EnumName(string ours, string value) => ours switch
        {
            "headOutputDevice" => value switch
            {
                "Headless" => "Server",
                "Screen" or "Screen360" or "Autodetect" or "UNKNOWN" => "Screen",
                "StaticCamera" or "StaticCamera360" => "Camera",
                _ => "VR",
            },
            _ => value,
        };

        private static void EnumIn(PackageNodeScope s, string input, string node, string port, string type)
        {
            if (Constant(s, input) is string name)
                s.Set(node, port, EnumName(type, name));
            else
                s.In(input, node, port);
        }

        public static void CompareEnums(PackageNodeScope s, string type, bool equal)
        {
            string same = s.Add("IsSame", type);
            EnumIn(s, "A", same, "a", type);
            EnumIn(s, "B", same, "b", type);
            if (equal)
            {
                s.Out("", same, "same");
                return;
            }
            string not = s.Add("Not", "bool");
            s.Wire(same, "same", not, "value");
            s.Out("", not, "result");
        }

        // The package's number for the value. Its access levels have no group levels: those read as contacts
        // plus, and the two above them keep the package's numbers.
        public static void EnumToInt(PackageNodeScope s)
        {
            if (EnumOf(Raw(s, 0)) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            string number = s.Add("EnumToNumber", type);
            s.In("Value", number, "value");
            if (type != "worldAccessLevel")
            {
                s.Out("", number, "number");
                return;
            }
            string low = s.Add("Min", "int,int");
            s.Wire(number, "number", low, "a");
            s.Set(low, "b", 3d);
            string high = s.Add("Subtract", "int,int");
            s.Wire(number, "number", high, "a");
            s.Set(high, "b", 3d);
            string open = s.Add("GreaterOrEqual", "int,int");
            s.Wire(number, "number", open, "a");
            s.Set(open, "b", 7d);
            string pick = s.Add("Conditional", "int");
            s.Wire(open, "result", pick, "condition");
            s.Wire(high, "result", pick, "whenTrue");
            s.Wire(low, "result", pick, "whenFalse");
            s.Out("", pick, "result");
        }
    }
}
