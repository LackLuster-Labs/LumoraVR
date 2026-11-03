// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Once, in the copy, when the object this script is on was duplicated: right after the copy's own start, on
// the machine its script runs on. The original hears nothing.
[Node("World/Slots/Events/On Duplicated", Description = "Once, in the copy, when the object this script is on was duplicated: right after the copy's own start, on the machine its script runs on.")]
public sealed class OnDuplicated : EventNode<ObjectDuplicated>
{
}
