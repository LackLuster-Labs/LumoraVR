// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Lumora.Fluids.Hooks;

namespace Lumora.Source.Modules;

// Boots the fluid module before any other code in the game assembly runs. Naming a module type here is also what
// keeps the project reference in the assembly's metadata, so both module assemblies load with the game.
//
// In an exported game this runs inside Godot's own .NET entry point, before Godot's function pointers are set: it
// must never touch a Godot type, directly or through the boot. FluidModule.Boot only fills managed registries and
// leaves every Godot call to the first fluid hook. -xlinka
internal static class FluidsLink
{
#pragma warning disable CA2255 // the game assembly is the host here, not a library someone else loads
    [ModuleInitializer]
    internal static void Boot() => FluidModule.Boot();
#pragma warning restore CA2255
}
