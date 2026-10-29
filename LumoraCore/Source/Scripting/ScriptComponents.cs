// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Lumora.Core.Components;
using Lumora.Core.Networking.Sync;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// What the component nodes reach on a component, all of it what a person reaches in the inspector: the type it
// shows as, its value fields by member name, and the methods opened to scripts with [ScriptMethod]. No private
// state, no lists, no reference fields (Reference Named has those, under the reference rules), nothing hidden
// from the inspector, nothing host-only and nothing under Avatar Protection. Every write still goes through
// ScriptWrites and the gate. -xlinka
internal static class ScriptComponents
{
    private static readonly ConcurrentDictionary<Type, string[]> Names = new();
    private static readonly ConcurrentDictionary<Type, Type?> ValueTypes = new();
    private static readonly ConcurrentDictionary<Type, Dictionary<string, MethodInfo>> Methods = new();
    private static readonly ConcurrentDictionary<Type, bool> Hidden = new();

    // The names a component goes by: its type's plain name and, for a generic one, as the browser shows it
    // ("ValueVariable<float>", and the runtime spelling "ValueVariable<Single>" older scripts use), then the same
    // for each type it is built on short of Component, so "Collider" finds every collider.
    public static bool IsNamed(Component component, string type)
    {
        foreach (var name in Names.GetOrAdd(component.GetType(), NamesOf))
        {
            if (name == type)
                return true;
        }
        return false;
    }

    public static string TypeName(Component component) => ComponentLibrary.DisplayName(component.GetType());

    // A component a script may find: one the inspector lists.
    public static bool Findable(ScriptContext ctx, Component? component)
        => component != null && !component.IsDestroyed && !Hidden.GetOrAdd(component.GetType(), IsHidden) && ScriptReads.Visible(ctx, component);

    private static bool IsHidden(Type type) => type.GetCustomAttribute<HideInInspectorAttribute>() != null;

    private static string[] NamesOf(Type type)
    {
        var names = new List<string>();
        for (var current = type; current != null && current != typeof(Component); current = current.BaseType)
        {
            var plain = current.Name;
            int tick = plain.IndexOf('`');
            names.Add(tick >= 0 ? plain[..tick] : plain);
            if (current.IsGenericType)
            {
                names.Add(ComponentLibrary.DisplayName(current));
                names.Add(ComponentLibrary.DisplayName(current, clrNames: true));
            }
        }
        return names.ToArray();
    }

    // The member at index when it is a value field a script may name, with the type its value is.
    public static bool IsValueField(Component component, int index, out IField field, out Type valueType)
    {
        field = null!;
        valueType = null!;
        if (component is AvatarProtection || component.GetSyncMember(index) is not IField member || member is ISyncRef
            || member is ConflictingSyncElement { IsHostOnly: true }
            || component.GetSyncMemberFieldInfo(index).GetCustomAttribute<HideInInspectorAttribute>() != null)
            return false;
        if (ValueTypes.GetOrAdd(member.GetType(), ValueTypeOf) is not { } type)
            return false;
        field = member;
        valueType = type;
        return true;
    }

    public static int Find(Component component, string name)
    {
        for (int i = 0; i < component.SyncMemberCount; i++)
        {
            if (component.GetSyncMemberName(i) == name)
                return i;
        }
        return -1;
    }

    private static Type? ValueTypeOf(Type memberType)
    {
        foreach (var face in memberType.GetInterfaces())
        {
            if (face.IsGenericType && face.GetGenericTypeDefinition() == typeof(IField<>))
                return face.GetGenericArguments()[0];
        }
        return null;
    }

    // The method a script may call on a component by this name, when its type opens one.
    public static MethodInfo? Method(Component component, string name)
        => Methods.GetOrAdd(component.GetType(), MethodsOf).GetValueOrDefault(name);

    public static IEnumerable<string> MethodNames(Component component) => Methods.GetOrAdd(component.GetType(), MethodsOf).Keys;

    private static Dictionary<string, MethodInfo> MethodsOf(Type type)
    {
        var found = new SortedDictionary<string, MethodInfo>(StringComparer.Ordinal);
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.IsSpecialName || method.ReturnType != typeof(void) || method.GetParameters().Length != 0
                || method.GetCustomAttribute<ScriptMethodAttribute>() == null)
                continue;
            found.TryAdd(method.Name, method);
        }
        return new Dictionary<string, MethodInfo>(found, StringComparer.Ordinal);
    }
}
