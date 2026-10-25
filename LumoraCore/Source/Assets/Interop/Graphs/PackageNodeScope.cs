// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Script;

namespace Lumora.Core.Assets.Interop;

// What a handler converting one package node works with. It adds our nodes, then says how the package node's
// members land on them: In for an input it reads, Out for an output others read, Op for an operation flow
// arrives at, Exit for a call flow leaves by. Names on the package side are its member names, "" being the
// node itself; names on our side are our node ids from Add and our port names. -xlinka
internal sealed class PackageNodeScope
{
    private readonly PackageGraphWriter _writer;

    public PackageNode Node { get; }
    public PackageNodeIndex Index { get; }
    public bool Failed { get; private set; }

    // The slot the script sits on.
    public Slot Home => _writer.Home;

    public PackageGraphWriter Writer => _writer;

    public PackageNodeScope(PackageNode node, PackageNodeIndex index, PackageGraphWriter writer)
    {
        Node = node;
        Index = index;
        _writer = writer;
    }

    // Our type for the package node's generic argument i, or null.
    public string? Arg(int i) => Node.Arg(i);

    public string Add(string type, string? signature = null) => _writer.Add(type, signature);

    // What feeds the package input goes into our port; with nothing wired, the value given, if any.
    public void In(string theirInput, string ourNode, string ourPort, object? unwired = null)
        => _writer.Input(ourNode, ourPort, Node, Node.Ref(theirInput), unwired);

    // Each entry of a package list input into the port ports gives for its position. Answers how many.
    public int InList(string theirList, string ourNode, Func<int, string> ports, object? unwired = null)
    {
        var ids = Node.Refs(theirList);
        for (int i = 0; i < ids.Count; i++)
            _writer.Input(ourNode, ports(i), Node, ids[i], unwired);
        return ids.Count;
    }

    // Entry i of a package list input into one port.
    public void InAt(string theirList, int entry, string ourNode, string ourPort, object? unwired = null)
    {
        var ids = Node.Refs(theirList);
        _writer.Input(ourNode, ourPort, Node, entry < ids.Count ? ids[entry] : null, unwired);
    }

    // Another package node's input into one of our ports, for a node that reads through one beside it.
    public void InFrom(PackageNode other, string theirInput, string ourNode, string ourPort, object? unwired = null)
        => _writer.Input(ourNode, ourPort, other, other.Ref(theirInput), unwired);

    // Another package node's call leaving by one of our exits.
    public void ExitFrom(PackageNode other, string theirCall, string ourNode, string ourExit)
        => _writer.Exit(ourNode, ourExit, other, other.Ref(theirCall));

    // Whoever reads the output reads what feeds another node's input.
    public void OutFrom(string theirOutput, PackageNode other, string otherInput)
        => _writer.Output(Node, theirOutput, new PackageGraphWriter.Through(other, otherInput));

    public void Out(string theirOutput, string ourNode, string ourPort)
        => _writer.Output(Node, theirOutput, new PackageGraphWriter.Port(ourNode, ourPort));

    public void OutConstant(string theirOutput, object? raw) => _writer.Output(Node, theirOutput, new PackageGraphWriter.Constant(raw));

    public void OutWorld(string theirOutput, IWorldElement? element) => _writer.Output(Node, theirOutput, new PackageGraphWriter.WorldObject(element));

    // Whoever reads the output reads what feeds the input instead.
    public void OutThrough(string theirOutput, string theirInput)
        => _writer.Output(Node, theirOutput, new PackageGraphWriter.Through(Node, theirInput));

    public void Op(string theirOperation, string ourNode, string ourIn = "in")
        => _writer.Operation(Node, theirOperation, new PackageGraphWriter.FlowIn(ourNode, ourIn));

    // Flow arriving at the operation goes straight on wherever the call leads.
    public void OpThrough(string theirOperation, string theirCall)
        => _writer.Operation(Node, theirOperation, new PackageGraphWriter.CallThrough(Node, theirCall));

    public void Exit(string theirCall, string ourNode, string ourExit) => _writer.Exit(ourNode, ourExit, Node, Node.Ref(theirCall));

    public void Wire(string fromNode, string fromPort, string toNode, string toPort) => _writer.Wire(fromNode, fromPort, toNode, toPort);

    public void Flow(string fromNode, string fromExit, string toNode, string toIn = "in") => _writer.Flow(fromNode, fromExit, toNode, toIn);

    // A value on one of our ports: a number, bool, text, number list, or a package constant's stored data.
    public void Set(string ourNode, string port, object value)
    {
        if (PackageGraphWriter.ToValue(value, null) is { } converted)
            _writer.Set(ourNode, port, converted);
    }

    public void Bind(string ourNode, string port, IWorldElement element, string type) => _writer.Bind(ourNode, port, element, type);

    public bool Wired(string theirInput) => Node.Ref(theirInput) != null;

    // The input leads, through sources, globals and variables, to something the package itself had already lost,
    // so in the package it never reaches anything either.
    public bool Lost(string theirInput)
    {
        string? id = Node.Ref(theirInput);
        for (int i = 0; i < 16 && id != null; i++)
        {
            if (!Index.TryNode(id, out var node, out _))
                return !Index.Holds(id);
            id = node.Class switch
            {
                "GlobalReference" => node.Ref("Reference"),
                "ElementSource" or "SlotSource" or "ReferenceSource" or "ObjectValueSource" or "ValueSource" => node.Ref("Source"),
                "ReferenceAsVariable" => node.Ref("Reference"),
                "FieldAsVariable" => node.Ref("Field"),
                "ObjectRelay" or "ValueRelay" => node.Ref("Input"),
                _ => null,
            };
            if (id == null)
                return false;
        }
        return false;
    }

    // Flow arriving at the operation stops there.
    public void OpEnds(string theirOperation) => _writer.Operation(Node, theirOperation, new PackageGraphWriter.Ends());

    // The input leads, through relays, sources and globals, to a reference nobody set, so in the package it reads
    // nothing at all. An input wired to anything that computes is not unset.
    public bool Unset(string theirInput)
    {
        string? id = Node.Ref(theirInput);
        for (int i = 0; i < 16 && id != null; i++)
        {
            if (!Index.TryNode(id, out var node, out _))
                return false;
            switch (node.Class)
            {
                case "GlobalReference":
                    id = node.Ref("Reference");
                    break;
                case "ElementSource" or "SlotSource" or "ReferenceSource" or "ObjectValueSource" or "ValueSource":
                    id = node.Ref("Source");
                    break;
                case "ObjectRelay" or "ValueRelay":
                    id = node.Ref("Input");
                    break;
                default:
                    return false;
            }
        }
        return id == null;
    }

    // Whether anything reads one of the package node's outputs: "" for the node itself, else by name, "List[i]"
    // for an entry of a list output.
    public bool IsRead(string theirOutput)
    {
        if (theirOutput.Length == 0)
            return Index.Read.Contains(Node.Id);
        string name = theirOutput;
        int entry = -1;
        int open = theirOutput.IndexOf('[');
        if (open > 0 && theirOutput.EndsWith(']'))
        {
            name = theirOutput[..open];
            entry = int.Parse(theirOutput[(open + 1)..^1], System.Globalization.CultureInfo.InvariantCulture);
        }
        if (Node.Data.TryGetNode(name) is not Persistence.DataTreeDictionary member)
            return false;
        if (entry < 0)
            return (member.TryGetNode("ID") as Persistence.DataTreeValue)?.Value is string id && Index.Read.Contains(id);
        return member.TryGetNode("Data") is Persistence.DataTreeList list && entry < list.Count
            && ((list[entry] as Persistence.DataTreeDictionary)?.TryGetNode("ID") as Persistence.DataTreeValue)?.Value is string itemId
            && Index.Read.Contains(itemId);
    }

    // The package node an input reads from, through any relays, or null for a world object or nothing.
    public PackageNode? Feeder(string theirInput)
    {
        string? id = Node.Ref(theirInput);
        for (int depth = 0; depth < 32 && id != null; depth++)
        {
            if (!Index.TryNode(id, out var node, out _))
                return null;
            if (!node.Class.StartsWith("ValueRelay", StringComparison.Ordinal) && !node.Class.StartsWith("ObjectRelay", StringComparison.Ordinal)
                && !node.Class.StartsWith("ContinuouslyChanging", StringComparison.Ordinal))
                return node;
            id = node.Ref("Input");
        }
        return null;
    }

    // The world object a member points at: directly, or through a global reference node it names.
    public IWorldElement? WorldOf(string member)
    {
        string? id = Node.Ref(member);
        if (Index.TryNode(id, out var node, out _))
            return node.Class.StartsWith("GlobalReference", StringComparison.Ordinal)
                ? Index.World(node.Ref("Reference")) ?? PackageNodeTable.InGraphHolder(this, node.Ref("Reference")) : null;
        return Index.World(id);
    }

    public PackageNode? Helper(string? cls = null) => Index.Helper(Node, cls);

    // Not converted: kept as a placeholder, and why said once.
    public void Fail(string why)
    {
        if (!Failed)
            _writer.Problem(why);
        Failed = true;
    }

    public void Problem(string why) => _writer.Problem(why);
}
