// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Linq;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Networking.Sync;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// A connection may change its receiving node's overload, but never disconnect another wire or clear
// a value. This is also used for drag targets, without changing any synced state.
internal static class ScriptGraphOverloads
{
    internal static bool CanWrite(SyncElement member)
        => member.AllowsConfigWrite && !member.IsBlockedByDrive && member.IsWriteAllowed();

    public static NodeOverload? SelectInput(ScriptGraphNode node, string input, ScriptType given)
    {
        if (node.Overload is not { } current || current.ConnectableInputIndex(input) is not (int index and >= 0))
            return null;
        if (node.Language.Accepts(current.Inputs[index].Type, given))
            return current;
        if (node.Language.Kind != GraphKind.Script || !CanWrite(node.Signature))
            return null;

        return NodeOverloadInference.ForInput(node.Language.Registry, current, input, given)
            .Where(candidate => Preserves(node, input, current, candidate))
            .OrderBy(candidate => Changes(current, candidate))
            .ThenBy(candidate => candidate.Signature, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static int Changes(NodeOverload current, NodeOverload candidate)
        => current.Inputs.Count(p => p.Type != candidate.Inputs[candidate.InputIndex(p.Name)].Type)
            + current.Outputs.Count(p => p.Type != candidate.Outputs[candidate.OutputIndex(p.Name)].Type);

    private static bool Preserves(ScriptGraphNode node, string replacedInput, NodeOverload current, NodeOverload candidate)
    {
        if (!current.Inputs.Select(p => (p.Name, p.IsSetting)).SequenceEqual(candidate.Inputs.Select(p => (p.Name, p.IsSetting)))
            || !current.Outputs.Select(p => p.Name).SequenceEqual(candidate.Outputs.Select(p => p.Name))
            || !current.FlowInputs.SequenceEqual(candidate.FlowInputs) || !current.FlowOutputs.SequenceEqual(candidate.FlowOutputs))
            return false;

        foreach (var wire in node.Inputs)
        {
            if (wire.Port.Value == replacedInput || wire.Node.Value.IsNull)
                continue;
            int input = candidate.ConnectableInputIndex(wire.Port.Value);
            var producer = wire.Node.Target?.Overload;
            int output = producer?.OutputIndex(wire.NodePort.Value) ?? -1;
            if (input < 0 || output < 0 || !node.Language.Accepts(candidate.Inputs[input].Type, producer!.Outputs[output].Type))
                return false;
        }

        foreach (var other in node.Space!.Nodes)
        foreach (var wire in other.Inputs)
        {
            if (!ReferenceEquals(wire.Node.Target, node))
                continue;
            int output = candidate.OutputIndex(wire.NodePort.Value);
            var consumer = other.Overload;
            int input = consumer?.ConnectableInputIndex(wire.Port.Value) ?? -1;
            if (output < 0 || input < 0 || !node.Language.Accepts(consumer!.Inputs[input].Type, candidate.Outputs[output].Type))
                return false;
        }

        foreach (var value in node.Values)
        {
            // Stored variables have an additional node reference, outside their ordinary input list.
            if (value.Port.Value == "var" && current.Definition.Kind == NodeKind.WriteVariable)
            {
                var variable = value.Reference.Target as ScriptGraphNode;
                if (variable == null && !string.IsNullOrEmpty(value.Json.Value))
                {
                    try
                    {
                        var name = GraphValue.ParseJson(value.Json.Value);
                        if (name.Kind == GraphValueKind.String)
                            variable = node.Space.Nodes.FirstOrDefault(n => n.NodeId.Value == name.String);
                    }
                    catch (GraphFormatException)
                    {
                        return false;
                    }
                }
                if (variable?.Overload is { Outputs.Count: > 0 } stored && stored.Outputs[0].Type != candidate.Inputs[0].Type)
                    return false;
                continue;
            }
            int index = candidate.InputIndex(value.Port.Value);
            if (index < 0 || current.Inputs[index].Type == candidate.Inputs[index].Type)
                continue;
            var type = candidate.Inputs[index].Type;
            if (!value.Reference.Value.IsNull)
            {
                if (value.Reference.Target is not { IsDestroyed: false } target
                    || !type.ClrType.IsInstanceOfType(target) && !ShaderPropertyFields.Accepts(target, type.ClrType))
                    return false;
            }
            else if (!string.IsNullOrEmpty(value.Json.Value))
            {
                try
                {
                    if (!GraphValue.ParseJson(value.Json.Value).TryValidate(type, out _))
                        return false;
                }
                catch (GraphFormatException)
                {
                    return false;
                }
            }
        }
        return true;
    }
}
