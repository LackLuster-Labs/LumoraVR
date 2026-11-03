// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The slot a component sits on. Nothing for a component the script cannot see.
[Node("World/Components/Slot Of", Capability.WorldRead, Description = "The slot a component sits on.",
    Keywords = "component slot, owner slot")]
[Evaluates(Evaluation.Tree)]
public sealed class ComponentSlot : ReadNode
{
    [return: Port("slot")]
    public static Slot? Run(ScriptContext ctx, Component? component)
        => ScriptReads.Visible(ctx, component) ? component!.Slot : null;
}
