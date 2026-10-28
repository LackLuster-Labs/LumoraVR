// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Components.UI.Worlds;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// One strip under the bar while the inventory service holds saves until the account's email is verified: what is
// going on, where the link went, the mail again and a re-check. Everything already saved stays as it is, so the strip
// is the only thing that changes. The moment a check finds the email verified it turns green for a few seconds and
// goes. The account is read again whenever the window opens. -xlinka
public sealed partial class InventoryScreen
{
    private const float VerifyHeight = 44f;
    private const float VerifiedLinger = 4f;

    private Slot? _verifyRow;
    private bool _verifyHeldShown;
    private bool _verifiedFlash;
    private bool _verifyBusy;
    private bool _verifyTicking;

    private void BuildVerifyRow(Slot root)
    {
        _verifyRow = root.AddSlot("Verify");
        _verifyRow.AttachComponent<RectTransform>();
        BrowserParts.Height(_verifyRow, VerifyHeight);
        _verifyRow.ActiveSelf.Value = false;
        AccountEmail.Changed += OnEmailChanged;
    }

    private void OnEmailChanged() => OnUi(RebuildVerifyRow);

    private void RecheckEmail()
    {
        if (_source is CloudInventorySource && _source.SignedIn)
            _ = AccountEmail.RecheckAsync(force: true);
        RebuildVerifyRow();
    }

    private void RebuildVerifyRow()
    {
        var row = _verifyRow;
        if (row == null || row.IsDestroyed)
            return;
        bool held = _source is CloudInventorySource && _source.SignedIn && AccountEmail.SavesHeld;
        if (_verifyHeldShown && !held && _source.SignedIn && AccountEmail.Known && AccountEmail.Verified)
        {
            _verifiedFlash = true;
            World?.RunInSeconds(VerifiedLinger, () =>
            {
                _verifiedFlash = false;
                if (!IsDestroyed)
                    RebuildVerifyRow();
            });
        }
        _verifyHeldShown = held;
        row.DestroyChildren();
        bool show = held || _verifiedFlash;
        row.ActiveSelf.Value = show;
        MarkDirty();
        if (!show)
            return;

        var layout = row.GetComponent<HorizontalLayout>() ?? row.AttachComponent<HorizontalLayout>();
        layout.Spacing.Value = 8f;
        layout.PaddingLeft.Value = 8f;
        layout.PaddingRight.Value = 6f;
        layout.PaddingTop.Value = 6f;
        layout.PaddingBottom.Value = 6f;
        layout.ForceExpandWidth.Value = false;
        layout.ForceExpandHeight.Value = true;
        var ink = held ? DashTheme.Warning : DashTheme.Positive;

        var back = row.AddSlot("Back");
        BrowserParts.Fill(back.AttachComponent<RectTransform>());
        back.AttachComponent<IgnoreLayout>();
        // Opaque mixes rather than see-through tints: the window is glass, and a tint over it took whatever colour the
        // sky behind had.
        SettingsUI.Panel(back, Mix(DashTheme.Surface, ink, 0.10f), Mix(DashTheme.Surface, ink, 0.55f), DashTheme.RadiusControl);

        var badge = row.AddSlot("Badge");
        badge.AttachComponent<RectTransform>();
        BrowserParts.Size(badge, 32f, 32f);
        SettingsUI.Panel(badge, Mix(DashTheme.Surface, ink, 0.24f), color.Transparent, 16f);
        if (held)
            _parts.Label(badge, "@", DashTheme.FontBody, ink, BrowserParts.Weight.Bold, TextHorizontalAlignment.Center);
        else
        {
            var glyph = badge.AddSlot("Check");
            var glyphRect = glyph.AttachComponent<RectTransform>();
            BrowserParts.Fill(glyphRect);
            SettingsUI.SetOffsets(glyphRect, new float2(7f, 7f), new float2(-7f, -7f));
            var icon = glyph.AttachComponent<Lumora.Core.Assets.ImageProvider>();
            icon.GenerateMipmaps.Value = false;
            icon.URL.Value = new System.Uri("res://Assets/Textures/Icons/check.svg");
            var image = glyph.AttachComponent<Image>();
            image.Texture.Target = icon;
            image.Tint.Value = ink;
        }

        var words = row.AddSlot("Text");
        words.AttachComponent<RectTransform>();
        words.AttachComponent<LayoutElement>().FlexibleWidth.Value = 1f;
        string line = held ? AccountEmailLocale.InventoryLine(AccountEmail.Address ?? string.Empty).Resolve() : AccountEmailLocale.NowVerified.Resolve();
        var text = _parts.Label(words, line, DashTheme.FontSmall, DashTheme.Text, BrowserParts.Weight.Semibold);
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        if (!held)
            return;

        int wait = AccountEmail.ResendWaitSeconds;
        string send = _verifyBusy ? AccountEmailLocale.Sending.Resolve()
            : wait > 0 ? AccountEmailLocale.SendAgainIn(wait).Resolve() : AccountEmailLocale.SendAgain.Resolve();
        AddInlineButton(row, send, wait > 0 || _verifyBusy ? ControlFill : AccentColor, 150f, SendFromInventory);
        AddInlineButton(row, AccountEmailLocale.Check.Resolve(), TabFill, 116f, CheckFromInventory);
        if (wait > 0 && !_verifyTicking && World != null)
        {
            _verifyTicking = true;
            World.RunInSeconds(1f, () =>
            {
                _verifyTicking = false;
                if (!IsDestroyed)
                    RebuildVerifyRow();
            });
        }
    }

    private async void SendFromInventory()
    {
        if (_verifyBusy || AccountEmail.ResendWaitSeconds > 0)
            return;
        _verifyBusy = true;
        RebuildVerifyRow();
        var outcome = await AccountEmail.ResendAsync();
        OnUi(() =>
        {
            _verifyBusy = false;
            SetStatus(AccountEmail.Describe(outcome).Resolve(), outcome == ResendOutcome.Sent ? DashTheme.TextDim : DashTheme.Warning);
            RebuildVerifyRow();
        });
    }

    private async void CheckFromInventory()
    {
        SetStatus(AccountEmailLocale.Checking.Resolve(), DashTheme.TextDim);
        await AccountEmail.RecheckAsync(force: true);
        OnUi(() =>
        {
            bool verified = AccountEmail.Known && AccountEmail.Verified && !AccountEmail.SavesHeld;
            SetStatus((verified ? AccountEmailLocale.NowVerified : AccountEmailLocale.StillNotVerified).Resolve(),
                verified ? DashTheme.TextDim : DashTheme.Warning);
            RebuildVerifyRow();
        });
    }

    private static color Mix(in color a, in color b, float t)
        => new(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, 1f);

    // Saving from here asks first: the strip above already says why, and the line says it again where the press was.
    private bool RefusedForEmail()
    {
        if (!AccountEmail.SavesHeld)
            return false;
        SetStatus(AccountEmailLocale.SaveRefused.Resolve(), DashTheme.Warning);
        return true;
    }
}
