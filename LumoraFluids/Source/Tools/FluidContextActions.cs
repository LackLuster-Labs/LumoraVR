// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Components.UI;
using Lumora.Core.Localization;

namespace Lumora.Fluids;

// The wearer's own fluid items on their Self ring: "Clean off" while their coat is over 5 mL, and the stick mode as a
// small topic page (Sticky, Wet, Off). Only ever for the user whose root it sits on, on their own machine. -xlinka
[ComponentCategory("Users")]
public sealed class FluidContextActions : ContextMenuItemSource
{
    public const float CleanOffAboveMl = 5f;

    private static readonly float[] CleanColour = { 0.16f, 0.26f, 0.32f, 0.92f };
    private static readonly float[] GooColour = { 0.20f, 0.30f, 0.16f, 0.92f };
    private static readonly float[] PickedColour = { 0.26f, 0.42f, 0.20f, 0.95f };

    public override void PopulateContextMenu(ContextMenuPage page, ContextMenuContext context)
    {
        var root = Slot?.ActiveUserRoot;
        if (root == null || !root.IsLocalUserRoot)
            return;

        var baker = FluidResidueBaker.Find(World);
        if (baker != null && !baker.IsCleaning && baker.CoatMl() > CleanOffAboveMl)
        {
            page.AddItem(new ContextMenuItem
            {
                Label = FluidLocale.CleanOff.Resolve(),
                FillColor = CleanColour,
                OnPressed = _ => FluidResidueBaker.Find(World)?.CleanOff(),
            });
        }

        var mode = FluidPersonPublisher.ReadStickMode();
        var goo = page.GetOrAddSubPage(FluidLocale.FluidStick.WithArgs(Name(mode)).Resolve(), GooColour);
        AddMode(goo, FluidStickMode.Sticky, mode);
        AddMode(goo, FluidStickMode.Wet, mode);
        AddMode(goo, FluidStickMode.Off, mode);
    }

    private static void AddMode(ContextMenuPage page, FluidStickMode mode, FluidStickMode current)
    {
        page.AddItem(new ContextMenuItem
        {
            Label = Name(mode),
            FillColor = mode == current ? PickedColour : GooColour,
            OnPressed = _ => FluidPersonPublisher.SetStickMode(mode),
        });
    }

    private static string Name(FluidStickMode mode) => FluidLocale.StickModeName(mode).Resolve();
}
