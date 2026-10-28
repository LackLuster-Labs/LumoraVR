// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Listing;

namespace Lumora.Core.Components.UI;

// Owns every button on a pooled People row. A button's press action is a SyncDelegate, which a closure
// cannot be named as, so the press lands on a method here; rows are rebound as lists scroll, so what is
// registered is the ROW and a control index, and the item is read off the row when it fires. -xlinka
[ComponentCategory("Hidden")]
public sealed class PeopleRelay : Component
{
    private readonly struct Binding
    {
        public readonly ListingRow Row;
        public readonly int Index;

        public Binding(ListingRow row, int index)
        {
            Row = row;
            Index = index;
        }
    }

    internal PeopleScreen? Screen;

    private readonly Dictionary<Button, Binding> _bindings = new();

    internal void Register(Button button, ListingRow row, int index)
    {
        if (button == null || row == null)
            return;
        _bindings[button] = new Binding(row, index);
        button.SetAction(OnPressed);
    }

    // A press kept down on the row's item hands it to the screen to time; the screen says when one turned
    // into a hold, and the release that ends it is then no press.
    internal void RegisterHold(Button button, ListingRow row)
    {
        if (button == null || row == null)
            return;
        button.Pressed += _ => Screen?.HoldDown(button, row);
        button.Released += _ => Screen?.HoldUp(button);
    }

    [SyncMethod]
    public void OnPressed(Button button, UIInteractionContext context)
    {
        if (button == null || Screen == null || Screen.IsDestroyed)
            return;
        if (Screen.TakeHeldRelease(button))
            return;
        if (!_bindings.TryGetValue(button, out var binding) || binding.Row.Item is not { } item)
            return;
        Screen.Press(item, binding.Index);
    }
}
