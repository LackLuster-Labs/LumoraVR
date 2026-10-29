// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Reflection;

namespace Lumora.Core;

// Built once from [ComponentCategory] attributes.
public static class ComponentLibrary
{
    public sealed class CategoryNode
    {
        public string Name = "";
        public string Path = "";
        public readonly SortedDictionary<string, CategoryNode> Subcategories = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<Type> Types = new();
    }

    public const string HiddenCategory = "Hidden";

    private static CategoryNode? _root;
    private static readonly object _buildLock = new();

    public static CategoryNode Root
    {
        get
        {
            if (_root == null)
            {
                lock (_buildLock)
                    _root ??= Build();
            }
            return _root;
        }
    }

    public static CategoryNode? GetNode(string path)
    {
        var node = Root;
        if (string.IsNullOrEmpty(path))
            return node;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.Subcategories.TryGetValue(part, out node!))
                return null;
        }
        return node;
    }

    private static CategoryNode Build()
    {
        var root = new CategoryNode();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types!; }
            catch { continue; }

            foreach (var type in types)
            {
                if (type == null || type.IsAbstract || !type.IsPublic)
                    continue;
                if (!typeof(Component).IsAssignableFrom(type))
                    continue;

                string category = type.GetCustomAttribute<ComponentCategoryAttribute>()?.Category ?? "Uncategorized";

                // "Hidden" is a component that exists for the engine's own use and has no business
                // being attachable from the browser - a save-file placeholder, say. Everything else
                // without a category still lands in Uncategorized. -xlinka
                if (category == HiddenCategory)
                    continue;

                var node = root;
                foreach (var part in category.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!node.Subcategories.TryGetValue(part, out var child))
                    {
                        child = new CategoryNode { Name = part, Path = node.Path.Length == 0 ? part : node.Path + "/" + part };
                        node.Subcategories[part] = child;
                    }
                    node = child;
                }

                // An open generic is listed ONCE and the type argument is chosen in a second step.
                //
                // It used to expand into one browser row per closed form, which put 2200 rows into the
                // tree across the generic components and meant every type added to the table cost
                // another row on each of them. A generic that declares no usable argument still stays
                // out entirely, which is what the enumeration is checked for here. -xlinka
                if (type.IsGenericTypeDefinition)
                {
                    foreach (var _ in GenericComponentTypes.Enumerate(type))
                    {
                        node.Types.Add(type);
                        break;
                    }
                    continue;
                }

                node.Types.Add(type);
            }
        }

        SortTypes(root);
        return root;
    }

    private static void SortTypes(CategoryNode node)
    {
        // Sorted by display name: every closed form of one generic shares the same Type.Name, so
        // sorting on that would leave them in whatever order reflection produced.
        node.Types.Sort((a, b) => string.CompareOrdinal(DisplayName(a), DisplayName(b)));
        foreach (var child in node.Subcategories.Values)
            SortTypes(child);
    }

    // Built-in types go by the names people type ("float", not "Single"): searching the type picker for float
    // found every float type but float itself. clrNames gives the runtime names, for what was matched on before.
    public static string DisplayName(Type type, bool clrNames = false)
    {
        if (type == null)
            return "";
        if (!type.IsGenericType)
            return clrNames ? type.Name : KeywordName(type) ?? type.Name;

        var name = type.Name;
        int tick = name.IndexOf('`');
        if (tick >= 0)
            name = name[..tick];

        var args = type.GetGenericArguments();
        var argNames = new string[args.Length];
        for (int i = 0; i < args.Length; i++)
            argNames[i] = DisplayName(args[i], clrNames);
        return $"{name}<{string.Join(", ", argNames)}>";
    }

    private static string? KeywordName(Type type)
    {
        if (type == typeof(float)) return "float";
        if (type == typeof(double)) return "double";
        if (type == typeof(decimal)) return "decimal";
        if (type == typeof(int)) return "int";
        if (type == typeof(uint)) return "uint";
        if (type == typeof(long)) return "long";
        if (type == typeof(ulong)) return "ulong";
        if (type == typeof(short)) return "short";
        if (type == typeof(ushort)) return "ushort";
        if (type == typeof(byte)) return "byte";
        if (type == typeof(sbyte)) return "sbyte";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(char)) return "char";
        if (type == typeof(string)) return "string";
        return null;
    }
}
