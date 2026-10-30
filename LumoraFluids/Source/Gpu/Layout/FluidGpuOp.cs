// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Fluids;

// What a FluidGpuCommand asks the commands kernel to do. Values are written into FluidGpuCommand.Op and read by
// the kernel as plain numbers, so only append. System is the system index in the frame; A, B and C per op:
//
//   Emit, Burst     A = point (system local) + litres, B = velocity + spread (rad), C = tint RGBA8 bits, origin,
//                   substance, nozzle radius. Count = particles, Seed = the deterministic seed.
//   Remove          A = sphere centre + radius, B.x = litres at most (0 = all in the sphere).
//   ClearAll        nothing.
//   ClearOrigin     Count = origin index.
//   Evaporate       Count = origin index, A.x = litres.
//   FadeOrigin      Count = origin index, A.x = seconds.
//   Settle          nothing; the system relaxes and the keeper reports a full cell write.
//   Release         a person's release: A = point + litres, B = velocity + body part, C = substance, kind, seed.
//   RemoveParticles A..C hold up to 12 particle ids as uint bits, Count says how many.
//   Reseed          A.x = first ledger index, A.y = count; cells fill nearest-first, at most 2k particles a frame.
//   WakeBox         A = box centre, B = half sizes, in the system's frame.
//   DropAnchors     Count = target kind, Seed = target index (a collider, body or person).
//   FreezeAnchors   same target fields, C.x = 1 to freeze, 0 to thaw.
//   ZeroVelocity    the energy watchdog's answer for one system.
//   RemoveBox       A = box centre + litres at most (0 = all in the box), B = half sizes + drain index + 1 (0 = not a
//                   drain), C = the box's rotation in the system's frame; Count = origin filter, 255 = any, as Remove.
//                   Not in the commands kernel yet: drains keep their Remove spheres until it is. -xlinka
public enum FluidGpuOp : uint
{
    None = 0,
    Emit = 1,
    Burst = 2,
    Remove = 3,
    ClearAll = 4,
    ClearOrigin = 5,
    Evaporate = 6,
    FadeOrigin = 7,
    Settle = 8,
    Release = 9,
    RemoveParticles = 10,
    Reseed = 11,
    WakeBox = 12,
    DropAnchors = 13,
    FreezeAnchors = 14,
    ZeroVelocity = 15,
    RemoveBox = 16,
}
