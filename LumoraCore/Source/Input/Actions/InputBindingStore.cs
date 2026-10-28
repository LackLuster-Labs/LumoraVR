// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Lumora.Core.Logging;

namespace Lumora.Core.Input.Actions;

// Reads and writes the user's binding overrides as JSON.
//
// Only actions the user actually changed are written. That is not a size optimization: it means a
// later build can move a default (fix a bad shipped binding, add a gamepad column) and everybody
// picks it up, while the three keys somebody deliberately remapped stay remapped. An action that
// was deliberately CLEARED is written as an empty list, which is why "absent" and "empty" have to
// stay distinguishable.
//
// Control ids are written out longhand ("Keyboard/W", "Gamepad/FaceDown") rather than as numbers,
// so the file is readable and survives us reordering an enum. -xlinka
public static class InputBindingStore
{
    public const string SettingsKey = "Engine.Input.Bindings";

    public static string Serialize(InputBindingMap map)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", InputBindingMap.MapVersion);
            writer.WriteStartObject("actions");

            foreach (var action in map.AllActions())
            {
                if (!action.IsOverridden)
                    continue;

                writer.WriteStartArray(action.Path);
                foreach (var binding in action.Bindings)
                    WriteBinding(writer, binding);
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteBinding(Utf8JsonWriter writer, InputBinding binding)
    {
        writer.WriteStartObject();
        writer.WriteString("control", binding.Control.Serialize());
        if (binding.Source != BindingAxis.Auto)
            writer.WriteString("source", binding.Source.ToString());
        if (binding.Target != BindingAxis.Auto)
            writer.WriteString("target", binding.Target.ToString());
        if (binding.Scale != 1f)
            writer.WriteNumber("scale", binding.Scale);
        if (binding.Modifiers.Count > 0)
        {
            writer.WriteStartArray("modifiers");
            foreach (var modifier in binding.Modifiers)
                writer.WriteStringValue(modifier.Serialize());
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    // Layer stored overrides onto a map that already holds the shipped defaults. Anything malformed
    // is skipped and the default is kept: a corrupt binding file must never leave someone unable to
    // walk. True when an override written by an older map was moved on, so the caller saves it back.
    public static bool Apply(InputBindingMap map, string? json)
    {
        if (map == null || string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            int storedVersion = 1;
            if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number)
                storedVersion = version.GetInt32();
            if (storedVersion > InputBindingMap.MapVersion)
            {
                Logger.Warn($"InputBindingStore: stored bindings are version {storedVersion}, this build understands {InputBindingMap.MapVersion}; using defaults.");
                return false;
            }

            if (!root.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Object)
                return false;

            int applied = 0;
            foreach (var entry in actions.EnumerateObject())
            {
                var action = map.Find(entry.Name);
                if (action == null || entry.Value.ValueKind != JsonValueKind.Array)
                    continue;

                var parsed = new List<InputBinding>();
                bool malformed = false;
                foreach (var element in entry.Value.EnumerateArray())
                {
                    if (!TryReadBinding(element, out var binding))
                    {
                        malformed = true;
                        break;
                    }
                    parsed.Add(binding);
                }
                if (malformed)
                    continue;

                action.ClearBindings();
                foreach (var binding in parsed)
                    action.Bind(binding);
                action.IsOverridden = true;
                applied++;
            }

            if (applied > 0)
                Logger.Log($"InputBindingStore: applied {applied} rebound action(s).");

            bool moved = storedVersion < 2 && MoveBackstepOffChatKey(map);
            if (storedVersion < 3)
                moved |= MovePadColumn(map);
            if (storedVersion < 4)
                moved |= MoveFlyDownOffCtrl(map);
            return moved;
        }
        catch (Exception ex)
        {
            Logger.Warn($"InputBindingStore: failed to read stored bindings, keeping defaults: {ex.Message}");
            return false;
        }
    }

    // A backstep override saved before G opened the chat carries G with it whenever any part of the action
    // was rebound, the pad half included, because an override stores the whole list. Left there, G would
    // hop you backwards and open the chat at once. It goes to B with the stock map, unless the user already
    // put B on something of their own, and then the backstep keeps only its other controls. -xlinka
    private static bool MoveBackstepOffChatKey(InputBindingMap map)
    {
        var backstep = map.Locomotion.BlinkBackstep;
        if (!backstep.IsOverridden)
            return false;

        var oldKey = ControlRef.Key(InputBindingDefaults.OldBlinkBackstepKey);
        var newKey = ControlRef.Key(InputBindingDefaults.BlinkBackstepKey);
        var kept = new List<InputBinding>();
        InputBinding? moved = null;
        bool hasNew = false;
        foreach (var binding in backstep.Bindings)
        {
            if (binding.Control == oldKey && binding.Modifiers.Count == 0)
            {
                moved ??= binding;
                continue;
            }
            hasNew |= binding.Control == newKey && binding.Modifiers.Count == 0;
            kept.Add(binding);
        }
        if (moved == null)
            return false;

        bool newTaken = false;
        foreach (var action in map.AllActions())
        {
            if (action == backstep || !action.IsOverridden)
                continue;
            foreach (var binding in action.Bindings)
                newTaken |= binding.Control == newKey && binding.Modifiers.Count == 0;
        }

        backstep.ClearBindings();
        foreach (var binding in kept)
            backstep.Bind(binding);
        if (!hasNew && !newTaken)
            backstep.Bind(new InputBinding(newKey, moved.Source, moved.Target, moved.Scale));
        Logger.Log($"InputBindingStore: moved the saved backstep off {oldKey.Describe()}{(hasNew || newTaken ? string.Empty : " onto " + newKey.Describe())}.");
        return true;
    }

    // Version 4 made ctrl the camera's chord key, so a saved free-camera altitude list that still sinks on the
    // stock ctrl keys would drop the camera through the floor every time ctrl + right is held to look. The plain
    // ctrl keys go and C comes in with the stock scale, unless the list already sinks on C. Anything else in it
    // is the user's and stays. -xlinka
    private static bool MoveFlyDownOffCtrl(InputBindingMap map)
    {
        var fly = map.Camera.FlyVertical;
        if (!fly.IsOverridden)
            return false;

        var left = ControlRef.Key(Key.LeftControl);
        var right = ControlRef.Key(Key.RightControl);
        var down = ControlRef.Key(InputBindingDefaults.FlyDownKey);
        var kept = new List<InputBinding>();
        bool removed = false, hasDown = false;
        foreach (var binding in fly.Bindings)
        {
            if ((binding.Control == left || binding.Control == right) && binding.Modifiers.Count == 0)
            {
                removed = true;
                continue;
            }
            hasDown |= binding.Control == down && binding.Modifiers.Count == 0;
            kept.Add(binding);
        }
        if (!removed)
            return false;

        fly.ClearBindings();
        foreach (var binding in kept)
            fly.Bind(binding);
        if (!hasDown)
            fly.Bind(down, scale: -1f);
        Logger.Log($"InputBindingStore: moved the saved free-camera sink off ctrl{(hasDown ? string.Empty : " onto " + down.Describe())}.");
        return true;
    }

    // The pad column as it shipped before version 3, by action. Only the controls: the axes and scales on them
    // never mattered to whether a control was the stock one.
    private static readonly Dictionary<string, string[]> PadColumnV2 = new(StringComparer.Ordinal)
    {
        ["Locomotion/Move"] = new[] { InputControlCatalog.PadLeftStick },
        ["Locomotion/Turn"] = new[] { InputControlCatalog.PadRightStick },
        ["Locomotion/Fly"] = new[] { InputControlCatalog.PadDPadUp, InputControlCatalog.PadDPadDown },
        ["Locomotion/Jump"] = new[] { InputControlCatalog.PadFaceDown },
        ["Locomotion/Sprint"] = new[] { InputControlCatalog.PadLeftShoulder },
        ["Locomotion/Crouch"] = new[] { InputControlCatalog.PadLeftTrigger },
        ["Locomotion/BlinkAim"] = new[] { InputControlCatalog.PadFaceRight },
        ["Locomotion/BlinkBackstep"] = new[] { InputControlCatalog.PadRightStickPress },
        ["Interaction.Right/Primary"] = new[] { InputControlCatalog.PadRightTrigger },
        ["Interaction.Right/Grab"] = new[] { InputControlCatalog.PadRightShoulder },
        ["Interaction.Right/Secondary"] = new[] { InputControlCatalog.PadFaceLeft },
        ["Interaction.Right/ContextMenu"] = new[] { InputControlCatalog.PadFaceUp },
        ["Interaction.Right/HoldScroll"] = new[] { InputControlCatalog.PadDPadUp, InputControlCatalog.PadDPadDown },
        ["Interaction.Right/Stick"] = new[] { InputControlCatalog.PadRightStick },
        ["Interaction.Left/Stick"] = new[] { InputControlCatalog.PadLeftStick },
        ["Menu/ToggleDashboard"] = new[] { InputControlCatalog.PadStart },
    };

    private static HashSet<string> OldPad(InputAction action)
        => PadColumnV2.TryGetValue(action.Path, out var controls) ? new HashSet<string>(controls, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);

    private static bool IsPad(InputBinding binding) => binding.Control.Device == InputDeviceKind.Gamepad;

    // Version 3 moved most of the pad column, and an override stores an action's whole list, pad half and all,
    // whichever half was rebound. So a keyboard rebind of crouch saved before carries LT along, which is the grab
    // now. For each saved action: the stock pad controls that moved away go, and the new stock ones come in, but
    // only where the user never touched the pad half (it still held exactly the old stock, or nothing because
    // the action had no pad control before). A pad half they rebound or cleared is theirs and stays as it is.
    // Then the other way round: a new stock control lands nowhere a rebound action in the same set already put
    // one of the user's own choices, and that action keeps the rest of its stock. -xlinka
    private static bool MovePadColumn(InputBindingMap map)
    {
        bool changed = false;

        foreach (var action in map.AllActions())
        {
            if (!action.IsOverridden)
                continue;
            var old = OldPad(action);
            var stock = new List<InputBinding>();
            foreach (var binding in StockBindings(map, action))
            {
                if (IsPad(binding))
                    stock.Add(binding);
            }
            var stockIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in stock)
                stockIds.Add(binding.Control.Control);
            if (old.SetEquals(stockIds))
                continue;

            bool hadPad = false, own = false;
            var kept = new List<InputBinding>();
            foreach (var binding in action.Bindings)
            {
                if (IsPad(binding))
                {
                    hadPad = true;
                    own |= !old.Contains(binding.Control.Control);
                    if (old.Contains(binding.Control.Control) && !stockIds.Contains(binding.Control.Control))
                        continue;
                }
                kept.Add(binding);
            }
            bool cleared = old.Count > 0 && !hadPad;
            if (!cleared && !own)
            {
                foreach (var binding in stock)
                {
                    bool present = false;
                    foreach (var k in kept)
                        present |= k.Control == binding.Control && k.Modifiers.Count == 0;
                    if (!present && !TakenByOwnChoice(action, binding.Control))
                        kept.Add(binding);
                }
            }
            if (SameList(kept, action.Bindings))
                continue;

            action.ClearBindings();
            foreach (var binding in kept)
                action.Bind(binding);
            changed = true;
            Logger.Log($"InputBindingStore: moved the saved pad controls of {action.Path} to {action.DescribeBindingsFor(InputDeviceKind.Gamepad)}.");
        }

        foreach (var action in map.AllActions())
        {
            if (action.IsOverridden || action.Kind != InputActionKind.Digital)
                continue;
            var old = OldPad(action);
            var dropped = new List<InputBinding>();
            foreach (var binding in action.Bindings)
            {
                if (IsPad(binding) && !old.Contains(binding.Control.Control) && TakenByOwnChoice(action, binding.Control))
                    dropped.Add(binding);
            }
            if (dropped.Count == 0)
                continue;
            var kept = new List<InputBinding>();
            foreach (var binding in action.Bindings)
            {
                if (!dropped.Contains(binding))
                    kept.Add(binding);
            }
            action.ClearBindings();
            foreach (var binding in kept)
                action.Bind(binding);
            action.IsOverridden = true;
            changed = true;
            Logger.Log($"InputBindingStore: left {dropped[0].Control.Describe()} off {action.Path}; a rebound action in its set already uses it.");
        }

        return changed;
    }

    // A plain binding on this control that another rebound action in the same set holds as the user's own pick
    // rather than as stock carried along. Digital on both sides: analog overlaps are allowed on purpose.
    private static bool TakenByOwnChoice(InputAction action, in ControlRef control)
    {
        if (action.Kind != InputActionKind.Digital || action.Set == null)
            return false;
        foreach (var other in action.Set.Actions)
        {
            if (ReferenceEquals(other, action) || !other.IsOverridden || other.Kind != InputActionKind.Digital)
                continue;
            var otherOld = OldPad(other);
            foreach (var binding in other.Bindings)
            {
                if (binding.Control == control && binding.Modifiers.Count == 0 && !otherOld.Contains(control.Control))
                    return true;
            }
        }
        return false;
    }

    // What the shipped map binds an action to, read off a scratch pass of the defaults and put back after.
    private static List<InputBinding> StockBindings(InputBindingMap map, InputAction action)
    {
        var saved = new List<InputBinding>(action.Bindings);
        bool overridden = action.IsOverridden;
        action.ClearBindings();
        InputBindingDefaults.ApplyTo(map, action);
        var stock = new List<InputBinding>(action.Bindings);
        action.ClearBindings();
        foreach (var binding in saved)
            action.Bind(binding);
        action.IsOverridden = overridden;
        return stock;
    }

    private static bool SameList(List<InputBinding> a, IReadOnlyList<InputBinding> b)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!ReferenceEquals(a[i], b[i]))
                return false;
        }
        return true;
    }

    private static bool TryReadBinding(JsonElement element, out InputBinding binding)
    {
        binding = null!;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        if (!element.TryGetProperty("control", out var controlText)
            || controlText.ValueKind != JsonValueKind.String
            || !ControlRef.TryParse(controlText.GetString(), out var control))
            return false;

        var source = ReadAxis(element, "source");
        var target = ReadAxis(element, "target");

        float scale = 1f;
        if (element.TryGetProperty("scale", out var scaleValue) && scaleValue.ValueKind == JsonValueKind.Number)
            scale = scaleValue.GetSingle();

        List<ControlRef>? modifiers = null;
        if (element.TryGetProperty("modifiers", out var modifierArray) && modifierArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var modifier in modifierArray.EnumerateArray())
            {
                if (modifier.ValueKind != JsonValueKind.String || !ControlRef.TryParse(modifier.GetString(), out var parsedModifier))
                    return false;
                modifiers ??= new List<ControlRef>();
                modifiers.Add(parsedModifier);
            }
        }

        binding = new InputBinding(control, source, target, scale, modifiers);
        return true;
    }

    private static BindingAxis ReadAxis(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && Enum.TryParse<BindingAxis>(value.GetString(), ignoreCase: true, out var axis))
            return axis;
        return BindingAxis.Auto;
    }
}
