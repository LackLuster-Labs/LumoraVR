// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;

namespace Lumora.Core.Assets.Interop;

// Package nodes first seen in worlds: maths, comparisons, vectors and colours.
//
// A package colour is four numbers and the profile they are in; ours is the colour itself, held as sRGB. So a
// node that converts between profiles changes the numbers and not the colour, and ours passes the colour on as
// it is, while a node that sets the profile reads the same numbers another way, which changes the colour, and
// ours takes the curve between the two. Nodes that work on the numbers (channels, HSV, scaling) see linear
// numbers where the graph shows the colour arriving in the linear profile, a colour read from a hex code being
// one, and hand the result back as sRGB. Where the graph does not show it the colour is taken as sRGB, what a
// colour is unless something set it otherwise. sRGB with alpha differs from sRGB in its alpha alone, which ours
// leaves linear. The two curves agree up to 1; past 1 the package's runs straight and ours carries on. -xlinka
internal static partial class PackageNodeTable
{
    private static void WorldE()
    {
        On("SqrMagnitude_Float3", s =>
        {
            string dot = s.Add("Dot", "float3,float3");
            s.In("A", dot, "a");
            s.In("A", dot, "b");
            s.Out("", dot, "result");
        });

        // Bounds the wrong way round hold nothing in the package, where ours turns them round: constant bounds
        // in order go to ours, anything else to the two comparisons the package makes.
        On("IsBetween_Float", s =>
        {
            if (RawColor.Number(s, "Min") is { } min && RawColor.Number(s, "Max") is { } max && min <= max)
            {
                string between = s.Add("IsBetween", "float,float,float");
                s.In("Value", between, "value");
                s.In("Min", between, "min");
                s.In("Max", between, "max");
                s.Out("", between, "result");
                return;
            }
            string above = s.Add("GreaterOrEqual", "float,float");
            s.In("Value", above, "a");
            s.In("Min", above, "b");
            string below = s.Add("LessOrEqual", "float,float");
            s.In("Value", below, "a");
            s.In("Max", below, "b");
            string both = s.Add("And", "bool,bool");
            s.Wire(above, "result", both, "a");
            s.Wire(below, "result", both, "b");
            s.Out("", both, "result");
        });

        // One operand is itself, none is the type's zero.
        On("ValueMinMulti", s =>
        {
            if (s.Arg(0) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            int count = s.Node.Refs("Operands").Count;
            if (count == 0)
            {
                s.OutConstant("", RawColor.Zero(type));
                return;
            }
            var parts = new string[System.Math.Max(count, 2)];
            Array.Fill(parts, type);
            string min = s.Add("Min", string.Join(",", parts));
            if (count == 1)
            {
                s.InAt("Operands", 0, min, "a");
                s.InAt("Operands", 0, min, "b");
            }
            else
            {
                s.InList("Operands", min, Lettered);
            }
            s.Out("", min, "result");
        });

        // True when an even number of operands are true; with none, true.
        On("XNOR_Multi_Bool", s =>
        {
            int count = s.Node.Refs("Operands").Count;
            if (count == 0)
            {
                s.OutConstant("", true);
                return;
            }
            var parts = new string[System.Math.Max(count, 2)];
            Array.Fill(parts, "bool");
            string odd = s.Add("Xor", string.Join(",", parts));
            s.InList("Operands", odd, Lettered);
            string not = s.Add("Not", "bool");
            s.Wire(odd, "result", not, "value");
            s.Out("", not, "result");
        });

        On("ValueInc", s =>
        {
            if (s.Arg(0) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            string add = s.Add("Add", $"{type},{type}");
            s.In("N", add, "a");
            if (Identity(type) is { } one)
                s.Set(add, "b", one);
            s.Out("", add, "result");
        });

        // Ours keeps alpha 0 to 1 and red, green, blue and brightness at 0 and up, where the package takes any
        // number; the same for the colours and amounts worlds use. Alpha reads the same in either profile.
        RawColor.Channel("ColorXSetRed", "SetColorChannel", "Red");
        RawColor.Channel("ColorXSetGreen", "SetColorChannel", "Green");
        RawColor.Channel("ColorXSetBlue", "SetColorChannel", "Blue");
        RawColor.Channel("ColorXSetAlpha", "SetColorChannel", "Alpha");
        RawColor.Channel("ColorXMulRGB", "MultiplyColorChannel", "RGB");
        RawColor.Channel("ColorXMulValue", "MultiplyColorChannel", "Brightness");
        RawColor.Channel("ColorXSetValue", "SetColorChannel", "Brightness");

        // HSV back to RGB is straight in saturation, so scaling it is a lerp from the gray of the colour's
        // brightness to the colour, by the amount, past 1 and below 0 alike, as the package does with no limit.
        On("ColorXMulSaturation", s =>
        {
            string profile = RawColor.Profile(s, s.Node, "Color");
            string hsv = s.Add("ColorToHsv", "colorHDR");
            string gray = s.Add("MakeHdrColor", "float,float,float,float");
            s.Wire(hsv, "brightness", gray, "r");
            s.Wire(hsv, "brightness", gray, "g");
            s.Wire(hsv, "brightness", gray, "b");
            s.Wire(hsv, "alpha", gray, "a");
            string blend = s.Add("Lerp", "colorHDR,colorHDR,float");
            s.Wire(gray, "color", blend, "a");
            RawColor.Into(s, "Color", profile, null, (hsv, "value"), (blend, "b"));
            s.In("Value", blend, "t");
            RawColor.Out(s, "", profile, blend, "result");
        });

        Same(s =>
        {
            string profile = RawColor.Profile(s, s.Node, "A");
            string op = s.Add(s.Node.Class == "Mul_ColorX_Float" ? "Multiply" : "Divide", "colorHDR,float");
            RawColor.Into(s, "A", profile, new[] { 1d, 1d, 1d, 1d }, (op, "a"));
            s.In("B", op, "b", 1d);
            RawColor.Out(s, "", profile, op, "result");
        }, "Mul_ColorX_Float", "Div_ColorX_Float");

        On("ColorXToHSV", s =>
        {
            string profile = RawColor.Profile(s, s.Node, "C");
            string hsv = s.Add("ColorToHsv", "colorHDR");
            RawColor.Into(s, "C", profile, null, (hsv, "value"));
            s.Out("H", hsv, "hue");
            s.Out("S", hsv, "saturation");
            s.Out("V", hsv, "brightness");
        });

        // The profile comes out as its name, for a port of that type; ours has none.
        On("Unpack_ColorX", s =>
        {
            string profile = RawColor.Profile(s, s.Node, "V");
            string split = s.Add("SplitHdrColor");
            RawColor.Into(s, "V", profile, null, (split, "value"));
            s.Out("R", split, "r");
            s.Out("G", split, "g");
            s.Out("B", split, "b");
            s.Out("A", split, "a");
            s.OutConstant("Profile", profile);
        });

        // Ours keeps saturation 0 to 1 and brightness at 0 and up; the package carries on past both.
        On("HSV_ToColorX", s =>
        {
            string profile = RawColor.Setting(s, s.Node, "TargetProfile", "sRGB");
            string make = s.Add("FromHsvHdr");
            s.In("H", make, "hue");
            s.In("S", make, "saturation");
            s.In("V", make, "brightness");
            RawColor.Out(s, "", profile, make, "color");
        });

        // The package reads a code into the linear profile, so the code's numbers are a linear colour. On a bad
        // digit it keeps what it read, where ours gives clear black.
        On("ColorXFromHexCode", s =>
        {
            string parse = s.Add("ColorFromHex");
            s.In("HexCode", parse, "hex");
            s.Out("Parsed", parse, "parsed");
            string shown = s.Add("ColorToSrgb", "color");
            s.Wire(parse, "color", shown, "value");
            s.Out("Color", shown, "color");
        });

        On("ConvertColorProfile", s => s.OutThrough("", "Color"));

        On("ColorXSetProfile", s =>
        {
            bool fromLinear = RawColor.Profile(s, s.Node, "Color") == "Linear";
            bool toLinear = RawColor.Setting(s, s.Node, "Profile", "Linear") == "Linear";
            if (fromLinear == toLinear)
            {
                s.OutThrough("", "Color");
                return;
            }
            string curve = s.Add(toLinear ? "ColorToSrgb" : "ColorToLinear", "colorHDR");
            s.In("Color", curve, "value");
            s.Out("", curve, "color");
        });
    }

    private static class RawColor
    {
        // A channel or HSV part of a colour, changed on the package's numbers.
        public static void Channel(string cls, string node, string channel)
        {
            On(cls, s =>
            {
                string profile = channel == "Alpha" ? "sRGB" : Profile(s, s.Node, "Color");
                string edit = s.Add(node, "colorHDR,colorchannel,float");
                s.Set(edit, "channel", channel);
                Into(s, "Color", profile, null, (edit, "value"));
                s.In("Value", edit, "amount");
                Out(s, "", profile, edit, "color");
            });
        }

        // The profile a colour input's numbers are in, as far as the graph shows: Linear, sRGB or sRGBAlpha.
        public static string Profile(PackageNodeScope s, PackageNode node, string input, int depth = 0)
        {
            var feeder = Feeder(s.Index, node, input);
            if (feeder == null || depth > 32)
                return "sRGB";
            return feeder.Class switch
            {
                "ValueInput" or "GlobalValue" => feeder.Stored("Value") is Lumora.Core.Persistence.DataTreeList stored && PackageGraphWriter.IsLinearProfile(stored) ? "Linear" : "sRGB",
                "ColorXFromHexCode" => "Linear",
                "ConvertColorProfile" => Setting(s, feeder, "TargetProfile", "Linear"),
                "ColorXSetProfile" => Setting(s, feeder, "Profile", "Linear"),
                "HSV_ToColorX" => Setting(s, feeder, "TargetProfile", "sRGB"),
                "Mul_ColorX_Float" or "Div_ColorX_Float" => Profile(s, feeder, "A", depth + 1),
                "ColorXSetRed" or "ColorXSetGreen" or "ColorXSetBlue" or "ColorXSetAlpha" or "ColorXMulRGB" or "ColorXMulValue"
                    or "ColorXSetValue" or "ColorXMulSaturation" or "ColorXMulAlpha" => Profile(s, feeder, "Color", depth + 1),
                _ => "sRGB",
            };
        }

        // A profile input: a constant, its default when nothing reaches it, and sRGB when it is worked out as it
        // runs, there being no profile type here to carry it.
        public static string Setting(PackageNodeScope s, PackageNode node, string input, string unwired)
        {
            if (node.Ref(input) == null || Feeder(s.Index, node, input) is not { } feeder)
                return unwired;
            if (feeder.Class is "ValueInput" or "GlobalValue" && Name(feeder.Value("Value")) is { } name)
                return name;
            return "sRGB";
        }

        private static string? Name(object? stored) => stored switch
        {
            string text => text is "Linear" or "sRGB" or "sRGBAlpha" ? text : null,
            IConvertible number => Convert.ToInt32(number, CultureInfo.InvariantCulture) switch
            {
                0 => "Linear",
                1 => "sRGB",
                2 => "sRGBAlpha",
                _ => null,
            },
            _ => null,
        };

        private static PackageNode? Feeder(PackageNodeIndex index, PackageNode node, string input)
        {
            string? id = node.Ref(input);
            for (int depth = 0; depth < 32 && id != null; depth++)
            {
                if (!index.TryNode(id, out var feeder, out _))
                    return null;
                if (feeder.Class is not ("ValueRelay" or "ObjectRelay" or "ContinuouslyChangingValueRelay" or "ContinuouslyChangingObjectRelay"))
                    return feeder;
                id = feeder.Ref("Input");
            }
            return null;
        }

        // The colour input into our ports as the package's numbers.
        public static void Into(PackageNodeScope s, string input, string profile, object? unwired,
            params (string Node, string Port)[] ports)
        {
            if (profile != "Linear")
            {
                foreach (var (node, port) in ports)
                    s.In(input, node, port, unwired);
                return;
            }
            string linear = s.Add("ColorToLinear", "colorHDR");
            s.In(input, linear, "value", unwired);
            foreach (var (node, port) in ports)
                s.Wire(linear, "color", node, port);
        }

        // Numbers in the profile back out as our colour.
        public static void Out(PackageNodeScope s, string output, string profile, string node, string port)
        {
            if (profile != "Linear")
            {
                s.Out(output, node, port);
                return;
            }
            string srgb = s.Add("ColorToSrgb", "colorHDR");
            s.Wire(node, port, srgb, "value");
            s.Out(output, srgb, "color");
        }

        // A constant number input, 0 when unwired; null when it is worked out.
        public static double? Number(PackageNodeScope s, string input)
        {
            if (!s.Wired(input))
                return 0d;
            if (s.Feeder(input) is not { Class: "ValueInput" or "GlobalValue" } constant)
                return null;
            return constant.Value("Value") is IConvertible n and not string and not bool ? Convert.ToDouble(n, CultureInfo.InvariantCulture) : null;
        }

        public static object Zero(string type) => type switch
        {
            "float2" or "int2" => new[] { 0d, 0d },
            "float3" or "int3" => new[] { 0d, 0d, 0d },
            "float4" or "int4" or "colorHDR" or "color" or "floatQ" => new[] { 0d, 0d, 0d, 0d },
            _ => 0d,
        };
    }
}
