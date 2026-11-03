// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Once, when the script starts. A live graph swap keeps its stored values and does not start again.
[Node("Flow/Events/On Start", Description = "Once, when the script starts.",
    Keywords = "init, initialize, on load, first")]
public sealed class OnStart : EventNode<ScriptStarted>
{
}
