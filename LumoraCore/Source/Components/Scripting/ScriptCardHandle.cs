// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components.Scripting;

// Marks the part of a node card the laser is on, for the node tool: a card's header to move it by, or a
// port to drag a wire from. Lives on the card, which is local to each machine, so it holds plain
// references and syncs nothing. -xlinka
[ComponentCategory("Hidden")]
public sealed class ScriptCardHandle : Component
{
    public ScriptGraphNode? Node { get; set; }
    public string Port { get; set; } = string.Empty;
    public ScriptCardHandleKind Kind { get; set; }

    public bool IsPort => Kind is not (ScriptCardHandleKind.Header or ScriptCardHandleKind.Material);

    public static ScriptCardHandle? Find(Slot? slot)
    {
        for (var current = slot; current != null; current = current.Parent)
        {
            var handle = current.GetComponent<ScriptCardHandle>();
            if (handle != null)
                return handle;
        }
        return null;
    }
}
