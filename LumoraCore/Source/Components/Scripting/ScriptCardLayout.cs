// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Math;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Core.Components.Scripting;

// Where everything sits on a node's card, in canvas units from its top-left corner, y up. Worked out
// from the node's ports alone, so the card and the wires that meet it agree without either one asking
// the canvas where it put anything.
//
// Inputs down the left, flow first; outputs down the right, flow first; then the node's settings (a
// Stored Value's name and start, a Write Value's target, a Function's name, the Function a Return or a
// call points at, the name another script's Function is called by, the object an event listens to); then a
// Display's or Pulse's readout, or a Call's button; then a shader node's preview of what it makes, or a Material
// Output's material well; then up to two lines of what is wrong with it.
//
// A relay's card is a short bar with its one port at each end and nothing else, so it sits on its wire like a
// bead rather than a box. A relay with something wrong opens out into a full card, which can say what.
public sealed class ScriptCardLayout
{
    public const float PreviewSize = 200f;
    public const float CompactWidth = 144f;
    public const float CompactHeight = 56f;

    public List<ScriptCardPort> Left { get; } = new();
    public List<ScriptCardPort> Right { get; } = new();
    public List<ScriptCardPort> Settings { get; } = new();
    public int ProblemLines { get; private set; }
    public bool Readout { get; private set; }
    public bool CallButton { get; private set; }
    public bool Preview { get; private set; }
    public bool OutputWell { get; private set; }
    public float InputLabelWidth { get; private set; } = 80f;
    public float OutputLabelWidth { get; private set; } = 104f;
    public float EditorWidth { get; private set; } = 180f;
    public float EditorLeft => ScriptEditorStyle.PortInset + InputLabelWidth + 10f;
    public float OutputLeft => Width - ScriptEditorStyle.PortInset - OutputLabelWidth;
    public float Width { get; private set; } = ScriptEditorStyle.CardWidth;
    public int OptionsRows { get; private set; }
    public bool OptionsOpen { get; set; }
    public bool Compact { get; private set; }
    public float OptionsTop => ProblemTop(ProblemLines) + ScriptEditorStyle.Padding;

    public int PortRows => System.Math.Max(Left.Count, Right.Count);

    public float ExtraHeight => Readout || CallButton ? ScriptEditorStyle.RowHeight * 1.5f : 0f;

    public float PreviewHeight => Preview ? PreviewSize + ScriptEditorStyle.Padding
        : OutputWell ? OutputWellHeight + ScriptEditorStyle.Padding : 0f;

    // The sphere's square with the driven materials beside it, then a row of buttons and a row of what happened.
    public static float OutputWellHeight => PreviewSize + ScriptEditorStyle.RowHeight * 2f;

    public float Height => Compact ? CompactHeight
        : ScriptEditorStyle.HeaderHeight + (PortRows + Settings.Count + ProblemLines) * ScriptEditorStyle.RowHeight
        + ExtraHeight + PreviewHeight + ScriptEditorStyle.Padding * 2f
        + (OptionsOpen ? OptionsRows * ScriptEditorStyle.RowHeight + ScriptEditorStyle.Padding : 0f);

    public float RowTop(int row) => Compact
        ? (CompactHeight - ScriptEditorStyle.RowHeight) * 0.5f + row * ScriptEditorStyle.RowHeight
        : ScriptEditorStyle.HeaderHeight + ScriptEditorStyle.Padding + row * ScriptEditorStyle.RowHeight;

    public float SettingTop(int index) => RowTop(PortRows + index);

    public float ExtraTop => RowTop(PortRows + Settings.Count);

    public float PreviewTop => ExtraTop + ExtraHeight;

    public float ProblemTop(int line) => PreviewTop + PreviewHeight + line * ScriptEditorStyle.RowHeight;

    public static ScriptCardLayout For(ScriptGraphNode node, int problemLines)
    {
        var overload = node.Overload;
        var layout = For(overload, node.Space?.Kind.Value == GraphKind.Shader, problemLines);
        if (overload == null)
            layout.OptionsRows = string.IsNullOrEmpty(node.Signature.Value) ? 0 : 1;
        else if (overload.Definition.Kind == NodeKind.Function || overload.Definition.IsScriptCall)
            layout.OptionsRows = 1;
        else
            layout.OptionsRows = (overload.Definition.IsGeneric || ScriptGraphEditing.SignatureChoices(node).Count > 1 ? 1 : 0)
                + (overload.Definition.IsVariadic ? 1 : 0) + (overload.Definition.GrowsExits ? 1 : 0);
        return layout;
    }

    // The same card from the node's ports alone, for placing a node before any slot holds it. The options
    // drawer is left uncounted: it adds height only once opened, and a card starts shut.
    public static ScriptCardLayout For(NodeOverload? overload, bool preview, int problemLines)
    {
        var layout = new ScriptCardLayout { ProblemLines = System.Math.Clamp(problemLines, 0, ScriptEditorStyle.MaxProblemLines) };
        if (overload == null)
        {
            layout.ProblemLines = System.Math.Max(layout.ProblemLines, 1);
            return layout;
        }

        foreach (var input in overload.FlowInputs)
            layout.Left.Add(new ScriptCardPort(input, ScriptCardHandleKind.FlowInput, null));
        if (overload.Definition.Kind == NodeKind.Relay && layout.ProblemLines == 0)
        {
            layout.Compact = true;
            layout.Width = CompactWidth;
            foreach (var input in overload.Inputs)
                layout.Left.Add(new ScriptCardPort(input.Name, ScriptCardHandleKind.DataInput, input.Type));
            foreach (var exit in overload.FlowOutputs)
                layout.Right.Add(new ScriptCardPort(exit, ScriptCardHandleKind.FlowOutput, null));
            foreach (var output in overload.Outputs)
                layout.Right.Add(new ScriptCardPort(output.Name, ScriptCardHandleKind.DataOutput, output.Type));
            return layout;
        }
        switch (overload.Definition.Kind)
        {
            case NodeKind.StoredVariable:
                layout.Settings.Add(new ScriptCardPort("name", ScriptCardHandleKind.DataInput, ScriptTypes.String));
                layout.Settings.Add(new ScriptCardPort("initial", ScriptCardHandleKind.DataInput, overload.Outputs[0].Type));
                break;
            case NodeKind.WriteVariable:
                layout.Settings.Add(new ScriptCardPort("var", ScriptCardHandleKind.Variable, null));
                break;
            case NodeKind.Function:
                layout.Settings.Add(new ScriptCardPort("name", ScriptCardHandleKind.DataInput, ScriptTypes.String));
                break;
            case NodeKind.Return:
            case NodeKind.CallFunction:
            case NodeKind.EvaluateFunction:
                layout.Settings.Add(new ScriptCardPort("fn", ScriptCardHandleKind.Variable, null));
                break;
            case NodeKind.Await when overload.Definition.IsScriptCall:
                layout.Settings.Add(new ScriptCardPort("function", ScriptCardHandleKind.DataInput, ScriptTypes.String));
                break;
            case NodeKind.Event when overload.Definition.TakesSource:
                layout.Settings.Add(new ScriptCardPort("source", ScriptCardHandleKind.DataInput,
                    ScriptTypes.TryGet(overload.Definition.SourceType, out var sourceType) ? sourceType : ScriptTypes.Get("slot")));
                break;
            case NodeKind.Display:
            case NodeKind.Pulse:
                layout.Readout = true;
                break;
            case NodeKind.Event when overload.Definition.TakesInterval:
                layout.Settings.Add(new ScriptCardPort("seconds", ScriptCardHandleKind.DataInput, ScriptTypes.Float));
                break;
            case NodeKind.Event when overload.PayloadType == typeof(CallPressed):
                layout.CallButton = true;
                break;
        }
        foreach (var input in overload.Inputs)
            (input.IsSetting ? layout.Settings : layout.Left).Add(new ScriptCardPort(input.Name, ScriptCardHandleKind.DataInput, input.Type));

        foreach (var exit in overload.FlowOutputs)
            layout.Right.Add(new ScriptCardPort(exit, ScriptCardHandleKind.FlowOutput, null));
        foreach (var output in overload.Outputs)
            layout.Right.Add(new ScriptCardPort(output.Name, ScriptCardHandleKind.DataOutput, output.Type));
        layout.Preview = preview && overload.Outputs.Count > 0;
        layout.OutputWell = preview && overload.Definition.ClrType == typeof(Lumora.Shading.MaterialOutput);
        foreach (var port in layout.Left)
            layout.InputLabelWidth = MathF.Max(layout.InputLabelWidth, LabelWidth(port.Name));
        foreach (var port in layout.Settings)
            layout.InputLabelWidth = MathF.Max(layout.InputLabelWidth, LabelWidth(port.Name));
        foreach (var port in layout.Right)
            layout.OutputLabelWidth = MathF.Max(layout.OutputLabelWidth, LabelWidth(port.Name));
        bool hasEditor = overload.Inputs.Count > 0 || layout.Settings.Count > 0;
        float leftWidth = hasEditor ? layout.EditorLeft + layout.EditorWidth : ScriptEditorStyle.PortInset + layout.InputLabelWidth;
        layout.Width = MathF.Max(ScriptEditorStyle.CardWidth,
            leftWidth + ScriptEditorStyle.ColumnGap + layout.OutputLabelWidth + ScriptEditorStyle.PortInset);
        return layout;
    }

    private static float LabelWidth(string name)
        => System.Math.Clamp(name.Length * ScriptEditorStyle.FontSize * 0.6f + 12f, 80f, 260f);

    // The point a wire meets for a port, on the card's edge, in canvas units.
    public bool TryAnchor(ScriptCardHandleKind kind, string port, out float2 point)
    {
        point = default;
        if (kind == ScriptCardHandleKind.Material)
        {
            if (!OutputWell)
                return false;
            point = new float2(10f + PreviewSize * 0.5f, -(PreviewTop + PreviewSize * 0.5f));
            return true;
        }
        bool left = kind is ScriptCardHandleKind.FlowInput or ScriptCardHandleKind.DataInput;
        var list = left ? Left : Right;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Kind != kind || list[i].Name != port)
                continue;
            float y = -(RowTop(i) + ScriptEditorStyle.RowHeight * 0.5f);
            point = new float2(left ? 0f : Width, y);
            return true;
        }
        return false;
    }
}
