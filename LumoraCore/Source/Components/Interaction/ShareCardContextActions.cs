// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.UI;

namespace Lumora.Core.Components;

// The link card's own entries on the radial menu, while it is in the local user's hand: the menu walks the user
// root, and a held object hangs under it.
[ComponentCategory("Hidden")]
public sealed class ShareCardContextActions : ContextMenuItemSource
{
    public readonly SyncRef<ShareCard> Card;

    private static readonly float[] OpenFill = { 0.08f, 0.26f, 0.26f, 0.92f };
    private static readonly float[] KeepFill = { 0.12f, 0.18f, 0.30f, 0.92f };

    public ShareCardContextActions()
    {
        Card = new SyncRef<ShareCard>(this);
    }

    public override void PopulateContextMenu(ContextMenuPage page, ContextMenuContext context)
    {
        var card = Card.Target;
        if (card == null || card.IsDestroyed || !card.Enabled.Value)
            return;
        if (Slot?.ActiveUserRoot?.ActiveUser != World?.LocalUser)
            return;
        page.AddItem(new ContextMenuItem
        {
            Group = ContextMenuGroup.Held,
            Label = "Open",
            Detail = card.Title.Value,
            FillColor = OpenFill,
            OnPressed = _ => card.Open(),
        });
        page.AddItem(new ContextMenuItem
        {
            Group = ContextMenuGroup.Held,
            Label = "Add to inventory",
            FillColor = KeepFill,
            OnPressed = _ => card.AddToInventory(),
        });
    }
}
