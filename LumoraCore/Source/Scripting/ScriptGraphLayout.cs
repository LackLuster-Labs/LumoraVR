// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// A graph document laid out flat for reading: x to the right, y up, z = 0, each node's position the top-left
// of its card, every card sized as the editor builds it and none over another.
//
// Each connected part is laid out on its own. Columns: the longest path in from the starts, then a node that
// only hands values on, or one nothing leads into, moves right to stand just left of the first thing it
// feeds, so flow runs left to right and a value is made left of where it is read; a node with room either
// way then leaves a crowded column for a short one. A wire running back against all that is left out of the
// columns. Order in a column: barycentre sweeps with a long wire held in every column it crosses, keeping the
// order with the fewest crossings. Heights: column by column, each card goes where its wires come in level,
// as near as the order and the gap allow, swept both ways a few times; placing cards only from what is left
// of them drifts the rows down into a staircase. A part far wider than tall wraps into bands, and one far
// taller than a person reaches folds into strips side by side. The parts are
// then shelved into one block, biggest first, centred on x = 0 and standing on y = 0: unpacking puts a graph
// just above its object, and a script on an avatar's root sits at the floor. -xlinka
public static class ScriptGraphLayout
{
    // Parts sit further apart than the columns and cards inside one, so each reads as its own.
    private const float PartGapX = ShaderGraphBuilder.ColumnGap * 2f;
    private const float PartGapY = ShaderGraphBuilder.RowGap * 2f;
    // How much wider than tall the shelves of parts run, and how wide a part runs before it wraps: about ten
    // cards, what a reader takes in without walking.
    private const float BlockAspect = 2f;
    private const float WrapWidth = 3f;
    // Flow is the spine a reader follows, so it pulls hardest toward level; a write's store pulls least.
    private const float FlowWeight = 2f;
    private const float VariableWeight = 0.5f;
    // How many columns a value node may leave its readers by to ease a crowded column.
    private const int BalanceReach = 2;
    private const int OrderRounds = 8;
    private const int PlaceRounds = 12;
    private const float Settled = 0.0005f;

    private readonly record struct Edge(int From, int To, float FromY, float ToY, float Weight, bool Flow);

    private sealed class Part
    {
        public readonly List<int> Nodes = new();
        public readonly List<Edge> Edges = new();
        public float Width;
        public float Height;
        public float X;
        public float Top;
    }

    // Lays the graph out in place and returns the block's size in metres. Problem lines are how many lines of
    // problems a node's card will show, by node id; a node this build cannot read is given room for them.
    public static float2 Arrange(ScriptGraph graph, GraphLanguage language, IReadOnlyDictionary<string, int>? problemLines = null)
    {
        int count = graph.Nodes.Count;
        if (count == 0)
            return float2.Zero;

        var registry = language.Registry;
        bool previews = language.Kind == GraphKind.Shader;
        var ids = new Dictionary<string, int>(count, StringComparer.Ordinal);
        var cards = new ScriptCardLayout[count];
        var overloads = new NodeOverload?[count];
        var width = new float[count];
        var height = new float[count];
        for (int i = 0; i < count; i++)
        {
            var node = graph.Nodes[i];
            ids.TryAdd(node.Id, i);
            var overload = Resolve(registry, node);
            int lines = problemLines != null && problemLines.TryGetValue(node.Id, out var told) ? told : 0;
            if (overload == null)
                lines = ScriptEditorStyle.MaxProblemLines;
            overloads[i] = overload;
            cards[i] = ScriptCardLayout.For(overload, previews, lines);
            width[i] = cards[i].Width * ScriptEditorStyle.CardScale;
            height[i] = cards[i].Height * ScriptEditorStyle.CardScale;
        }

        var edges = new List<Edge>(graph.Flow.Count + graph.Data.Count);
        foreach (var wire in graph.Flow)
            AddWire(wire, flow: true);
        foreach (var wire in graph.Data)
            AddWire(wire, flow: false);
        // A write names its store in a setting rather than by a wire; the store still reads best beside it.
        for (int i = 0; i < count; i++)
        {
            if (overloads[i]?.Definition.Kind != NodeKind.WriteVariable || !graph.Nodes[i].Values.TryGetValue("var", out var named)
                || named.Kind != GraphValueKind.String || named.String is not { } storeId || !ids.TryGetValue(storeId, out int store) || store == i)
                continue;
            int setting = cards[i].Settings.FindIndex(s => s.Name == "var");
            float toY = setting >= 0 ? RowAnchor(cards[i].SettingTop(setting)) : HeaderAnchor;
            int output = cards[store].Right.FindIndex(p => p.Kind == ScriptCardHandleKind.DataOutput);
            float fromY = output >= 0 ? RowAnchor(cards[store].RowTop(output)) : HeaderAnchor;
            edges.Add(new Edge(store, i, fromY, toY, VariableWeight, false));
        }

        var parts = Split(count, edges);
        var x = new float[count];
        var top = new float[count];
        var local = new int[count];
        foreach (var part in parts)
            LayOut(part, width, height, x, top, local);
        var size = Shelve(parts);

        // Centred across, standing on the origin.
        float shiftX = -size.x * 0.5f;
        float shiftY = size.y;
        foreach (var part in parts)
        {
            foreach (int v in part.Nodes)
                graph.Nodes[v].Position = new float3(part.X + x[v] + shiftX, part.Top + top[v] + shiftY, 0f);
        }
        return size;

        void AddWire(GraphWire wire, bool flow)
        {
            if (!ids.TryGetValue(wire.FromNode, out int from) || !ids.TryGetValue(wire.ToNode, out int to) || from == to)
                return;
            edges.Add(new Edge(from, to,
                Anchor(cards[from], flow ? ScriptCardHandleKind.FlowOutput : ScriptCardHandleKind.DataOutput, wire.FromPort),
                Anchor(cards[to], flow ? ScriptCardHandleKind.FlowInput : ScriptCardHandleKind.DataInput, wire.ToPort),
                flow ? FlowWeight : 1f, flow));
        }
    }

    // The box the graph's cards fill where they stand now, in the graph space's metres, for any graph laid out
    // or not. A card is measured shut, with no problem lines but the ones a node this build cannot read shows.
    public static ShaderGraphBuilder.Bounds Extent(ScriptGraph graph, GraphLanguage language)
    {
        if (graph.Nodes.Count == 0)
            return default;
        bool previews = language.Kind == GraphKind.Shader;
        float left = float.MaxValue, right = float.MinValue, top = float.MinValue, bottom = float.MaxValue;
        foreach (var node in graph.Nodes)
        {
            var overload = Resolve(language.Registry, node);
            var card = ScriptCardLayout.For(overload, previews, overload == null ? ScriptEditorStyle.MaxProblemLines : 0);
            var at = node.Position;
            left = MathF.Min(left, at.x);
            right = MathF.Max(right, at.x + card.Width * ScriptEditorStyle.CardScale);
            top = MathF.Max(top, at.y);
            bottom = MathF.Min(bottom, at.y - card.Height * ScriptEditorStyle.CardScale);
        }
        return new ShaderGraphBuilder.Bounds(left, right, top, bottom);
    }

    // Where the cards gather: the mean of their middles, in the graph's own metres.
    public static float2 Focus(ScriptGraph graph, GraphLanguage language)
    {
        if (graph.Nodes.Count == 0)
            return float2.Zero;
        bool previews = language.Kind == GraphKind.Shader;
        float x = 0f, y = 0f;
        foreach (var node in graph.Nodes)
        {
            var overload = Resolve(language.Registry, node);
            var card = ScriptCardLayout.For(overload, previews, overload == null ? ScriptEditorStyle.MaxProblemLines : 0);
            x += node.Position.x + card.Width * ScriptEditorStyle.CardScale * 0.5f;
            y += node.Position.y - card.Height * ScriptEditorStyle.CardScale * 0.5f;
        }
        return new float2(x / graph.Nodes.Count, y / graph.Nodes.Count);
    }

    // Laid out in a way nobody could work in: taller than a person reaches, any card over another, or cards
    // strewn so thin the block is mostly empty. What a converted graph can come in as, and a document written
    // without positions, every node at the origin; a graph someone placed by hand is left as they placed it,
    // so this is only asked of a graph nobody has opened yet.
    public static bool Untidy(ScriptGraph graph, GraphLanguage language)
    {
        if (graph.Nodes.Count < 2)
            return false;
        var block = Extent(graph, language);
        if (block.Height > TallestBlock || block.Width > WidestBlock || Overlapping(graph, language, 0.1f))
            return true;
        bool previews = language.Kind == GraphKind.Shader;
        float cards = 0f;
        foreach (var node in graph.Nodes)
        {
            var overload = Resolve(language.Registry, node);
            var card = ScriptCardLayout.For(overload, previews, overload == null ? ScriptEditorStyle.MaxProblemLines : 0);
            cards += card.Width * card.Height * ScriptEditorStyle.CardScale * ScriptEditorStyle.CardScale;
        }
        return cards < block.Width * block.Height * SparsestShare;
    }

    // Metres: a block a person standing can reach the top of, and walk the width of without losing it.
    private const float TallestBlock = 2.5f;
    private const float WidestBlock = 12f;
    // How little of its block a graph's cards may cover.
    private const float SparsestShare = 0.04f;

    // Two cards in one plane, one covering more than a share of the smaller.
    private static bool Overlapping(ScriptGraph graph, GraphLanguage language, float share)
    {
        int count = graph.Nodes.Count;
        if (count < 2)
            return false;
        bool previews = language.Kind == GraphKind.Shader;
        var cards = new (float Left, float Right, float Top, float Bottom, float Depth)[count];
        for (int i = 0; i < count; i++)
        {
            var node = graph.Nodes[i];
            var overload = Resolve(language.Registry, node);
            var card = ScriptCardLayout.For(overload, previews, overload == null ? ScriptEditorStyle.MaxProblemLines : 0);
            var at = node.Position;
            cards[i] = (at.x, at.x + card.Width * ScriptEditorStyle.CardScale, at.y, at.y - card.Height * ScriptEditorStyle.CardScale, at.z);
        }
        Array.Sort(cards, static (a, b) => a.Left.CompareTo(b.Left));
        for (int i = 0; i < count; i++)
        {
            var a = cards[i];
            for (int j = i + 1; j < count && cards[j].Left < a.Right; j++)
            {
                var b = cards[j];
                if (MathF.Abs(a.Depth - b.Depth) > SamePlane)
                    continue;
                float wide = MathF.Min(a.Right, b.Right) - b.Left;
                float tall = MathF.Min(a.Top, b.Top) - MathF.Max(a.Bottom, b.Bottom);
                if (wide <= 0f || tall <= 0f)
                    continue;
                float smaller = MathF.Min((a.Right - a.Left) * (a.Top - a.Bottom), (b.Right - b.Left) * (b.Top - b.Bottom));
                if (wide * tall > smaller * share)
                    return true;
            }
        }
        return false;
    }

    // Metres of depth within which two cards count as one plane.
    private const float SamePlane = 0.05f;

    private static NodeOverload? Resolve(NodeRegistry registry, GraphNode node)
    {
        if (!registry.TryGetDefinition(node.Type, out var definition) || definition.Version != node.Version)
            return null;
        string? signature = string.IsNullOrEmpty(node.Signature) ? null : node.Signature;
        return registry.TryResolve(definition, signature, out var overload, out _) ? overload : null;
    }

    private static float HeaderAnchor => -ScriptEditorStyle.HeaderHeight * 0.5f * ScriptEditorStyle.CardScale;

    private static float RowAnchor(float rowTop) => -(rowTop + ScriptEditorStyle.RowHeight * 0.5f) * ScriptEditorStyle.CardScale;

    // Where a wire meets a card, below the card's top in metres; a port the card does not show meets its header.
    private static float Anchor(ScriptCardLayout card, ScriptCardHandleKind kind, string port)
        => card.TryAnchor(kind, port, out var point) ? point.y * ScriptEditorStyle.CardScale : HeaderAnchor;

    // Connected parts, in the order of their first node.
    private static List<Part> Split(int count, List<Edge> edges)
    {
        var root = new int[count];
        for (int i = 0; i < count; i++)
            root[i] = i;
        int Find(int v)
        {
            while (root[v] != v)
            {
                root[v] = root[root[v]];
                v = root[v];
            }
            return v;
        }
        foreach (var edge in edges)
        {
            int a = Find(edge.From), b = Find(edge.To);
            if (a != b)
                root[System.Math.Max(a, b)] = System.Math.Min(a, b);
        }

        var parts = new List<Part>();
        var partOf = new int[count];
        var partOfRoot = new int[count];
        Array.Fill(partOfRoot, -1);
        for (int i = 0; i < count; i++)
        {
            int r = Find(i);
            if (partOfRoot[r] < 0)
            {
                partOfRoot[r] = parts.Count;
                parts.Add(new Part());
            }
            partOf[i] = partOfRoot[r];
            parts[partOf[i]].Nodes.Add(i);
        }
        foreach (var edge in edges)
            parts[partOf[edge.From]].Edges.Add(edge);
        return parts;
    }

    private static void LayOut(Part part, float[] width, float[] height, float[] x, float[] top, int[] local)
    {
        int m = part.Nodes.Count;
        if (m == 1)
        {
            int only = part.Nodes[0];
            x[only] = 0f;
            top[only] = 0f;
            part.Width = width[only];
            part.Height = height[only];
            return;
        }
        for (int i = 0; i < m; i++)
            local[part.Nodes[i]] = i;
        var nodes = part.Nodes;
        int k = part.Edges.Count;
        var from = new int[k];
        var to = new int[k];
        var outs = new List<int>[m];
        var ins = new List<int>[m];
        for (int i = 0; i < m; i++)
        {
            outs[i] = new List<int>();
            ins[i] = new List<int>();
        }
        for (int e = 0; e < k; e++)
        {
            from[e] = local[part.Edges[e].From];
            to[e] = local[part.Edges[e].To];
            outs[from[e]].Add(e);
            ins[to[e]].Add(e);
        }

        // Wires that close a loop, found depth first from the starts, flow first so chains stay whole.
        var back = new bool[k];
        var state = new byte[m];
        var pre = new int[m];
        int visited = 0;
        var stack = new Stack<(int Node, int Next)>();
        for (int pass = 0; pass < 3; pass++)
        {
            for (int v = 0; v < m; v++)
            {
                if (state[v] != 0)
                    continue;
                bool start = ins[v].Count == 0;
                bool leadsFlow = outs[v].Exists(e => part.Edges[e].Flow);
                if ((pass == 0 && !(start && leadsFlow)) || (pass == 1 && !start))
                    continue;
                state[v] = 1;
                pre[v] = visited++;
                stack.Push((v, 0));
                while (stack.Count > 0)
                {
                    var (at, next) = stack.Pop();
                    if (next >= outs[at].Count)
                    {
                        state[at] = 2;
                        continue;
                    }
                    stack.Push((at, next + 1));
                    int e = outs[at][next];
                    int w = to[e];
                    if (state[w] == 1)
                        back[e] = true;
                    else if (state[w] == 0)
                    {
                        state[w] = 1;
                        pre[w] = visited++;
                        stack.Push((w, 0));
                    }
                }
            }
        }

        // Columns: longest path in, then values and starts moved up to what they feed.
        var waiting = new int[m];
        for (int e = 0; e < k; e++)
        {
            if (!back[e])
                waiting[to[e]]++;
        }
        var order = new List<int>(m);
        var ready = new Queue<int>();
        for (int v = 0; v < m; v++)
        {
            if (waiting[v] == 0)
                ready.Enqueue(v);
        }
        var layer = new int[m];
        while (ready.Count > 0)
        {
            int v = ready.Dequeue();
            order.Add(v);
            foreach (int e in outs[v])
            {
                if (back[e])
                    continue;
                layer[to[e]] = System.Math.Max(layer[to[e]], layer[v] + 1);
                if (--waiting[to[e]] == 0)
                    ready.Enqueue(to[e]);
            }
        }
        for (int i = order.Count - 1; i >= 0; i--)
        {
            int v = order[i];
            bool fed = false, flows = false;
            foreach (int e in ins[v])
            {
                if (back[e])
                    continue;
                fed = true;
                flows |= part.Edges[e].Flow;
            }
            int nearest = int.MaxValue;
            foreach (int e in outs[v])
            {
                if (back[e])
                    continue;
                flows |= part.Edges[e].Flow;
                nearest = System.Math.Min(nearest, layer[to[e]]);
            }
            if (nearest != int.MaxValue && (!fed || !flows))
                layer[v] = nearest - 1;
        }
        int columns = Compact(layer);

        // The tallest column is what a reader has to look up and down. A value node with room to move goes to
        // the shortest column near it between its inputs and its readers, when that one ends no taller than what
        // it left. Flow stays where it is: an event far from what it starts is a wire across the whole graph.
        var stacked = new float[columns];
        for (int v = 0; v < m; v++)
            stacked[layer[v]] += height[nodes[v]] + ShaderGraphBuilder.RowGap;
        for (int round = 0; round < 2; round++)
        {
            for (int i = order.Count - 1; i >= 0; i--)
            {
                int v = order[i];
                int lo = 0, hi = int.MaxValue;
                bool flows = false;
                foreach (int e in ins[v])
                {
                    if (back[e])
                        continue;
                    lo = System.Math.Max(lo, layer[from[e]] + 1);
                    flows |= part.Edges[e].Flow;
                }
                foreach (int e in outs[v])
                {
                    if (back[e])
                        continue;
                    hi = System.Math.Min(hi, layer[to[e]] - 1);
                    flows |= part.Edges[e].Flow;
                }
                // A node nothing reads stays beside its inputs.
                if (flows || hi == int.MaxValue)
                    continue;
                int at = layer[v], target = at;
                lo = System.Math.Max(lo, at - BalanceReach);
                hi = System.Math.Min(hi, at + BalanceReach);
                float own = height[nodes[v]] + ShaderGraphBuilder.RowGap;
                for (int c = hi; c >= lo; c--)
                {
                    if (c != at && stacked[c] + own <= stacked[at] - own && (target == at || stacked[c] < stacked[target]))
                        target = c;
                }
                if (target == at)
                    continue;
                stacked[at] -= own;
                stacked[target] += own;
                layer[v] = target;
            }
        }

        // Long wires become chains of stand-ins, one per column they cross, so they take part in the order.
        var orderLayer = new List<int>(m);
        var key = new List<double>(m);
        for (int v = 0; v < m; v++)
        {
            orderLayer.Add(layer[v]);
            key.Add(pre[v]);
        }
        var segFrom = new List<int>();
        var segTo = new List<int>();
        var segFracFrom = new List<float>();
        var segFracTo = new List<float>();
        for (int e = 0; e < k; e++)
        {
            if (back[e] || layer[to[e]] <= layer[from[e]])
                continue;
            var edge = part.Edges[e];
            int previous = from[e];
            float previousFrac = Fraction(edge.FromY, height[edge.From]);
            for (int column = layer[from[e]] + 1; column < layer[to[e]]; column++)
            {
                int standIn = orderLayer.Count;
                orderLayer.Add(column);
                key.Add(pre[from[e]] + 0.5);
                AddSegment(previous, standIn, previousFrac, 0.5f);
                previous = standIn;
                previousFrac = 0.5f;
            }
            AddSegment(previous, to[e], previousFrac, Fraction(edge.ToY, height[edge.To]));
        }
        int total = orderLayer.Count;
        var ups = new List<int>[total];
        var downs = new List<int>[total];
        for (int i = 0; i < total; i++)
        {
            ups[i] = new List<int>();
            downs[i] = new List<int>();
        }
        for (int s = 0; s < segFrom.Count; s++)
        {
            downs[segFrom[s]].Add(s);
            ups[segTo[s]].Add(s);
        }
        var rows = new List<int>[columns];
        for (int c = 0; c < columns; c++)
            rows[c] = new List<int>();
        for (int i = 0; i < total; i++)
            rows[orderLayer[i]].Add(i);
        var pos = new int[total];
        var sortKey = new double[total];
        foreach (var row in rows)
        {
            row.Sort((a, b) => key[a] != key[b] ? key[a].CompareTo(key[b]) : a.CompareTo(b));
            for (int i = 0; i < row.Count; i++)
                pos[row[i]] = i;
        }

        var best = new List<int>[columns];
        for (int c = 0; c < columns; c++)
            best[c] = new List<int>(rows[c]);
        long fewest = Crossings();
        for (int round = 0; round < OrderRounds && fewest > 0; round++)
        {
            for (int c = 1; c < columns; c++)
                Reorder(rows[c], fromLeft: true);
            for (int c = columns - 2; c >= 0; c--)
                Reorder(rows[c], fromLeft: false);
            long crossings = Crossings();
            if (crossings < fewest)
            {
                fewest = crossings;
                for (int c = 0; c < columns; c++)
                {
                    best[c].Clear();
                    best[c].AddRange(rows[c]);
                }
            }
        }

        // Heights, column by column, each card toward where its wires come in level.
        var placed = new List<int>[columns];
        var columnWidth = new float[columns];
        for (int c = 0; c < columns; c++)
        {
            placed[c] = new List<int>();
            foreach (int i in best[c])
            {
                if (i >= m)
                    continue;
                placed[c].Add(i);
                columnWidth[c] = MathF.Max(columnWidth[c], width[nodes[i]]);
            }
        }
        var y = new float[m];
        var touching = new List<int>[m];
        for (int v = 0; v < m; v++)
            touching[v] = new List<int>();
        for (int e = 0; e < k; e++)
        {
            if (layer[from[e]] == layer[to[e]])
                continue;
            touching[from[e]].Add(e);
            touching[to[e]].Add(e);
        }
        foreach (var column in placed)
        {
            float cursor = 0f;
            foreach (int v in column)
            {
                y[v] = cursor;
                cursor -= height[nodes[v]] + ShaderGraphBuilder.RowGap;
            }
        }
        var wanted = new float[m];
        var pull = new float[m];
        var blockStart = new int[m];
        var blockWeight = new double[m];
        var blockSum = new double[m];
        for (int round = 0; round < PlaceRounds; round++)
        {
            float moved = 0f;
            for (int c = 0; c < columns; c++)
                moved = MathF.Max(moved, Settle(placed[c]));
            for (int c = columns - 2; c >= 0; c--)
                moved = MathF.Max(moved, Settle(placed[c]));
            if (moved < Settled)
                break;
        }

        var columnX = new float[columns];
        float across = 0f;
        for (int c = 0; c < columns; c++)
        {
            columnX[c] = across;
            across += columnWidth[c] + ShaderGraphBuilder.ColumnGap;
        }
        float fullWidth = across - ShaderGraphBuilder.ColumnGap;
        float highest = float.MinValue, lowest = float.MaxValue;
        for (int v = 0; v < m; v++)
        {
            highest = MathF.Max(highest, y[v]);
            lowest = MathF.Min(lowest, y[v] - height[nodes[v]]);
        }
        float fullHeight = highest - lowest;

        // A part far wider than it is tall, a long chain most often, is wrapped like a line of text: its columns
        // cut into bands stacked top to bottom, each cut near an even share where the fewest wires run across.
        // Wires crossing in front of column c, for a cut there.
        var crossing = new int[columns + 1];
        for (int e = 0; e < k; e++)
        {
            int a = System.Math.Min(layer[from[e]], layer[to[e]]), b = System.Math.Max(layer[from[e]], layer[to[e]]);
            if (a == b)
                continue;
            crossing[a + 1]++;
            crossing[b + 1]--;
        }
        for (int c = 1; c <= columns; c++)
            crossing[c] += crossing[c - 1];
        int bands = System.Math.Min((int)MathF.Ceiling(fullWidth / WrapWidth),
            (int)MathF.Floor(MathF.Sqrt(fullWidth / (BlockAspect * MathF.Max(fullHeight, 0.01f)))));
        var starts = new List<int> { 0 };
        if (bands > 1)
        {
            float share = fullWidth / bands;
            for (int j = 1; j < bands; j++)
            {
                float ideal = share * j;
                int cut = -1;
                for (int c = starts[^1] + 1; c < columns; c++)
                {
                    float off = MathF.Abs(columnX[c] - ideal);
                    if (off > share * 0.35f)
                        continue;
                    if (cut < 0 || crossing[c] < crossing[cut] || (crossing[c] == crossing[cut] && off < MathF.Abs(columnX[cut] - ideal)))
                        cut = c;
                }
                if (cut > 0)
                    starts.Add(cut);
            }
        }
        var bandOf = new int[columns];
        for (int b = 0; b < starts.Count; b++)
        {
            int end = b + 1 < starts.Count ? starts[b + 1] : columns;
            for (int c = starts[b]; c < end; c++)
                bandOf[c] = b;
        }
        var bandHigh = new float[starts.Count];
        var bandLow = new float[starts.Count];
        Array.Fill(bandHigh, float.MinValue);
        Array.Fill(bandLow, float.MaxValue);
        for (int v = 0; v < m; v++)
        {
            int b = bandOf[layer[v]];
            bandHigh[b] = MathF.Max(bandHigh[b], y[v]);
            bandLow[b] = MathF.Min(bandLow[b], y[v] - height[nodes[v]]);
        }
        var bandTop = new float[starts.Count];
        float below = 0f;
        part.Width = 0f;
        for (int b = 0; b < starts.Count; b++)
        {
            bandTop[b] = below;
            below -= bandHigh[b] - bandLow[b] + PartGapY;
            int last = b + 1 < starts.Count ? starts[b + 1] - 1 : columns - 1;
            part.Width = MathF.Max(part.Width, columnX[last] + columnWidth[last] - columnX[starts[b]]);
        }
        part.Height = -(below + PartGapY);
        for (int v = 0; v < m; v++)
        {
            int b = bandOf[layer[v]];
            x[nodes[v]] = columnX[layer[v]] - columnX[starts[b]];
            top[nodes[v]] = y[v] - bandHigh[b] + bandTop[b];
        }
        if (starts.Count == 1)
            Fold(part, nodes, height, x, top);

        void AddSegment(int a, int b, float fracA, float fracB)
        {
            segFrom.Add(a);
            segTo.Add(b);
            segFracFrom.Add(fracA);
            segFracTo.Add(fracB);
        }

        // Each one at the mean of the places of what it is wired to in the column beside; one wired to nothing
        // there keeps its place.
        void Reorder(List<int> row, bool fromLeft)
        {
            foreach (int i in row)
            {
                var segments = fromLeft ? ups[i] : downs[i];
                if (segments.Count == 0)
                {
                    sortKey[i] = pos[i];
                    continue;
                }
                double sum = 0;
                foreach (int s in segments)
                    sum += fromLeft ? pos[segFrom[s]] + segFracFrom[s] : pos[segTo[s]] + segFracTo[s];
                sortKey[i] = sum / segments.Count;
            }
            row.Sort((a, b) => sortKey[a] != sortKey[b] ? sortKey[a].CompareTo(sortKey[b]) : pos[a].CompareTo(pos[b]));
            for (int i = 0; i < row.Count; i++)
                pos[row[i]] = i;
        }

        long Crossings()
        {
            long sum = 0;
            var pairs = new List<(float A, float B)>();
            var lower = new List<float>();
            for (int c = 0; c + 1 < columns; c++)
            {
                pairs.Clear();
                foreach (int i in rows[c])
                {
                    foreach (int s in downs[i])
                        pairs.Add((pos[i] + segFracFrom[s], pos[segTo[s]] + segFracTo[s]));
                }
                if (pairs.Count < 2)
                    continue;
                pairs.Sort((p, q) => p.A != q.A ? p.A.CompareTo(q.A) : p.B.CompareTo(q.B));
                lower.Clear();
                foreach (var pair in pairs)
                    lower.Add(pair.B);
                sum += Inversions(lower);
            }
            return sum;
        }

        // The column's cards as near their wanted heights as their order and gaps allow, by weight: tops kept
        // a card and a gap apart, top first, is a run that may only go down, which pooling adjacent
        // violators solves exactly.
        float Settle(List<int> column)
        {
            int n = column.Count;
            if (n == 0)
                return 0f;
            for (int i = 0; i < n; i++)
            {
                int v = column[i];
                double weight = 0, sum = 0;
                foreach (int e in touching[v])
                {
                    var edge = part.Edges[e];
                    float w = edge.Weight / System.Math.Max(1, System.Math.Abs(layer[to[e]] - layer[from[e]]));
                    float level = from[e] == v ? y[to[e]] + edge.ToY - edge.FromY : y[from[e]] + edge.FromY - edge.ToY;
                    weight += w;
                    sum += w * level;
                }
                wanted[i] = weight > 0 ? (float)(sum / weight) : y[v];
                pull[i] = weight > 0 ? (float)weight : 0.001f;
            }
            int blocks = 0;
            float offset = 0f;
            for (int i = 0; i < n; i++)
            {
                double target = wanted[i] + offset;
                blockStart[blocks] = i;
                blockWeight[blocks] = pull[i];
                blockSum[blocks] = pull[i] * target;
                blocks++;
                while (blocks > 1 && blockSum[blocks - 2] / blockWeight[blocks - 2] < blockSum[blocks - 1] / blockWeight[blocks - 1])
                {
                    blockWeight[blocks - 2] += blockWeight[blocks - 1];
                    blockSum[blocks - 2] += blockSum[blocks - 1];
                    blocks--;
                }
                offset += height[nodes[column[i]]] + ShaderGraphBuilder.RowGap;
            }
            float moved = 0f;
            offset = 0f;
            for (int b = 0, i = 0; b < blocks; b++)
            {
                int end = b + 1 < blocks ? blockStart[b + 1] : n;
                float level = (float)(blockSum[b] / blockWeight[b]);
                for (; i < end; i++)
                {
                    int v = column[i];
                    float next = level - offset;
                    moved = MathF.Max(moved, MathF.Abs(next - y[v]));
                    y[v] = next;
                    offset += height[nodes[v]] + ShaderGraphBuilder.RowGap;
                }
            }
            return moved;
        }
    }

    // How far down its card a port sits, 0 at the top and short of 1 at the bottom.
    private static float Fraction(float anchor, float cardHeight)
        => cardHeight > 0f ? System.Math.Clamp(-anchor / cardHeight, 0f, 0.999f) : 0.5f;

    // Columns renumbered from 0 with none left empty; returns how many there are.
    private static int Compact(int[] layer)
    {
        int max = 0;
        foreach (int l in layer)
            max = System.Math.Max(max, l);
        int min = int.MaxValue;
        foreach (int l in layer)
            min = System.Math.Min(min, l);
        var used = new bool[max - min + 1];
        foreach (int l in layer)
            used[l - min] = true;
        var renumber = new int[used.Length];
        int columns = 0;
        for (int i = 0; i < used.Length; i++)
            renumber[i] = used[i] ? columns++ : -1;
        for (int v = 0; v < layer.Length; v++)
            layer[v] = renumber[layer[v] - min];
        return columns;
    }

    // Pairs out of order, by merge sort.
    private static long Inversions(List<float> values)
    {
        var a = values.ToArray();
        var scratch = new float[a.Length];
        long count = 0;
        for (int width = 1; width < a.Length; width *= 2)
        {
            for (int lo = 0; lo < a.Length; lo += width * 2)
            {
                int mid = System.Math.Min(lo + width, a.Length), hi = System.Math.Min(lo + width * 2, a.Length);
                int i = lo, j = mid, o = lo;
                while (i < mid && j < hi)
                {
                    if (a[j] < a[i])
                    {
                        count += mid - i;
                        scratch[o++] = a[j++];
                    }
                    else
                    {
                        scratch[o++] = a[i++];
                    }
                }
                while (i < mid)
                    scratch[o++] = a[i++];
                while (j < hi)
                    scratch[o++] = a[j++];
            }
            (a, scratch) = (scratch, a);
        }
        return count;
    }

    // A part taller than a person reaches, many chains side by side down the same columns most often, is folded
    // the other way: its rows cut into strips that stand side by side, as many as the widest block allows, each
    // cut where the fewest cards straddle it. A card a cut runs through stays with the strip its top is in.
    private static void Fold(Part part, List<int> nodes, float[] height, float[] x, float[] top)
    {
        float tall = part.Height, wide = MathF.Max(part.Width, 0.01f);
        if (tall <= TallestBlock)
            return;
        int strips = System.Math.Min((int)MathF.Ceiling(tall / TallestBlock), (int)MathF.Floor((WidestBlock + PartGapX) / (wide + PartGapX)));
        if (strips < 2)
            return;
        float share = tall / strips;
        var cuts = new List<float> { float.MaxValue };
        for (int j = 1; j < strips; j++)
        {
            float ideal = -share * j;
            float best = float.NaN;
            int fewest = int.MaxValue;
            foreach (int v in nodes)
            {
                float y = top[v] + 1e-4f;
                if (MathF.Abs(y - ideal) > share * 0.35f || y >= cuts[^1])
                    continue;
                int straddle = 0;
                foreach (int u in nodes)
                {
                    if (top[u] > y && top[u] - height[u] < y)
                        straddle++;
                }
                if (straddle < fewest || (straddle == fewest && MathF.Abs(y - ideal) < MathF.Abs(best - ideal)))
                {
                    fewest = straddle;
                    best = y;
                }
            }
            if (!float.IsNaN(best))
                cuts.Add(best);
        }
        if (cuts.Count < 2)
            return;
        var stripTop = new float[cuts.Count];
        var stripLow = new float[cuts.Count];
        Array.Fill(stripTop, float.MinValue);
        Array.Fill(stripLow, float.MaxValue);
        var stripOf = new Dictionary<int, int>(nodes.Count);
        foreach (int v in nodes)
        {
            int s = 0;
            while (s + 1 < cuts.Count && top[v] <= cuts[s + 1])
                s++;
            stripOf[v] = s;
            stripTop[s] = MathF.Max(stripTop[s], top[v]);
            stripLow[s] = MathF.Min(stripLow[s], top[v] - height[v]);
        }
        float highest = 0f;
        foreach (int v in nodes)
        {
            int s = stripOf[v];
            x[v] += s * (wide + PartGapX);
            top[v] -= stripTop[s];
            highest = MathF.Max(highest, stripTop[s] - stripLow[s]);
        }
        part.Width = cuts.Count * wide + (cuts.Count - 1) * PartGapX;
        part.Height = highest;
    }

    // The parts in rows, biggest first, each row filled left to right up to a width that keeps the block
    // about twice as wide as it is tall. Returns the block's size; each part's place is its top-left, from the
    // block's top-left.
    private static float2 Shelve(List<Part> parts)
    {
        float widest = 0f;
        double area = 0;
        foreach (var part in parts)
        {
            widest = MathF.Max(widest, part.Width);
            area += (part.Width + PartGapX) * (double)(part.Height + PartGapY);
        }
        float limit = MathF.Max(widest, (float)System.Math.Sqrt(area * BlockAspect));
        var sorted = new List<int>(parts.Count);
        for (int i = 0; i < parts.Count; i++)
            sorted.Add(i);
        sorted.Sort((a, b) =>
        {
            var p = parts[a];
            var q = parts[b];
            if (p.Nodes.Count != q.Nodes.Count)
                return q.Nodes.Count.CompareTo(p.Nodes.Count);
            float pa = p.Width * p.Height, qa = q.Width * q.Height;
            return pa != qa ? qa.CompareTo(pa) : a.CompareTo(b);
        });
        float across = 0f, rowTop = 0f, rowHeight = 0f, right = 0f;
        foreach (int i in sorted)
        {
            var part = parts[i];
            if (across > 0f && across + part.Width > limit)
            {
                rowTop -= rowHeight + PartGapY;
                across = 0f;
                rowHeight = 0f;
            }
            part.X = across;
            part.Top = rowTop;
            across += part.Width + PartGapX;
            rowHeight = MathF.Max(rowHeight, part.Height);
            right = MathF.Max(right, part.X + part.Width);
        }
        return new float2(right, rowHeight - rowTop);
    }
}
