// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// How a node's answer behaves over time. Every node has one: the base class gives it, and a ReadNode can say
// more with [Evaluates]. The compiler and the runtime lean on it to skip work, so a node must never claim to
// change less than it does; one that leaves it out of a read is only ever re-read more often. -xlinka
public enum Evaluation : byte
{
    // Same inputs, same outputs, nothing else looked at. Every ValueNode.
    Pure,

    // Reads something that cannot change while the script runs, such as the slot the script sits on.
    Fixed,

    // Reads the world and answers the same until a slot or component comes, goes or moves, children change
    // order, or a name or tag changes: parents, children, searches by name.
    Tree,

    // Reads the field it is handed and answers the same until that field changes.
    Field,

    // Reads what moves by itself: time, people, positions, input, media. Every ReadNode that says nothing else.
    Changing,

    // Starts a flow when something happens. Every EventNode.
    Event,

    // Changes something when flow arrives. Every ActionNode.
    Action,

    // Lowered by the compiler from its shape: flow, memory, drives, awaits, variables, functions.
    Shaped,
}
