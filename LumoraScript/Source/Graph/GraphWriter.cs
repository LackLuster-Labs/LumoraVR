// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Lumora.Core.Math;

namespace Lumora.Script;

// The canonical form: fixed key order, values sorted by name, no whitespace. It is what gets hashed,
// so two spellings of one graph compile once.
internal static class GraphWriter
{
    public static byte[] Write(ScriptGraph graph)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", ScriptGraph.FormatName);

            writer.WriteStartArray("bindings");
            foreach (var binding in graph.Bindings)
            {
                writer.WriteStartObject();
                writer.WriteString("name", binding.Name);
                writer.WriteString("type", binding.Type);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("nodes");
            foreach (var node in graph.Nodes)
            {
                writer.WriteStartObject();
                writer.WriteString("id", node.Id);
                writer.WriteString("type", node.Type);
                writer.WriteNumber("v", node.Version);
                if (!string.IsNullOrEmpty(node.Signature))
                    writer.WriteString("sig", node.Signature);
                writer.WriteStartArray("pos");
                writer.WriteNumberValue(node.Position.x);
                writer.WriteNumberValue(node.Position.y);
                writer.WriteNumberValue(node.Position.z);
                writer.WriteEndArray();
                if (!IsSquare(node.Rotation))
                {
                    writer.WriteStartArray("rot");
                    writer.WriteNumberValue(node.Rotation.x);
                    writer.WriteNumberValue(node.Rotation.y);
                    writer.WriteNumberValue(node.Rotation.z);
                    writer.WriteNumberValue(node.Rotation.w);
                    writer.WriteEndArray();
                }
                if (node.Values.Count > 0)
                {
                    writer.WriteStartObject("values");
                    foreach (var pair in node.Values.OrderBy(p => p.Key, System.StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(pair.Key);
                        WriteValue(writer, pair.Value);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            WriteWires(writer, "flow", graph.Flow);
            WriteWires(writer, "data", graph.Data);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    // Within rounding of no turn: what a node read back through a hand lands on when it was never turned.
    private static bool IsSquare(floatQ rotation) => System.Math.Abs(floatQ.Dot(rotation, floatQ.Identity)) > 1f - 1e-6f;

    private static void WriteWires(Utf8JsonWriter writer, string name, System.Collections.Generic.List<GraphWire> wires)
    {
        writer.WriteStartArray(name);
        foreach (var wire in wires)
        {
            writer.WriteStartArray();
            writer.WriteStringValue($"{wire.FromNode}.{wire.FromPort}");
            writer.WriteStringValue($"{wire.ToNode}.{wire.ToPort}");
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    public static string WriteValueText(GraphValue value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteValue(writer, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter writer, GraphValue value)
    {
        switch (value.Kind)
        {
            case GraphValueKind.Bool:
                writer.WriteBooleanValue(value.Bool);
                break;
            case GraphValueKind.Number:
                writer.WriteNumberValue(value.Number);
                break;
            case GraphValueKind.String:
                writer.WriteStringValue(value.String);
                break;
            case GraphValueKind.Binding:
                writer.WriteStartObject();
                writer.WriteNumber("binding", value.Binding);
                writer.WriteEndObject();
                break;
            case GraphValueKind.Numbers:
                writer.WriteStartArray();
                foreach (var number in value.Numbers)
                    writer.WriteNumberValue(number);
                writer.WriteEndArray();
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
