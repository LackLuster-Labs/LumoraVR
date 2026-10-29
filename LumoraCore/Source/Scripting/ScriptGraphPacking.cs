// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Between the two forms of a script: the unpacked graph, node slots under a ScriptGraphSpace that people
// edit together, and the graph document a ScriptHost runs.
//
// Build reads the node slots into a document without writing anything, since every peer runs it to
// check the graph. Node ids that clash or do not parse get a stand-in in the document only; bound world
// objects become binding numbers in order of first use, typed by the port they are bound to.
// Unpack writes node slots back from a document, bindings becoming references to the host's objects.
// -xlinka
public static class ScriptGraphPacking
{
    public const string SpaceSlotName = "Script Graph";

    // Without positions the document is what the graph does and nothing else, so moving a node is not a
    // new graph to anyone running it.
    public static ScriptGraphBuild Build(ScriptGraphSpace space, bool withPositions = true)
    {
        var problems = new List<(ScriptGraphNode?, string)>();
        var graph = new ScriptGraph();
        var ids = new Dictionary<ScriptGraphNode, string>();
        var byId = new Dictionary<string, ScriptGraphNode>(StringComparer.Ordinal);

        // In id order, so a node's place among its siblings, or in a hand, is not a different graph.
        var nodes = new List<ScriptGraphNode>(space.Nodes);
        nodes.Sort(static (x, y) =>
        {
            int byName = string.CompareOrdinal(x.NodeId.Value, y.NodeId.Value);
            return byName != 0 ? byName : x.ReferenceID.CompareTo(y.ReferenceID);
        });
        foreach (var node in nodes)
        {
            string id = node.NodeId.Value ?? string.Empty;
            if (!GraphNode.IsValidId(id) || byId.ContainsKey(id))
                id = StandIn(node, byId);
            ids[node] = id;
            byId[id] = node;
        }

        var bindings = new List<IWorldElement>();
        var bindingTypes = new List<string>();

        foreach (var node in nodes)
        {
            var graphNode = new GraphNode(ids[node], node.NodeType.Value ?? string.Empty)
            {
                Version = node.Version.Value,
                Signature = string.IsNullOrEmpty(node.Signature.Value) ? null : node.Signature.Value,
                Position = !withPositions ? float3.Zero
                    : ReferenceEquals(node.Slot.Parent, space.Slot) ? node.Slot.LocalPosition.Value
                    : space.Slot.GlobalPointToLocal(node.Slot.GlobalPosition),
                Rotation = !withPositions ? floatQ.Identity
                    : ReferenceEquals(node.Slot.Parent, space.Slot) ? node.Slot.LocalRotation.Value
                    : space.Slot.GlobalRotationToLocal(node.Slot.GlobalRotation),
            };
            var overload = node.Resolve(out _);

            foreach (var value in node.Values)
            {
                string port = value.Port.Value ?? string.Empty;
                if (!NodePort.IsValidName(port))
                {
                    problems.Add((node, $"has a setting called '{port}'"));
                    continue;
                }
                if (graphNode.Values.ContainsKey(port))
                {
                    problems.Add((node, $"sets '{port}' twice"));
                    continue;
                }

                if (!value.Reference.Value.IsNull)
                {
                    var target = value.Reference.Target;
                    if (target is ScriptGraphNode named)
                    {
                        if (ids.TryGetValue(named, out var namedId))
                            graphNode.Values[port] = GraphValue.Of(namedId);
                        else
                            problems.Add((node, $"'{port}' points at a node outside this graph"));
                    }
                    else if (target == null || target.IsDestroyed)
                    {
                        problems.Add((node, $"'{port}' is bound to something that is gone"));
                    }
                    else if (TryBind(node, overload, port, target, bindings, bindingTypes, problems, out int index))
                    {
                        graphNode.Values[port] = GraphValue.OfBinding(index);
                    }
                    continue;
                }

                var json = value.Json.Value;
                if (string.IsNullOrEmpty(json))
                    continue;
                try
                {
                    graphNode.Values[port] = GraphValue.ParseJson(json);
                }
                catch (GraphFormatException ex)
                {
                    problems.Add((node, $"'{port}': {ex.Message}"));
                }
            }
            graph.Nodes.Add(graphNode);
        }

        foreach (var node in nodes)
        {
            foreach (var wire in node.Inputs)
            {
                if (wire.Node.Target is not { } from || !ids.TryGetValue(from, out var fromId))
                    continue;
                if (Endpoints(node, wire, problems))
                    graph.Data.Add(new GraphWire(fromId, wire.NodePort.Value, ids[node], wire.Port.Value));
            }
            foreach (var wire in node.Exits)
            {
                if (wire.Node.Target is not { } to || !ids.TryGetValue(to, out var toId))
                    continue;
                if (Endpoints(node, wire, problems))
                    graph.Flow.Add(new GraphWire(ids[node], wire.Port.Value, toId, wire.NodePort.Value));
            }
        }

        for (int i = 0; i < bindings.Count; i++)
            graph.Bindings.Add(new GraphBinding("b" + i, bindingTypes[i]));

        // Through the reader once, so an unpacked graph meets the same caps a loaded one does.
        try
        {
            ScriptGraph.Parse(graph.ToUtf8Json());
        }
        catch (GraphFormatException ex)
        {
            problems.Add((null, ex.Message));
        }

        return new ScriptGraphBuild(graph, bindings, byId, problems);
    }

    // Writes a document back out as node slots under the host, standing where ScriptGraphPlacement puts it for
    // whoever opens it, held there while the object moves; with nobody opening it, where it was left or above
    // the host, facing the given point, riding the host.
    // A graph nobody has opened yet, a converted one or a document written without positions, is laid out for
    // reading first when it came in too tall or in a muddle; on a copy, since the loaded document is shared by
    // every script running it. From its first unpack on, a graph keeps wherever it was left.
    public static ScriptGraphSpace Unpack(ScriptHost host, ScriptGraph graph, UserRoot? opener = null, float3? facing = null)
    {
        if (!ScriptGraphPlacement.HasRemembered(host) && ScriptGraphLayout.Untidy(graph, GraphLanguage.Script))
        {
            graph = ScriptGraph.Parse(graph.ToUtf8Json());
            ScriptGraphLayout.Arrange(graph, GraphLanguage.Script);
        }
        var spaceSlot = host.Slot.AddSlot(SpaceSlotName);
        var space = spaceSlot.AttachComponent<ScriptGraphSpace>();
        space.Host.Target = host;
        ScriptGraphPlacement.For(host, ScriptGraphLayout.Extent(graph, GraphLanguage.Script), opener, facing,
            ScriptGraphLayout.Focus(graph, GraphLanguage.Script)).Apply(spaceSlot);
        if (opener != null)
            space.HoldHere();

        var made = new Dictionary<string, ScriptGraphNode>(StringComparer.Ordinal);
        foreach (var graphNode in graph.Nodes)
        {
            var slot = spaceSlot.AddSlot(graphNode.Type);
            slot.LocalPosition.Value = graphNode.Position;
            if (graphNode.Rotation != floatQ.Identity)
                slot.LocalRotation.Value = graphNode.Rotation;
            var node = slot.AttachComponent<ScriptGraphNode>();
            node.NodeId.Value = graphNode.Id;
            node.NodeType.Value = graphNode.Type;
            node.Signature.Value = graphNode.Signature ?? string.Empty;
            node.Version.Value = graphNode.Version;
            slot.Name.Value = node.Title;
            ScriptGraphEditing.FitNodeBody(node);
            made[graphNode.Id] = node;
        }

        foreach (var graphNode in graph.Nodes)
        {
            var node = made[graphNode.Id];
            var kind = node.Overload?.Definition.Kind;
            // The settings that name another node by its id: a Write Value's "var", a Return's or a call's "fn".
            string? pointer = kind == NodeKind.WriteVariable ? "var" : kind is { } known && FunctionSignature.FollowsFunction(known) ? "fn" : null;
            foreach (var (port, value) in graphNode.Values)
            {
                if (value.Kind == GraphValueKind.Binding)
                {
                    var bound = value.Binding < host.Bindings.Count ? host.Bindings[value.Binding] : null;
                    if (bound != null)
                        node.SetReference(port, bound);
                }
                else if (port == pointer && value.Kind == GraphValueKind.String && made.TryGetValue(value.String!, out var named))
                {
                    node.SetReference(port, named);
                }
                else
                {
                    node.SetValue(port, value);
                }
            }
        }

        foreach (var wire in graph.Data)
        {
            if (!made.TryGetValue(wire.FromNode, out var from) || !made.TryGetValue(wire.ToNode, out var to))
                continue;
            var entry = to.Inputs.Add();
            entry.Port.Value = wire.ToPort;
            entry.Node.Target = from;
            entry.NodePort.Value = wire.FromPort;
        }
        foreach (var wire in graph.Flow)
        {
            if (!made.TryGetValue(wire.FromNode, out var from) || !made.TryGetValue(wire.ToNode, out var to))
                continue;
            var entry = from.Exits.Add();
            entry.Port.Value = wire.FromPort;
            entry.Node.Target = to;
            entry.NodePort.Value = wire.ToPort;
        }
        return space;
    }

    private static bool TryBind(ScriptGraphNode node, NodeOverload? overload, string port, IWorldElement target,
        List<IWorldElement> bindings, List<string> bindingTypes, List<(ScriptGraphNode?, string)> problems, out int index)
    {
        index = -1;
        if (overload == null)
            return false;
        int input = overload.InputIndex(port);
        ScriptType type;
        if (input >= 0)
            type = overload.Inputs[input].Type;
        else if (port == "source" && overload.Definition.TakesSource)
            type = ScriptTypes.Get(overload.Definition.SourceType);
        else
        {
            problems.Add((node, $"'{port}' is bound, but {node.Title} has no input called that"));
            return false;
        }
        if (!type.IsHandle || type == ScriptTypes.String)
        {
            problems.Add((node, $"'{port}' takes a {type.Name}, which cannot be bound to an object"));
            return false;
        }
        if (!type.ClrType.IsInstanceOfType(target) && !ShaderPropertyFields.Accepts(target, type.ClrType))
        {
            problems.Add((node, $"'{port}' takes {type.Name}; the bound object is a {target.GetType().Name}"));
            return false;
        }

        for (int i = 0; i < bindings.Count; i++)
        {
            if (ReferenceEquals(bindings[i], target) && bindingTypes[i] == type.Name)
            {
                index = i;
                return true;
            }
        }
        bindings.Add(target);
        bindingTypes.Add(type.Name);
        index = bindings.Count - 1;
        return true;
    }

    private static bool Endpoints(ScriptGraphNode node, ScriptGraphWire wire, List<(ScriptGraphNode?, string)> problems)
    {
        if (NodePort.IsValidName(wire.Port.Value) && NodePort.IsValidName(wire.NodePort.Value))
            return true;
        problems.Add((node, $"has a wire on '{wire.Port.Value}' to '{wire.NodePort.Value}', which is not a port name"));
        return false;
    }

    private static string StandIn(ScriptGraphNode node, Dictionary<string, ScriptGraphNode> taken)
    {
        string id = node.FreshId();
        for (int i = 2; taken.ContainsKey(id); i++)
            id = node.FreshId() + "_" + i;
        return id;
    }
}
