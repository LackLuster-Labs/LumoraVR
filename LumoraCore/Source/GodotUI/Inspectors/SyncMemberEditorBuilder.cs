// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Reflection;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.GodotUI.Inspectors;

/// <summary>
/// How a sync member should be edited in the inspector UI.
/// </summary>
public enum FieldEditorKind
{
    Bool,
    Int,
    Float,
    Float2,
    Float3,
    Float4,
    Color,
    ColorHDR,
    Quaternion,
    String,
    Enum,
    SyncRef,
    SyncList,
    Unsupported,
}

/// <summary>
/// Describes one inspectable sync member and how the Godot side should render it.
/// </summary>
public sealed class FieldEditorInfo
{
    public string Name { get; init; } = "";
    public FieldEditorKind Kind { get; init; }
    public bool IsReadOnly { get; init; }
    /// <summary>Human-readable display string for the current value.</summary>
    public string CurrentValueString { get; init; } = "";
    public object? RawValue { get; init; }
    public ISyncMember Member { get; init; } = null!;
    /// <summary>Nesting depth — reserved for future nested SyncObject support.</summary>
    public int Depth { get; init; }

    // ── Enum ─────────────────────────────────────────────────────────────────
    /// <summary>All valid enum names when Kind == Enum.</summary>
    public string[]? EnumNames { get; init; }

    // ── Float / Int — optional slider range ──────────────────────────────────
    public float? RangeMin { get; init; }
    public float? RangeMax { get; init; }

    // ── SyncRef ──────────────────────────────────────────────────────────────
    /// <summary>Display name of the referenced object (slot name, component type, etc.).</summary>
    public string? RefTargetName { get; init; }
    /// <summary>C# type name of the referenced object.</summary>
    public string? RefTargetTypeName { get; init; }

    // ── SyncList ─────────────────────────────────────────────────────────────
    public int ListCount { get; init; }
}

/// <summary>
/// Reflects over a Component's sync members and produces typed FieldEditorInfo descriptors
/// that the Godot inspector scene can consume to build the correct editor widget per field.
/// </summary>
public static class SyncMemberEditorBuilder
{
    private static readonly BindingFlags _declaredFlags =
        BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.DeclaredOnly;

    // ── Public entry point ───────────────────────────────────────────────────

    /// <summary>
    /// Build descriptors for every sync member exposed by the component.
    /// Walks the inheritance chain down to (but not including) Component when
    /// showInherited is false.
    /// </summary>
    public static IReadOnlyList<FieldEditorInfo> Build(Component component, bool showInherited = false)
    {
        var result = new List<FieldEditorInfo>();
        if (component == null) return result;

        var seen = new HashSet<ISyncMember>(ReferenceEqualityComparer.Instance);
        var stopType = showInherited ? typeof(object) : typeof(Component);

        for (var type = component.GetType(); type != null && type != stopType; type = type.BaseType)
        {
            CollectFromProperties(component, type, result, seen);
            CollectFromFields(component, type, result, seen);
        }

        return result;
    }

    // ── Collection helpers ───────────────────────────────────────────────────

    private static void CollectFromProperties(
        object target, Type type, List<FieldEditorInfo> result, HashSet<ISyncMember> seen)
    {
        foreach (var prop in type.GetProperties(_declaredFlags))
        {
            if (!typeof(ISyncMember).IsAssignableFrom(prop.PropertyType) || !prop.CanRead)
                continue;

            ISyncMember? member;
            try { member = prop.GetValue(target) as ISyncMember; }
            catch { continue; }

            if (member == null || !seen.Add(member)) continue;
            result.Add(BuildInfo(prop.Name, member));
        }
    }

    private static void CollectFromFields(
        object target, Type type, List<FieldEditorInfo> result, HashSet<ISyncMember> seen)
    {
        foreach (var field in type.GetFields(_declaredFlags))
        {
            if (!typeof(ISyncMember).IsAssignableFrom(field.FieldType))
                continue;

            ISyncMember? member;
            try { member = field.GetValue(target) as ISyncMember; }
            catch { continue; }

            if (member == null || !seen.Add(member)) continue;
            result.Add(BuildInfo(member.Name ?? field.Name, member));
        }
    }

    // ── Descriptor builder ───────────────────────────────────────────────────

    /// <summary>
    /// Build a descriptor for a single named sync member.
    /// Public so the Godot side can call it for individual fields (e.g. list elements).
    /// </summary>
    public static FieldEditorInfo BuildInfo(string name, ISyncMember member)
    {
        if (member is IField field)
            return BuildFromField(name, field, member);

        if (member is ISyncRef syncRef)
            return BuildFromRef(name, syncRef, member);

        if (member is ISyncList syncList)
        {
            return new FieldEditorInfo
            {
                Name = name,
                Kind = FieldEditorKind.SyncList,
                Member = member,
                ListCount = syncList.Count,
                CurrentValueString = $"[{syncList.Count} items]",
            };
        }

        return new FieldEditorInfo
        {
            Name = name,
            Kind = FieldEditorKind.Unsupported,
            Member = member,
            CurrentValueString = member.GetValueAsObject()?.ToString() ?? "?",
        };
    }

    private static FieldEditorInfo BuildFromField(string name, IField field, ISyncMember member)
    {
        var kind = ClassifyType(field.ValueType);
        var raw = member.GetValueAsObject();

        string[]? enumNames = null;
        if (kind == FieldEditorKind.Enum)
            enumNames = Enum.GetNames(field.ValueType);

        return new FieldEditorInfo
        {
            Name = name,
            Kind = kind,
            IsReadOnly = !field.CanWrite,
            RawValue = raw,
            CurrentValueString = FormatValue(raw, kind),
            Member = member,
            EnumNames = enumNames,
        };
    }

    private static FieldEditorInfo BuildFromRef(string name, ISyncRef syncRef, ISyncMember member)
    {
        string? refTargetName = null;
        string? refTypeName = null;

        var target = syncRef.Target;
        if (target != null)
        {
            refTypeName = target.GetType().Name;
            refTargetName = target is Slot slot ? (slot.Name?.Value ?? "Slot") : refTypeName;
        }

        return new FieldEditorInfo
        {
            Name = name,
            Kind = FieldEditorKind.SyncRef,
            Member = member,
            RefTargetName = refTargetName,
            RefTargetTypeName = refTypeName,
            CurrentValueString = refTargetName != null
                ? $"\u2192 {refTargetName} ({refTypeName})"
                : "null",
        };
    }

    // ── Type classification ──────────────────────────────────────────────────

    private static FieldEditorKind ClassifyType(Type t)
    {
        if (t == typeof(bool)) return FieldEditorKind.Bool;
        if (t == typeof(string)) return FieldEditorKind.String;
        if (t == typeof(float2)) return FieldEditorKind.Float2;
        if (t == typeof(float3)) return FieldEditorKind.Float3;
        if (t == typeof(float4)) return FieldEditorKind.Float4;
        if (t == typeof(color)) return FieldEditorKind.Color;
        if (t == typeof(colorHDR)) return FieldEditorKind.ColorHDR;
        if (t == typeof(floatQ)) return FieldEditorKind.Quaternion;
        if (t.IsEnum) return FieldEditorKind.Enum;

        if (t == typeof(float) || t == typeof(double))
            return FieldEditorKind.Float;

        if (t == typeof(int) || t == typeof(long) || t == typeof(short) ||
            t == typeof(byte) || t == typeof(uint) || t == typeof(ulong) ||
            t == typeof(ushort) || t == typeof(sbyte))
            return FieldEditorKind.Int;

        return FieldEditorKind.Unsupported;
    }

    // ── Value formatting ─────────────────────────────────────────────────────

    private static string FormatValue(object? raw, FieldEditorKind kind)
    {
        if (raw == null) return "null";
        return kind switch
        {
            FieldEditorKind.Float => ((IConvertible)raw).ToSingle(null).ToString("G6"),
            FieldEditorKind.Float2 => raw is float2 v2 ? $"({v2.x:G5}, {v2.y:G5})" : raw.ToString()!,
            FieldEditorKind.Float3 => raw is float3 v3 ? $"({v3.x:G5}, {v3.y:G5}, {v3.z:G5})" : raw.ToString()!,
            FieldEditorKind.Float4 => raw is float4 v4 ? $"({v4.x:G5}, {v4.y:G5}, {v4.z:G5}, {v4.w:G5})" : raw.ToString()!,
            FieldEditorKind.Color => raw is color c ? $"#{F2X(c.r)}{F2X(c.g)}{F2X(c.b)}{F2X(c.a)}  ({c.r:F3}, {c.g:F3}, {c.b:F3}, {c.a:F3})" : raw.ToString()!,
            FieldEditorKind.ColorHDR => raw is colorHDR h ? $"HDR ({h.r:F3}, {h.g:F3}, {h.b:F3}, {h.a:F3})" : raw.ToString()!,
            FieldEditorKind.Quaternion => raw is floatQ q ? $"({q.x:F3}, {q.y:F3}, {q.z:F3}, {q.w:F3})" : raw.ToString()!,
            _ => raw.ToString() ?? "?",
        };
    }

    private static string F2X(float f)
    {
        int v = System.Math.Clamp((int)MathF.Round(f * 255f), 0, 255);
        return v.ToString("X2");
    }

    // ── Typed write helpers ─────────────────────────────────────────────────
    // Called by the Godot UI after the user finishes editing a control.

    /// <summary>
    /// Parse a raw string from a text input and write it to the field.
    /// Handles: bool, int, long, float, double, string, enum.
    /// Returns false if the field is read-only, the type is unsupported, or parsing fails.
    /// </summary>
    public static bool TrySetFromString(ISyncMember member, string rawInput)
    {
        if (member is not IField field || !field.CanWrite) return false;
        try
        {
            var t = field.ValueType;
            object? parsed =
                t == typeof(bool) ? bool.Parse(rawInput) :
                t == typeof(int) ? int.Parse(rawInput) :
                t == typeof(long) ? long.Parse(rawInput) :
                t == typeof(short) ? short.Parse(rawInput) :
                t == typeof(byte) ? byte.Parse(rawInput) :
                t == typeof(uint) ? uint.Parse(rawInput) :
                t == typeof(ulong) ? ulong.Parse(rawInput) :
                t == typeof(float) ? float.Parse(rawInput, System.Globalization.CultureInfo.InvariantCulture) :
                t == typeof(double) ? double.Parse(rawInput, System.Globalization.CultureInfo.InvariantCulture) :
                t == typeof(string) ? rawInput :
                t.IsEnum ? Enum.Parse(t, rawInput) :
                (object?)null;

            if (parsed == null && t != typeof(string)) return false;
            field.BoxedValue = parsed!;
            return true;
        }
        catch { return false; }
    }

    public static void SetBool(ISyncMember member, bool value)
    {
        if (member is IField<bool> f) f.Value = value;
        else if (member is IField field && field.CanWrite) field.BoxedValue = value;
    }

    public static void SetInt(ISyncMember member, int value)
    {
        if (member is IField<int> fi) fi.Value = value;
        else if (member is IField<long> fl) fl.Value = value;
        else if (member is IField<short> fs) fs.Value = (short)value;
        else if (member is IField<byte> fb) fb.Value = (byte)value;
        else if (member is IField<uint> fu) fu.Value = (uint)value;
        else if (member is IField<ulong> fU) fU.Value = (ulong)value;
        else if (member is IField f && f.CanWrite)
            f.BoxedValue = Convert.ChangeType(value, f.ValueType);
    }

    public static void SetFloat(ISyncMember member, float value)
    {
        if (member is IField<float> ff) ff.Value = value;
        else if (member is IField<double> fd) fd.Value = value;
        else if (member is IField f && f.CanWrite)
            f.BoxedValue = Convert.ChangeType(value, f.ValueType);
    }

    public static void SetFloat2(ISyncMember member, float2 value)
    {
        if (member is IField<float2> f) f.Value = value;
        else if (member is IField field && field.CanWrite) field.BoxedValue = value;
    }

    public static void SetFloat3(ISyncMember member, float3 value)
    {
        if (member is IField<float3> f) f.Value = value;
        else if (member is IField field && field.CanWrite) field.BoxedValue = value;
    }

    public static void SetFloat4(ISyncMember member, float4 value)
    {
        if (member is IField<float4> f) f.Value = value;
        else if (member is IField field && field.CanWrite) field.BoxedValue = value;
    }

    public static void SetColor(ISyncMember member, color value)
    {
        if (member is IField<color> f) f.Value = value;
        else if (member is IField field && field.CanWrite) field.BoxedValue = value;
    }

    public static void SetColorHDR(ISyncMember member, colorHDR value)
    {
        if (member is IField<colorHDR> f) f.Value = value;
        else if (member is IField field && field.CanWrite) field.BoxedValue = value;
    }

    public static void SetQuaternion(ISyncMember member, floatQ value)
    {
        if (member is IField<floatQ> f) f.Value = value;
        else if (member is IField field && field.CanWrite) field.BoxedValue = value;
    }

    public static void SetEnum(ISyncMember member, string enumName)
    {
        if (member is not IField field || !field.CanWrite || !field.ValueType.IsEnum) return;
        try { field.BoxedValue = Enum.Parse(field.ValueType, enumName); } catch { }
    }

    public static void SetString(ISyncMember member, string value)
    {
        if (member is IField<string> f) f.Value = value;
        else if (member is IField field && field.CanWrite) field.BoxedValue = value;
    }
}
