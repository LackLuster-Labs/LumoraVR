// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Lumora.Core.Assets.Interop;

// Maths and values that are not one node of ours: followers with a memory, comparisons within a tolerance,
// angles, colours, text formatted from a constant pattern, and picking by index, each built from our nodes.
// -xlinka
internal static partial class PackageNodeTable
{
    private static void Maths()
    {
        // Rotations compose rather than multiply: the second turn first, then the first.
        On("ValueMul", s =>
        {
            if (s.Arg(0) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            if (type == "floatQ")
            {
                string turn = s.Add("CombineRotations", "floatQ,floatQ");
                s.In("A", turn, "then", new[] { 0d, 0d, 0d, 1d });
                s.In("B", turn, "first", new[] { 0d, 0d, 0d, 1d });
                s.Out("", turn, "rotation");
                return;
            }
            string product = s.Add("Multiply", $"{type},{type}");
            s.In("A", product, "a", Identity(type));
            s.In("B", product, "b", Identity(type));
            s.Out("", product, "result");
        });
        // A whole number divided by zero is 0 in the package, where ours would stop the call.
        On("ValueDiv", s =>
        {
            if (s.Arg(0) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            if (type is not ("int" or "long" or "uint" or "ulong"))
            {
                string plain = s.Add("Divide", $"{type},{type}");
                s.In("A", plain, "a", Identity(type));
                s.In("B", plain, "b", Identity(type));
                s.Out("", plain, "result");
                return;
            }
            string zero = s.Add("IsEqual", $"{type},{type}");
            s.In("B", zero, "a", 1d);
            s.Set(zero, "b", 0d);
            string safe = s.Add("Conditional", type);
            s.Wire(zero, "result", safe, "condition");
            s.Set(safe, "whenTrue", 1d);
            s.In("B", safe, "whenFalse", 1d);
            string quotient = s.Add("Divide", $"{type},{type}");
            s.In("A", quotient, "a", 1d);
            s.Wire(safe, "result", quotient, "b");
            string result = s.Add("Conditional", type);
            s.Wire(zero, "result", result, "condition");
            s.Set(result, "whenTrue", 0d);
            s.Wire(quotient, "result", result, "whenFalse");
            s.Out("", result, "result");
        });
        Map("ValueAbs", "Abs", "{T}", ins: "N=value", outs: "=result");
        Map("ValueSmoothLerp", "SmoothFollow", "{T}", ins: "Input=value Speed=speed", outs: "=result", values: "Speed=0");
        Map("SmoothSlerp_floatQ", "SmoothFollow", "floatQ", ins: "Input=value Speed=speed", outs: "=result", values: "Speed=0");
        Map("ValueConstantLerp", "SteadyFollow", "{T}", ins: "Input=value Speed=speed", outs: "=result", values: "Speed=0");
        Map("ValueDelta", "FrameChange", "{T}", ins: "Value=value", outs: "=delta");
        Map("Approximately_Float", "Approximately", "float,float,float", ins: "A=a B=b Epsilon=epsilon", outs: "=result");
        Map("Approximately_Float3", "Approximately", "float3,float3,float", ins: "A=a B=b Epsilon=epsilon", outs: "=result");
        Map("Approximately_FloatQ", "Approximately", "floatQ,floatQ,float", ins: "A=a B=b Epsilon=epsilon", outs: "=result",
            values: "A=[0,0,0,1] B=[0,0,0,1]");
        foreach (var (cls, type) in new[] { ("Exp_Float", "float"), ("Exp_Double", "double"), ("Exp_Float2", "float2"), ("Exp_Float3", "float3"),
            ("Exp_Float4", "float4"), ("Exp_Color", "color"), ("Exp_ColorX", "colorHDR") })
            Map(cls, "Exp", type, ins: "N=power", outs: "=result");
        Map("FromToRotation_floatQ", "FromToRotation", "float3,float3", ins: "From=from To=to", outs: "=rotation");
        Map("Angle_Float3", "AngleBetween", "float3,float3", ins: "A=a B=b", outs: "=degrees");
        Map("Angle_Float2", "AngleBetween", "float2,float2", ins: "A=a B=b", outs: "=degrees");
        foreach (var (cls, type) in new[] { ("Avg_Float", "float"), ("Avg_Double", "double"), ("Avg_Float2", "float2"), ("Avg_Float3", "float3"),
            ("Avg_Float4", "float4"), ("Avg_ColorX", "colorHDR") })
            Map(cls, "Average", $"{type},{type}", ins: "A=a B=b", outs: "=result");
        Map("Unpack_Float4", "SplitVector", "float4", ins: "V=vector", outs: "X=x Y=y Z=z W=w");
        Map("Parse_Int", "ParseInt", null, ins: "Str=text", outs: "Value=value IsParsed=ok");
        Map("ComposeTRS_Float4x4", "ComposeTRS", null, ins: "Position=position Rotation=rotation Scale=scale", outs: "=matrix",
            values: "Rotation=[0,0,0,1] Scale=[1,1,1]");
        foreach (var (cls, part) in new[] { ("Decomposed_Position_Float4x4", "position"), ("Decomposed_Rotation_Float4x4", "rotation"),
            ("Decomposed_Scale_Float4x4", "scale") })
            Map(cls, "DecomposeMatrix", "float4x4", ins: "A=matrix", outs: $"={part}");
        foreach (var (cls, part) in new[] { ("Decomposed_Rotation_Float3x3", "rotation"), ("Decomposed_Scale_Float3x3", "scale") })
            Map(cls, "DecomposeMatrix", "float3x3", ins: "A=matrix", outs: $"={part}");

        // Away from the plane across b: what lies along b turned back, the rest kept.
        On("Reflect_Float3", s =>
        {
            string along = s.Add("Dot", "float3,float3");
            s.In("A", along, "a");
            s.In("B", along, "b");
            string length = s.Add("Dot", "float3,float3");
            s.In("B", length, "a");
            s.In("B", length, "b");
            string share = s.Add("Divide", "float,float");
            s.Wire(along, "result", share, "a");
            s.Wire(length, "result", share, "b");
            string twice = s.Add("Multiply", "float,float");
            s.Wire(share, "result", twice, "a");
            s.Set(twice, "b", 2d);
            string back = s.Add("Multiply", "float3,float");
            s.In("B", back, "a");
            s.Wire(twice, "result", back, "b");
            string mirrored = s.Add("Subtract", "float3,float3");
            s.In("A", mirrored, "a");
            s.Wire(back, "result", mirrored, "b");
            s.Out("", mirrored, "result");
        });

        // Along the rotations by lerp, 0 the first and 1 the next: each neighbouring pair blended by the part of
        // lerp between them, and the pair lerp falls in picked. Before the first it is the first, past the end
        // the last.
        On("MultiSlerp_floatQ", s =>
        {
            int count = s.Node.Refs("Operands").Count;
            var identity = new[] { 0d, 0d, 0d, 1d };
            if (count == 0)
            {
                s.OutConstant("", identity);
                return;
            }
            if (count == 1)
            {
                string only = s.Add("Slerp", "floatQ,floatQ,float");
                s.InAt("Operands", 0, only, "a", identity);
                s.InAt("Operands", 0, only, "b", identity);
                s.Set(only, "t", 0d);
                s.Out("", only, "rotation");
                return;
            }
            var pairs = new string[count - 1];
            for (int k = 0; k < count - 1; k++)
            {
                string part = s.Add("Subtract", "float,float");
                s.In("Lerp", part, "a", 0d);
                s.Set(part, "b", (double)k);
                string t = s.Add("Clamp", "float,float,float");
                s.Wire(part, "result", t, "value");
                s.Set(t, "min", 0d);
                s.Set(t, "max", 1d);
                string blend = s.Add("Slerp", "floatQ,floatQ,float");
                s.InAt("Operands", k, blend, "a", identity);
                s.InAt("Operands", k + 1, blend, "b", identity);
                s.Wire(t, "result", blend, "t");
                pairs[k] = blend;
            }
            string chain = pairs[^1];
            string port = "rotation";
            for (int k = count - 3; k >= 0; k--)
            {
                string before = s.Add("LessThan", "float,float");
                s.In("Lerp", before, "a", 0d);
                s.Set(before, "b", (double)(k + 1));
                string pick = s.Add("Conditional", "floatQ");
                s.Wire(before, "result", pick, "condition");
                s.Wire(pairs[k], "rotation", pick, "whenTrue");
                s.Wire(chain, port, pick, "whenFalse");
                chain = pick;
                port = "result";
            }
            s.Out("", chain, port);
        });
        Map("InvertColorX", "InvertColor", "colorHDR", ins: "Color=value", outs: "=color");
        Map("ColorXHue", "FromHsvHdr", null, ins: "Hue=hue", outs: "=color", set: "saturation=1 brightness=1");
        Map("ColorXMulAlpha", "MultiplyColorChannel", "colorHDR,colorchannel,float", ins: "Color=value Value=amount", outs: "=color",
            set: "channel=Alpha");
        Map("ToString_DateTime", "FormatDate", null, ins: "V=date Format=format", outs: "=text", values: "Format=G");

        On("ValueSquare", s =>
        {
            if (s.Arg(0) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            string square = s.Add("Multiply", $"{type},{type}");
            s.In("N", square, "a");
            s.In("N", square, "b");
            s.Out("", square, "result");
        });

        // Left to right, one Subtract a step.
        On("ValueSubMulti", s =>
        {
            if (s.Arg(0) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            int count = s.Node.Refs("Inputs").Count;
            if (count < 2)
            {
                s.OutThrough("", "Inputs");
                return;
            }
            string? last = null;
            for (int i = 1; i < count; i++)
            {
                string step = s.Add("Subtract", $"{type},{type}");
                if (last == null)
                    s.InAt("Inputs", 0, step, "a");
                else
                    s.Wire(last, "result", step, "a");
                s.InAt("Inputs", i, step, "b");
                last = step;
            }
            s.Out("", last!, "result");
        });

        // Not the same, by value, for every type.
        Same(s =>
        {
            var type = s.Node.Class == "ObjectNotEquals" ? ObjectType(s.Node.Args.Count > 0 ? s.Node.Args[0] : null) : s.Arg(0);
            if (type == null)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            string same = s.Add("IsSame", type);
            s.In("A", same, "a");
            s.In("B", same, "b");
            string not = s.Add("Not", "bool");
            s.Wire(same, "same", not, "value");
            s.Out("", not, "result");
        }, "ValueNotEquals", "ObjectNotEquals");

        // Output i is the value where the index is i, else the default.
        On("ValueDemultiplex", s =>
        {
            if (s.Arg(0) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            int count = s.Node.Refs("ValueOutputs").Count;
            if (s.Node.Stored("ValueOutputs") is Persistence.DataTreeList outputs)
                count = System.Math.Max(count, outputs.Count);
            for (int i = 0; i < count; i++)
            {
                if (!s.IsRead($"ValueOutputs[{i}]"))
                    continue;
                string at = s.Add("IsEqual", "int,int");
                s.In("Index", at, "a");
                s.Set(at, "b", (double)i);
                string pick = s.Add("Conditional", type);
                s.Wire(at, "result", pick, "condition");
                s.In("Value", pick, "whenTrue");
                s.In("DefaultValue", pick, "whenFalse");
                s.Out($"ValueOutputs[{i}]", pick, "result");
            }
        });

        // The first wired entry equal to the match, -1 when none.
        On("IndexOfFirstValueMatch", s =>
        {
            if (s.Arg(0) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            var entries = s.Node.Refs("Values");
            string? chain = null;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i] == null)
                    continue;
                string same = s.Add("IsSame", type);
                s.In("Match", same, "a");
                s.InAt("Values", i, same, "b");
                string pick = s.Add("Conditional", "int");
                s.Wire(same, "same", pick, "condition");
                s.Set(pick, "whenTrue", (double)i);
                if (chain == null)
                    s.Set(pick, "whenFalse", -1d);
                else
                    s.Wire(chain, "result", pick, "whenFalse");
                chain = pick;
            }
            if (chain == null)
            {
                s.OutConstant("Index", -1d);
                s.OutConstant("FoundMatch", false);
                return;
            }
            s.Out("Index", chain, "result");
            string found = s.Add("GreaterOrEqual", "int,int");
            s.Wire(chain, "result", found, "a");
            s.Set(found, "b", 0d);
            s.Out("FoundMatch", found, "result");
        });

        // A constant pattern becomes the text it builds: its pieces and each value in turn, numbers to the
        // decimals a pattern like {0:F3} or {0:.0} asks for.
        On("FormatString", s =>
        {
            var format = s.Feeder("Format") is { Class: "ValueObjectInput" } constant ? constant.Value("Value") as string : null;
            if (format == null)
            {
                s.Fail("formats with a pattern worked out as it runs, which is not carried");
                return;
            }
            var pieces = new List<Action<string, string>>();
            int at = 0;
            foreach (Match match in Regex.Matches(format, @"\{(\d+)(?::([^}]*))?\}"))
            {
                if (match.Index > at)
                {
                    string literal = format[at..match.Index].Replace("{{", "{").Replace("}}", "}");
                    pieces.Add((node, port) => s.Set(node, port, literal));
                }
                at = match.Index + match.Length;
                int index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                string spec = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;
                var feeder = FeederAt(s, "Parameters", index);
                string? type = feeder == null ? null : ValueTypeOf(feeder);
                pieces.Add((node, port) =>
                {
                    if (type == "string" || type == null)
                    {
                        s.InAt("Parameters", index, node, port);
                        return;
                    }
                    int? decimals = Decimals(spec);
                    if (decimals != null && type is "float" or "double")
                    {
                        string number = s.Add("FormatNumber", $"{type},int");
                        s.InAt("Parameters", index, number, "value");
                        s.Set(number, "decimals", (double)decimals.Value);
                        s.Wire(number, "result", node, port);
                        return;
                    }
                    string text = s.Add("ToText", type);
                    s.InAt("Parameters", index, text, "value");
                    s.Wire(text, "result", node, port);
                });
            }
            if (at < format.Length)
            {
                string tail = format[at..].Replace("{{", "{").Replace("}}", "}");
                pieces.Add((node, port) => s.Set(node, port, tail));
            }
            if (pieces.Count == 0)
            {
                s.OutConstant("", string.Empty);
                return;
            }
            if (pieces.Count == 1)
                pieces.Add((node, port) => s.Set(node, port, string.Empty));
            var parts = new string[pieces.Count];
            Array.Fill(parts, "string");
            string joined = s.Add("Concat", string.Join(",", parts));
            for (int i = 0; i < pieces.Count; i++)
                pieces[i](joined, Lettered(i));
            s.Out("", joined, "result");
        });
    }

    // The decimals a numeric format asks for: Fk or Nk, bare F or N for two, or a custom 0.00 or .0.
    private static int? Decimals(string spec)
    {
        if (spec.Length == 0)
            return null;
        if (spec[0] is 'F' or 'f' or 'N' or 'n')
            return spec.Length > 1 && int.TryParse(spec[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) ? k : 2;
        int dot = spec.IndexOf('.');
        return dot >= 0 ? spec.Length - dot - 1 : 0;
    }

    // The package node feeding entry i of a list input, through relays and casts.
    private static PackageNode? FeederAt(PackageNodeScope s, string list, int index)
    {
        var ids = s.Node.Refs(list);
        string? id = index < ids.Count ? ids[index] : null;
        for (int depth = 0; depth < 32 && s.Index.TryNode(id, out var node, out _); depth++)
        {
            if (node.Class is not ("ValueRelay" or "ObjectRelay" or "ValueToObjectCast" or "ObjectCast" or "ContinuouslyChangingValueRelay" or "ContinuouslyChangingObjectRelay"))
                return node;
            id = node.Ref("Input");
        }
        return null;
    }

    // What type of value a package node gives, from its own generic argument, where it says.
    private static string? ValueTypeOf(PackageNode node)
    {
        if (node.Class is "ValueObjectInput" or "ObjectConditional" || node.Class.StartsWith("ToString", StringComparison.Ordinal)
            || node.Class.StartsWith("FormatString", StringComparison.Ordinal) || node.Class.StartsWith("Concatenate", StringComparison.Ordinal))
            return "string";
        return node.Arg(0) ?? (node.Class.EndsWith("_Float", StringComparison.Ordinal) ? "float" : null);
    }
}
