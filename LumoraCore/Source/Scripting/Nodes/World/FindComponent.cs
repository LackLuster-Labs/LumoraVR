// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The first component on slot whose type goes by this name, the way the component browser names it: TextRenderer,
// Collider for any collider, ValueVariable<float> for one closed form. With children it looks under slot too, level
// by level, slot itself first. Nothing when there is none the script may see, or the type is empty. Components the
// inspector does not list are never found.
[Node("World/Components/Find Component", Capability.WorldRead, Description = "The first component on slot whose type goes by this name, the way the component browser names it: TextRenderer, Collider for any collider, ValueVariable<float> for one closed form.",
    Keywords = "component by type, find script")]
[Evaluates(Evaluation.Tree)]
public sealed class FindComponent : ReadNode
{
    [return: Port("component")]
    public static Component? Run(ScriptContext ctx, Slot? slot, string? type, bool children = false)
    {
        if (!ScriptReads.Visible(ctx, slot) || string.IsNullOrEmpty(type))
            return null;
        var level = new List<Slot> { slot! };
        var next = new List<Slot>();
        while (level.Count > 0)
        {
            foreach (var current in level)
            {
                foreach (var component in current.Components)
                {
                    if (ScriptComponents.Findable(ctx, component) && ScriptComponents.IsNamed(component, type))
                        return component;
                }
                if (!children)
                    return null;
                foreach (var child in current.Children)
                {
                    if (ScriptReads.Visible(ctx, child))
                        next.Add(child);
                }
            }
            (level, next) = (next, level);
            next.Clear();
        }
        return null;
    }
}
