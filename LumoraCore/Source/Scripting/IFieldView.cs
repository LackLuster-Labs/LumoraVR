// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Scripting;

// A field that is part of another member read as a type of its own. Member is what really changes: the gate
// judges writes on it, change stamps come from it, and a drive holds it. Owner is what a node binds to.
internal interface IFieldView
{
    SyncElement Member { get; }
    IWorldElement Owner { get; }
}
