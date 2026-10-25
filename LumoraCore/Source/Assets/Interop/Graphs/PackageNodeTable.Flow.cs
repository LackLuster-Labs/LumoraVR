// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lumora.Core.Assets.Interop;

// Flow: branches, waits, starting a chain beside another, sending flow to receivers by tag
// (PackageNodeTable.Impulses.cs), and the buttons on a card. A wait here ends its chain and runs what follows as a call of its own, so a chain that went on at
// once beside a wait (a started task, a wait's own trigger) is a Sequence that runs it first. -xlinka
internal static partial class PackageNodeTable
{
    private static void Flow()
    {
        Map("If", "If", null, ins: "Condition=condition", ops: "=in", exits: "OnTrue=true OnFalse=false");

        On("StartAsyncTask", s =>
        {
            bool task = s.Wired("TaskStart"), started = s.Wired("OnStarted");
            if (task && started)
            {
                string both = s.Add("Sequence");
                s.Op("", both);
                s.Exit("TaskStart", both, "out0");
                s.Exit("OnStarted", both, "out1");
                return;
            }
            s.OpThrough("", task ? "TaskStart" : started ? "OnStarted" : "OnFailed");
        });

        Same(s =>
        {
            string wait = s.Add("Delay");
            s.In("Duration", wait, "seconds", 0d);
            Start(s, wait);
            s.Exit("Next", wait, "then");
        }, "DelaySecondsFloat", "DelayUpdatesOrSecondsFloat");

        // A number of frames: one Delay of no time waits one frame, so that many in a row.
        Same(s =>
        {
            int frames = 1;
            if (s.Feeder("Updates") is { } count && count.Class == "ValueInput" && count.Value("Value") is IConvertible n)
                frames = System.Math.Clamp(Convert.ToInt32(n, CultureInfo.InvariantCulture), 1, 64);
            else if (s.Wired("Updates"))
                s.Problem("waits a frame count worked out as it runs; it waits one frame");
            string first = s.Add("Delay");
            s.Set(first, "seconds", 0d);
            string last = first;
            for (int i = 1; i < frames; i++)
            {
                string next = s.Add("Delay");
                s.Set(next, "seconds", 0d);
                s.Flow(last, "then", next);
                last = next;
            }
            if (s.Node.Class == "DelayUpdatesWithObject")
            {
                if (ObjectType(s.Node.Args.Count > 0 ? s.Node.Args[0] : null) is not { } type)
                {
                    s.Fail("its value's type has no counterpart here");
                    return;
                }
                string kept = s.Add("StoredValue", type);
                string keep = s.Add("WriteValue", type);
                s.Set(keep, "var", kept);
                s.In("Value", keep, "value");
                s.Out("DelayedValue", kept, "value");
                s.Op("", keep);
                // The value is kept before the trigger's chain runs, as the package keeps it.
                if (s.Wired("OnTriggered"))
                {
                    string both = s.Add("Sequence");
                    s.Flow(keep, "next", both);
                    s.Exit("OnTriggered", both, "out0");
                    s.Flow(both, "out1", first);
                }
                else
                {
                    s.Flow(keep, "next", first);
                }
            }
            else
            {
                Start(s, first);
            }
            s.Exit("Next", last, "then");
        }, "DelayUpdates", "DelayUpdatesWithObject");

        // The start runs once before the first turn, then the body while the condition holds.
        On("While", s =>
        {
            string loop = s.Add("While");
            s.In("Condition", loop, "condition", false);
            s.Exit("LoopIteration", loop, "body");
            s.Exit("LoopEnd", loop, "completed");
            if (!s.Wired("LoopStart"))
            {
                s.Op("", loop);
                return;
            }
            string both = s.Add("Sequence");
            s.Op("", both);
            s.Exit("LoopStart", both, "out0");
            s.Flow(both, "out1", loop);
        });

        // The value as flow arrived, kept for after the wait, then the trigger's chain, then the wait.
        Same(s =>
        {
            if (s.Arg(0) is not { } type)
            {
                s.Fail("its value's type has no counterpart here");
                return;
            }
            string kept = s.Add("StoredValue", type);
            string keep = s.Add("WriteValue", type);
            s.Set(keep, "var", kept);
            s.In("Value", keep, "value");
            s.Out("DelayedValue", kept, "value");
            s.Op("", keep);
            string wait = s.Add("Delay");
            s.In("Duration", wait, "seconds", 0d);
            if (s.Wired("OnTriggered"))
            {
                string both = s.Add("Sequence");
                s.Flow(keep, "next", both);
                s.Exit("OnTriggered", both, "out0");
                s.Flow(both, "out1", wait);
            }
            else
            {
                s.Flow(keep, "next", wait);
            }
            s.Exit("Next", wait, "then");
        }, "DelayWithValueSecondsFloat", "DelayWithValueSecondsDouble", "DelayWithValueSecondsInt");

        // What the value was the delay ago: every frame it is noted with the time, notes older than the delay are
        // dropped, and the oldest left is the answer, nothing before the first note.
        On("DelayValue", s =>
        {
            if (s.Arg(0) is not { } type)
            {
                s.Fail("its value's type has no counterpart here");
                return;
            }
            string list = $"list<{type}>";
            string values = s.Add("StoredValue", list);
            string stamps = s.Add("StoredValue", "list<double>");
            string answer = s.Add("ListGetAt", type);
            s.Wire(values, "value", answer, "list");
            s.Set(answer, "index", 0d);
            s.Out("DelayedValue", answer, "item");

            string tick = s.Add("OnUpdate");
            string missing = s.Add("IsNull", list);
            s.Wire(values, "value", missing, "value");
            string first = s.Add("If");
            s.Wire(missing, "null", first, "condition");
            s.Flow(tick, "next", first);
            string makeValues = s.Add("NewList", type);
            string setValues = s.Add("WriteValue", list);
            s.Set(setValues, "var", values);
            s.Wire(makeValues, "list", setValues, "value");
            string makeStamps = s.Add("NewList", "double");
            string setStamps = s.Add("WriteValue", "list<double>");
            s.Set(setStamps, "var", stamps);
            s.Wire(makeStamps, "list", setStamps, "value");
            s.Flow(first, "true", makeValues);
            s.Flow(makeValues, "next", setValues);
            s.Flow(setValues, "next", makeStamps);
            s.Flow(makeStamps, "next", setStamps);

            string now = s.Add("WorldTime");
            string count = s.Add("ListCount", "double");
            s.Wire(stamps, "value", count, "list");
            string any = s.Add("GreaterThan", "int,int");
            s.Wire(count, "result", any, "a");
            s.Set(any, "b", 0d);
            string oldest = s.Add("ListGetAt", "double");
            s.Wire(stamps, "value", oldest, "list");
            s.Set(oldest, "index", 0d);
            string age = s.Add("Subtract", "double,double");
            s.Wire(now, "seconds", age, "a");
            s.Wire(oldest, "item", age, "b");
            string stale = s.Add("GreaterThan", "double,double");
            s.Wire(age, "result", stale, "a");
            s.In("DelaySeconds", stale, "b", 0d);
            string drop = s.Add("And", "bool,bool");
            s.Wire(any, "result", drop, "a");
            s.Wire(stale, "result", drop, "b");
            string trim = s.Add("While");
            s.Wire(drop, "result", trim, "condition");
            s.Flow(first, "false", trim);
            s.Flow(setStamps, "next", trim);
            string dropValue = s.Add("ListRemoveAt", type);
            s.Wire(values, "value", dropValue, "list");
            s.Set(dropValue, "index", 0d);
            string dropStamp = s.Add("ListRemoveAt", "double");
            s.Wire(stamps, "value", dropStamp, "list");
            s.Set(dropStamp, "index", 0d);
            s.Flow(trim, "body", dropValue);
            s.Flow(dropValue, "next", dropStamp);
            string noteValue = s.Add("ListAdd", type);
            s.Wire(values, "value", noteValue, "list");
            s.In("Value", noteValue, "item");
            string noteStamp = s.Add("ListAdd", "double");
            s.Wire(stamps, "value", noteStamp, "list");
            s.Wire(now, "seconds", noteStamp, "item");
            s.Flow(trim, "completed", noteValue);
            s.Flow(noteValue, "next", noteStamp);
        });

        On("LocalImpulseTimeoutSeconds", s =>
        {
            string gate = s.Add("Cooldown");
            s.In("Timeout", gate, "seconds", 0d);
            s.Op("Trigger", gate);
            s.Exit("Next", gate, "next");
        });

        Same(s =>
        {
            string call = s.Add("Call");
            s.Exit("Target", call, "next");
        }, "CallInput", "AsyncCallInput");

        Same(SendImpulse, "DynamicImpulseTrigger", "DynamicImpulseTriggerWithObject");
        Same(ReceiveImpulse, "DynamicImpulseReceiver", "DynamicImpulseReceiverWithObject");
    }

    // A wait with a trigger of its own: the trigger's chain runs first, then the wait. Wait null means the
    // node's own flow in is already taken and only the trigger needs placing ahead of it.
    private static void Start(PackageNodeScope s, string? wait)
    {
        if (!s.Wired("OnTriggered"))
        {
            if (wait != null)
                s.Op("", wait);
            return;
        }
        string both = s.Add("Sequence");
        s.Op("", both);
        s.Exit("OnTriggered", both, "out0");
        if (wait != null)
            s.Flow(both, "out1", wait);
    }
}
