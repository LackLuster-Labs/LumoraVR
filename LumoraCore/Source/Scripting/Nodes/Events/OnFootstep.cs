// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Anyone's foot came down in this world, a step or both feet on landing. Steps are the avatar's own walk,
// worked out on each machine as it poses everyone, so they are heard only while this world is the one in
// front of the machine running the script: the host's, for a Shared script. A foot a tracker moves takes no
// steps here. -xlinka
[Node("Users/Movement/On Footstep", Description = "Anyone's foot came down in this world, a step or both feet on landing.",
    Keywords = "footstep, step, walk, foot, land, landing")]
public sealed class OnFootstep : EventNode<Footstep>
{
}
