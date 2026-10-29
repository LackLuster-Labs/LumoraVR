// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Reflection;
using Lumora.Core.Math;

namespace Lumora.Core;

public enum GenericTypeGroup
{
    Explicit,

    Values,

    WorldElements,

    // Enums only, for a component that makes no sense for a number or a string.
    Enums,
}

// Declares the type arguments a generic component can be attached with.
//
// A generic component class is not attachable by itself, only its closed forms are, so the browser
// and the type table need to know which ones exist up front. Without this the type never appears
// anywhere a person could pick it. -xlinka
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class ComponentGenericTypesAttribute : Attribute
{
    public GenericTypeGroup Group { get; }
    public Type[] ExtraTypes { get; }

    public ComponentGenericTypesAttribute(GenericTypeGroup group, params Type[] extraTypes)
    {
        Group = group;
        ExtraTypes = extraTypes ?? Array.Empty<Type>();
    }

    public ComponentGenericTypesAttribute(params Type[] types)
    {
        Group = GenericTypeGroup.Explicit;
        ExtraTypes = types ?? Array.Empty<Type>();
    }
}

public static class GenericComponentTypes
{
    // Value types offered to generic components. Kept to what BOTH coders handle: a type the save
    // coder cannot write would attach fine and then throw on the first save, and one the sync coder
    // cannot encode would go over the wire as a null marker. -xlinka
    public static readonly Type[] ValueTypes =
    {
        typeof(bool),
        typeof(byte),
        typeof(sbyte),
        typeof(short),
        typeof(ushort),
        typeof(int),
        typeof(uint),
        typeof(long),
        typeof(ulong),
        typeof(float),
        typeof(double),
        typeof(string),
        typeof(Uri),
        typeof(float2),
        typeof(float3),
        typeof(float4),
        typeof(floatQ),
        typeof(color),
        typeof(colorHDR),
        typeof(int4),
        typeof(BoundingBox),
        typeof(float3x3),
        typeof(int2),
        typeof(int3),
        typeof(bool2),
        typeof(bool3),
        typeof(bool4),
        typeof(float2x2),
        typeof(float4x4),
        typeof(Rect),
        typeof(SphericalHarmonicsL2),
        typeof(char),
        typeof(decimal),
        typeof(DateTime),
    };

    // Every enum a component actually declares a Sync field of.
    //
    // Discovered rather than listed, because a hand-written list is wrong the day someone adds an enum
    // and nobody remembers this file. Scanning the Sync fields is also the right FILTER: an enum that no
    // component stores is an enum nothing can drive, and the browser gains nothing by offering it.
    //
    // Both coders take any enum without registration (SyncCoder and DataTreeCoder each branch on
    // IsEnum), so unlike a new value type this needs no coder work at all. -xlinka
    private static Type[]? _enumTypes;
    private static int _enumScanAssemblyCount;

    public static Type[] EnumTypes
    {
        get
        {
            // .NET loads assemblies on demand, so GetAssemblies() returns only what has been touched so
            // far. Caching the first answer forever would permanently miss every enum in an assembly
            // that loaded later. Re-scan when the count moves; it never moves on a warm run, so this
            // costs one integer compare per call in practice. -xlinka
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            if (_enumTypes == null || assemblies.Length != _enumScanAssemblyCount)
            {
                _enumScanAssemblyCount = assemblies.Length;
                _enumTypes = DiscoverSyncedEnums(assemblies);
            }
            return _enumTypes;
        }
    }

    private static Type[] DiscoverSyncedEnums(Assembly[] assemblies)
    {
        var found = new HashSet<Type>();

        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // A partially loadable assembly still has usable types; take those and move on rather
                // than losing every enum in the tree to one bad reference.
                types = Array.FindAll(ex.Types, t => t != null)!;
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type == null || !typeof(Component).IsAssignableFrom(type))
                    continue;

                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    var fieldType = field.FieldType;
                    if (!fieldType.IsGenericType)
                        continue;
                    if (fieldType.GetGenericTypeDefinition() != typeof(Sync<>))
                        continue;

                    var argument = fieldType.GetGenericArguments()[0];
                    if (argument.IsEnum)
                        found.Add(argument);
                }
            }
        }

        var result = new Type[found.Count];
        found.CopyTo(result);
        Array.Sort(result, (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    public static readonly Type[] WorldElementTypes =
    {
        typeof(Slot),
        typeof(User),
    };

    // Empty for a generic component that never declared a set, and for arguments the definition's constraints
    // reject.
    public static IEnumerable<Type> Enumerate(Type definition)
    {
        if (definition == null || !definition.IsGenericTypeDefinition)
            yield break;
        if (definition.GetGenericArguments().Length != 1)
            yield break;

        var declaration = definition.GetCustomAttribute<ComponentGenericTypesAttribute>(inherit: false);
        if (declaration == null)
            yield break;

        foreach (var argument in Candidates(declaration))
        {
            var closed = TryClose(definition, argument);
            if (closed != null)
                yield return closed;
        }
    }

    private static IEnumerable<Type> Candidates(ComponentGenericTypesAttribute declaration)
    {
        switch (declaration.Group)
        {
            case GenericTypeGroup.Values:
                foreach (var type in ValueTypes)
                    yield return type;
                // Enums ride with the value types: an enum IS a value as far as storing, driving,
                // copying and comparing go, and until now not one enum field in the engine could be
                // driven by anything. Components that need arithmetic reject them through their own
                // IsValidGenericType, which TryClose already honours, so the ones that cannot use an
                // enum never offer it. -xlinka
                foreach (var type in EnumTypes)
                    yield return type;
                break;

            case GenericTypeGroup.Enums:
                foreach (var type in EnumTypes)
                    yield return type;
                break;
            case GenericTypeGroup.WorldElements:
                foreach (var type in WorldElementTypes)
                    yield return type;
                break;
        }

        foreach (var type in declaration.ExtraTypes)
            yield return type;
    }

    // MakeGenericType is the constraint checker: it throws for an argument the definition refuses,
    // which beats re-implementing generic constraint resolution here.
    private static Type? TryClose(Type definition, Type argument)
    {
        if (argument == null)
            return null;
        try
        {
            var closed = definition.MakeGenericType(argument);
            return IsDeclaredValid(closed) ? closed : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // Constraints cannot express "this type has arithmetic I implemented", so components that only
    // work for some of a group say so with a static IsValidGenericType. Every generic component in the
    // tree already declares one; until this read it, the browser offered the closed forms it says no to
    // and a person could attach a component that could never do anything. -xlinka
    private static bool IsDeclaredValid(Type closed)
    {
        var property = closed.GetProperty("IsValidGenericType",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (property == null || property.PropertyType != typeof(bool))
            return true;
        try
        {
            return property.GetValue(null) is not bool valid || valid;
        }
        catch (TargetInvocationException)
        {
            return false;
        }
    }
}
