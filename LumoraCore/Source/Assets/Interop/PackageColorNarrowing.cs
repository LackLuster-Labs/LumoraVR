// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Lumora.Core.Math;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// WHICH OF A PACKAGE'S COLOURS ARE PLAIN COLOURS HERE.
//
// A package keeps every colour in one high-range type. Ours splits them: materials and particles take colorHDR,
// while interface tints, text, buttons and lights take color. A generic component closed over the package's
// colour therefore comes in as colorHDR, and a colorHDR drive cannot take a color field at all; on a world's
// interface that is most of them, from a smoothed button tint to every copy onto an image.
//
// So before the build the package's colour plumbing is read as a graph. Each generic component over the
// package colour is a node; a reference from one into another's colour member, and a variable name two of them
// share, joins them. A group whose colour links all land on color fields, and that nothing fixed at the high
// range touches (a material colour, a node graph reaching in, a variable a node graph names), closes over
// color instead. A group that has to stay at the high range keeps it, except at its last step: a copy or a
// variable driver whose own target is a color field becomes the converting one of ours, so a scene colour a
// node graph keeps in a variable still reaches the lights and tints it was meant for. -xlinka
public static partial class PackageMemberMapper
{
    // The components built as something else than their package colour argument would make them, by id, with the
    // type they are built as: the form over color, or a converter.
    public static Dictionary<string, Type> PlainColourComponents(DataTreeDictionary graph, IReadOnlyList<string> typeNames)
        => ColourPlumbing.Find(graph, typeNames);

    private static class ColourPlumbing
    {
        private const int NoColour = 0;
        private const int PlainColour = 1;
        private const int HighColour = 2;

        public static Dictionary<string, Type> Find(DataTreeDictionary graph, IReadOnlyList<string> typeNames)
        {
            var narrowed = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            var resolved = new PackageTypeMap.Resolution[typeNames.Count];
            var hasColour = new bool[typeNames.Count];
            bool any = false;
            for (int i = 0; i < typeNames.Count; i++)
            {
                resolved[i] = PackageTypeMap.Resolve(typeNames[i]);
                hasColour[i] = resolved[i].Fate == PackageTypeFate.Mapped && resolved[i].Target != null && ColourArgument.IsMatch(typeNames[i]);
                any |= hasColour[i];
            }
            if (!any)
                return narrowed;

            var components = new Dictionary<string, (int Type, DataTreeDictionary Data)>(StringComparer.OrdinalIgnoreCase);
            var members = new Dictionary<string, (string Owner, string Member)>(StringComparer.OrdinalIgnoreCase);
            if (graph.TryGetNode("Object") is DataTreeDictionary root)
                IndexSlot(root, components, members);
            foreach (var listName in new[] { "Assets", "Dependencies" })
            {
                if (graph.TryGetNode(listName) is DataTreeList list)
                {
                    foreach (var entry in list.Children)
                    {
                        if (entry is DataTreeDictionary worker)
                            IndexComponent(worker, components, members);
                    }
                }
            }

            var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var flags = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, (type, _)) in components)
            {
                if (hasColour[type])
                    parent[id] = id;
            }

            string Root(string id)
            {
                while (!string.Equals(parent[id], id, StringComparison.OrdinalIgnoreCase))
                    id = parent[id] = parent[parent[id]];
                return id;
            }
            void Union(string a, string b)
            {
                a = Root(a);
                b = Root(b);
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                    return;
                parent[a] = b;
                flags[b] = flags.GetValueOrDefault(b) | flags.GetValueOrDefault(a);
            }
            void Mark(string id, int colour) => flags[Root(id)] = flags.GetValueOrDefault(Root(id)) | colour;

            // Strings a node graph holds, for the variable names it reaches by name.
            var graphStrings = new HashSet<string>(StringComparer.Ordinal);
            var variables = new Dictionary<string, string>(StringComparer.Ordinal);
            // What each one's own drive lands on, for the last step of a group that stays at the high range.
            var drives = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var (id, (type, data)) in components)
            {
                bool candidate = hasColour[type];
                bool script = PackageTypeMap.IsVisualScripting(typeNames[type]);
                var ours = resolved[type].Fate == PackageTypeFate.Mapped ? resolved[type].Target : null;

                if (candidate && (data.TryGetNode("VariableName") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: string name }
                    && name.Length > 0)
                {
                    string key = LastSegment(name);
                    if (variables.TryGetValue(key, out var first))
                        Union(id, first);
                    else
                        variables[key] = id;
                }

                foreach (var (memberName, node) in data.Children)
                {
                    if (memberName == "ID")
                        continue;
                    int own = candidate || ours != null ? ColourOf(ours, memberName) : NoColour;
                    VisitStrings(node, value =>
                    {
                        if (script)
                            graphStrings.Add(value);
                        if (!members.TryGetValue(value, out var target) || !components.TryGetValue(target.Owner, out var other))
                            return;
                        bool otherCandidate = hasColour[other.Type];
                        var otherOurs = resolved[other.Type].Fate == PackageTypeFate.Mapped ? resolved[other.Type].Target : null;
                        int theirs = ColourOf(otherOurs, target.Member);
                        if (candidate && otherCandidate)
                        {
                            if (own == HighColour && theirs == HighColour)
                                Union(id, target.Owner);
                        }
                        else if (candidate)
                        {
                            if (own == HighColour && theirs != NoColour)
                            {
                                Mark(id, theirs);
                                if (memberName == "Target")
                                    drives[id] = drives.GetValueOrDefault(id) | theirs;
                            }
                        }
                        else if (otherCandidate && theirs == HighColour)
                        {
                            // A node graph reaching into the member is typed at the high range; a component of ours
                            // says by its own member.
                            if (script)
                                Mark(target.Owner, HighColour);
                            else if (own != NoColour)
                                Mark(target.Owner, own);
                        }
                    });
                }
            }

            foreach (var value in graphStrings)
            {
                if (variables.TryGetValue(LastSegment(value), out var holder))
                    Mark(holder, HighColour);
            }

            var plainTypes = new Type?[typeNames.Count];
            foreach (var id in parent.Keys)
            {
                int type = components[id].Type;
                if (flags.GetValueOrDefault(Root(id)) != PlainColour)
                {
                    if (drives.GetValueOrDefault(id) == PlainColour && Converter(resolved[type].Target) is { } converter)
                        narrowed[id] = converter;
                    continue;
                }
                plainTypes[type] ??= PackageTypeMap.PlainColourType(typeNames[type]);
                if (plainTypes[type] is { } plain)
                    narrowed[id] = plain;
            }
            return narrowed;
        }

        // The converting form of a high-range copy or variable driver, which keeps its members' names.
        private static Type? Converter(Type? closed)
        {
            if (closed is not { IsGenericType: true } || closed.GetGenericArguments()[0] != typeof(colorHDR))
                return null;
            var definition = closed.GetGenericTypeDefinition();
            if (definition == typeof(Components.Utility.CopyValue<>))
                return typeof(Components.Utility.ColorFromHdr);
            if (definition == typeof(Components.Variables.ValueVariableDriver<>))
                return typeof(Components.Variables.ColorVariableDriver);
            return null;
        }

        // The package's colour as a generic argument, anywhere in the list.
        private static readonly Regex ColourArgument = new(@"(?<=[<,]\s*)colorX(?=\s*[,>])", RegexOptions.CultureInvariant);

        private static string LastSegment(string name)
        {
            int slash = name.LastIndexOf('/');
            return slash >= 0 ? name[(slash + 1)..] : name;
        }

        private static void IndexSlot(DataTreeDictionary slot, Dictionary<string, (int, DataTreeDictionary)> components,
            Dictionary<string, (string, string)> members)
        {
            if (slot.TryGetNode("Components") is { } holder)
            {
                var list = holder as DataTreeList ?? (holder as DataTreeDictionary)?.TryGetNode("Data") as DataTreeList;
                if (list != null)
                {
                    foreach (var entry in list.Children)
                    {
                        if (entry is DataTreeDictionary worker)
                            IndexComponent(worker, components, members);
                    }
                }
            }
            if (slot.TryGetNode("Children") is DataTreeList children)
            {
                foreach (var child in children.Children)
                {
                    if (child is DataTreeDictionary childSlot)
                        IndexSlot(childSlot, components, members);
                }
            }
        }

        private static void IndexComponent(DataTreeDictionary worker, Dictionary<string, (int, DataTreeDictionary)> components,
            Dictionary<string, (string, string)> members)
        {
            if (worker.TryGetNode("Type") is not DataTreeValue { Value: IConvertible typeValue } || typeValue is string
                || worker.TryGetNode("Data") is not DataTreeDictionary data
                || data.TryGetNode("ID") is not DataTreeValue { Value: string id } || id.Length == 0)
                return;
            components[id] = (Convert.ToInt32(typeValue, System.Globalization.CultureInfo.InvariantCulture), data);
            foreach (var (name, node) in data.Children)
            {
                if (node is DataTreeDictionary member && member.TryGetNode("ID") is DataTreeValue { Value: string memberId } && memberId.Length > 0)
                {
                    members[memberId] = (id, name);
                    // A copy into one entry of a list, or into a field of a record in it (a gradient's stop colour),
                    // is a copy into the list: the entries' ids answer for it.
                    if (member.TryGetNode("Data") is DataTreeList entries)
                    {
                        foreach (var entry in entries.Children)
                        {
                            if (entry is not DataTreeDictionary item)
                                continue;
                            if (item.TryGetNode("ID") is DataTreeValue { Value: string entryId } && entryId.Length > 0)
                                members.TryAdd(entryId, (id, name));
                            foreach (var inner in (item.TryGetNode("Data") as DataTreeDictionary ?? item).Children.Values)
                            {
                                if (inner is DataTreeDictionary field && field.TryGetNode("ID") is DataTreeValue { Value: string fieldId } && fieldId.Length > 0)
                                    members.TryAdd(fieldId, (id, name));
                            }
                        }
                    }
                }
            }
        }

        private static void VisitStrings(DataTreeNode node, Action<string> visit, int depth = 0)
        {
            if (depth > 8)
                return;
            switch (node)
            {
                case DataTreeValue { Value: string text } when text.Length > 0:
                    visit(text);
                    break;
                case DataTreeDictionary dictionary:
                    foreach (var (key, child) in dictionary.Children)
                    {
                        if (key != "ID")
                            VisitStrings(child, visit, depth + 1);
                    }
                    break;
                case DataTreeList list:
                    foreach (var child in list.Children)
                        VisitStrings(child, visit, depth + 1);
                    break;
            }
        }

        // Which colour our member under the package's member name holds, if any.
        private static int ColourOf(Type? ours, string theirMember)
        {
            if (ours == null)
                return NoColour;
            // Tint drivers ride on an interaction element there and are ColorDrivers beside it here: plain colour.
            if (theirMember == "ColorDrivers" && typeof(Helio.UI.InteractionElement).IsAssignableFrom(ours))
                return PlainColour;
            var field = ours.GetField(ResolveMemberName(ours, theirMember), BindingFlags.Public | BindingFlags.Instance);
            return field == null ? NoColour : ColourIn(field.FieldType, 0);
        }

        private static int ColourIn(Type type, int depth)
        {
            if (type == typeof(colorHDR))
                return HighColour;
            if (type == typeof(color))
                return PlainColour;
            if (depth > 4)
                return NoColour;
            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    int found = ColourIn(argument, depth + 1);
                    if (found != NoColour)
                        return found;
                }
            }
            return type.BaseType is { } baseType && baseType != typeof(object) ? ColourIn(baseType, depth + 1) : NoColour;
        }
    }
}
