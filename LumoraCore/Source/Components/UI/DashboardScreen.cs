// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

[ComponentCategory("Hidden")]
[UserspaceOnly]
public class DashboardScreen : UIComponent
{
    public readonly Sync<string> Label;
    public readonly Sync<color> ActiveColor;
    // Set on a tab that exists only to say a feature isn't here yet. The nav draws it muted so it
    // never reads as something you can use. -xlinka
    public readonly Sync<bool> Placeholder;

    // Idle color of this screen's nav label. Override to make one tab stand out (Exit does).
    public virtual color NavLabelColor => DashTheme.TextDim;

    // What is waiting in this screen, shown as a count on its dock plate; 0 shows nothing. The dock reads
    // it every frame, so it has to be cheap. -xlinka
    public virtual int DockBadge => 0;

    private bool _built;
    private Slot? _contentSlot;
    // Overlays this screen parents OUTSIDE its own content slot - e.g. a modal + dim backdrop hosted on the
    // canvas root so they can cover the nav chrome. Those do NOT hide when this screen's content slot goes
    // inactive, so HideScreen deactivates them here. Structural safety net: a screen can't leave an on-top
    // overlay drawing over the next screen even if it forgets a custom OnHide. Register at build time. -xlinka
    private System.Collections.Generic.List<Slot>? _offRootOverlays;

    public Slot? ContentSlot
    {
        get
        {
            EnsureContent();
            return _contentSlot;
        }
    }

    public DashboardScreen()
    {
        Label = new Sync<string>(this, string.Empty);
        ActiveColor = new Sync<color>(this, new color(0.28f, 0.72f, 1f, 1f));
        Placeholder = new Sync<bool>(this, false);
    }

    public override void OnStart()
    {
        base.OnStart();
        // Content is built lazily on first ShowScreen (or first ContentSlot access), NOT here. Opening the dash
        // used to tessellate every screen's UI up front (all screens OnStart at once), a big one-frame comp
        // spike. Now only the screen you actually land on builds; the rest defer until navigated to. - xlinka
    }

    public void ShowScreen()
    {
        EnsureContent();
        Slot.ActiveSelf.Value = true;
        OnShow();
        // Activating the slot doesn't dirty the canvas on its own, so a freshly
        // shown screen's chunks stay unbuilt until something else forces a full
        // dirty (hover, or switching screens) - that's the "flick screens to make
        // it render". Force a full layout+render here so it shows immediately.
        Slot.GetComponentInParents<Canvas>()?.MarkLayoutDirty();
    }

    public void HideScreen()
    {
        Slot.ActiveSelf.Value = false;
        OnHide();
        // A modal belongs to the screen that raised it. The host sits on the dashboard, not on this
        // screen's content, so switching tabs would otherwise leave a confirm sitting over the next
        // screen - still wired to the thing you walked away from. Looked up rather than created: a
        // dashboard where nobody has opened a dialog should not grow a host just from tab switching.
        Slot.GetComponentInParents<ModalHost>()?.CloseAll();
        // Safety net for off-root overlays (canvas-root modals/backdrops). A screen with a stateful menu still
        // overrides OnHide for a clean reset; this just guarantees nothing it parented above its own content
        // survives the switch and draws over the next screen. -xlinka
        if (_offRootOverlays != null)
        {
            for (int i = 0; i < _offRootOverlays.Count; i++)
            {
                var overlay = _offRootOverlays[i];
                if (overlay != null && !overlay.IsDestroyed)
                    overlay.ActiveSelf.Value = false;
            }
        }
    }

    // Register a slot this screen parents OUTSIDE its own content slot (a modal/backdrop on the canvas
    // root). HideScreen deactivates every registered overlay so it can't draw over the next screen. -xlinka
    protected void RegisterOverlay(Slot overlay)
    {
        if (overlay == null)
            return;
        _offRootOverlays ??= new System.Collections.Generic.List<Slot>();
        if (!_offRootOverlays.Contains(overlay))
            _offRootOverlays.Add(overlay);
    }

    protected virtual void OnShow()
    {
    }

    protected virtual void OnHide()
    {
    }

    protected virtual void OnContentReady(Slot contentSlot)
    {
    }

    protected virtual void BuildContent(UIBuilder builder)
    {
    }

    private void EnsureContent()
    {
        ComponentExecutionPolicy.Demand(GetType(), World);
        if (_built) return;
        _built = true;

        var rect = RectTransform ?? Slot.GetComponent<RectTransform>() ?? Slot.AttachComponent<RectTransform>();
        Fill(rect);

        _contentSlot = Slot.AddSlot("Content");
        Fill(_contentSlot.AttachComponent<RectTransform>());
        OnContentReady(_contentSlot);
        BuildContent(new UIBuilder(_contentSlot));
    }

    private static void Fill(RectTransform rect)
    {
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;
        rect.OffsetMin.Value = float2.Zero;
        rect.OffsetMax.Value = float2.Zero;
    }
}
