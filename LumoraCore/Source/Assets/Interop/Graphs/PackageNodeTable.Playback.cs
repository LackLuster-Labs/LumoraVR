// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// Players and loads. Anything that plays is its slot here, as the media nodes take it. A load tracker the
// package kept per person is read for the machine the script runs on: each machine loads its own copy, so
// that is the one answer it has. -xlinka
internal static partial class PackageNodeTable
{
    private static void Playback()
    {
        Map("Pause", "PauseMedia", null, ins: "Target=slot", ops: "=in", exits: "Next=next");
        Map("Resume", "ResumeMedia", null, ins: "Target=slot", ops: "=in", exits: "Next=next");
        Map("Toggle", "ToggleMedia", null, ins: "Target=slot", ops: "=in", exits: "Next=next");
        Map("ShiftPosition", "SkipMedia", null, ins: "Target=slot Delta=seconds", ops: "=in", exits: "Next=next");
        Map("SetNormalizedPosition", "SeekMediaFraction", null, ins: "Target=slot NormalizedPosition=fraction", ops: "=in", exits: "Next=next");
        Map("SetLoop", "SetMediaLoop", null, ins: "Target=slot Loop=loop", ops: "=in", exits: "Next=next", values: "Loop=false");
        Map("SetSpeed", "SetMediaSpeed", null, ins: "Target=slot Speed=speed", ops: "=in", exits: "Next=next", values: "Speed=0");

        Map("Position", "MediaPosition", null, ins: "Source=slot", outs: "=seconds");
        Map("IsLooped", "MediaState", null, ins: "Source=slot", outs: "=loop");
        Map("Speed", "MediaState", null, ins: "Source=slot", outs: "=speed");

        // A length or a fraction not known reads -1 here and 0 in the package, which graphs divide by and draw bars with.
        Same(s =>
        {
            bool exact = s.Node.Class == "ClipLengthDouble";
            string length = s.Add("MediaLength");
            s.In("Source", length, "slot");
            s.Out("", AtLeastZero(s, length, exact ? "exact" : "seconds", exact ? "double" : "float"), "result");
        }, "ClipLengthFloat", "ClipLengthDouble");
        On("NormalizedPosition", s =>
        {
            string state = s.Add("MediaState");
            s.In("Source", state, "slot");
            s.Out("", AtLeastZero(s, state, "fraction", "float"), "result");
        });
        On("PlaybackState", s =>
        {
            string state = s.Add("MediaState");
            s.In("Source", state, "slot");
            foreach (var (theirs, ours) in new[] { ("IsPlaying", "playing"), ("Loop", "loop"), ("Position", "position"), ("Speed", "speed") })
                s.Out(theirs, state, ours);
            s.Out("NormalizedPosition", AtLeastZero(s, state, "fraction", "float"), "result");
            s.Out("ClipLength", AtLeastZero(s, state, "length", "float"), "result");
        });

        // Waits on a frame after the player stops, however it stopped.
        foreach (var (cls, play, begun) in new[] { ("PlayAndWait", true, "OnStarted"), ("Wait", false, "OnWaitBegin") })
        {
            On(cls, s =>
            {
                string wait = s.Add("AwaitMediaStop");
                s.In("Target", wait, "slot");
                string? started = null;
                if (play)
                {
                    started = s.Add("PlayMedia");
                    s.In("Target", started, "slot");
                }
                string next = wait;
                if (s.Wired(begun))
                {
                    next = s.Add("Sequence");
                    s.Exit(begun, next, "out0");
                    s.Flow(next, "out1", wait);
                }
                if (started != null)
                {
                    s.Flow(started, "next", next);
                    s.Op("", started);
                }
                else
                {
                    s.Op("", next);
                }
                s.Exit("OnPlaybackFinished", wait, "ended");
                s.Exit("OnPlaybackFinished", wait, "stopped");
            });
        }

        On("AssetLoadProgress", s =>
        {
            var asset = TrackedAsset(s, "Tracker", out string why);
            if (asset is not IAssetProvider provider || AssetHandle(provider) is not { } type)
            {
                s.Fail(why);
                return;
            }
            string load = s.Add("AssetLoadInfo", type);
            s.Bind(load, "asset", provider, type);
            s.Out("LoadState", load, "state");
            s.Out("DownloadProgress", load, "progress");
            if (s.Wired("User") && s.Feeder("User")?.Class is not ("LocalUser" or "MachineUser"))
                s.Problem("reads someone's load, which only their machine knows; this machine's own is read");
        });

        // Only ever read by the load node above, which finds the tracker by its name itself.
        if (TryGet("DynamicVariableObjectInput", out var variable))
        {
            On("DynamicVariableObjectInput", s =>
            {
                if (s.Node.Args.Count == 0 || !s.Node.Args[0].StartsWith("UsersAssetLoadProgress", StringComparison.Ordinal))
                {
                    variable(s);
                    return;
                }
                s.OutConstant("Value", null);
                s.OutConstant("HasValue", TrackerNamed(s.Index, s.Index.ConstantText(s.Node.Ref("VariableName")), out _));
            });
        }

        // A player kept in a named variable is kept as its component here; the media nodes take its slot.
        foreach (var cls in new[] { "DynamicVariableObjectInput", "ReadDynamicObjectVariable" })
        {
            if (!TryGet(cls, out var plain))
                continue;
            On(cls, s =>
            {
                if (s.Node.Args.Count == 0 || s.Node.Args[0] != "IPlayable")
                {
                    plain(s);
                    return;
                }
                bool named = s.Node.Class == "DynamicVariableObjectInput";
                string read = s.Add("ReadScopedVariable", "component");
                if (!named && s.Wired("Source"))
                    s.In("Source", read, "slot");
                else
                    s.Wire(s.Add("SelfSlot"), "slot", read, "slot");
                s.In(named ? "VariableName" : "Path", read, "path");
                string slot = s.Add("ComponentSlot");
                s.Wire(read, "value", slot, "component");
                s.Out("Value", slot, "slot");
                s.Out(named ? "HasValue" : "FoundValue", read, "found");
            });
        }
    }

    // The asset a load tracker watches: the tracker read from a named variable, through relays, and its asset.
    private static IWorldElement? TrackedAsset(PackageNodeScope s, string input, out string why)
    {
        var feeder = s.Feeder(input);
        if (feeder?.Class is not ("DynamicVariableObjectInput" or "ReadDynamicObjectVariable"))
        {
            why = "reads a load tracker the graph works out as it runs";
            return null;
        }
        string? name = s.Index.ConstantText(feeder.Ref(feeder.Class == "DynamicVariableObjectInput" ? "VariableName" : "Path"));
        why = $"reads a load tracker named '{name}' that nothing in the package holds";
        if (!TrackerNamed(s.Index, name, out var asset))
            return null;
        if (asset == null)
            why = $"reads a load tracker named '{name}' whose asset did not come across";
        return asset;
    }

    // Whether a variable of that name holds a load tracker, and the asset it watches. A tracker that came across
    // is read off its component here; one that did not, off what the package stored for it and its variable.
    private static bool TrackerNamed(PackageNodeIndex index, string? name, out IWorldElement? asset)
    {
        asset = null;
        if (string.IsNullOrEmpty(name))
            return false;
        foreach (var element in index.Built)
        {
            if (element is not Component variable || !variable.GetType().IsGenericType
                || variable.GetType().GetGenericTypeDefinition() != typeof(Components.Variables.ReferenceVariable<>)
                || (PackageNodeIndex.MemberOf(variable, "VariableName", -1) as IField)?.BoxedValue as string != name
                || (PackageNodeIndex.MemberOf(variable, "Reference", -1) as ISyncRef)?.Target is not Component tracker
                || !tracker.GetType().IsGenericType || tracker.GetType().GetGenericTypeDefinition() != typeof(Components.Assets.AssetLoadTally<>))
                continue;
            asset = (PackageNodeIndex.MemberOf(tracker, "Asset", -1) as ISyncRef)?.Target;
            return true;
        }
        foreach (var (_, part) in index.UnattachedParts)
        {
            if (part.Type != "DynamicReferenceVariable" || Stored(part.Data, "VariableName") as string != name
                || Stored(part.Data, "Reference") is not string trackerId
                || index.UnattachedPart(trackerId) is not { Type: "UsersAssetLoadProgress" } tracker)
                continue;
            asset = index.World(Stored(tracker.Data, "Asset") as string);
            return true;
        }
        return false;
    }

    private static string AtLeastZero(PackageNodeScope s, string node, string port, string type)
    {
        string floor = s.Add("Max", $"{type},{type}");
        s.Wire(node, port, floor, "a");
        s.Set(floor, "b", 0d);
        return floor;
    }

    private static object? Stored(DataTreeDictionary data, string member)
        => ((data.TryGetNode(member) as DataTreeDictionary)?.TryGetNode("Data") as DataTreeValue)?.Value;

    // The script type an asset provider is read as.
    private static string? AssetHandle(IAssetProvider provider) => provider switch
    {
        IAssetProvider<TextureAsset> => "texture",
        IAssetProvider<MeshDataAsset> => "mesh",
        IAssetProvider<MaterialAsset> => "material",
        IAssetProvider<AudioClip> => "audioclip",
        IAssetProvider<FontSet> => "font",
        IAssetProvider<AnimationAsset> => "animation",
        IAssetProvider<CubemapAsset> => "cubemap",
        _ => null,
    };
}
