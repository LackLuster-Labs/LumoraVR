// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// The one-workgroup small-N path is not used. One workgroup is one shader core: on every desktop card measured it
// ran 3-35x slower than the normal chain at 1k-8k particles, and the headsets it was meant for never simulate. The
// solver never loads this kernel; the file stays so the path has a home if a tile GPU ever simulates. -xlinka

// Godot's import hashes this file alone and never its includes, so an include change recompiles nothing until
// this stamp changes too. Include stamp 1.

#[compute]
#version 450

#include "Include/FluidLayout.glsli"

layout(local_size_x = FLUID_GROUP_SIZE, local_size_y = 1, local_size_z = 1) in;

void main()
{
}
