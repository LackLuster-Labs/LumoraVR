// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Holds a field at value while the script runs. Every machine running the script works value out every
// frame and shows it in the field there; nothing is sent. The field is bound to the script, and the host
// links it as the script's author, so a script drives only what its author could set by hand, and a field
// another drive already holds stays with that one. Stopping the script lets the field go, and so does on
// while it is false, which then gives the field back to whatever writes it. With heard bound to a field it
// holds: every other write is refused, the value tried goes into heard and heardCount counts the tries, on
// the machine that tried, so a script anywhere can hear them. -xlinka
[Node("World/Fields/Drive Field", Capability.WorldWrite, Description = "Holds a field at value while the script runs.",
    Keywords = "bind, link, hold value, copy value, keep set, override")]
public sealed class DriveField<T> : DriveNode
{
    public override void Build(FlowBuilder f)
        => f.Drive(f.Setting<IField<T>>("field"), f.Input<T>("value"), f.Input<bool>("on", true), f.Setting<IField<T>>("heard"),
            f.Setting<IField<int>>("heardCount"));
}
