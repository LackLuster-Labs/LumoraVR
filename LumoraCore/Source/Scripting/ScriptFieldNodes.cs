// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Lumora.Core.Components;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Localization;
using Lumora.Core.Networking.Sync;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The node a field turns into when it is held up to the node tool, already pointed at it.
//
// A value field gives Drive Field, Set Field and Get Field of its type, and a Constant holding the field
// itself. A reference field gives Drive Reference, Set Reference Target and Reference Target of the kind it
// points at, and a Constant holding the reference. A drive or a write starts out holding what the field holds
// now, so a drive put on a field leaves it where it was until something is wired into its value; an open graph
// runs as it stands, and a drive starting empty would throw the field to zero the moment it was made.
//
// The node goes into the graph being worked on when it is put down by it, else into a group of its own that
// joins a graph when it is wired into one. Made, pointed and filled, it is one undo step. -xlinka
public static class ScriptFieldNodes
{
    public static readonly LocaleText DriveLabel = LocaleText.Keyed("Loom.Tool.Drive", "Drive");
    public static readonly LocaleText WriteLabel = LocaleText.Keyed("Loom.Tool.Write", "Write");
    public static readonly LocaleText ReadLabel = LocaleText.Keyed("Loom.Tool.Read", "Read");
    public static readonly LocaleText ReferenceLabel = LocaleText.Keyed("Loom.Tool.Reference", "Reference");

    // Between a card made from a field and one it would have landed on, in metres.
    private const float CardGap = 0.025f;

    private static readonly ScriptFieldNodeKind[] Order =
        { ScriptFieldNodeKind.Drive, ScriptFieldNodeKind.Write, ScriptFieldNodeKind.Read, ScriptFieldNodeKind.Reference };

    public static string Label(ScriptFieldNodeKind kind) => (kind switch
    {
        ScriptFieldNodeKind.Drive => DriveLabel,
        ScriptFieldNodeKind.Write => WriteLabel,
        ScriptFieldNodeKind.Read => ReadLabel,
        _ => ReferenceLabel,
    }).Resolve();

    // What a member can be made into, in the order the menu shows them; none for anything that is not a world
    // field.
    public static List<ScriptFieldNodeKind> Offers(IWorldElement? member)
    {
        var offers = new List<ScriptFieldNodeKind>();
        if (member is not SyncElement { IsDestroyed: false, IsLocalElement: false } || FieldOf(member) == null)
            return offers;
        foreach (var kind in Order)
        {
            if (NodeFor(member, kind) is { } made && Resolves(made.Type, made.Signature))
                offers.Add(kind);
        }
        return offers;
    }

    // The node type and signature a member and a kind make, or null when they make none.
    public static (string Type, string Signature)? NodeFor(IWorldElement member, ScriptFieldNodeKind kind)
    {
        if (member is ISyncRef reference)
        {
            if (kind == ScriptFieldNodeKind.Reference)
                return ("Constant", "reference");
            if (TargetHandle(reference) is not { } handle)
                return null;
            return kind switch
            {
                ScriptFieldNodeKind.Drive => ("DriveReference", handle.Name),
                ScriptFieldNodeKind.Write => ("SetReferenceTarget", handle.Name),
                _ => ("ReferenceTarget", handle.Name),
            };
        }
        if (FieldOf(member) is not { } field)
            return null;
        if (!ScriptTypes.TryGet(field.ValueType, out var type))
            return kind == ScriptFieldNodeKind.Reference ? ("Constant", "anyfield") : null;
        return kind switch
        {
            ScriptFieldNodeKind.Drive => ("DriveField", type.Name),
            ScriptFieldNodeKind.Write => ("SetField", type.Name),
            ScriptFieldNodeKind.Read => ("GetField", type.Name),
            _ => ("Constant", $"field<{type.Name}>"),
        };
    }

    // The input a made node takes the member on.
    public static string TargetPort(IWorldElement member, ScriptFieldNodeKind kind)
        => kind == ScriptFieldNodeKind.Reference ? "value" : member is ISyncRef ? "reference" : "field";

    // Makes the node for a member at a point: into a graph, on its plane, when one is given, else into a group of
    // its own there facing the given point.
    public static ScriptGraphNode? Spawn(World world, IWorldElement member, ScriptFieldNodeKind kind, Math.float3 point, Math.float3 facing,
        ScriptGraphSpace? into, out string? error)
    {
        error = null;
        if (member == null || member.IsDestroyed)
        {
            error = "that field is gone";
            return null;
        }
        if (NodeFor(member, kind) is not { } made)
        {
            error = "nothing to make from that field";
            return null;
        }
        if (kind is ScriptFieldNodeKind.Drive or ScriptFieldNodeKind.Write && member is SyncElement element && !element.IsWriteAllowed())
        {
            error = "you may not change that field";
            return null;
        }

        if (into is not { IsDestroyed: false })
        {
            var placed = ScriptGraphEditing.PlaceNode(world, made.Type, made.Signature, point, facing, out error);
            if (placed != null)
                Point(placed, member, kind);
            return placed;
        }

        var local = into.Slot.GlobalPointToLocal(point);
        local.z = 0f;
        ScriptGraphNode? node;
        try
        {
            node = ScriptGraphEditing.MakeNode(into, made.Type, made.Signature, local, out error);
        }
        catch (UnauthorizedAccessException)
        {
            error = "you may not add nodes to that graph";
            return null;
        }
        if (node == null)
            return null;
        Point(node, member, kind);
        node.Slot.LocalPosition.Value = FreeSpot(into, node, local - new Math.float3(ScriptCardLayout.For(node, 0).Width * ScriptEditorStyle.CardScale * 0.5f, 0f, 0f));
        InspectorUndo.Record(world, SlotExistenceUndoBatch.Created(world, new[] { node.Slot }, ScriptGraphEditing.AddNodeStep));
        return node;
    }

    // A card made where another already stands goes down below it, so several made in a row from one spot stand
    // in a column instead of in a pile.
    private static Math.float3 FreeSpot(ScriptGraphSpace space, ScriptGraphNode node, Math.float3 at)
    {
        var layout = ScriptCardLayout.For(node, 0);
        float width = layout.Width * ScriptEditorStyle.CardScale;
        float height = layout.Height * ScriptEditorStyle.CardScale;
        float gap = space.Slot.GlobalScaleToLocal(CardGap);
        for (int tries = 0; tries < 32; tries++)
        {
            bool moved = false;
            foreach (var other in space.Nodes)
            {
                if (ReferenceEquals(other, node) || !ReferenceEquals(other.Slot.Parent, space.Slot))
                    continue;
                var corner = other.Slot.LocalPosition.Value;
                var size = space.View?.CardOf(other)?.Layout ?? ScriptCardLayout.For(other, 0);
                float otherWidth = size.Width * ScriptEditorStyle.CardScale;
                float otherHeight = size.Height * ScriptEditorStyle.CardScale;
                if (at.x < corner.x + otherWidth && at.x + width > corner.x && at.y > corner.y - otherHeight && at.y - height < corner.y)
                {
                    at.y = corner.y - otherHeight - gap;
                    moved = true;
                }
            }
            if (!moved)
                break;
        }
        return at;
    }

    // The member on the node's target input, and for a drive or a write, what the field holds now as the value.
    private static void Point(ScriptGraphNode node, IWorldElement member, ScriptFieldNodeKind kind)
    {
        node.SetReference(TargetPort(member, kind), member);
        if (kind is not (ScriptFieldNodeKind.Drive or ScriptFieldNodeKind.Write))
            return;
        if (member is ISyncRef reference)
        {
            if (reference.Target is { IsDestroyed: false } target && TargetHandle(reference) is { } handle && handle.ClrType.IsInstanceOfType(target))
                node.SetReference("target", target);
            return;
        }
        if (FieldOf(member) is { } field && ScriptTypes.TryGet(field.ValueType, out var type) && ConstantOf(type, field.BoxedValue) is { } value)
            node.SetValue("value", value);
    }

    // A value field, or a shader property as the field of its own type (a material panel's row hands one over).
    private static IField? FieldOf(IWorldElement member)
        => member as IField ?? (member is Components.Assets.ShaderUniformParam param ? ShaderPropertyFields.Natural(param) : null);

    // The handle a reference's target travels as: the kind it holds, or the nearest kind a script names.
    public static ScriptType? TargetHandle(ISyncRef reference)
    {
        var held = reference.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ISyncRef<>))?.GetGenericArguments()[0];
        if (held == null)
            return null;
        if (ScriptTypes.TryGet(held, out var exact) && exact.IsHandle && exact != ScriptTypes.String)
            return exact;
        ScriptType? best = null;
        foreach (var known in ScriptTypes.Known)
        {
            if (!known.IsHandle || known == ScriptTypes.String || known.Family != null || !typeof(IWorldElement).IsAssignableFrom(known.ClrType)
                || !known.ClrType.IsAssignableFrom(held))
                continue;
            if (best == null || best.ClrType.IsAssignableFrom(known.ClrType))
                best = known;
        }
        return best;
    }

    // A value as a card constant of its type: numbers, flags, text, an enum by its name, a struct by its numbers
    // in declaration order. Null for anything else, which leaves the input at its default.
    public static GraphValue? ConstantOf(ScriptType type, object? value)
    {
        if (value == null)
            return null;
        var clr = type.ClrType;
        if (clr.IsEnum)
            return clr.IsDefined(typeof(FlagsAttribute), false) ? null : GraphValue.Of(value.ToString() ?? string.Empty);
        switch (type.Kind)
        {
            case ScriptTypeKind.Bool:
                return value is bool flag ? GraphValue.Of(flag) : null;
            case ScriptTypeKind.Int:
            case ScriptTypeKind.Long:
            case ScriptTypeKind.Float:
            case ScriptTypeKind.Double:
                return Number(value) is double number && double.IsFinite(number) ? GraphValue.Of(number) : null;
            case ScriptTypeKind.Vector:
                var numbers = new List<double>();
                return Flatten(value, clr, numbers, 0) && numbers.All(double.IsFinite) ? GraphValue.Of(numbers.ToArray()) : null;
            case ScriptTypeKind.Coded:
                return clr.IsPrimitive && clr != typeof(char) && Number(value) is double coded ? GraphValue.Of(coded) : null;
        }
        return value is string text && type == ScriptTypes.String ? GraphValue.Of(text) : null;
    }

    // A float by its shortest form, so a card shows 0.2896 where the field holds it, not the float's widened tail.
    private static double? Number(object value)
    {
        try
        {
            if (value is float single)
                return double.Parse(single.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            return value is bool flag ? (flag ? 1.0 : 0.0) : Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return null;
        }
    }

    // A struct's numbers, nested structs walked in declaration order, the way its lanes are laid out.
    private static bool Flatten(object value, Type type, List<double> into, int depth)
    {
        if (depth > 4)
            return false;
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.MetadataToken))
        {
            var inner = field.GetValue(value);
            if (inner == null)
                return false;
            if (field.FieldType.IsPrimitive)
            {
                if (Number(inner) is not double number)
                    return false;
                into.Add(number);
            }
            else if (field.FieldType.IsValueType && !field.FieldType.IsEnum)
            {
                if (!Flatten(inner, field.FieldType, into, depth + 1))
                    return false;
            }
            else
            {
                return false;
            }
        }
        return true;
    }

    private static bool Resolves(string type, string signature)
    {
        var registry = ScriptEnvironment.Registry;
        return registry.TryGetDefinition(type, out var definition) && registry.TryResolve(definition, signature, out _, out _);
    }
}
