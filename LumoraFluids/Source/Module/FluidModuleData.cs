// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Fluids;

// The data side's registrations, run once by the module's boot from the game's module initializer. Pure managed:
// in an export this runs before Godot's interop exists and before the data folder is pinned, so nothing here may
// reach Godot, the engine instance or the log. Every registry it fills is a static one that needs nothing from the
// engine. What goes wrong is kept in Problems, and the boot logs it once the engine has a log. -xlinka
public static class FluidModuleData
{
    private static readonly List<string> _problems = new();
    private static bool _registered;

    public static IReadOnlyList<string> Problems => _problems;

    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;

        // Node ports carry these, and a node whose port type is unknown is left out of the registry. The core's
        // field scan finds an enum only in a plain Sync<> field: FluidStickMode lives in a WearerSync and
        // FluidReleaseKind in no field at all.
        RegisterEnum("fluidStickMode", typeof(FluidStickMode));
        RegisterEnum("fluidReleaseKind", typeof(FluidReleaseKind));
        RegisterEnum("fluidPourMode", typeof(FluidPourMode));
        RegisterEnum("fluidPreset", typeof(FluidPreset));

        ScriptEnvironment.AddNodeAssembly(typeof(FluidModuleData).Assembly);
        FluidCatalog.Register();
        FluidCoupling.Register();
    }

    // The core's scan registers enums under the same lower-first names later; one name for one type twice is a
    // no-op there, so the order never matters. A clash with another type of the same name is a real problem.
    private static void RegisterEnum(string name, Type type)
    {
        try
        {
            ScriptTypes.RegisterEnum(name, type);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _problems.Add($"Loom enum '{name}' not registered: {ex.Message}");
        }
    }
}
