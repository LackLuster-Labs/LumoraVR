// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// Fluid leaving a person, or taken up by them: whose, how (a throw, a drip, a footprint, a blob from a tool, a
// squeezed sponge...), where, how fast, and how many litres.
public readonly record struct FluidReleased(User? User, FluidReleaseKind Kind, float3 Point, float3 Velocity, float Litres);
