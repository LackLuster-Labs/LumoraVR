// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Text.Json;
using Lumora.Core.Math;

namespace Lumora.Script;

// Reads graph.json strictly. Nesting is bounded by the parser and every name, id and signature is checked
// as it is read; how big the graph is, nothing checks. Unknown keys are ignored, so a newer editor's extras
// do not break an older reader, and they never reach the canonical form. -xlinka
internal static class GraphReader
{
    public static ScriptGraph Read(ReadOnlySpan<byte> utf8, GraphLimits limits)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions
            {
                MaxDepth = limits.MaxDepth,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException ex)
        {
            throw new GraphFormatException($"not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new GraphFormatException("a graph is a JSON object");

            var format = String(Required(root, "format"), "format", 64);
            if (format != ScriptGraph.FormatName)
                throw new GraphFormatException($"format '{format}' is not {ScriptGraph.FormatName}");

            var graph = new ScriptGraph();
            if (root.TryGetProperty("bindings", out var bindings))
                ReadBindings(graph, bindings);
            ReadNodes(graph, Required(root, "nodes"), limits);
            if (root.TryGetProperty("flow", out var flow))
                ReadWires(graph.Flow, flow, "flow");
            if (root.TryGetProperty("data", out var data))
                ReadWires(graph.Data, data, "data");
            return graph;
        }
    }

    // One value on its own, as an editor keeps it between packs. The same rules as a value in a graph.
    public static GraphValue ReadValue(string json, GraphLimits limits)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = limits.MaxDepth,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException ex)
        {
            throw new GraphFormatException($"not valid JSON: {ex.Message}");
        }

        using (document)
            return Value(document.RootElement, "value", limits);
    }

    private static void ReadBindings(ScriptGraph graph, JsonElement bindings)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Array(bindings, "bindings"))
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new GraphFormatException("a binding is an object");
            var name = String(Required(entry, "name"), "binding name", NodePort.MaxNameLength);
            if (!NodePort.IsValidName(name) || !names.Add(name))
                throw new GraphFormatException($"binding name '{name}' is not usable or is repeated");
            var type = String(Required(entry, "type"), "binding type", ScriptTypes.MaxNameLength);
            graph.Bindings.Add(new GraphBinding(name, type));
        }
    }

    private static void ReadNodes(ScriptGraph graph, JsonElement nodes, GraphLimits limits)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Array(nodes, "nodes"))
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new GraphFormatException("a node is an object");

            var id = String(Required(entry, "id"), "node id", GraphNode.MaxIdLength);
            if (!GraphNode.IsValidId(id))
                throw new GraphFormatException($"'{id}' is not a usable node id");
            if (!ids.Add(id))
                throw new GraphFormatException($"node id '{id}' is used twice");

            var type = String(Required(entry, "type"), "node type", NodePort.MaxNameLength);
            if (!NodePort.IsValidName(type))
                throw new GraphFormatException($"'{type}' is not a usable node type");

            var node = new GraphNode(id, type);
            if (entry.TryGetProperty("v", out var version))
            {
                if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int v) || v < 1 || v > 1_000_000)
                    throw new GraphFormatException($"node '{id}' has an unusable version");
                node.Version = v;
            }
            if (entry.TryGetProperty("sig", out var signature))
                node.Signature = String(signature, "sig", limits.MaxSignatureLength);
            if (entry.TryGetProperty("pos", out var position))
            {
                var numbers = Numbers(position, "pos", 3);
                if (numbers.Length != 3)
                    throw new GraphFormatException($"node '{id}' position needs three numbers");
                node.Position = new float3((float)numbers[0], (float)numbers[1], (float)numbers[2]);
            }
            if (entry.TryGetProperty("rot", out var rotation))
            {
                var numbers = Numbers(rotation, "rot", 4);
                if (numbers.Length != 4)
                    throw new GraphFormatException($"node '{id}' rotation needs four numbers");
                var turn = new floatQ((float)numbers[0], (float)numbers[1], (float)numbers[2], (float)numbers[3]);
                // A turn that is no turn at all reads as square rather than failing the whole graph.
                node.Rotation = turn.Length > 1e-6f ? turn.Normalized : floatQ.Identity;
            }
            if (entry.TryGetProperty("values", out var values))
                ReadValues(node, values, limits);
            graph.Nodes.Add(node);
        }
    }

    private static void ReadValues(GraphNode node, JsonElement values, GraphLimits limits)
    {
        if (values.ValueKind != JsonValueKind.Object)
            throw new GraphFormatException($"node '{node.Id}' values is an object");

        foreach (var property in values.EnumerateObject())
        {
            if (!NodePort.IsValidName(property.Name))
                throw new GraphFormatException($"node '{node.Id}' has a value named '{property.Name}'");
            if (node.Values.ContainsKey(property.Name))
                throw new GraphFormatException($"node '{node.Id}' sets '{property.Name}' twice");
            node.Values.Add(property.Name, Value(property.Value, $"{node.Id}.{property.Name}", limits));
        }
    }

    private static GraphValue Value(JsonElement element, string where, GraphLimits limits)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return GraphValue.Null;
            case JsonValueKind.True:
                return GraphValue.Of(true);
            case JsonValueKind.False:
                return GraphValue.Of(false);
            case JsonValueKind.Number:
                return GraphValue.Of(Number(element, where));
            case JsonValueKind.String:
                return GraphValue.Of(String(element, where, int.MaxValue));
            case JsonValueKind.Array:
                return GraphValue.Of(Numbers(element, where, limits.MaxNumbersPerValue));
            case JsonValueKind.Object:
            {
                int properties = 0;
                foreach (var _ in element.EnumerateObject())
                    properties++;
                if (properties != 1 || !element.TryGetProperty("binding", out var slot)
                    || slot.ValueKind != JsonValueKind.Number || !slot.TryGetInt32(out int index)
                    || index < 0)
                    throw new GraphFormatException($"{where}: the only object value is {{\"binding\": n}}");
                return GraphValue.OfBinding(index);
            }
            default:
                throw new GraphFormatException($"{where}: unsupported value");
        }
    }

    private static void ReadWires(List<GraphWire> target, JsonElement wires, string what)
    {
        foreach (var entry in Array(wires, what))
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() != 2)
                throw new GraphFormatException($"a {what} wire is [\"node.port\", \"node.port\"]");
            var (fromNode, fromPort) = Endpoint(entry[0], what);
            var (toNode, toPort) = Endpoint(entry[1], what);
            target.Add(new GraphWire(fromNode, fromPort, toNode, toPort));
        }
    }

    private static (string Node, string Port) Endpoint(JsonElement element, string what)
    {
        var text = String(element, $"{what} wire end", GraphNode.MaxIdLength + NodePort.MaxNameLength + 1);
        int dot = text.IndexOf('.');
        if (dot <= 0 || dot != text.LastIndexOf('.'))
            throw new GraphFormatException($"'{text}' is not node.port");
        var node = text[..dot];
        var port = text[(dot + 1)..];
        if (!GraphNode.IsValidId(node) || !NodePort.IsValidName(port))
            throw new GraphFormatException($"'{text}' is not node.port");
        return (node, port);
    }

    private static JsonElement Required(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) ? value : throw new GraphFormatException($"missing \"{name}\"");

    private static JsonElement.ArrayEnumerator Array(JsonElement element, string what)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new GraphFormatException($"\"{what}\" is an array");
        return element.EnumerateArray();
    }

    private static string String(JsonElement element, string what, int maxLength)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw new GraphFormatException($"{what} is a string");
        var text = element.GetString()!;
        if (text.Length > maxLength)
            throw new GraphFormatException($"{what} is longer than {maxLength}");
        return text;
    }

    private static double Number(JsonElement element, string what)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out double value) || !double.IsFinite(value))
            throw new GraphFormatException($"{what} is not a finite number");
        return value;
    }

    private static double[] Numbers(JsonElement element, string what, int max)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new GraphFormatException($"{what} is an array of numbers");
        int length = element.GetArrayLength();
        if (length == 0 || length > max)
            throw new GraphFormatException($"{what} holds 1 to {max} numbers");
        var numbers = new double[length];
        for (int i = 0; i < length; i++)
            numbers[i] = Number(element[i], what);
        return numbers;
    }
}
