// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The name a component's type shows as in the component browser, the one Find Component takes. Empty for a
// component the script cannot see.
[Node("World/Components/Component Type", Capability.WorldRead, Description = "The name a component's type shows as in the component browser, the one Find Component takes.")]
[Evaluates(Evaluation.Tree)]
public sealed class ComponentTypeName : ReadNode
{
    [return: Port("type")]
    public static string Run(ScriptContext ctx, Component? component)
        => ScriptReads.Visible(ctx, component) ? ScriptComponents.TypeName(component!) : string.Empty;
}
