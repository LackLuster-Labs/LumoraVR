// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// The overload a node picked from the browser starts as, when that is not the one declared first: the
// number form of a node whose vector forms came first. Graphs saved without a "sig" still open as the first
// declared, so marking one changes no saved node. One overload of a node at most.
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class PreferredAttribute : Attribute
{
}
