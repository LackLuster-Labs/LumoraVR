// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Lumora.Core.Math;
using Lumora.Core.Persistence;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Core.Assets.Interop;

// Writes one of a package's graphs as one of ours. Each package node is handed to its handler, which adds our
// nodes and says where the package node's outputs come from, where flow into its operations goes, and which
// of our ports its inputs and calls feed. Those are joined once every node has been handled, so nothing waits
// on the order the package stored them in: an input reads through relays and constants to whatever finally
// gives the value, a world object becomes a binding typed by the port it feeds, and a call goes to whichever
// of our nodes took the operation it names.
//
// The package's space is the mirror of ours, and the importer mirrored everything it put in the world. The
// graph is left to work in the package's space, so its constants and its maths stay as written, and every
// value crossing into or out of the world is mirrored where it crosses: a position or a direction has its x
// negated, an angle like a turn rate has its y and z negated, and a rotation is turned half a turn about x on
// both sides. -xlinka
internal sealed class PackageGraphWriter
{
    public abstract record Source;
    public sealed record Port(string Node, string Name) : Source;
    public sealed record Constant(object? Raw) : Source;
    public sealed record WorldObject(IWorldElement? Element) : Source;
    public sealed record Through(PackageNode Node, string Input) : Source;

    public abstract record Target;
    public sealed record FlowIn(string Node, string Name) : Target;
    public sealed record CallThrough(PackageNode Node, string Call) : Target;
    // Flow that arrives here stops, as it does in the package.
    public sealed record Ends : Target;

    private enum Spatial : byte { None, Point, Axial, Rotation }

    private readonly PackageNodeIndex _index;
    private readonly NodeRegistry _registry;
    private readonly Dictionary<(PackageNode, string), Source> _outputs = new();
    private readonly Dictionary<(PackageNode, string), Target> _operations = new();
    private readonly List<(string Node, string Port, PackageNode From, string? Id, object? Unwired)> _inputs = new();
    private readonly List<(string Node, string Exit, PackageNode From, string? Id)> _exits = new();
    private readonly List<(IWorldElement Element, string Type)> _bindings = new();
    private readonly Dictionary<string, GraphNode> _ours = new(StringComparer.Ordinal);
    private int _next;
    private PackageNode? _current;

    // The slot the script will sit on, where a converted store keeps its value.
    public Slot Home { get; }

    // The first of our nodes each package node became, and the field or reference a store became.
    public Dictionary<PackageNode, string> Main { get; } = new();
    public Dictionary<PackageNode, IWorldElement> Holders { get; } = new();

    public ScriptGraph Graph { get; } = new();
    public List<string> Problems { get; } = new();
    public List<PackageNode> Unconverted { get; } = new();
    public int Converted { get; private set; }

    public PackageGraphWriter(PackageNodeIndex index, NodeRegistry registry, Slot home)
    {
        _index = index;
        _registry = registry;
        Home = home;
    }

    public IReadOnlyList<IWorldElement> Bindings => _bindings.Select(b => b.Element).ToList();

    // Handling starts for one package node.
    public void Begin(PackageNode node) => _current = node;

    public void Done(bool converted)
    {
        if (converted)
            Converted++;
        else if (_current != null)
            Unconverted.Add(_current);
    }

    public string Add(string type, string? signature = null)
    {
        string id = "n" + (++_next).ToString(CultureInfo.InvariantCulture);
        var node = new GraphNode(id, type) { Signature = signature };
        Graph.Nodes.Add(node);
        _ours[id] = node;
        if (_current != null)
            Main.TryAdd(_current, id);
        return id;
    }

    // A package node nothing here converts, kept as a node of no known type so the graph shows where it was.
    public string Placeholder(PackageNode node)
    {
        var name = new string(node.Class.Where(char.IsAsciiLetterOrDigit).ToArray());
        return Add("Missing" + (name.Length > 0 ? name : "Node"));
    }

    public void Output(PackageNode node, string output, Source source) => _outputs[(node, output)] = source;

    public bool HasOutput(PackageNode node, string output) => _outputs.ContainsKey((node, output));

    public Source? OutputOf(PackageNode node, string output) => _outputs.GetValueOrDefault((node, output));

    public void Operation(PackageNode node, string operation, Target target) => _operations[(node, operation)] = target;

    public void Input(string ourNode, string ourPort, PackageNode from, string? id, object? unwired)
        => _inputs.Add((ourNode, ourPort, from, id, unwired));

    public void Exit(string ourNode, string ourExit, PackageNode from, string? id)
        => _exits.Add((ourNode, ourExit, from, id));

    public void Wire(string fromNode, string fromPort, string toNode, string toPort)
        => Graph.Data.Add(new GraphWire(fromNode, fromPort, toNode, toPort));

    public void Flow(string fromNode, string fromExit, string toNode, string toIn)
        => Graph.Flow.Add(new GraphWire(fromNode, fromExit, toNode, toIn));

    public void Set(string ourNode, string port, GraphValue value) => _ours[ourNode].Values[port] = value;

    public void Bind(string ourNode, string port, IWorldElement element, string type)
        => _ours[ourNode].Values[port] = GraphValue.OfBinding(BindingOf(element, type));

    public void Problem(string message) => Problems.Add(_current != null ? $"{_current.Class}: {message}" : message);

    // Joins everything up once every node is handled.
    public void Finish()
    {
        foreach (var (node, port, from, id, unwired) in _inputs)
        {
            var source = id == null ? null : Resolve(from, id, 0);
            if (source == null)
            {
                if (unwired != null)
                    SetConstant(node, port, unwired);
                else if (id != null)
                    Problems.Add($"{from.Class}: an input comes from something that was not converted");
                continue;
            }
            switch (source)
            {
                case Port p:
                    Wire(p.Node, p.Name, node, port);
                    break;
                case Constant c:
                    if (c.Raw != null)
                        SetConstant(node, port, c.Raw);
                    else if (unwired != null)
                        SetConstant(node, port, unwired);
                    break;
                case WorldObject w when port == "source" && Overload(node)?.Definition is { TakesSource: true } listens:
                    // What an event listens to is a setting, bound to the slot of whatever it names.
                    if (w.Element is Component owner && listens.SourceType == "slot")
                        Bind(node, port, owner.Slot, "slot");
                    else if (w.Element != null)
                        Bind(node, port, w.Element, listens.SourceType);
                    break;
                case WorldObject w:
                    if (w.Element != null && PortType(node, port, input: true) is { IsHandle: true } type && type != ScriptTypes.String)
                    {
                        if (type.ClrType.IsInstanceOfType(w.Element))
                            Bind(node, port, w.Element, type.Name);
                        else if (w.Element is Component component && type.ClrType.IsInstanceOfType(component.Slot))
                            Bind(node, port, component.Slot, type.Name);
                        else
                            Problems.Add($"{from.Class}: {Describe(w.Element)} cannot go into {_ours[node].Type}.{port}, which takes {type.Name}");
                    }
                    break;
            }
        }

        foreach (var (node, exit, from, id) in _exits)
        {
            if (id == null)
                continue;
            if (ResolveFlow(id, 0, out bool ends) is { } target)
                Flow(node, exit, target.Node, target.Name);
            else if (!ends)
                Problems.Add($"{from.Class}: flow goes on to something that was not converted");
        }

        ConvertWhereTypesDiffer();
        MirrorAtTheWorld();
        for (int i = 0; i < _bindings.Count; i++)
            Graph.Bindings.Add(new GraphBinding("b" + i.ToString(CultureInfo.InvariantCulture), _bindings[i].Type));
    }

    private Source? Resolve(PackageNode from, string id, int depth)
    {
        if (depth > 64)
            return null;
        if (_index.TryNode(id, out var node, out var member))
        {
            if (!_outputs.TryGetValue((node, member), out var source))
                return null;
            if (source is Through through)
                return through.Node.Ref(through.Input) is { } next ? Resolve(through.Node, next, depth + 1) : new Constant(null);
            return source;
        }
        return new WorldObject(_index.World(id));
    }

    // Where flow sent to an operation lands; ends when it stops there, on purpose, with nothing to land on.
    private FlowIn? ResolveFlow(string id, int depth, out bool ends)
    {
        ends = false;
        if (depth > 64 || !_index.TryNode(id, out var node, out var member) || !_operations.TryGetValue((node, member), out var target))
            return null;
        switch (target)
        {
            case FlowIn flow:
                return flow;
            case CallThrough call when call.Node.Ref(call.Call) is { } next:
                return ResolveFlow(next, depth + 1, out ends);
            default:
                ends = true;
                return null;
        }
    }

    private int BindingOf(IWorldElement element, string type)
    {
        for (int i = 0; i < _bindings.Count; i++)
        {
            if (ReferenceEquals(_bindings[i].Element, element) && _bindings[i].Type == type)
                return i;
        }
        _bindings.Add((element, type));
        return _bindings.Count - 1;
    }

    private static string Describe(IWorldElement element) => element switch
    {
        Slot slot => $"slot '{slot.Name.Value}'",
        Component component => component.GetType().Name,
        _ => element.GetType().Name,
    };

    // The overload an added node will resolve to, for the type of one of its ports.
    private NodeOverload? Overload(string ourNode)
    {
        var node = _ours[ourNode];
        if (!_registry.TryGetDefinition(node.Type, out var definition))
            return null;
        return _registry.TryResolve(definition, node.Signature, out var overload, out _) ? overload : null;
    }

    private ScriptType? PortType(string ourNode, string port, bool input)
    {
        var overload = Overload(ourNode);
        if (overload == null)
            return null;
        var ports = input ? overload.Inputs : overload.Outputs;
        int index = input ? overload.InputIndex(port) : overload.OutputIndex(port);
        return index >= 0 ? ports[index].Type : null;
    }

    // A package constant as one of our values, shaped by the port it lands on.
    private void SetConstant(string ourNode, string port, object raw)
    {
        if (raw is GraphValue ready)
        {
            Set(ourNode, port, ready);
            return;
        }
        var type = PortType(ourNode, port, input: true);
        if (ToValue(raw, type) is { } value)
            Set(ourNode, port, value);
    }

    public static bool IsLinearProfile(DataTreeList list)
    {
        foreach (var item in list)
        {
            if ((item as DataTreeValue)?.Value is string profile)
                return profile == "Linear";
        }
        return false;
    }

    public static GraphValue? ToValue(object? raw, ScriptType? type)
    {
        if (raw is DataTreeValue leaf)
            raw = leaf.Value;
        switch (raw)
        {
            case null:
                return null;
            case GraphValue value:
                return value;
            case bool flag:
                return GraphValue.Of(flag);
            case string text:
                if (type != null && type.LaneCount > 1 && Numbers(text) is { Length: > 0 } parsed)
                    return GraphValue.Of(parsed);
                return GraphValue.Of(PackageTextMarkup.Translate(text));
            case DataTreeList list:
            {
                var numbers = new List<double>();
                foreach (var item in list)
                {
                    if ((item as DataTreeValue)?.Value is IConvertible number and not string and not bool)
                        numbers.Add(Convert.ToDouble(number, CultureInfo.InvariantCulture));
                    else
                        break;
                }
                for (int i = 0; i < numbers.Count; i++)
                {
                    if (double.IsNaN(numbers[i]))
                        return null;
                    numbers[i] = Finite(numbers[i]);
                }
                // A colour names its profile last; ours hold sRGB, so a Linear one is converted.
                if (numbers.Count >= 3 && IsLinearProfile(list))
                {
                    for (int i = 0; i < 3; i++)
                    {
                        float lane = (float)numbers[i];
                        numbers[i] = lane < 0f ? -ColorSpace.LinearToGamma(-lane) : ColorSpace.LinearToGamma(lane);
                    }
                }
                return numbers.Count > 0 ? GraphValue.Of(numbers.ToArray()) : null;
            }
            case double[] lanes:
                return GraphValue.Of(lanes);
            case IConvertible number:
            {
                double d = Convert.ToDouble(number, CultureInfo.InvariantCulture);
                return double.IsNaN(d) ? null : GraphValue.Of(Finite(d));
            }
        }
        return null;
    }

    // A graph holds finite numbers, so an infinity stands as the largest number there is: a float port reads that
    // back as the infinity itself, a double port as the largest double, and nothing compares past either. A
    // package seeds a search for the nearest of something with one; dropped, the search had nothing to beat.
    private static double Finite(double d)
        => double.IsPositiveInfinity(d) ? double.MaxValue : double.IsNegativeInfinity(d) ? double.MinValue : d;

    private static double[]? Numbers(string text)
    {
        var parts = text.Split(new[] { '[', ']', ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var numbers = new List<double>();
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                break;
            numbers.Add(d);
        }
        return numbers.Count > 0 ? numbers.ToArray() : null;
    }

    // A wire between two types the language does not join gets the conversion between them: a number to text, a
    // double to a float, one whole number to another, a colour to HDR and back. Anything else is left for the
    // compile to name.
    private void ConvertWhereTypesDiffer()
    {
        // A component asked only for its slot, when what arrives is already a slot (what a collision names
        // here), is that slot.
        foreach (var wire in Graph.Data.ToList())
        {
            if (_ours[wire.ToNode].Type != "ComponentSlot" || PortType(wire.FromNode, wire.FromPort, input: false) != ScriptTypes.Get("slot"))
                continue;
            for (int i = 0; i < Graph.Data.Count; i++)
            {
                var read = Graph.Data[i];
                if (read.FromNode == wire.ToNode && read.FromPort == "slot")
                    Graph.Data[i] = new GraphWire(wire.FromNode, wire.FromPort, read.ToNode, read.ToPort);
            }
            Graph.Data.Remove(wire);
        }

        var wires = Graph.Data.ToList();
        Graph.Data.Clear();
        foreach (var wire in wires)
        {
            var from = PortType(wire.FromNode, wire.FromPort, input: false);
            var to = PortType(wire.ToNode, wire.ToPort, input: true);
            if (from == null || to == null || ScriptTypes.Accepts(to, from) || Converter(from.Name, to.Name) is not { } converter)
            {
                Graph.Data.Add(wire);
                continue;
            }
            string node = Add(converter.Type, converter.Signature);
            Wire(wire.FromNode, wire.FromPort, node, "value");
            Graph.Data.Add(new GraphWire(node, converter.Output, wire.ToNode, wire.ToPort));
        }
    }

    private static (string Type, string? Signature, string Output)? Converter(string from, string to)
    {
        bool number = from is "float" or "double" or "int" or "long" or "byte" or "sbyte" or "short" or "ushort" or "uint" or "ulong";
        return (from, to) switch
        {
            ("colorHDR", "color") => ("HdrToColor", null, "color"),
            ("color", "colorHDR") => ("ColorToHdr", null, "color"),
            (_, "string") => ("ToText", from, "result"),
            (_, "float") when number => ("ToFloat", from, "result"),
            (_, "double") when number => ("ToDouble", from, "result"),
            (_, "int") when number => ("ToInt", from, "result"),
            (_, "long") when number => ("ToLong", from, "result"),
            (_, "byte") when number => ("ToByte", from, "result"),
            _ => null,
        };
    }

    // Every wire into or out of a node that reads or changes the world, and every constant set on one, mirrored
    // where it carries a position, a direction, an angle or a rotation. A wire between two such ports of one
    // kind carries a value already in our space and is left alone.
    private void MirrorAtTheWorld()
    {
        var wires = Graph.Data.ToList();
        Graph.Data.Clear();
        foreach (var wire in wires)
        {
            var from = Kind(wire.FromNode, wire.FromPort, input: false);
            var to = Kind(wire.ToNode, wire.ToPort, input: true);
            if (from == to || (from == Spatial.None && to == Spatial.None))
            {
                Graph.Data.Add(wire);
                continue;
            }
            var (node, port) = (wire.FromNode, wire.FromPort);
            if (from != Spatial.None)
                (node, port) = Mirror(from, node, port);
            if (to != Spatial.None)
                (node, port) = Mirror(to, node, port);
            Graph.Data.Add(new GraphWire(node, port, wire.ToNode, wire.ToPort));
        }

        foreach (var node in Graph.Nodes.ToList())
        {
            foreach (var (port, value) in node.Values.ToList())
            {
                if (value.Kind != GraphValueKind.Numbers)
                    continue;
                var kind = Kind(node.Id, port, input: true);
                var n = value.Numbers;
                if (kind == Spatial.Point && n.Length == 3)
                    node.Values[port] = GraphValue.Of(new[] { -n[0], n[1], n[2] });
                else if (kind == Spatial.Axial && n.Length == 3)
                    node.Values[port] = GraphValue.Of(new[] { n[0], -n[1], -n[2] });
                else if (kind == Spatial.Rotation && n.Length == 4)
                    node.Values[port] = GraphValue.Of(new[] { n[0], -n[1], -n[2], n[3] });
            }
        }
    }

    private (string Node, string Port) Mirror(Spatial kind, string node, string port)
    {
        if (kind == Spatial.Rotation)
        {
            // Half a turn about x on both sides: the reflection's conjugate.
            var halfTurn = GraphValue.Of(new[] { 1d, 0d, 0d, 0d });
            string first = Add("CombineRotations", "floatQ,floatQ");
            Wire(node, port, first, "then");
            Set(first, "first", halfTurn);
            string second = Add("CombineRotations", "floatQ,floatQ");
            Wire(first, "rotation", second, "first");
            Set(second, "then", halfTurn);
            return (second, "rotation");
        }
        string flip = Add("Multiply", "float3,float3");
        Wire(node, port, flip, "a");
        Set(flip, "b", GraphValue.Of(kind == Spatial.Point ? new[] { -1d, 1d, 1d } : new[] { 1d, -1d, -1d }));
        return (flip, "result");
    }

    private Spatial Kind(string ourNode, string port, bool input)
    {
        var overload = Overload(ourNode);
        if (overload == null || overload.Definition.Capability == Capability.Core || overload.Definition.Path.StartsWith("Math/", StringComparison.Ordinal))
            return Spatial.None;
        // A scale has no handedness.
        if (overload.Definition.Path.EndsWith("Convert Scale", StringComparison.Ordinal))
            return Spatial.None;
        var ports = input ? overload.Inputs : overload.Outputs;
        int index = input ? overload.InputIndex(port) : overload.OutputIndex(port);
        if (index < 0)
            return Spatial.None;
        var type = ports[index].Type;
        if (type == ScriptTypes.Get(typeof(floatQ)))
            return Spatial.Rotation;
        if (type != ScriptTypes.Get(typeof(float3)))
            return Spatial.None;

        // A field's value is what the field is: its name says.
        string name = port;
        if (overload.Definition.Path.StartsWith("World/Fields/", StringComparison.Ordinal))
        {
            name = "Value";
            foreach (var value in _ours[ourNode].Values.Values)
            {
                if (value.Kind == GraphValueKind.Binding && value.Binding < _bindings.Count
                    && _index.FieldName(_bindings[value.Binding].Element) is { } field)
                    name = field;
            }
        }
        return Classify(name);
    }

    private static Spatial Classify(string name)
    {
        string lower = name.ToLowerInvariant();
        foreach (var never in new[] { "scale", "size", "extent", "range", "color", "colour", "value", "seed", "phase" })
        {
            if (lower.Contains(never, StringComparison.Ordinal))
                return Spatial.None;
        }
        foreach (var axial in new[] { "angular", "euler", "torque", "spin", "angles" })
        {
            if (lower.Contains(axial, StringComparison.Ordinal))
                return Spatial.Axial;
        }
        return Spatial.Point;
    }
}
