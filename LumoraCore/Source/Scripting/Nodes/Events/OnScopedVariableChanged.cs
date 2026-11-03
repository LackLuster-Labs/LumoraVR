// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// A named variable the source can reach changed value, came, or went: in the nearest scope that takes
// unnamed variables and the nearest scope of each name above it. A scope that comes or goes above the
// source, the object moving into or out of one, brings or takes all its variables. Looked at once a frame
// on whichever machine the script runs, once per variable however often it changed, and only in scopes
// that moved since the last look. -xlinka
[Node("Variables/Scoped/On Scoped Variable Changed", Description = "A named variable the source can reach changed value, came, or went: in the nearest scope that takes unnamed variables and the nearest scope of each name above it.")]
[SourcedEvent]
public sealed class OnScopedVariableChanged : EventNode<ScopedVariableChanged>
{
}
