// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// A button on its own card that runs a flow by hand, for trying a graph out while it is open. Whoever
// pressed it comes out as "caller". -xlinka
[Node("Flow/Call", Description = "A button on its own card that runs a flow by hand, for trying a graph out while it is open.",
    Keywords = "button, run, test, trigger manually, fire")]
public sealed class Call : EventNode<CallPressed>
{
}
