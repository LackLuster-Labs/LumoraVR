// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Input;

namespace Lumora.Fluids;

// A strand let go: off whose limb (nobody's when it ran between props), how long it was when it snapped, in metres,
// the prop it ran off when it was not a person's, and the system it belonged to.
public readonly record struct StrandSnapped(User? User, BodyNode Part, float Length, Slot? Slot, Slot? System);
