// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Lumora.Core.Components.Interaction;

namespace Lumora.Core.Components.Utility;

// Hands every press of the button beside it on to another slot, as if that slot had been pressed too: the
// actions there and any script listening for its presses fire with the same presser. One relay pointing at a
// slot that relays back again stops after the first pass. -xlinka
[ComponentCategory("Utility/Buttons")]
public sealed class PressRelay : Component, IPressReceiver, IButtonPressReceiver
{
    public readonly SyncRef<Slot> Target;

    private bool _relaying;

    public PressRelay()
    {
        Target = new SyncRef<Slot>(this);
    }

    public void OnPressed(in PressEvent press)
    {
        var target = Target.Target;
        if (_relaying || target == null || target.IsDestroyed || ReferenceEquals(target, Slot))
            return;
        _relaying = true;
        try
        {
            new PressEvent(press.Source, press.Actor, press.Point).Send(target);
        }
        finally
        {
            _relaying = false;
        }
    }

    public void OnButtonPressed(Button button, in UIInteractionContext context)
    {
        var target = Target.Target;
        if (_relaying || target == null || target.IsDestroyed || ReferenceEquals(target, Slot))
            return;
        _relaying = true;
        try
        {
            foreach (var component in target.GetComponents<Component>())
            {
                if (component is not IButtonPressReceiver receiver || component.IsDestroyed || !component.Enabled.Value)
                    continue;
                try
                {
                    receiver.OnButtonPressed(button, in context);
                }
                catch (System.Exception ex)
                {
                    Logging.Logger.Error($"PressRelay: a press receiver on '{target.Name.Value}' threw: {ex.Message}");
                }
            }
        }
        finally
        {
            _relaying = false;
        }
    }
}
