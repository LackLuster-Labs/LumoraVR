// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// "Pressed {0} times" with the value in place of {0}. {{ and }} are literal braces.
[Node("Strings/Format", Description = "\"Pressed {0} times\" with the value in place of {0}. {{ and }} are literal braces.",
    Keywords = "template, placeholder, string format, interpolate")]
public sealed class Format : ValueNode
{
    public static string Run(string format, int arg0) => TextFormat.Fill(format, arg0);
    public static string Run(string format, long arg0) => TextFormat.Fill(format, arg0);
    public static string Run(string format, float arg0) => TextFormat.Fill(format, arg0);
    public static string Run(string format, double arg0) => TextFormat.Fill(format, arg0);
    public static string Run(string format, bool arg0) => TextFormat.Fill(format, arg0);
    public static string Run(string format, string? arg0) => TextFormat.Fill(format, arg0);
    public static string Run(string format, float3 arg0) => TextFormat.Fill(format, arg0);
    public static string Run(string format, float2 arg0) => TextFormat.Fill(format, arg0);
    public static string Run(string format, float4 arg0) => TextFormat.Fill(format, arg0);
    public static string Run(string format, color arg0) => TextFormat.Fill(format, arg0);
}
