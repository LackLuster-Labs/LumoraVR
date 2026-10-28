// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;

namespace Lumora.Godot.Hooks;

// The one instance a renderer draws through, for asking where it is on screen. Null while it draws through
// anything else (one instance per surface) or nothing yet.
internal interface IDrawnInstance
{
    MeshInstance3D? DrawnInstance { get; }
}
