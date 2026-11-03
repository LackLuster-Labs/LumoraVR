// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components;
using Lumora.Script;
using HelioText = Helio.UI.Text;

namespace Lumora.Core.Scripting;

// The words a slot shows: its text renderer's, else its UI text's. A slot with neither fails.
[Node("World/Components/Set Text", Capability.WorldWrite, Description = "The words a slot shows: its text renderer's, else its UI text's.",
    Keywords = "label, change text, write text, display text")]
public sealed class SetText : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, string? text)
    {
        if (!ScriptReads.Reached(ctx, slot))
            return;
        text ??= string.Empty;
        if (slot!.GetComponent<TextRenderer>() is { IsDestroyed: false } renderer)
            ScriptWrites.Set(ctx, renderer.Text, text);
        else if (slot.GetComponent<HelioText>() is { IsDestroyed: false } label)
            ScriptWrites.Set(ctx, label.Content, text);
        else
            ctx.Fail($"{ScriptReads.Named(slot)} shows no text");
    }
}
