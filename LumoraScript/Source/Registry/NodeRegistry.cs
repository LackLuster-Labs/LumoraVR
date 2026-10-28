// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Lumora.Script;

// Every node class, read once. A node class that is malformed (a port type outside the vocabulary, a
// [Wasm] that does not fit its types, two nodes with one name) is left out and listed in Problems,
// rather than taking every other node down with it. -xlinka
public sealed class NodeRegistry
{
    private readonly Dictionary<string, NodeDefinition> _byName = new(StringComparer.Ordinal);
    private readonly List<NodeDefinition> _definitions = new();
    private readonly List<string> _problems = new();

    public IReadOnlyList<NodeDefinition> Definitions => _definitions;

    public IReadOnlyList<string> Problems => _problems;

    private Func<MethodInfo, bool>? _keepOverload;

    public static NodeRegistry Scan(IEnumerable<Assembly> assemblies) => Scan(assemblies, null, null);

    // Only the node classes keepType takes, each with only the Run overloads keepOverload takes; a class left
    // with none is left out. The shader registry takes Loom's value nodes this way, by their [Gpu] overloads.
    public static NodeRegistry Scan(IEnumerable<Assembly> assemblies, Func<Type, bool>? keepType, Func<MethodInfo, bool>? keepOverload)
    {
        var registry = new NodeRegistry { _keepOverload = keepOverload };
        foreach (var assembly in assemblies.Distinct())
        {
            foreach (var type in LoadableTypes(assembly).OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (!typeof(ScriptNode).IsAssignableFrom(type) || type.IsAbstract || (keepType != null && !keepType(type)))
                    continue;
                var attribute = type.GetCustomAttribute<NodeAttribute>();
                if (attribute == null)
                    continue;
                try
                {
                    registry.Add(type, attribute);
                }
                catch (Exception ex)
                {
                    registry._problems.Add($"{type.FullName}: {ex.Message}");
                }
            }
        }
        return registry;
    }

    public bool TryGetDefinition(string name, out NodeDefinition definition)
        => _byName.TryGetValue(name, out definition!);

    // Picks the overload a graph node names. For a generic node the signature is its type arguments;
    // for an overloaded one it is its input types, and leaving it out picks the first overload the class
    // declares, so a graph saved while the node had only that one still opens after more are added.
    public bool TryResolve(NodeDefinition definition, string? signature, out NodeOverload overload, out string error)
    {
        overload = null!;
        error = string.Empty;

        if (FunctionSignature.IsFunctionNode(definition.Kind) || definition.IsScriptCall)
            return TryShapeFunction(definition, signature, out overload, out error);

        if (definition.IsGeneric)
        {
            if (string.IsNullOrEmpty(signature))
            {
                error = $"{definition.Name} needs its type in \"sig\"";
                return false;
            }
            return TryClose(definition, signature, out overload, out error);
        }

        var candidates = definition.Overloads;
        if (string.IsNullOrEmpty(signature))
        {
            overload = candidates[0];
            return true;
        }
        if (definition.GrowsExits)
            return TryGrow(definition, signature, out overload, out error);

        if (!TryCanonicalSignature(signature, out var canonical))
        {
            error = $"\"{signature}\" names a type that does not exist";
            return false;
        }
        foreach (var candidate in candidates)
        {
            if (candidate.Signature == canonical)
            {
                overload = candidate;
                return true;
            }
        }
        if (definition.IsVariadic && TryFold(definition, canonical, out overload, out error))
            return true;
        error = string.IsNullOrEmpty(error) ? $"{definition.Name} has no overload ({canonical})" : error;
        return false;
    }

    // A function node takes the ports its own "sig" spells, whatever they are, so its shapes are made as graphs
    // ask for them rather than read from the class. No "sig" is no ports, so a fresh one can be placed. Each
    // shape is kept by the signature that made it, up to a bound, so an open graph asking every frame makes
    // nothing new.
    private const int MaxShapedPerNode = 4096;

    private static bool TryShapeFunction(NodeDefinition definition, string? signature, out NodeOverload overload, out string error)
    {
        overload = null!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(signature))
        {
            overload = definition.Overloads[0];
            return true;
        }
        lock (definition.Lock)
        {
            if (definition.Shaped.TryGetValue(signature, out overload!))
                return true;
        }
        if (!FunctionSignature.TryParse(signature, out var parsed, out var why))
        {
            error = why!;
            return false;
        }
        NodeOverload made;
        try
        {
            made = FunctionShape(definition, parsed!);
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }
        lock (definition.Lock)
        {
            if (definition.Shaped.Count >= MaxShapedPerNode)
                definition.Shaped.Clear();
            if (!definition.Shaped.TryGetValue(made.Signature, out overload!))
            {
                overload = made;
                definition.Shaped[made.Signature] = made;
            }
            definition.Shaped[signature] = overload;
        }
        return true;
    }

    // A Function's arguments come out of it and its body runs from next; a Return takes what the function gives
    // and ends the flow there; a call takes the arguments and gives the outputs, with flow or without. A call to
    // another script's Function is an await: the slot to call on comes first, and its answer runs returned with
    // the outputs or failed with the reason.
    private static NodeOverload FunctionShape(NodeDefinition definition, FunctionSignature signature)
    {
        if (definition.IsScriptCall)
        {
            if (!ScriptTypes.TryGet("slot", out var slot))
                throw new InvalidOperationException("calling another script needs the slot type, which this host does not have");
            var takes = new List<NodePort> { new("target", slot, null, hasDefault: false) };
            takes.AddRange(signature.Inputs);
            var gives = new List<NodePort>(signature.Outputs) { new(CallTicket.ReasonPort, ScriptTypes.String, null, hasDefault: false) };
            return new NodeOverload(definition, Array.Empty<ScriptType>(), takes, gives, new[] { "in" },
                new[] { "next", CallTicket.ReturnedExit, CallTicket.FailedExit }, null, false, null, null, typeof(CallAnswer), function: signature);
        }
        IReadOnlyList<NodePort> none = Array.Empty<NodePort>();
        IReadOnlyList<NodePort> inputs = signature.Inputs;
        IReadOnlyList<NodePort> outputs = signature.Outputs;
        string[] flowIn = { "in" };
        string[] flowOut = { "next" };
        string[] noFlow = Array.Empty<string>();
        switch (definition.Kind)
        {
            case NodeKind.Function:
                outputs = inputs;
                inputs = none;
                flowIn = noFlow;
                break;
            case NodeKind.Return:
                inputs = outputs;
                outputs = none;
                flowOut = noFlow;
                break;
            case NodeKind.EvaluateFunction:
                flowIn = noFlow;
                flowOut = noFlow;
                break;
        }
        return new NodeOverload(definition, Array.Empty<ScriptType>(), inputs, outputs, flowIn, flowOut, null, false, null, null, null,
            function: signature);
    }

    // A flow node's run of exits at the length its "sig" gives, "6" for six. The declared length is the node's
    // own overload, so a graph saved with no "sig" opens as it always did.
    private static bool TryGrow(NodeDefinition definition, string signature, out NodeOverload overload, out string error)
    {
        overload = null!;
        error = string.Empty;
        if (!int.TryParse(signature.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count))
        {
            error = $"{definition.Name} takes its number of exits in \"sig\", not \"{signature}\"";
            return false;
        }
        if (count < FlowBuilder.MinExits || count > FlowBuilder.MaxExits)
        {
            error = $"{definition.Name} has {FlowBuilder.MinExits} to {FlowBuilder.MaxExits} exits, not {count}";
            return false;
        }
        if (count == definition.ExitRun)
        {
            overload = definition.Overloads[0];
            return true;
        }
        var key = count.ToString(CultureInfo.InvariantCulture);
        lock (definition.Lock)
        {
            if (definition.Shaped.TryGetValue(key, out overload!))
                return true;
        }
        NodeOverload made;
        try
        {
            made = BuildFlow(definition, definition.ClrType, Array.Empty<ScriptType>(), count);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            error = $"{definition.Name}: {ex.Message}";
            return false;
        }
        lock (definition.Lock)
        {
            if (!definition.Shaped.TryGetValue(key, out overload!))
                definition.Shaped[key] = overload = made;
        }
        return true;
    }

    // Three or more inputs of one type on a variadic node: its two-input overload for that type, folded.
    private static bool TryFold(NodeDefinition definition, string canonical, out NodeOverload overload, out string error)
    {
        overload = null!;
        error = string.Empty;
        var names = ScriptTypes.SplitTopLevel(canonical);
        if (names == null || names.Length < 3 || names.Any(n => n != names[0]))
            return false;
        if (names.Length > definition.MaxInputs)
        {
            error = $"{definition.Name} takes at most {definition.MaxInputs} inputs";
            return false;
        }

        lock (definition.Lock)
        {
            if (definition.Closed.TryGetValue(canonical, out overload!))
                return true;
        }
        var pair = $"{names[0]},{names[0]}";
        var binary = definition.Overloads.FirstOrDefault(o => o.Signature == pair);
        if (binary == null || binary.Outputs.Count != 1 || binary.Outputs[0].Type != binary.Inputs[0].Type)
            return false;
        var folded = NodeOverload.Fold(binary, names.Length);
        lock (definition.Lock)
        {
            if (!definition.Closed.TryGetValue(canonical, out overload!))
            {
                definition.Closed.Add(canonical, folded);
                overload = folded;
            }
        }
        return true;
    }

    private void Add(Type type, NodeAttribute attribute)
    {
        if (string.IsNullOrWhiteSpace(attribute.Path) || attribute.Path.Length > 128)
            throw new InvalidOperationException("a node needs a path");
        if (attribute.Version < 1)
            throw new InvalidOperationException("a node version starts at 1");

        var name = type.IsGenericTypeDefinition ? type.Name[..type.Name.IndexOf('`')] : type.Name;
        if (!NodePort.IsValidName(name))
            throw new InvalidOperationException($"'{name}' is not a usable node name");

        var kind = KindOf(type);
        var formerNames = type.GetCustomAttributes<FormerNameAttribute>().Select(a => a.Name).ToArray();
        var definition = new NodeDefinition(name, attribute.Path, kind, attribute.Capability, attribute.Version, type, formerNames, attribute.Description,
            attribute.Keywords);

        // An await closes per type like any generic node; its payload is not generic, so its outputs never move.
        if (type.IsGenericTypeDefinition && kind is NodeKind.Event)
            throw new InvalidOperationException("event nodes cannot be generic");
        if (type.IsGenericTypeDefinition && kind is NodeKind.Await && PayloadOf(type, typeof(AwaitNode<>)) is { ContainsGenericParameters: true })
            throw new InvalidOperationException("an await node's payload cannot be generic");

        if (!definition.IsGeneric)
        {
            switch (kind)
            {
                case NodeKind.Event:
                    definition.Overloads.Add(BuildEvent(definition, type));
                    break;
                case NodeKind.Await when definition.IsScriptCall:
                    // A host with no slots has no scripts on them to call; the node is not offered there.
                    if (!ScriptTypes.TryGet("slot", out _))
                        return;
                    definition.Overloads.Add(FunctionShape(definition, FunctionSignature.Empty));
                    break;
                case NodeKind.Await:
                    definition.Overloads.Add(BuildAwait(definition, type));
                    break;
                case NodeKind.Flow:
                    definition.Overloads.Add(BuildFlow(definition, type, Array.Empty<ScriptType>()));
                    break;
                case NodeKind.Memory:
                case NodeKind.Drive:
                    definition.Overloads.Add(BuildShaped(definition, type, Array.Empty<ScriptType>()));
                    break;
                case NodeKind.Pulse:
                case NodeKind.Relay:
                    definition.Overloads.Add(new NodeOverload(definition, Array.Empty<ScriptType>(), Array.Empty<NodePort>(),
                        Array.Empty<NodePort>(), new[] { "in" }, new[] { "next" }, null, false, null, null, null));
                    break;
                case NodeKind.Function:
                case NodeKind.Return:
                case NodeKind.CallFunction:
                case NodeKind.EvaluateFunction:
                    definition.Overloads.Add(FunctionShape(definition, FunctionSignature.Empty));
                    break;
                default:
                    definition.Overloads.AddRange(BuildMethods(definition, type, Array.Empty<ScriptType>()));
                    if (_keepOverload != null)
                    {
                        definition.Overloads.RemoveAll(o => o.Method != null && !_keepOverload(o.Method));
                        if (definition.Overloads.Count == 0)
                            return;
                    }
                    definition.Preferred = PreferredOf(definition.Overloads);
                    break;
            }
            if (definition.Overloads.Count == 0)
                throw new InvalidOperationException("no public static Run overload");

            var signatures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var overload in definition.Overloads)
            {
                if (!signatures.Add(overload.Signature))
                    throw new InvalidOperationException($"two overloads take ({overload.Signature})");
            }
        }

        foreach (var key in formerNames.Prepend(name))
        {
            if (_byName.TryGetValue(key, out var clash))
                throw new InvalidOperationException($"the name '{key}' is already {clash.ClrType.FullName}");
        }
        foreach (var key in formerNames.Prepend(name))
            _byName.Add(key, definition);
        _definitions.Add(definition);
    }

    private static NodeKind KindOf(Type type)
    {
        if (type.IsGenericTypeDefinition && type == typeof(StoredValue<>))
            return NodeKind.StoredVariable;
        if (type.IsGenericTypeDefinition && type == typeof(WriteValue<>))
            return NodeKind.WriteVariable;
        if (type.IsGenericTypeDefinition && (type == typeof(IncrementValue<>) || type == typeof(DecrementValue<>)))
            return NodeKind.WriteVariable;
        if (type.IsGenericTypeDefinition && type == typeof(Display<>))
            return NodeKind.Display;
        if (type.IsGenericTypeDefinition && type == typeof(Constant<>))
            return NodeKind.Constant;
        if (type == typeof(Pulse))
            return NodeKind.Pulse;
        if (type == typeof(FlowRelay) || (type.IsGenericTypeDefinition && type == typeof(Relay<>)))
            return NodeKind.Relay;
        if (type == typeof(Function))
            return NodeKind.Function;
        if (type == typeof(Return))
            return NodeKind.Return;
        if (type == typeof(CallFunction))
            return NodeKind.CallFunction;
        if (type == typeof(EvaluateFunction))
            return NodeKind.EvaluateFunction;
        if (type == typeof(CallScriptFunction))
            return NodeKind.Await;
        if (typeof(ValueNode).IsAssignableFrom(type))
            return NodeKind.Value;
        if (typeof(ReadNode).IsAssignableFrom(type))
            return NodeKind.Read;
        if (typeof(ActionNode).IsAssignableFrom(type))
            return NodeKind.Action;
        if (typeof(FlowNode).IsAssignableFrom(type))
            return NodeKind.Flow;
        if (typeof(MemoryNode).IsAssignableFrom(type))
            return NodeKind.Memory;
        if (typeof(DriveNode).IsAssignableFrom(type))
            return NodeKind.Drive;
        if (PayloadOf(type, typeof(AwaitNode<>)) != null)
            return NodeKind.Await;
        if (EventPayloadOf(type) != null)
            return NodeKind.Event;
        throw new InvalidOperationException("derives from no node kind");
    }

    private static Type? EventPayloadOf(Type type) => PayloadOf(type, typeof(EventNode<>));

    // Increment and Decrement Value: a stored variable write whose input is the step, which the compiler
    // adds to or takes from what the variable holds.
    internal static bool IsStep(NodeDefinition definition)
        => definition.ClrType == typeof(IncrementValue<>) || definition.ClrType == typeof(DecrementValue<>);

    // The number types Add and Subtract take, which is what a stored value can be stepped by.
    internal static bool IsSteppable(Type type)
        => type == typeof(int) || type == typeof(long) || type == typeof(float) || type == typeof(double) || type == typeof(uint)
            || type == typeof(ulong);

    private static Type? PayloadOf(Type type, Type openBase)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == openBase)
                return current.GetGenericArguments()[0];
        }
        return null;
    }

    private bool TryClose(NodeDefinition definition, string signature, out NodeOverload overload, out string error)
    {
        overload = null!;
        error = string.Empty;

        var names = ScriptTypes.SplitTopLevel(signature) ?? new[] { signature };
        if (names.Length != definition.GenericArity)
        {
            error = $"{definition.Name} takes {definition.GenericArity} type argument(s), \"sig\" gives {names.Length}";
            return false;
        }

        var arguments = new ScriptType[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            if (!ScriptTypes.TryGet(names[i].Trim(), out arguments[i]))
            {
                error = $"\"{names[i].Trim()}\" is not a script type";
                return false;
            }
        }
        var key = string.Join(",", arguments.Select(a => a.Name));

        lock (definition.Lock)
        {
            if (definition.Closed.TryGetValue(key, out overload!))
                return true;
        }

        Type closed;
        try
        {
            closed = definition.ClrType.MakeGenericType(arguments.Select(a => a.ClrType).ToArray());
        }
        catch (ArgumentException)
        {
            error = $"{definition.Name} does not accept <{key}>";
            return false;
        }
        if (!IsDeclaredValid(closed, out var refusal))
        {
            error = refusal ?? $"{definition.Name} does not accept <{key}>";
            return false;
        }

        try
        {
            switch (definition.Kind)
            {
                case NodeKind.StoredVariable:
                    overload = new NodeOverload(definition, arguments, Array.Empty<NodePort>(),
                        new[] { new NodePort("value", arguments[0], null, hasDefault: false) },
                        Array.Empty<string>(), Array.Empty<string>(), null, false, null, null, null);
                    break;
                case NodeKind.WriteVariable:
                    var written = IsStep(definition)
                        ? new NodePort("by", arguments[0], Convert.ChangeType(1, arguments[0].ClrType, System.Globalization.CultureInfo.InvariantCulture), hasDefault: true)
                        : new NodePort("value", arguments[0], null, hasDefault: false);
                    overload = new NodeOverload(definition, arguments, new[] { written }, Array.Empty<NodePort>(),
                        new[] { "in" }, new[] { "next" }, null, false, null, null, null);
                    break;
                case NodeKind.Display:
                    overload = new NodeOverload(definition, arguments,
                        new[] { new NodePort("value", arguments[0], null, hasDefault: false) }, Array.Empty<NodePort>(),
                        Array.Empty<string>(), Array.Empty<string>(), null, false, null, null, null);
                    break;
                case NodeKind.Constant:
                case NodeKind.Relay:
                    overload = new NodeOverload(definition, arguments,
                        new[] { new NodePort("value", arguments[0], null, hasDefault: false, isSetting: definition.Kind == NodeKind.Constant) },
                        new[] { new NodePort("result", arguments[0], null, hasDefault: false) },
                        Array.Empty<string>(), Array.Empty<string>(), null, false, null, null, null);
                    break;
                case NodeKind.Flow:
                    overload = BuildFlow(definition, closed, arguments);
                    break;
                case NodeKind.Memory:
                case NodeKind.Drive:
                    overload = BuildShaped(definition, closed, arguments);
                    break;
                case NodeKind.Await:
                    overload = BuildAwait(definition, closed, arguments);
                    break;
                default:
                    var built = BuildMethods(definition, closed, arguments);
                    if (built.Count != 1)
                    {
                        error = $"{definition.Name}<{key}> has {built.Count} Run overloads; a generic node has exactly one";
                        return false;
                    }
                    overload = built[0];
                    break;
            }
        }
        catch (Exception ex)
        {
            error = $"{definition.Name}<{key}>: {ex.Message}";
            return false;
        }

        lock (definition.Lock)
        {
            if (definition.Closed.TryGetValue(key, out var raced))
            {
                overload = raced;
                return true;
            }
            definition.Closed.Add(key, overload);
        }
        return true;
    }

    // Constraints cannot say "a type with a JSON form", so a generic node that works for only some of what
    // its constraints let through says so with a static IsValidGenericType, the way generic components do.
    private static bool IsDeclaredValid(Type closed, out string? refusal)
    {
        refusal = null;
        var property = closed.GetProperty("IsValidGenericType", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (property == null || property.PropertyType != typeof(bool))
            return true;
        try
        {
            return property.GetValue(null) is true;
        }
        catch (TargetInvocationException ex)
        {
            refusal = ex.InnerException?.Message;
            return false;
        }
    }

    private static List<NodeOverload> BuildMethods(NodeDefinition definition, Type type, IReadOnlyList<ScriptType> typeArguments)
    {
        var overloads = new List<NodeOverload>();
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                     .OrderBy(m => m.MetadataToken))
        {
            if (method.Name != "Run")
                continue;
            overloads.Add(BuildMethod(definition, method, typeArguments));
        }
        return overloads;
    }

    private static NodeOverload BuildMethod(NodeDefinition definition, MethodInfo method, IReadOnlyList<ScriptType> typeArguments)
    {
        if (method.IsGenericMethodDefinition)
            throw new InvalidOperationException("Run cannot be a generic method; make the node class generic");

        var parameters = method.GetParameters();
        bool takesContext = parameters.Length > 0 && typeof(ScriptContextBase).IsAssignableFrom(parameters[0].ParameterType);
        if (takesContext && definition.Kind == NodeKind.Value)
            throw new InvalidOperationException("a ValueNode is pure and takes no context");

        var inputs = new List<NodePort>();
        var outputs = new List<NodePort>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        if (method.ReturnType != typeof(void))
        {
            var portName = method.ReturnParameter.GetCustomAttribute<PortAttribute>()?.Name ?? "result";
            outputs.Add(Port(portName, method.ReturnType, null, hasDefault: false, names));
        }

        for (int i = takesContext ? 1 : 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            var portName = parameter.GetCustomAttribute<PortAttribute>()?.Name ?? parameter.Name ?? $"arg{i}";
            if (parameter.IsOut)
            {
                outputs.Add(Port(portName, parameter.ParameterType.GetElementType()!, null, hasDefault: false, names));
                continue;
            }
            if (parameter.ParameterType.IsByRef)
                throw new InvalidOperationException($"parameter '{parameter.Name}' is ref; only out is allowed");
            var (fallback, hasDefault) = DefaultOf(parameter, portName);
            inputs.Add(Port(portName, parameter.ParameterType, fallback, hasDefault, names, parameter.IsDefined(typeof(SettingAttribute), false)));
        }

        byte? inline = null;
        var wasm = method.GetCustomAttribute<WasmAttribute>();
        if (wasm != null)
        {
            CheckInline(definition, wasm, inputs, outputs, takesContext);
            inline = wasm.Opcode;
        }

        bool flow = definition.Kind == NodeKind.Action;
        return new NodeOverload(definition, typeArguments, inputs, outputs,
            flow ? new[] { "in" } : Array.Empty<string>(),
            flow ? new[] { ActionNode.NextExit, ActionNode.FailedExit } : Array.Empty<string>(),
            method, takesContext, inline, null, null, Body(definition, method, inputs, outputs, takesContext, wasm != null));
    }

    // The writer a [WasmBody] names: a static method of the node class taking a WasmBodyWriter. Only for a
    // pure value overload of plain numbers, never one a variadic node folds, and never both inline forms.
    private static Action<WasmBodyWriter>? Body(NodeDefinition definition, MethodInfo method, List<NodePort> inputs,
        List<NodePort> outputs, bool takesContext, bool hasOpcode)
    {
        var body = method.GetCustomAttribute<WasmBodyAttribute>();
        if (body == null)
            return null;
        if (definition.Kind != NodeKind.Value || takesContext || hasOpcode)
            throw new InvalidOperationException("[WasmBody] is only for ValueNode overloads without [Wasm]");
        if (inputs.Any(p => p.Type.IsHandle) || outputs.Any(p => p.Type.IsHandle))
            throw new InvalidOperationException("[WasmBody] takes and gives plain numbers only");
        if (definition.IsVariadic && inputs.Count == 2 && inputs[0].Type == inputs[1].Type)
            throw new InvalidOperationException("[WasmBody] cannot go on an overload its variadic node folds");
        var writer = method.DeclaringType!.GetMethod(body.Writer, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(WasmBodyWriter) }, null);
        if (writer == null || writer.ReturnType != typeof(void))
            throw new InvalidOperationException($"[WasmBody] names {body.Writer}, which is not a static void {body.Writer}(WasmBodyWriter)");
        return (Action<WasmBodyWriter>)writer.CreateDelegate(typeof(Action<WasmBodyWriter>));
    }

    // An input's default: what [Default] spells, else its C# one. A [Default] goes through the literal rules a
    // saved value does, so the card shows, and the compiler uses, exactly what a graph could have written.
    // Past those, a vector's float or double lane may be an infinity, which no saved value spells but an
    // empty box's corners are: the lanes are checked with zero there, then the infinity is put in. -xlinka
    private static (object? Value, bool Has) DefaultOf(ParameterInfo parameter, string port)
    {
        if (parameter.GetCustomAttribute<DefaultAttribute>() is not { } stated)
            return (parameter.HasDefaultValue ? parameter.DefaultValue : null, parameter.HasDefaultValue);
        if (!ScriptTypes.TryGet(parameter.ParameterType, out var type))
            return (null, false);
        if (type.Kind == ScriptTypeKind.Handle)
            throw new InvalidOperationException($"[Default] on '{port}' is for numbers, vectors, colours and rotations, not {type.Name}");
        var lanes = stated.Lanes;
        bool vector = type.Kind == ScriptTypeKind.Vector;
        if (lanes.Length == 1 && vector && type.LaneCount > 1)
            lanes = Enumerable.Repeat(lanes[0], type.LaneCount).ToArray();
        if (lanes.Length == 0 || (!vector && lanes.Length != 1) || lanes.Any(double.IsNaN) || (!vector && double.IsInfinity(lanes[0])))
            throw new InvalidOperationException($"[Default] on '{port}' does not spell a {type.Name}");
        var finite = lanes.Select(lane => double.IsInfinity(lane) ? 0d : lane).ToArray();
        var value = vector ? GraphValue.Of(finite) : GraphValue.Of(finite[0]);
        if (!ConstantValue.TryConvert(value, type, out var constant, out var error))
            throw new InvalidOperationException($"[Default] on '{port}': {error}");
        for (int i = 0; i < lanes.Length; i++)
        {
            if (!double.IsInfinity(lanes[i]))
                continue;
            var leaf = type.Leaves[i];
            if (leaf.ClrType == typeof(float))
                leaf.Set(constant.Value!, (float)lanes[i]);
            else if (leaf.ClrType == typeof(double))
                leaf.Set(constant.Value!, lanes[i]);
            else
                throw new InvalidOperationException($"[Default] on '{port}' puts an infinity in a {type.Name} lane that is not a float or a double");
        }
        return (constant.Value, true);
    }

    // The one overload [Preferred] marks, if any.
    private static NodeOverload? PreferredOf(List<NodeOverload> overloads)
    {
        NodeOverload? preferred = null;
        foreach (var overload in overloads)
        {
            if (overload.Method?.IsDefined(typeof(PreferredAttribute), false) != true)
                continue;
            if (preferred != null)
                throw new InvalidOperationException("two overloads are [Preferred]");
            preferred = overload;
        }
        return preferred;
    }

    private static NodePort Port(string name, Type clrType, object? defaultValue, bool hasDefault, HashSet<string> names, bool isSetting = false)
    {
        var normalized = NodePort.NormalizeName(name);
        if (!names.Add(normalized))
            throw new InvalidOperationException($"two ports are called '{normalized}'");
        if (!ScriptTypes.TryGet(clrType, out var type))
            throw new InvalidOperationException($"port '{normalized}' has type {clrType}, which scripts cannot carry");
        if (hasDefault && defaultValue == null && !type.IsHandle)
            defaultValue = type.DefaultValue;
        return new NodePort(normalized, type, defaultValue, hasDefault, isSetting);
    }

    private static void CheckInline(NodeDefinition definition, WasmAttribute wasm, List<NodePort> inputs, List<NodePort> outputs, bool takesContext)
    {
        if (definition.Kind != NodeKind.Value || takesContext)
            throw new InvalidOperationException("[Wasm] is only for ValueNode overloads");
        if (!InlineOps.TryGet(wasm.Opcode, out var op))
            throw new InvalidOperationException($"opcode 0x{wasm.Opcode:X2} cannot be inlined");
        if (inputs.Count != op.Arity || outputs.Count != 1)
            throw new InvalidOperationException($"opcode 0x{wasm.Opcode:X2} takes {op.Arity} input(s) and gives one output");

        var operand = inputs[0].Type;
        foreach (var input in inputs)
        {
            if (input.Type != operand)
                throw new InvalidOperationException("an inlined overload takes inputs of one type");
        }
        foreach (var lane in operand.Lanes)
        {
            if (lane != op.Operand)
                throw new InvalidOperationException($"opcode 0x{wasm.Opcode:X2} does not operate on {operand.Name}");
        }

        var result = outputs[0].Type;
        if (result.LaneCount != operand.LaneCount || result.Lanes.Any(l => l != op.Result))
            throw new InvalidOperationException($"opcode 0x{wasm.Opcode:X2} cannot produce {result.Name} from {operand.Name}");
        if (operand.LaneCount > 1 && result != operand)
            throw new InvalidOperationException("a lane-wise inlined overload returns its input type");
        if (wasm.Lanes != 0 && wasm.Lanes != operand.LaneCount)
            throw new InvalidOperationException($"[Wasm] says {wasm.Lanes} lanes, {operand.Name} has {operand.LaneCount}");
    }

    private static NodeOverload BuildEvent(NodeDefinition definition, Type type)
    {
        var payload = EventPayloadOf(type)!;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var outputs = new List<NodePort>();
        foreach (var property in PayloadProperties(payload))
            outputs.Add(Port(property.Name, property.PropertyType, null, hasDefault: false, names));
        return new NodeOverload(definition, Array.Empty<ScriptType>(), Array.Empty<NodePort>(), outputs,
            Array.Empty<string>(), new[] { "next" }, null, false, null, null, payload);
    }

    // An await node: Start's parameters after the context and the ticket are the inputs, the payload's
    // properties the outputs, and the exits next and then each one [Answers] names.
    private static NodeOverload BuildAwait(NodeDefinition definition, Type type, IReadOnlyList<ScriptType>? typeArguments = null)
    {
        var payload = PayloadOf(type, typeof(AwaitNode<>))!;
        var answers = type.GetCustomAttribute<AnswersAttribute>()?.Exits;
        if (answers == null || answers.Length == 0)
            throw new InvalidOperationException("an await node names the exits its answers run with [Answers]");
        var starts = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => m.Name == "Start").ToArray();
        if (starts.Length != 1)
            throw new InvalidOperationException("an await node has exactly one public static Start");
        var method = starts[0];
        if (method.IsGenericMethodDefinition || method.ReturnType != typeof(void))
            throw new InvalidOperationException("Start is not generic and gives nothing back; its answer does");

        var parameters = method.GetParameters();
        bool takesContext = parameters.Length > 0 && typeof(ScriptContextBase).IsAssignableFrom(parameters[0].ParameterType);
        int ticket = takesContext ? 1 : 0;
        var ticketType = typeof(AwaitTicket<>).MakeGenericType(payload);
        if (parameters.Length <= ticket || parameters[ticket].ParameterType != ticketType)
            throw new InvalidOperationException($"Start takes an AwaitTicket<{payload.Name}> first, after any context");

        var names = new HashSet<string>(StringComparer.Ordinal);
        var inputs = new List<NodePort>();
        for (int i = ticket + 1; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (parameter.IsOut || parameter.ParameterType.IsByRef)
                throw new InvalidOperationException($"parameter '{parameter.Name}' is ref or out; what comes back comes with the answer");
            var portName = parameter.GetCustomAttribute<PortAttribute>()?.Name ?? parameter.Name ?? $"arg{i}";
            var (fallback, hasDefault) = DefaultOf(parameter, portName);
            inputs.Add(Port(portName, parameter.ParameterType, fallback, hasDefault, names, parameter.IsDefined(typeof(SettingAttribute), false)));
        }
        var outputs = new List<NodePort>();
        foreach (var property in PayloadProperties(payload))
            outputs.Add(Port(property.Name, property.PropertyType, null, hasDefault: false, names));

        var exits = new List<string> { "next" };
        foreach (var answer in answers)
        {
            var exit = NodePort.NormalizeName(answer);
            if (exits.Contains(exit))
                throw new InvalidOperationException($"two flow ports are called '{exit}'");
            exits.Add(exit);
        }
        return new NodeOverload(definition, typeArguments ?? Array.Empty<ScriptType>(), inputs, outputs, new[] { "in" }, exits.ToArray(), method,
            takesContext, null, null, payload);
    }

    // Declaration order. A positional record's primary constructor states it outright; otherwise the
    // metadata order of the properties, which is the order they are written in.
    internal static PropertyInfo[] PayloadProperties(Type payload)
    {
        var properties = payload.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead && p.Name != "EqualityContract")
            .ToList();

        foreach (var constructor in payload.GetConstructors())
        {
            var parameters = constructor.GetParameters();
            if (parameters.Length != properties.Count || parameters.Length == 0)
                continue;
            var ordered = new PropertyInfo[parameters.Length];
            bool all = true;
            for (int i = 0; i < parameters.Length && all; i++)
            {
                var match = properties.FirstOrDefault(p => p.Name == parameters[i].Name && p.PropertyType == parameters[i].ParameterType);
                all = match != null;
                if (all)
                    ordered[i] = match!;
            }
            if (all)
                return ordered;
        }

        return properties.OrderBy(p => p.MetadataToken).ToArray();
    }

    // With exitCount, the node's run of exits that long, the overload a graph's "sig" asks for; without, the node's own,
    // which says how long its run is when it has one.
    private static NodeOverload BuildFlow(NodeDefinition definition, Type type, IReadOnlyList<ScriptType> typeArguments, int? exitCount = null)
    {
        if (type.GetConstructor(Type.EmptyTypes) == null)
            throw new InvalidOperationException("a flow node needs a public parameterless constructor");

        var node = (FlowNode)Activator.CreateInstance(type)!;
        var builder = new FlowBuilder { AskedExits = exitCount };
        node.Build(builder);
        if (builder.Shape == null)
            throw new InvalidOperationException("Build declared no shape");
        if (builder.DeclaredExits > 0 && definition.IsGeneric)
            throw new InvalidOperationException("a generic flow node cannot grow its exits; its \"sig\" is its types");
        if (exitCount != null && builder.DeclaredExits == 0)
            throw new InvalidOperationException("Build declared no run of exits to grow");
        if (exitCount == null && !definition.IsGeneric)
            definition.ExitRun = builder.DeclaredExits;
        if (builder.Shape.Kind is FlowShapeKind.Follow or FlowShapeKind.FrameDelta or FlowShapeKind.Drive)
            throw new InvalidOperationException($"{builder.Shape.Kind} belongs to a memory or drive node, not a flow node");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var port in builder.Inputs.Concat(builder.Outputs))
        {
            if (!names.Add(port.Name))
                throw new InvalidOperationException($"two ports are called '{port.Name}'");
        }
        var exits = new HashSet<string>(StringComparer.Ordinal) { "in" };
        foreach (var exit in builder.Exits)
        {
            if (!exits.Add(exit))
                throw new InvalidOperationException($"two flow ports are called '{exit}'");
        }

        return new NodeOverload(definition, typeArguments, builder.Inputs.ToArray(), builder.Outputs.ToArray(),
            new[] { "in" }, builder.Exits.ToArray(), null, false, null, builder.Shape, null,
            signature: exitCount is { } asked && asked != builder.DeclaredExits ? asked.ToString(CultureInfo.InvariantCulture) : null);
    }

    // A memory or a drive node: ports and shape from Build, the way a flow node's come, with no flow in or out.
    private static NodeOverload BuildShaped(NodeDefinition definition, Type type, IReadOnlyList<ScriptType> typeArguments)
    {
        if (type.GetConstructor(Type.EmptyTypes) == null)
            throw new InvalidOperationException("a memory or drive node needs a public parameterless constructor");

        var builder = new FlowBuilder();
        switch (Activator.CreateInstance(type))
        {
            case MemoryNode memory:
                memory.Build(builder);
                break;
            case DriveNode drive:
                drive.Build(builder);
                break;
        }
        var shape = builder.Shape ?? throw new InvalidOperationException("Build declared no shape");
        bool fits = definition.Kind == NodeKind.Drive
            ? shape.Kind == FlowShapeKind.Drive
            : shape.Kind is FlowShapeKind.Follow or FlowShapeKind.FrameDelta;
        if (!fits)
            throw new InvalidOperationException($"a {definition.Kind} node cannot have the {shape.Kind} shape");
        if (builder.Exits.Count > 0)
            throw new InvalidOperationException("a memory or drive node has no flow exits");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var port in builder.Inputs.Concat(builder.Outputs))
        {
            if (!names.Add(port.Name))
                throw new InvalidOperationException($"two ports are called '{port.Name}'");
        }
        return new NodeOverload(definition, typeArguments, builder.Inputs.ToArray(), builder.Outputs.ToArray(),
            Array.Empty<string>(), Array.Empty<string>(), null, false, null, shape, null);
    }

    private static bool TryCanonicalSignature(string signature, out string canonical)
    {
        canonical = string.Empty;
        var names = ScriptTypes.SplitTopLevel(signature);
        if (names == null)
            return false;
        var parts = new string[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            if (!ScriptTypes.TryGet(names[i].Trim(), out var type))
                return false;
            parts[i] = type.Name;
        }
        canonical = string.Join(",", parts);
        return true;
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }
}
