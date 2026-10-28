// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Lumora.Core.Persistence;

namespace Lumora.Core.Components.UI;

// Dashboard exit screen: a centered card offering "Exit and Save" (save the home, then quit) or
// "Exit and Discard" (quit and leave the home as it was). Closing its window cancels. Settings are not
// part of the choice: they save themselves, and the engine's shutdown sends the last of them.
[ComponentCategory("Hidden")]
public sealed class ExitScreen : DashboardScreen
{
    private const float CornerRadius = DashTheme.RadiusCard;

    private static readonly color CardFill = DashTheme.Surface;
    private static readonly color CardBorder = DashTheme.Outline;
    // Saving is the way out you want; discarding is the one that loses work.
    private static readonly color SaveFill = DashTheme.Accent;
    private static readonly color DiscardFill = DashTheme.Negative;
    private static readonly color TextPrimary = DashTheme.Text;
    private static readonly color TextDim = DashTheme.TextDim;
    private static readonly color ExitRed = DashTheme.Negative;

    public override color NavLabelColor => ExitRed;

    // Exit and Save is on offer only while the home holds something its last save or open did not. Checked
    // while the screen is up rather than every frame: the check hashes the whole home.
    private const double SaveCheckSeconds = 1.0;
    private static readonly color SaveOffFill = DashTheme.SurfaceHover;
    private static readonly LocaleText Message = "Exit.Message".AsLocale("Save your home, or exit and discard its changes.");
    private static readonly LocaleText NothingToSave = "Exit.NothingToSave".AsLocale("Your home has nothing new to save.");

    private Dashboard? _dashboard;
    private RectTransform? _cardRect;
    private Text? _titleText;
    private Text? _messageText;
    private Slot? _buttonsSlot;
    private Button? _saveButton;
    private bool _saveOffered = true;
    private double _nextSaveCheck = double.NegativeInfinity;
    private bool _exiting;

    protected override void BuildContent(UIBuilder builder)
    {
        _dashboard = Dashboard.Of(Slot);

        var card = builder.Current.AddSlot("ExitCard");
        var cardRect = card.AttachComponent<RectTransform>();
        _cardRect = cardRect;
        cardRect.AnchorMin.Value = new float2(0.5f, 0.5f);
        cardRect.AnchorMax.Value = new float2(0.5f, 0.5f);
        cardRect.OffsetMin.Value = new float2(-300f, -160f);
        cardRect.OffsetMax.Value = new float2(300f, 160f);
        ApplyRoundedPanel(card, CardFill, CardBorder);
        var cardPanel = card.GetComponent<BorderedImage>();
        if (cardPanel != null)
            cardPanel.Borders.Value = new float4(DashTheme.RadiusPanel, DashTheme.RadiusPanel,
                DashTheme.RadiusPanel, DashTheme.RadiusPanel);

        var col = card.AttachComponent<VerticalLayout>();
        col.Spacing.Value = 18f;
        col.PaddingLeft.Value = 28f;
        col.PaddingRight.Value = 28f;
        col.PaddingTop.Value = 28f;
        col.PaddingBottom.Value = 28f;
        col.ForceExpandWidth.Value = true;
        col.ForceExpandHeight.Value = false;

        _titleText = AddLabel(card, "Title", "Exit.Title".AsLocale("Exit Lumora"), DashTheme.FontDisplay, TextPrimary, 46f);
        _titleText.Font.Target = _dashboard?.FontBold.Target ?? _dashboard?.Font.Target!;
        _messageText = AddLabel(card, "Message", Message, DashTheme.FontBody, TextDim, 30f);

        _buttonsSlot = card.AddSlot("Buttons");
        _buttonsSlot.AttachComponent<RectTransform>();
        SetFixedHeight(_buttonsSlot, 60f);
        var row = _buttonsSlot.AttachComponent<HorizontalLayout>();
        row.Spacing.Value = 14f;
        row.ForceExpandWidth.Value = true;
        row.ForceExpandHeight.Value = true;

        _saveButton = AddButton(_buttonsSlot, "Save", "Exit.Save".AsLocale("Exit and Save"), SaveFill, OnExitAndSave);
        AddButton(_buttonsSlot, "Discard", "Exit.Discard".AsLocale("Exit and Discard"), DiscardFill, OnExitAndDiscard);

        AddLabel(card, "Hint", "Exit.Hint".AsLocale("Settings are always kept. Close this window to cancel."), DashTheme.FontSmall, DashTheme.TextMuted, 22f);
    }

    // The card is 600 wide where it has the room and gives way to the window it is in where it does not.
    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        if (!_exiting && Slot.IsActive && World.Time.TotalTime >= _nextSaveCheck)
        {
            _nextSaveCheck = World.Time.TotalTime + SaveCheckSeconds;
            UpdateSaveOffer();
        }
        var rect = _cardRect;
        var host = RectTransform;
        if (rect == null || rect.IsDestroyed || host == null)
            return;
        float half = System.MathF.Max(150f, System.MathF.Min(300f, host.LocalComputeRect.width * 0.5f - 16f));
        SettingsUI.SetOffsets(rect, new float2(-half, -160f), new float2(half, 160f));
    }

    protected override void OnShow()
    {
        base.OnShow();
        _nextSaveCheck = double.NegativeInfinity;
    }

    // Greyed and dead while saving would write nothing new, with the message saying why.
    private void UpdateSaveOffer()
    {
        var button = _saveButton;
        if (button == null || button.IsDestroyed)
            return;
        var home = Lumora.Core.Engine.Current?.WorldManager?.GetWorldByName("LocalHome");
        bool offer = home != null && !home.IsDestroyed && home.HasUnsavedChanges();
        if (offer == _saveOffered)
            return;
        _saveOffered = offer;
        button.Interactable.Value = offer;
        if (button.Slot.GetComponent<BorderedImage>() is { } fill)
            fill.Tint.Value = offer ? SaveFill : SaveOffFill;
        if (button.Slot.GetComponentInChildren<Text>() is { } label)
            label.Color.Value = offer ? WidgetScreen.OnFill(SaveFill) : DashTheme.TextMuted;
        if (_messageText != null)
            LocaleTextRegistry.Bind(_messageText, offer ? Message : NothingToSave);
        Slot.GetComponentInParents<Canvas>()?.MarkDirty();
    }

    private void OnExitAndSave()
    {
        if (_saveOffered)
            BeginExit(save: true);
    }

    private void OnExitAndDiscard() => BeginExit(save: false);

    // Show a brief "saving / exiting" state and let it render, then do the (blocking) save and quit
    // a few updates later - so there's visible feedback instead of an instant close.
    private void BeginExit(bool save)
    {
        if (_exiting)
            return;
        _exiting = true;

        // Rebound rather than assigned: the registry swaps the entry, so the wait message is still the
        // right language if somebody switched it a second ago.
        if (_titleText != null)
        {
            LocaleTextRegistry.Bind(_titleText, save
                ? "Exit.Saving".AsLocale("Saving and exiting…")
                : "Exit.Exiting".AsLocale("Exiting…"));
        }
        if (_messageText != null)
            LocaleTextRegistry.Bind(_messageText, "Exit.Wait".AsLocale("Please wait…"));
        if (_buttonsSlot != null)
            _buttonsSlot.ActiveSelf.Value = false;
        Slot.GetComponentInParents<Canvas>()?.MarkDirty();

        World.RunInUpdates(3, () =>
        {
            if (save)
            {
                var home = Lumora.Core.Engine.Current?.WorldManager?.GetWorldByName("LocalHome");
                if (home != null)
                    home.SaveToFile(Lumora.Core.Engine.LocalHomeSavePath);
            }
            Lumora.Core.Engine.Current?.RequestQuit();
        });
    }

    private Text AddLabel(Slot parent, string name, LocaleText content, float size, color textColor, float height)
    {
        var slot = parent.AddSlot(name);
        slot.AttachComponent<RectTransform>();
        SetFixedHeight(slot, height);
        var text = slot.AttachComponent<Text>();
        text.Font.Target = _dashboard?.Font.Target!;
        text.Size.Value = size;
        text.Color.Value = textColor;
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        return LocaleTextRegistry.Bind(text, in content);
    }

    // The slot name is fixed and the label is translated separately: naming a slot after its visible
    // text meant the hierarchy changed shape with the interface language.
    private Button AddButton(Slot parent, string name, LocaleText label, color fill, System.Action onClick)
    {
        var buttonSlot = parent.AddSlot(name);
        buttonSlot.AttachComponent<RectTransform>();
        var element = buttonSlot.AttachComponent<LayoutElement>();
        element.FlexibleWidth.Value = 1f;
        element.FlexibleHeight.Value = 1f;

        ApplyRoundedPanel(buttonSlot, fill, CardBorder);

        var button = buttonSlot.AttachComponent<Button>();
        // Both of these quit. Sliding off before letting go is the last way out.
        button.ActOnRelease.Value = true;
        button.Clicked += (_, _) => onClick();

        var labelSlot = buttonSlot.AddSlot("Label");
        var labelRect = labelSlot.AttachComponent<RectTransform>();
        labelRect.AnchorMin.Value = float2.Zero;
        labelRect.AnchorMax.Value = float2.One;
        labelRect.OffsetMin.Value = float2.Zero;
        labelRect.OffsetMax.Value = float2.Zero;
        var text = labelSlot.AttachComponent<Text>();
        text.Font.Target = _dashboard?.FontSemibold.Target ?? _dashboard?.Font.Target!;
        text.Size.Value = DashTheme.FontBody;
        text.Color.Value = WidgetScreen.OnFill(fill);
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        LocaleTextRegistry.Bind(text, in label);
        return button;
    }

    private void ApplyRoundedPanel(Slot slot, color fill, color border)
    {
        var image = slot.AttachComponent<BorderedImage>();
        image.Tint.Value = fill;
        image.BorderTint.Value = border;
        image.BorderThickness.Value = DashTheme.OutlineWidth;
        var rounded = _dashboard?.RoundedSprite;
        if (rounded != null)
        {
            image.Texture.Target = rounded;
            image.NineSlice.Value = true;
            image.Borders.Value = new float4(CornerRadius, CornerRadius, CornerRadius, CornerRadius);
        }
    }

    private static void SetFixedHeight(Slot slot, float height)
    {
        var element = slot.GetComponent<LayoutElement>() ?? slot.AttachComponent<LayoutElement>();
        element.MinHeight.Value = height;
        element.PreferredHeight.Value = height;
        element.FlexibleHeight.Value = 0f;
    }
}
