// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;

namespace Lumora.Script;

// One node class as the registry read it. A non-generic node has its overloads read at startup; a
// generic one is closed per type argument list the first time a graph asks for it.
public sealed class NodeDefinition
{
    public string Name { get; }
    public string Path { get; }
    public string Description { get; }
    public NodeKind Kind { get; }
    public Capability Capability { get; }
    public int Version { get; }
    public Type ClrType { get; }
    public int GenericArity { get; }
    public IReadOnlyList<string> FormerNames { get; }

    // The other words its [Node] gives search, each one as written.
    public IReadOnlyList<string> Keywords { get; }

    // An event that may name the object it listens to; see SourcedEventAttribute.
    public bool TakesSource { get; }

    // The script type a source is bound as, for an event that takes one.
    public string SourceType { get; } = "slot";

    // An event the host raises on a clock, every "seconds".
    public bool TakesInterval { get; }

    // Call Script Function: an await whose ports are its own "sig", like a function node's.
    public bool IsScriptCall { get; }

    // How many inputs a variadic node's two-input overloads stretch to; 0 for any other node.
    public int MaxInputs { get; }

    // How its answer behaves over time: from its base class, or what a ReadNode's [Evaluates] says.
    public Evaluation Evaluation { get; }

    public bool IsVariadic => MaxInputs > 2;

    // How many exits a flow node's run of them (FlowBuilder.Outs) has unless its graph says; 0 for a node
    // without one.
    public int ExitRun { get; internal set; }

    public bool GrowsExits => ExitRun > 0;

    // The overload a card picked from the browser starts as, when [Preferred] marks one. Graphs saved without
    // "sig" still open as the first declared.
    public NodeOverload? Preferred { get; internal set; }

    internal readonly List<NodeOverload> Overloads = new();
    internal readonly Dictionary<string, NodeOverload> Closed = new(StringComparer.Ordinal);

    // A function node's shapes, and a flow node's longer or shorter runs of exits, by the signature that made
    // them, kept apart from Closed: they are a graph's own ports, not choices of the node's to step through.
    internal readonly Dictionary<string, NodeOverload> Shaped = new(StringComparer.Ordinal);
    internal readonly object Lock = new();

    internal NodeDefinition(string name, string path, NodeKind kind, Capability capability, int version, Type clrType,
        IReadOnlyList<string> formerNames, string description, string? keywords = null)
    {
        Name = name;
        Path = path;
        Description = description?.Trim() ?? string.Empty;
        Keywords = string.IsNullOrWhiteSpace(keywords)
            ? Array.Empty<string>()
            : keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Kind = kind;
        Capability = capability;
        Version = version;
        ClrType = clrType;
        GenericArity = clrType.IsGenericTypeDefinition ? clrType.GetGenericArguments().Length : 0;
        FormerNames = formerNames;
        TakesSource = kind == NodeKind.Event && Attribute.IsDefined(clrType, typeof(SourcedEventAttribute), inherit: false);
        if (TakesSource && clrType.GetCustomAttributes(typeof(SourcedEventAttribute), false) is [SourcedEventAttribute sourced, ..])
            SourceType = sourced.SourceType;
        TakesInterval = kind == NodeKind.Event && Attribute.IsDefined(clrType, typeof(TimedEventAttribute), inherit: false);
        IsScriptCall = clrType == typeof(CallScriptFunction);
        if (kind == NodeKind.Value && clrType.GetCustomAttributes(typeof(VariadicAttribute), false) is [VariadicAttribute variadic, ..])
            MaxInputs = variadic.MaxInputs;
        var declared = clrType.GetCustomAttributes(typeof(EvaluatesAttribute), false) is [EvaluatesAttribute evaluates, ..] ? evaluates.Evaluation : (Evaluation?)null;
        if (declared != null && (kind != NodeKind.Read || declared is not (Evaluation.Fixed or Evaluation.Tree or Evaluation.Field or Evaluation.Changing)))
            throw new InvalidOperationException("[Evaluates] says Fixed, Tree, Field or Changing, on a ReadNode");
        Evaluation = kind switch
        {
            NodeKind.Value => Evaluation.Pure,
            NodeKind.Read => declared ?? Evaluation.Changing,
            NodeKind.Event => Evaluation.Event,
            NodeKind.Action => Evaluation.Action,
            _ => Evaluation.Shaped,
        };
    }

    public bool IsGeneric => GenericArity > 0;

    public IReadOnlyList<NodeOverload> KnownOverloads
    {
        get
        {
            lock (Lock)
            {
                var all = new List<NodeOverload>(Overloads);
                all.AddRange(Closed.Values);
                return all;
            }
        }
    }

    public override string ToString() => $"{Name} ({Path})";
}
