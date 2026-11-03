// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Which way the person here is asking to walk, from whatever their bindings use: a stick, or WASD.
[Node("Input/Actions/Move", Capability.Input, Description = "Which way the person here is asking to walk, from whatever their bindings use: a stick, or WASD.",
    Keywords = "WASD, walk input, movement, locomotion input, action, binding")]
public sealed class MoveInput : ReadNode
{
    [return: Port("value")]
    public static float2 Run(ScriptContext ctx)
        => ScriptInput.Of(ctx)?.Actions?.Locomotion.Move.Value ?? default;
}
