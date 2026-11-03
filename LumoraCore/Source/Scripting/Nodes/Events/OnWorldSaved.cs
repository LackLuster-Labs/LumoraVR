// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The world was saved and the save reached disk. Heard where the save was made: by a Shared script when
// the host saves, by a Local script only on a machine that saved its own copy.
[Node("World/Events/On World Saved", Description = "The world was saved and the save reached disk.")]
public sealed class OnWorldSaved : EventNode<SaveCompleted>
{
}
