// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Wasm;

// An import native code calls straight: Call takes one ulong per parameter lane and gives the one result lane
// (a Func of ulongs), and FuelCost is what the host function linked for the import charges, so both back ends
// burn the same fuel. A throw from it traps the way a throw from a host function does.
public sealed record WasmDirectImport(Delegate Call, int FuelCost);
