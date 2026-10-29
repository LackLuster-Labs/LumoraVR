// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components.Scripting;

public enum ScriptCardHandleKind : byte
{
    Header,
    DataInput,
    DataOutput,
    FlowInput,
    FlowOutput,

    // A Write Value's "var", or a Return's or a call's "fn": dragged onto a Stored Value card to choose what it
    // writes, or onto a Function card to choose the Function.
    Variable,

    // A Material Output's material well: dragged onto anything in the world to paint it with the graph's material.
    Material
}
