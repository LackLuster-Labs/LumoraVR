// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Holds a reference field on target while the script runs, the way Drive Field holds a value: worked out
// every frame on every machine running the script and shown there, linked by the host as the author. A
// target of another kind than the reference holds leaves it empty. -xlinka
[Node("World/References/Drive Reference", Capability.WorldWrite, Description = "Holds a reference field on target while the script runs, the way Drive Field holds a value: worked out every frame on every machine running the script and shown there, linked by the host as the author.",
    Keywords = "link reference, bind reference, hold reference")]
public sealed class DriveReference<T> : DriveNode where T : class, IWorldElement
{
    public override void Build(FlowBuilder f) => f.Drive(f.Setting<ISyncRef>("reference"), f.Input<T>("target"));
}
