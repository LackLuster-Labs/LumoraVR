// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components;
using Lumora.Core.Persistence;

namespace Lumora.Core.Security;

// The seals in a saved tree, read without loading it: for a save that never goes through a world, such as keeping
// something a friend sent. Only the owners are read; whether a seal checks out is the loaded seal's business.
public static class SealTree
{
    private static readonly string SealType = typeof(AvatarProtection).FullName!;
    // What saves from before the rename call it.
    private const string OldSealType = "Lumora.Core.Components.ItemSeal";

    public static HashSet<string> OwnersIn(DataTreeNode? tree)
    {
        var owners = new HashSet<string>(StringComparer.Ordinal);
        Walk(tree, owners);
        return owners;
    }

    private static void Walk(DataTreeNode? node, HashSet<string> owners)
    {
        switch (node)
        {
            case DataTreeDictionary dictionary:
                if (dictionary.TryGetNode("Type") is DataTreeValue { Value: string type } && (type == SealType || type == OldSealType)
                    && dictionary.TryGetDictionary("Data") is { } data)
                {
                    // An unsigned seal is inert wherever it turns up.
                    if (Read(data, nameof(AvatarProtection.Signature)).Length > 0)
                        owners.Add(Read(data, nameof(AvatarProtection.OwnerId)));
                    return;
                }
                foreach (var child in dictionary.Children.Values)
                    Walk(child, owners);
                break;
            case DataTreeList list:
                foreach (var child in list.Children)
                    Walk(child, owners);
                break;
        }
    }

    // A field that cannot be read is empty: an owner nobody's account matches.
    private static string Read(DataTreeDictionary data, string member)
    {
        if (data.TryGetNode(member) is not { } field)
            return string.Empty;
        try
        {
            return DataTreeCoder.Decode<string>(field) ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
