// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Core.Assets.Interop;

// Text, casts, and the smaller maths and time nodes first seen in the media worlds. Text is compared exactly
// here where the package compared by culture, which reads the same for the addresses, numbers and names
// graphs work through. A length left unwired runs to the end of the text. -xlinka
internal static partial class PackageNodeTable
{
    private const double ToTheEnd = int.MaxValue;

    private static void Text()
    {
        On("IndexOfString", s =>
        {
            string find = s.Add("IndexOf", "string,string,int,bool");
            s.In("Str", find, "text");
            s.In("Part", find, "find");
            s.In("StartIndex", find, "start", 0d);
            s.In("SearchFromEnd", find, "fromEnd", false);
            s.Out("", find, "index");
        });
        Map("Substring", "Substring", "string,int,int", ins: "Str=text StartIndex=start Length=length", outs: "=result",
            values: $"StartIndex=0 Length={ToTheEnd}");
        Map("StringRemove", "TextRemove", null, ins: "Str=text StartIndex=start Length=count", outs: "=result",
            values: $"StartIndex=0 Length={ToTheEnd}");
        Map("ReplaceSubstring", "Replace", null, ins: "Str=text SearchFor=find ReplaceWith=with", outs: "=result");

        // Nothing or nothing in it.
        On("IsStringEmpty", s =>
        {
            string length = s.Add("TextLength");
            s.In("A", length, "text");
            string none = s.Add("IsEqual", "int,int");
            s.Wire(length, "result", none, "a");
            s.Set(none, "b", 0d);
            s.Out("", none, "result");
        });

        // A number style or culture given to the parse is not carried: numbers read with a point for decimals.
        foreach (var (cls, ours, signature, parsed) in new[]
                 {
                     ("Parse_Float", "ParseNumber", (string?)null, "ok"),
                     ("Parse_Float2", "Parse", "float2", "parsed"),
                 })
        {
            On(cls, s =>
            {
                string parse = s.Add(ours, signature);
                s.In("Str", parse, "text");
                s.Out("Value", parse, "value");
                s.Out("IsParsed", parse, parsed);
                if (s.Wired("NumberStyle") || s.Wired("FormatProvider"))
                    s.Problem("its number style and culture are not carried; it reads numbers with a point for decimals");
            });
        }

        On("ToString_object", ObjectToText);

        Map("Cast_double_To_float", "ToFloat", "double", ins: "Input=value", outs: "=result");
        Map("Cast_float_To_double", "ToDouble", "float", ins: "Input=value", outs: "=result");

        Map("LegacyRoundToInt_Float2", "RoundToInt", "float2", ins: "N=value", outs: "=result");
        Map("Unpack_Int2", "SplitVector", "int2", ins: "V=vector", outs: "X=x Y=y");
        Map("Normalized_Float2", "Normalize", "float2", ins: "A=vector", outs: "=result");
        Map("Magnitude_Float2", "VectorLength", "float2", ins: "A=vector", outs: "=length");
        Map("Mul_Float2_Float", "Multiply", "float2,float", ins: "A=a B=b", outs: "=result", values: "A=[1,1] B=1");
        Map("Cos_Float", "Cos", "float", ins: "N=radians", outs: "=result");

        // The value three times over.
        On("ValueCube", s =>
        {
            if (s.Arg(0) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            string cube = s.Add("Multiply", $"{type},{type},{type}");
            foreach (var port in new[] { "a", "b", "c" })
                s.In("N", cube, port);
            s.Out("", cube, "result");
        });

        Map("TimeSpanFromSeconds", "SpanFromSeconds", null, ins: "Value=seconds", outs: "=span");
        Map("TimeSpanTotalHours", "SpanTotalHours", null, ins: "TimeSpan=span", outs: "=hours");
    }

    // Text for whatever the cast in front of it carried: an address as written, a field as its value's text, a
    // value in the form Parse reads back, and text as itself. A format given is not carried.
    private static void ObjectToText(PackageNodeScope s)
    {
        string? carried = null;
        var feeder = s.Feeder("Object");
        if (feeder != null && feeder.Class is "ObjectCast" or "ValueToObjectCast" && feeder.Args.Count > 0)
            carried = feeder.Args[0];
        if (s.Wired("Format") || s.Wired("FormatProvider"))
            s.Problem("its format is not carried; the value is written in its plain form");
        if (carried == null)
        {
            s.Fail("turns something into text whose type the graph does not say");
            return;
        }
        string? ours = feeder!.Class == "ValueToObjectCast" ? PackageNode.OurType(carried) : ObjectType(carried);
        string? field = null;
        if (ours != null && ours.StartsWith("field<", StringComparison.Ordinal))
        {
            field = ours;
            ours = ours[6..^1];
        }
        if (ours == null)
        {
            s.Fail($"turns a {carried} into text, which has no text form here");
            return;
        }
        if (ours == "string" && field == null)
        {
            s.OutThrough("", "Object");
            return;
        }
        string node;
        string port;
        if (ours == "uri")
            (node, port) = (s.Add("UrlToText"), "url");
        else if (ours == "string")
            (node, port) = (s.Add("Concat", "string,string"), "a");
        else if (Lumora.Script.ScriptTypes.TryGet(ours, out var type) && !type.IsHandle)
            (node, port) = (s.Add("ToText", ours), "value");
        else
        {
            s.Fail($"turns a {carried} into text, which has no text form here");
            return;
        }
        if (field != null)
        {
            string read = s.Add("GetField", ours);
            s.In("Object", read, "field");
            s.Wire(read, "result", node, port);
        }
        else
        {
            s.In("Object", node, port);
        }
        s.Out("", node, ours == "uri" ? "text" : "result");
    }
}
