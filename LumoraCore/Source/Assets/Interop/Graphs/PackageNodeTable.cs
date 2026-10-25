// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using Lumora.Script;

namespace Lumora.Core.Assets.Interop;

// How each kind of package node becomes ours, by its class name. Most are a node of ours with the ports
// renamed, written with Map; the rest are handlers of their own, by family, in the partial files beside this
// one. A node with no entry is not converted and stays a placeholder in its graph. -xlinka
internal static partial class PackageNodeTable
{
    private static readonly Dictionary<string, Action<PackageNodeScope>> Handlers = new(StringComparer.Ordinal);

    // The generated maps first, so a handler written for a node replaces its map.
    static PackageNodeTable()
    {
        Maps();
        Values();
        Flow();
        Fields();
        Events();
        Maths();
        World();
        Misc();
        Composites();
        Locomotion();
        Seats();
        WorldA();
        WorldB();
        WorldC();
        WorldD();
        WorldE();
        Web();
        Buttons();
        Text();
        Playback();
        Session();
    }

    public static bool TryGet(string cls, out Action<PackageNodeScope> handler) => Handlers.TryGetValue(cls, out handler!);

    public static bool Has(string cls) => Handlers.ContainsKey(cls);

    private static void On(string cls, Action<PackageNodeScope> handler) => Handlers[cls] = handler;

    private static void Same(Action<PackageNodeScope> handler, params string[] classes)
    {
        foreach (var cls in classes)
            Handlers[cls] = handler;
    }

    // A node of ours standing in for a package node port for port. Signature: null for our default overload, a
    // literal, or a template where {T}, {T0}, {T1} are the package node's generic arguments in our types and
    // {T}* repeats {T} once per entry of the list input. Ports are "Package=ours" pairs, "" for the node itself
    // and "List[]=" for a list input spread over our lettered inputs. Values are what an unwired package input
    // means where ours would differ: numbers, true or false, [lists], or "identity". Set gives our ports that
    // have nothing on the package side a value.
    private static void Map(string cls, string ours, string? signature, string ins = "", string outs = "", string ops = "",
        string exits = "", string values = "", string set = "")
    {
        var fixedValues = Pairs(set);
        var inputs = Pairs(ins);
        var outputs = Pairs(outs);
        var operations = Pairs(ops);
        var calls = Pairs(exits);
        var unwired = Pairs(values);
        On(cls, s =>
        {
            int listCount = 0;
            foreach (var (theirs, _) in inputs)
            {
                if (theirs.EndsWith("[]", StringComparison.Ordinal))
                    listCount = s.Node.Refs(theirs[..^2]).Count;
            }
            string? sig = Signature(s, signature, listCount);
            if (signature != null && sig == null)
            {
                s.Fail($"its type {string.Join(",", s.Node.Args)} has no counterpart here");
                return;
            }
            string node = s.Add(ours, sig);
            foreach (var (port, raw) in fixedValues)
            {
                if (Value(raw, s) is { } fixedValue)
                    s.Set(node, port, fixedValue);
            }
            foreach (var (theirs, port) in inputs)
            {
                if (theirs.EndsWith("[]", StringComparison.Ordinal))
                {
                    s.InList(theirs[..^2], node, Lettered);
                    continue;
                }
                unwired.TryGetValue(theirs, out var raw);
                s.In(theirs, node, port, raw == null ? null : Value(raw, s));
            }
            foreach (var (theirs, port) in outputs)
                s.Out(theirs, node, port);
            foreach (var (theirs, port) in operations)
                s.Op(theirs, node, port);
            foreach (var (theirs, port) in calls)
                s.Exit(theirs, node, port);
        });
    }

    private static string Lettered(int index) => ((char)('a' + index)).ToString();

    private static Dictionary<string, string> Pairs(string text)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            pairs[eq < 0 ? part : part[..eq]] = eq < 0 ? string.Empty : part[(eq + 1)..];
        }
        return pairs;
    }

    private static string? Signature(PackageNodeScope s, string? template, int listCount)
    {
        if (template == null)
            return null;
        int star = template.IndexOf('*');
        if (star > 0)
        {
            int begin = template.LastIndexOf(',', star) + 1;
            string repeated = template[begin..star];
            if (repeated is "{T}" or "{T0}")
                repeated = s.Arg(0) ?? string.Empty;
            if (repeated.Length == 0)
                return null;
            var parts = new string[System.Math.Max(listCount, 2)];
            Array.Fill(parts, repeated);
            template = template[..begin] + string.Join(",", parts) + template[(star + 1)..];
        }
        string[] tokens = { "{T}", "{T0}", "{T1}", "{T2}" };
        int[] args = { 0, 0, 1, 2 };
        for (int i = 0; i < tokens.Length; i++)
        {
            if (!template.Contains(tokens[i], StringComparison.Ordinal))
                continue;
            var t = s.Arg(args[i]);
            if (t == null)
                return null;
            template = template.Replace(tokens[i], t, StringComparison.Ordinal);
        }
        return template;
    }

    // A value written in the table, shaped for the node at hand.
    private static object? Value(string text, PackageNodeScope s)
    {
        if (text == "true" || text == "false")
            return text == "true";
        if (text == "identity")
            return Identity(s.Arg(0));
        if (text.StartsWith('['))
        {
            var parts = text.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries);
            var numbers = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                numbers[i] = double.Parse(parts[i], CultureInfo.InvariantCulture);
            return numbers;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            return number;
        return text;
    }

    // What multiplying by leaves alone, for a type.
    private static object? Identity(string? type) => type switch
    {
        "float2" or "int2" => new[] { 1d, 1d },
        "float3" or "int3" => new[] { 1d, 1d, 1d },
        "float4" or "int4" or "colorHDR" or "color" => new[] { 1d, 1d, 1d, 1d },
        "floatQ" => new[] { 0d, 0d, 0d, 1d },
        null => null,
        _ => 1d,
    };
}
