// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// Fluid touching someone or something: the person and the part of them it touched, or the prop's slot, where, how
// many litres, who poured or threw it when that is known, and the system it belongs to.
public readonly record struct FluidTouched(User? User, Slot? Slot, BodyNode Part, float3 Point, float Litres, User? From, Slot? System);
