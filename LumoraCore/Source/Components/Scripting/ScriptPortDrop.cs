// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Core.Components.Scripting;

// Binds a world object to a node input: drop a reference card, pulled from an inspector row, onto the
// input's row on a card. Takes the first thing on the card that fits the port: the element itself, what
// it points at, or for a slot, one of its components. A url input takes the address a card's url field or
// asset stands for, as its constant. Lives on the local card. -xlinka
[ComponentCategory("Hidden")]
public sealed class ScriptPortDrop : Component, IProxyReceiver
{
    public ScriptGraphNode? Node { get; set; }
    public string Port { get; set; } = string.Empty;
    public ScriptType? Type { get; set; }

    public bool TryReceiveProxy(IReadOnlyList<IGrabbable> held, Grabber grabber)
    {
        var node = Node;
        var type = Type;
        if (node == null || node.IsDestroyed || type == null)
            return false;

        for (int i = 0; i < held.Count; i++)
        {
            if (held[i] is not Component component || component.Slot == null)
                continue;
            var proxy = component.Slot.GetComponent<ReferenceProxy>();
            if (proxy?.Target.Target is not { } element)
                continue;

            Action? change = null;
            if (type.ClrType == typeof(Uri))
            {
                if (AddressOf(element) is { } address)
                    change = () => node.SetValue(Port, GraphValue.Of(address));
            }
            else if (Pick(type, element) is { } picked)
            {
                change = () => node.SetReference(Port, picked);
            }
            if (change == null)
                continue;
            if (!Edit(node, change))
                return false;
            proxy.Consume(held[i], grabber);
            return true;
        }
        return false;
    }

    // What the row's clear button does: the input back to its default, as one undo step.
    public bool Clear()
    {
        var node = Node;
        return node != null && !node.IsDestroyed && Edit(node, () => node.ClearValue(Port));
    }

    // One change to the input's entry as one undo step, its constant and its bound object together, so
    // undoing a bind brings back the constant the bind cleared.
    private bool Edit(ScriptGraphNode node, Action change)
    {
        var entry = node.FindValue(Port);
        string json = entry?.Json.Value ?? string.Empty;
        var reference = entry?.Reference.Value ?? default;
        try
        {
            change();
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        entry = node.FindValue(Port);
        if (entry == null)
            return false;
        string jsonAfter = entry.Json.Value ?? string.Empty;
        InspectorUndo.Record(this, CompositeUndoBatch.Combine(ScriptGraphEditing.ValueStep,
            jsonAfter != json ? new FieldEditUndoBatch(entry.Json, json, jsonAfter, ScriptGraphEditing.ValueStep) : null,
            entry.Reference.Value != reference ? new FieldEditUndoBatch(entry.Reference, reference, entry.Reference.Value, ScriptGraphEditing.ValueStep) : null));
        return true;
    }

    public static IWorldElement? Pick(ScriptType type, IWorldElement element)
    {
        var wanted = type.ClrType;
        if (wanted.IsInstanceOfType(element) || ShaderPropertyFields.Accepts(element, wanted))
            return element;
        if (element is ISyncRef reference && reference.Target is { } target && wanted.IsInstanceOfType(target))
            return target;
        if (element is Slot slot)
        {
            foreach (var candidate in slot.Components)
            {
                if (wanted.IsInstanceOfType(candidate))
                    return candidate;
            }
        }
        if (element is Component owner && wanted.IsInstanceOfType(owner.Slot))
            return owner.Slot;
        return null;
    }

    // The address a card stands for, for a url input: a url field's, or the one an asset loads from. Only a
    // whole one that names its scheme, the kind a url constant holds.
    public static string? AddressOf(IWorldElement element)
    {
        var url = element switch
        {
            IField<Uri> field => field.Value,
            IUrlAssetProvider provider => provider.SourceUrl,
            _ => null,
        };
        return url != null && TextToUrl.Run(url.OriginalString, out _) is { } whole ? whole.OriginalString : null;
    }
}
