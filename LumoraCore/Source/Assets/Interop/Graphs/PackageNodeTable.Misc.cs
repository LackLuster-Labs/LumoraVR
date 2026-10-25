// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Core.Assets.Interop;

// Time kept since a reset, sounds played once, tweens, scoped variables, counters, and the components that
// only remember what an on-change node saw last. -xlinka
internal static partial class PackageNodeTable
{
    private static void Misc()
    {
        // A render with no camera never starts, there or here: flow stops and nothing is written.
        On("RenderToTextureAsset", s =>
        {
            if (!s.Unset("Camera"))
            {
                s.Fail("renders a camera to an image, which has no counterpart here yet");
                return;
            }
            s.Problem("renders no camera, so it never renders");
            s.OpEnds("");
            s.OutConstant("RenderedAssetURL", null);
        });

        // What a change node saw last; a Watch remembers its own.
        Same(_ => { }, "ValueProxy", "RefProxy", "DynamicVariableInputProxy");

        // Seconds since the last reset, counting on from what the package saved.
        On("ElapsedTimeFloat", s =>
        {
            double saved = 0;
            if (s.Helper("ElapsedTime+Proxy")?.Value("StartTime") is IConvertible stored)
                saved = Convert.ToDouble(stored, System.Globalization.CultureInfo.InvariantCulture);

            string start = s.Add("StoredValue", "double");
            string begin = s.Add("OnStart");
            string clock = s.Add("WorldTime");
            string back = s.Add("Subtract", "double,double");
            s.Wire(clock, "seconds", back, "a");
            s.Set(back, "b", saved);
            string seed = s.Add("WriteValue", "double");
            s.Set(seed, "var", start);
            s.Wire(back, "result", seed, "value");
            s.Flow(begin, "next", seed);

            string since = s.Add("Subtract", "double,double");
            s.Wire(clock, "seconds", since, "a");
            s.Wire(start, "value", since, "b");
            string seconds = s.Add("ToFloat", "double");
            s.Wire(since, "result", seconds, "value");
            s.Out("", seconds, "result");

            string reset = s.Add("WriteValue", "double");
            s.Set(reset, "var", start);
            s.Wire(clock, "seconds", reset, "value");
            s.Op("Reset", reset);
            s.Exit("OnReset", reset, "next");
        });

        On("PlayOneShot", s =>
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
            if (s.Feeder("Rolloff") is { Class: "ValueInput" } rolloff && rolloff.Value("Value") is string curve)
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
            s.Exit("OnStartedPlaying", play, "next");
        });

        // To unwired means where the field already is.
        On("TweenValue", s =>
        {
            if (s.Arg(0) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            string tween = s.Add("TweenField", type);
            s.In("Target", tween, "field");
            if (s.Wired("To"))
                s.In("To", tween, "to");
            else
            {
                string now = s.Add("GetField", type);
                s.In("Target", now, "field");
                s.Wire(now, "result", tween, "to");
            }
            if (s.Wired("From"))
            {
                s.In("From", tween, "from");
                s.Set(tween, "useFrom", true);
            }
            s.In("Duration", tween, "duration", 1d);
            s.In("ProportionalDuration", tween, "perUnit");
            if (s.Feeder("Curve") is { Class: "ValueInput" } preset && preset.Value("Value") is string curve)
            {
                var (ours, direction) = Curve(curve);
                s.Set(tween, "curve", ours);
                s.Set(tween, "direction", direction);
            }
            s.Op("", tween);
            s.Exit("OnStarted", tween, "next");
            s.Exit("OnDone", tween, "done");
        });

        On("WriteDynamicValueVariable", s =>
        {
            if (s.Arg(0) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            string write = s.Add("WriteScopedVariable", type);
            if (s.Wired("Target"))
                s.In("Target", write, "slot");
            else
                s.Wire(s.Add("SelfSlot"), "slot", write, "slot");
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
        });

        // A scoped variable read by a name the graph keeps as a value, from the script's own slot up.
        Same(s =>
        {
            var type = s.Node.Class.Contains("Object") ? ObjectType(s.Node.Args.Count > 0 ? s.Node.Args[0] : null) : s.Arg(0);
            if (type == null)
            {
                s.Fail("its value's type has no counterpart here");
                return;
            }
            bool named = s.Node.Class.StartsWith("DynamicVariable", StringComparison.Ordinal);
            // No named variable here holds a list, so one of that name is never there to read.
            if (type.StartsWith("list<", StringComparison.Ordinal))
            {
                s.Problem("reads a named variable holding a list, which named variables here never hold; it reads as not there");
                s.OutConstant("Value", null);
                s.OutConstant(named ? "HasValue" : "FoundValue", false);
                return;
            }
            string read = s.Add("ReadScopedVariable", type);
            if (!named && s.Wired("Source"))
                s.In("Source", read, "slot");
            else
                s.Wire(s.Add("SelfSlot"), "slot", read, "slot");
            s.In(named ? "VariableName" : "Path", read, "path");
            s.Out("Value", read, "value");
            s.Out(named ? "HasValue" : "FoundValue", read, "found");
        }, "DynamicVariableValueInput", "DynamicVariableObjectInput", "ReadDynamicValueVariable", "ReadDynamicObjectVariable");

        On("AssetInput", s =>
        {
            var asset = s.Index.World(s.Node.Ref("Target"));
            if (asset == null)
                s.Problem($"names {s.Index.Describe(s.Node.Ref("Target"))}");
            s.OutWorld("", asset);
        });

        // Something there and not gone: Is Null, then Not.
        On("ElementExists", s =>
        {
            var feeder = s.Feeder("Element");
            string type = feeder?.Args.Count > 0 ? ObjectType(feeder.Args[0]) ?? "slot" : "slot";
            string none = s.Add("IsNull", type);
            s.In("Element", none, "value");
            string there = s.Add("Not", "bool");
            s.Wire(none, "null", there, "value");
            s.Out("", there, "result");
        });

        Same(s => Step(s, up: true), "ValueIncrement", "ValueIndirectIncrement");
        Same(s => Step(s, up: false), "ValueDecrement", "ValueIndirectDecrement");

        Map("GetTag", "SlotTag", null, ins: "Instance=slot", outs: "=tag");

        // The item at an index of a list, the item type's default outside it.
        On("GetAtValue", s =>
        {
            if (s.Arg(0) is not { } list || !list.StartsWith("list<", StringComparison.Ordinal) || s.Arg(1) is not { } item)
            {
                s.Fail("reads a collection of a kind that has no counterpart here");
                return;
            }
            string at = s.Add("ListGetAt", item);
            s.In("Collection", at, "list");
            s.In("Index", at, "index", 0d);
            s.Out("", at, "item");
        });
        On("UserFromID", s =>
        {
            string find = s.Add("UserFromId");
            s.In("UserId", find, "id");
            s.Out("", find, "user");
            if (s.Wired("MachineId"))
                s.Problem("also looks a user up by machine id, which scripts here never see; only the user id is looked up");
        });
        On("HeadSlot", s =>
        {
            string head = s.Add("UserTrackedSlot");
            s.In("UserRoot", head, "user");
            s.Set(head, "node", "Head");
            s.Out("", head, "slot");
        });
        On("IsPlatformDashOpened", s =>
        {
            string presence = s.Add("UserPresence");
            s.In("User", presence, "user");
            s.Out("", presence, "dashOpen");
        });

        // What to leave out, as what to show; the flash root it gives back is not kept here.
        On("FlashHighlightHierarchy", s =>
        {
            string flash = s.Add("FlashHighlight");
            s.In("HierarchyRoot", flash, "root");
            s.In("Color", flash, "color", new[] { 1d, 1d, 1d, 1d });
            s.In("Duration", flash, "seconds", 0.5d);
            s.In("TrackPosition", flash, "follow", true);
            foreach (var (theirs, ours) in new[] { ("ExcludeMeshes", "meshes"), ("ExcludeColliders", "colliders"), ("ExcludeDisabled", "hidden") })
            {
                string keep = s.Add("Not", "bool");
                s.In(theirs, keep, "value", false);
                s.Wire(keep, "result", flash, ours);
            }
            s.OutConstant("FlashRoot", null);
            s.Op("", flash);
            s.Exit("Next", flash, "next");
        });

        // An element's id is the element itself here, and a drive of an id field a drive of the reference.
        On("ReferenceID", s => s.OutThrough("", "Element"));

        On("OpenWorld", s => s.Fail("opens a world by its address in the package; scripts here never take anyone to another world"));
    }

    // One up or down on a number something writes into: a field, or a value the graph keeps.
    private static void Step(PackageNodeScope s, bool up)
    {
        var target = VariableOf(s, "Variable");
        var type = WrittenType(s);
        if (target == null || type == null)
        {
            s.Fail($"counts {(up ? "up" : "down")} something that did not come across");
            return;
        }
        string step;
        bool reports = true;
        switch (target)
        {
            case FieldVar field:
                step = s.Add(up ? "IncrementField" : "DecrementField", type);
                s.Bind(step, "field", field.Field, $"field<{type}>");
                break;
            case FieldFrom from:
                step = s.Add(up ? "IncrementField" : "DecrementField", type);
                s.InFrom(from.Node, from.Input, step, "field");
                break;
            case StoredVar stored:
                step = s.Add(up ? "IncrementValue" : "DecrementValue", type);
                s.Set(step, "var", stored.OurNode);
                reports = false;
                break;
            default:
                s.Fail($"counts {(up ? "up" : "down")} a reference");
                return;
        }
        s.Op("", step);
        AfterWrite(s, step, reports, "OnWritten", "OnFail");
    }

    // A package curve preset as our curve and direction.
    private static (string Curve, string Direction) Curve(string preset)
    {
        foreach (var direction in new[] { "InOut", "In", "Out" })
        {
            if (!preset.EndsWith(direction, StringComparison.Ordinal) || preset.Length == direction.Length)
                continue;
            string curve = preset[..^direction.Length];
            curve = curve switch
            {
                "Quadradic" => "Quadratic",
                "ReboundSmall" or "ReboundLarge" or "Rebound" => "Back",
                "ElasticSmall" or "ElasticLarge" => "Elastic",
                _ => curve,
            };
            return (curve, direction);
        }
        return preset switch
        {
            "Sine" => ("Sine", "Out"),
            "Cosine" => ("Sine", "In"),
            _ => (preset, "InOut"),
        };
    }
}
