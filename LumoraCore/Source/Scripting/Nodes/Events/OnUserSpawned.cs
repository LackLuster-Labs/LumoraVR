// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Someone's body appeared in the world: their root is there with a head. On User Joined fires when the
// user record arrives, which can be frames before the body does. A body that is made again, a respawn,
// fires it again; people already standing there when the script starts do not. Looked for once a frame
// on whichever machine the script runs.
[Node("Users/Events/On User Spawned", Description = "Someone's body appeared in the world: their root is there with a head.",
    Keywords = "spawn, respawn, player spawned")]
public sealed class OnUserSpawned : EventNode<UserSpawned>
{
}
