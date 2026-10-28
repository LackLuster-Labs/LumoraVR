// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Lumora.Wasm;

namespace Lumora.Script;

// One concrete shape of a node: its ports and how it runs. A host call carries its import name and
// WebAssembly signature; an inlined overload carries its opcode; flow, event and variable nodes carry
// neither and the compiler lowers them itself.
public sealed class NodeOverload
{
    public NodeDefinition Definition { get; }
    public IReadOnlyList<ScriptType> TypeArguments { get; }
    // Invocation parameters, including settings. Their order is part of the saved signature and ABI.
    public PortList Inputs { get; }
    public PortList Outputs { get; }
    public IReadOnlyList<string> FlowInputs { get; }
    public IReadOnlyList<string> FlowOutputs { get; }
    public MethodInfo? Method { get; }
    public bool TakesContext { get; }
    public byte? InlineOpcode { get; }

    // Written inline by its node's [WasmBody] writer instead of called.
    public Action<WasmBodyWriter>? InlineBody { get; private init; }

    public FlowShape? Shape { get; }
    public Type? PayloadType { get; }

    // A variadic node with more than two inputs: the two-input overload folded over them. Its host call,
    // or its inlined instruction, is the two-input one's.
    public NodeOverload? Folds { get; }

    // What a graph writes in "sig" to pick this overload: the type arguments for a generic node, a function
    // node's ports spelled out, a flow node's number of exits when its run is not the length it declares, the
    // input types otherwise.
    public string Signature { get; }

    // A function node's ports, as its "sig" declares them; null for any other node.
    public FunctionSignature? FunctionSignature { get; }

    public string ImportName { get; }
    public WasmFuncType? ImportType { get; }

    private NodeInvoker? _invoker;
    private Delegate? _direct;
    private int _directMade;

    // What a node import's host function charges a call: the linker's default.
    public const int HostFuel = 10;

    private NodeOverload(NodeOverload binary, IReadOnlyList<NodePort> inputs)
    {
        Definition = binary.Definition;
        TypeArguments = binary.TypeArguments;
        Inputs = inputs as PortList ?? new PortList(inputs);
        Outputs = binary.Outputs;
        FlowInputs = binary.FlowInputs;
        FlowOutputs = binary.FlowOutputs;
        Method = binary.Method;
        TakesContext = binary.TakesContext;
        InlineOpcode = binary.InlineOpcode;
        Folds = binary;
        Signature = string.Join(",", Inputs.Select(i => i.Type.Name));
        ImportName = binary.ImportName;
        ImportType = binary.ImportType;
    }

    // Extra inputs are named on from the pair: a and b go on as c, d, e.
    internal static NodeOverload Fold(NodeOverload binary, int count)
    {
        var inputs = new List<NodePort>(binary.Inputs);
        var first = binary.Inputs[0];
        bool lettered = binary.Inputs[0].Name == "a" && binary.Inputs[1].Name == "b";
        for (int i = 2; i < count; i++)
        {
            var name = lettered ? ((char)('a' + i)).ToString() : $"{binary.Inputs[1].Name}{i}";
            inputs.Add(new NodePort(name, first.Type, first.Default, first.HasDefault, first.IsSetting));
        }
        return new NodeOverload(binary, inputs);
    }

    internal NodeOverload(NodeDefinition definition, IReadOnlyList<ScriptType> typeArguments, IReadOnlyList<NodePort> inputs,
        IReadOnlyList<NodePort> outputs, IReadOnlyList<string> flowInputs, IReadOnlyList<string> flowOutputs,
        MethodInfo? method, bool takesContext, byte? inlineOpcode, FlowShape? shape, Type? payloadType,
        Action<WasmBodyWriter>? inlineBody = null, FunctionSignature? function = null, string? signature = null)
    {
        InlineBody = inlineBody;
        FunctionSignature = function;
        Definition = definition;
        TypeArguments = typeArguments;
        Inputs = inputs as PortList ?? new PortList(inputs);
        Outputs = outputs as PortList ?? new PortList(outputs);
        FlowInputs = flowInputs;
        FlowOutputs = flowOutputs;
        Method = method;
        TakesContext = takesContext;
        InlineOpcode = inlineOpcode;
        Shape = shape;
        PayloadType = payloadType;

        var inputTypes = new string[inputs.Count];
        for (int i = 0; i < inputs.Count; i++)
            inputTypes[i] = inputs[i].Type.Name;

        var typeArgs = new string[typeArguments.Count];
        for (int i = 0; i < typeArguments.Count; i++)
            typeArgs[i] = typeArguments[i].Name;

        Signature = signature ?? function?.Canonical ?? (definition.IsGeneric ? string.Join(",", typeArgs) : string.Join(",", inputTypes));
        ImportName = definition.IsGeneric
            ? $"{definition.Name}<{string.Join(",", typeArgs)}>({string.Join(",", inputTypes)})"
            : $"{definition.Name}({string.Join(",", inputTypes)})";

        // An await's Start takes its await number before the inputs and gives nothing back: its outputs
        // arrive with the answer.
        if (IsHostCall)
            ImportType = definition.Kind == NodeKind.Await
                ? new WasmFuncType(new[] { WasmValueType.I32 }.Concat(LanesOf(inputs)).ToArray(), Array.Empty<WasmValueType>())
                : new WasmFuncType(LanesOf(inputs), LanesOf(outputs));
    }

    public bool IsHostCall => Method != null && InlineOpcode == null && InlineBody == null;

    // Built once, by whichever thread asks first: a compile on a worker makes the ones its graph links.
    public NodeInvoker Invoker => Folds?.Invoker ?? LazyInitializer.EnsureInitialized(ref _invoker, () => new NodeInvoker(this));

    // This overload as a call native code makes straight from lanes, when it is a pure one over plain numbers;
    // null otherwise. Built once.
    internal Delegate? DirectLanes
    {
        get
        {
            if (Volatile.Read(ref _directMade) == 0)
            {
                var made = NodeInvoker.DirectLanes(this);
                Interlocked.CompareExchange(ref _direct, made, null);
                Volatile.Write(ref _directMade, 1);
            }
            return _direct;
        }
    }

    public int InputIndex(string name)
    {
        for (int i = 0; i < Inputs.Count; i++)
        {
            if (Inputs[i].Name == name)
                return i;
        }
        return -1;
    }

    public int OutputIndex(string name)
    {
        for (int i = 0; i < Outputs.Count; i++)
        {
            if (Outputs[i].Name == name)
                return i;
        }
        return -1;
    }

    // Only parameters that can receive a data wire. InputIndex also finds editable settings.
    public int ConnectableInputIndex(string name)
    {
        int index = InputIndex(name);
        return index >= 0 && !Inputs[index].IsSetting ? index : -1;
    }

    public override string ToString() => ImportName;

    private static WasmValueType[] LanesOf(IReadOnlyList<NodePort> ports)
    {
        var lanes = new List<WasmValueType>();
        foreach (var port in ports)
            lanes.AddRange(port.Type.Lanes);
        return lanes.ToArray();
    }
}
