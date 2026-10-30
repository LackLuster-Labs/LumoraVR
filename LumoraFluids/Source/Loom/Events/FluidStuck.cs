// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Input;

namespace Lumora.Fluids;

// A foot or hand got stuck, or came free: whose, which limb, how strongly it is held (0 to 1), and which way it went.
public readonly record struct FluidStuck(User? User, BodyNode Part, float Strength, bool Stuck);
