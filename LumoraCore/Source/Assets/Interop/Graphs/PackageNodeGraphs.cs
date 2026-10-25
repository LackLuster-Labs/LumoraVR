// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Scripting;
using Lumora.Script;
using Logger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Assets.Interop;

// A package's node graphs, made into scripts on the imported object.
//
// Every connected graph becomes one script. It sits where the graph's nodes sat: on a slot named after the
// folder that held them, under the nearest slot of the package that came across, since the node slots
// themselves are pruned once their nodes are gone. Each package node is converted by its entry in
// PackageNodeTable; one with no entry stays in the graph as a placeholder, so the graph shows where it was.
// The nodes are laid out afresh rather than where the package kept them: its spacing does not fit our cards,
// and a node it had that became several of ours had them all in one place, so its places unpacked into a cloud
// of cards over one another. A graph that compiles is packed and runs; one that does not is packed all the
// same and says why it does not run, so its owner can open it and finish it. -xlinka
internal static class PackageNodeGraphs
{
    public static bool IsNodeType(string encoded) => PackageTypeMap.IsVisualScripting(encoded);

    public static async Task ConvertAsync(Slot root, PackageNodeInput input, IReadOnlyDictionary<string, IWorldElement> byId,
        PackageImportResult result)
    {
        if (input.Nodes.Count == 0 || root.IsDestroyed)
            return;
        var registry = ScriptEnvironment.Registry;
        var index = new PackageNodeIndex(input, byId);
        bool avatar = root.GetComponentsInChildren<Lumora.Core.Components.Avatar.HumanoidRig>().Any(r => r.IsHumanoid);

        foreach (var group in index.Groups)
        {
            if (group.All(Inert))
                continue;
            string? top = CommonAncestor(group.Select(n => n.SlotId), input);
            var home = Home(top, input, root);
            var writer = new PackageGraphWriter(index, registry, home);
            foreach (var node in group.OrderBy(Order).ThenBy(n => n.Id, StringComparer.Ordinal))
            {
                writer.Begin(node);
                var scope = new PackageNodeScope(node, index, writer);
                if (PackageNodeTable.TryGet(node.Class, out var handler))
                {
                    try
                    {
                        handler(scope);
                    }
                    catch (Exception ex)
                    {
                        scope.Fail("could not be converted: " + ex.Message);
                    }
                }
                else
                {
                    scope.Fail("has no counterpart here yet");
                }
                if (scope.Failed)
                    writer.Placeholder(node);
                writer.Done(!scope.Failed);
            }
            writer.Finish();
            result.ScriptNodesConverted += writer.Converted;
            result.ScriptNodesUnconverted += writer.Unconverted.Count;
            foreach (var node in writer.Unconverted)
                result.UnconvertedNodes[node.Class] = result.UnconvertedNodes.GetValueOrDefault(node.Class) + 1;

            if (writer.Graph.Nodes.Count == 0)
            {
                home.Destroy();
                continue;
            }
            ScriptGraphLayout.Arrange(writer.Graph, GraphLanguage.Script);
            await Pack(home, writer, ModeFor(writer.Graph, registry, avatar), result);
        }
        Logger.Log($"Package import: {result.ScriptsRunning} of {result.ScriptsMade} node graphs became running scripts; "
                 + $"{result.ScriptNodesConverted} nodes converted, {result.ScriptNodesUnconverted} not");
    }

    // Values and the components serving a node do nothing on their own: a graph of only those is left out.
    private static bool Inert(PackageNode node)
        => node.Class.Contains('+') || node.Class is "ValueInput" or "ValueObjectInput" or "RefObjectInput" or "GlobalValue"
            or "GlobalReference" or "ValueDisplay" or "ObjectDisplay" or "ValueRelay" or "ObjectRelay"
            || PackageNodeTable.IsButtonSender(node);

    // Stores first, so a write met later already knows the value it writes into; buttons last, so the receivers
    // they start have already said where the values they read come from.
    private static int Order(PackageNode node)
        => node.Class is "StoredValue" or "StoredObject" or "DataModelValueFieldStore" or "DataModelObjectFieldStore" or "DataModelObjectRefStore"
            or "DataModelBooleanToggle" ? 0 : PackageNodeTable.IsButtonSender(node) ? 2 : 1;

    // Where a graph runs. A graph in an avatar that reads its wearer's devices or machine, connects out from
    // it, does something to its wearer, or works something out every frame, ran on the wearer's machine, so it
    // runs on its author's, who wears it. A graph elsewhere that reads devices ran for whoever was at each
    // machine. One elsewhere that asks who is at the machine (who let go of it, whose arm to put it on) means
    // the person using it, which for a thing of someone's own is its author, so it runs on theirs; a world's
    // own runs on the host all the same. Anything else runs on the host.
    private static ScriptMode ModeFor(ScriptGraph graph, NodeRegistry registry, bool avatar)
    {
        bool input = false, machine = false, ticking = false;
        foreach (var node in graph.Nodes)
        {
            if (!registry.TryGetDefinition(node.Type, out var definition))
                continue;
            input |= definition.Capability is Capability.Input or Capability.UsersAffect;
            machine |= node.Type is "MachineUser" or "IsMachineUser" or "ConnectWebSocket";
            ticking |= node.Type == "OnUpdate";
        }
        if (avatar)
            return input || machine || ticking ? ScriptMode.Author : ScriptMode.Shared;
        return input ? ScriptMode.Local : machine ? ScriptMode.Author : ScriptMode.Shared;
    }

    private static async Task Pack(Slot home, PackageGraphWriter writer, ScriptMode mode, PackageImportResult result)
    {
        var graph = writer.Graph;
        var bindings = writer.Bindings;
        result.ScriptsMade++;
        foreach (var problem in writer.Problems)
            result.ScriptProblems[problem] = result.ScriptProblems.GetValueOrDefault(problem) + 1;

        CompileResult compiled;
        Uri? url;
        try
        {
            compiled = await ScriptEnvironment.CompileAsync(graph);
            // A card with errors on it is taller by their lines, so the graph is laid out again around them.
            if (ErrorLines(compiled) is { Count: > 0 } lines)
                ScriptGraphLayout.Arrange(graph, GraphLanguage.Script, lines);
            url = await ScriptGraphAsset.StoreAsync(graph);
        }
        catch (Exception ex)
        {
            await WorldContext.ToWorld();
            Logger.Warn($"Package import: script '{home.Name.Value}' could not be stored: {ex.Message}");
            return;
        }
        // The compile and the store finish on workers; the script goes on in the world.
        await WorldContext.ToWorld();
        if (home.IsDestroyed || url == null)
            return;

        var provider = home.AttachComponent<ScriptGraphProvider>();
        provider.URL.Value = url;
        var host = home.AttachComponent<ScriptHost>();
        host.Mode.Value = mode;
        host.Graph.Target = provider;
        foreach (var binding in bindings)
            host.Bindings.Add(binding.IsDestroyed ? null! : binding);

        if (compiled.Success)
        {
            result.ScriptsRunning++;
            return;
        }
        var first = compiled.Errors.FirstOrDefault();
        Logger.Log($"Package import: script '{home.Name.Value}' does not compile yet: {first?.NodeId} {first?.Message}");
    }

    private static Dictionary<string, int>? ErrorLines(CompileResult compiled)
    {
        Dictionary<string, int>? lines = null;
        foreach (var error in compiled.Errors)
        {
            if (error.NodeId == null)
                continue;
            lines ??= new Dictionary<string, int>(StringComparer.Ordinal);
            lines[error.NodeId] = lines.GetValueOrDefault(error.NodeId) + 1;
        }
        return lines;
    }

    // The slot a graph's script goes on: under the nearest slot of the package that came across, named after
    // the folder its nodes shared.
    private static Slot Home(string? top, PackageNodeInput input, Slot root)
    {
        var record = top != null && input.Slots.TryGetValue(top, out var found) ? found : null;
        string name = record?.Name is { Length: > 0 } named ? named : "Script";
        if (record?.Slot is { IsDestroyed: false } kept)
            return kept.AddSlot(name + " Script");
        for (var at = record; at != null; at = at.ParentId != null && input.Slots.TryGetValue(at.ParentId, out var up) ? up : null)
        {
            if (at.Slot is { IsDestroyed: false } survivor)
                return survivor.AddSlot(name);
        }
        return root.AddSlot(name);
    }

    private static string? CommonAncestor(IEnumerable<string> slots, PackageNodeInput input)
    {
        List<string>? common = null;
        foreach (var slot in slots)
        {
            var chain = Chain(slot, input);
            if (common == null)
            {
                common = chain;
                continue;
            }
            int same = 0;
            while (same < common.Count && same < chain.Count && common[same] == chain[same])
                same++;
            common.RemoveRange(same, common.Count - same);
        }
        return common is { Count: > 0 } ? common[^1] : null;
    }

    // Root first.
    private static List<string> Chain(string slot, PackageNodeInput input)
    {
        var chain = new List<string>();
        for (string? at = slot; at != null && input.Slots.TryGetValue(at, out var record) && chain.Count < 512; at = record.ParentId)
            chain.Add(at);
        chain.Reverse();
        return chain;
    }
}
