// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;

namespace Lumora.Core.Assets.Interop;

// What starts flow. A package's per-frame and on-change nodes become On Update, with a Watch for a change and
// an If for which way it went; its button events become sourced press and hover events on the button's slot.
// Which machine they run on is the script's mode, decided for the whole graph. -xlinka
internal static partial class PackageNodeTable
{
    private static void Events()
    {
        On("Update", s =>
        {
            string tick = s.Add("OnUpdate");
            s.Exit("OnUpdate", tick, "next");
        });

        On("FireWhileTrue", s =>
        {
            string tick = s.Add("OnUpdate");
            string check = s.Add("If");
            s.Flow(tick, "next", check);
            s.In("Condition", check, "condition");
            s.Exit("OnUpdate", check, "true");
        });

        Same(s =>
        {
            bool onTrue = s.Node.Class == "FireOnTrue";
            string tick = s.Add("OnUpdate");
            string watch = s.Add("Watch", "bool");
            string which = s.Add("If");
            s.Flow(tick, "next", watch);
            s.In("Condition", watch, "value");
            s.Flow(watch, "changed", which);
            s.In("Condition", which, "condition");
            s.Exit("OnChanged", which, onTrue ? "true" : "false");
        }, "FireOnTrue", "FireOnFalse");

        // A changed thing is another thing, or none once it has left the world.
        Same(s =>
        {
            if (ObjectType(s.Node.Args.Count > 0 ? s.Node.Args[0] : null) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            string tick = s.Add("OnUpdate");
            string watch = s.Add("Watch", type);
            s.Flow(tick, "next", watch);
            s.In("Value", watch, "value");
            s.Exit("OnChanged", watch, "changed");
        }, "FireOnValueChange", "FireOnRefChange");

        On("OnLoaded", s =>
        {
            string start = s.Add("OnStart");
            s.Exit("Trigger", start, "next");
        });
        On("OnDuplicate", s =>
        {
            string copied = s.Add("OnDuplicated");
            s.Exit("Trigger", copied, "next");
        });

        On("ButtonEvents", s =>
        {
            var button = s.WorldOf("Button");
            var slot = button switch
            {
                Slot direct => direct,
                Component component => component.Slot,
                _ => null,
            };
            if (slot == null)
            {
                string? id = s.Node.Ref("Button");
                if (s.Index.TryNode(id, out var global, out _))
                    id = global.Ref("Reference");
                // A button that did not come across, or no button at all, is never pressed: nothing leaves it,
                // and what reads its press point reads nothing. The rest of the graph still runs.
                s.Problem(id != null ? $"listens to {s.Index.Describe(id)}, so it never fires" : "listens to no button, so it never fires");
                s.OutConstant("GlobalPoint", null);
                s.OutConstant("LocalPoint", null);
                s.OutConstant("NormalizedPoint", null);
                return;
            }
            ButtonEvents(s, slot);
        });
    }

    // A press is heard as it lands, every frame it is held and as it ends, each carrying where it is. Each keeps
    // where it was in a Stored Value before going on, so whatever reads the point after any of them, or after a
    // wait, reads the latest one, as the package's single set of outputs does. -xlinka
    private static void ButtonEvents(PackageNodeScope s, Slot slot)
    {
        var points = new List<(string Theirs, string Ours, string Type)>();
        foreach (var point in new[] { ("GlobalPoint", "point", "float3"), ("LocalPoint", "localPoint", "float2"), ("NormalizedPoint", "normalizedPoint", "float2") })
        {
            if (s.IsRead(point.Item1))
                points.Add(point);
        }
        var steps = new List<(string Call, string Heard)>();
        foreach (var (call, ours) in new[]
                 {
                     ("Pressed", "OnPressBegin"), ("Pressing", "OnPressHold"), ("Released", "OnPressEnd"),
                     ("HoverEnter", "OnHoverEnter"), ("HoverStay", "OnHoverStay"), ("HoverLeave", "OnHoverExit"),
                 })
        {
            if (!s.Wired(call))
                continue;
            string heard = s.Add(ours);
            s.Bind(heard, "source", slot, "slot");
            if (call is "Pressed" or "Pressing" or "Released")
                steps.Add((call, heard));
            else
                s.Exit(call, heard, "next");
        }
        if (s.IsRead("Source"))
        {
            s.Problem("its press source is not carried, so what reads it reads nothing");
            s.OutConstant("Source", null);
        }
        if (steps.Count == 0)
        {
            if (points.Count > 0)
                s.Problem("its hover points are not carried, so what reads them reads nothing");
            foreach (var (theirs, _, _) in points)
                s.OutConstant(theirs, null);
            return;
        }
        if (steps.Count == 1 && points.Count == 0)
        {
            s.Exit(steps[0].Call, steps[0].Heard, "next");
            return;
        }
        var kept = new List<string>();
        foreach (var (theirs, _, type) in points)
        {
            string keep = s.Add("StoredValue", type);
            s.Out(theirs, keep, "value");
            kept.Add(keep);
        }
        foreach (var (call, heard) in steps)
        {
            string from = heard;
            for (int i = 0; i < points.Count; i++)
            {
                string write = s.Add("WriteValue", points[i].Type);
                s.Set(write, "var", kept[i]);
                s.Wire(heard, points[i].Ours, write, "value");
                s.Flow(from, "next", write);
                from = write;
            }
            s.Exit(call, from, "next");
        }
    }
}
