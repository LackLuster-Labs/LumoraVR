// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Fluids;

// One patch of skin fluid ran over since the last read: a person's slot and part, the patch's middle and outward
// normal in the part bone's space, how many particle frames touched it, the volume that touched (m3, summed over
// those frames) and the frame substance index. FluidSkinCollide collects them; the wearer's baker turns them into a
// thin coat that dries. -xlinka
public readonly record struct FluidWetSample(int Person, int Part, float3 Local, float3 Normal, int Contacts, float Volume, int FrameSubstance);
