// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Assets;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// The account's own way through: making one, the optional second lock, and confirming the email, as one window that
// walks from page to page; plus the card's row for an email still to confirm or a lock still to set, and the one
// notice a sign in owes an account whose email holds things back. The window hangs off the dashboard slot beside the
// sign-in dialog and takes the keys the same way, so the desktop keyboard and the VR keyboard both type into it.
// Every page is built fresh when it opens: nothing typed lives longer than the page that asked for it, the passwords
// least of all. -xlinka
public sealed partial class AccountWidgetPreset
{
    private const float CreateLinkWidth = 190f;
    private const float AccountRowHeight = 44f;
    private const float AccountRowTop = 138f;
    private const float FlowWidth = 520f;
    private const float FlowFieldBlock = 92f;
    private const float QrSize = 236f;
    private const int NameMin = 3;
    private const int NameMax = 32;
    private const int PasswordMin = 8;
    private const int PasswordMax = 100;
    private const float VerifyPollSeconds = 4f;
    private const float BadgeGrowSeconds = 0.45f;

    private static readonly Regex NamePattern = new(@"^[a-zA-Z0-9\s_-]+$", RegexOptions.Compiled);

    private enum FlowPage { None, Register, TwoFactorOffer, TwoFactorSetup, RecoveryCodes, VerifyEmail, Verified }

    private Pill? _createPill;
    private Slot? _accountRow;
    private Pill? _sendPill;
    private Pill? _checkPill;
    private Pill? _lockPill;
    private bool _profileVerified;
    private bool _twoFactorOn;
    private bool _profileKnown;
    private bool _cooldownTicking;
    private bool _checking;
    private bool _sending;
    public bool HasAccountRow { get; private set; }

    private Slot? _flowBackdrop;
    private Slot? _flowPanel;
    private RectTransform? _flowRect;
    private Slot? _flowContent;
    private FlowPage _page;
    private bool _flowOpen;
    private bool _flowBusy;
    private int _flowSerial;
    private Text? _flowStatus;
    private string _newName = string.Empty;
    private string _newEmail = string.Empty;
    private string _newPassword = string.Empty;
    private string _newConfirm = string.Empty;
    private string _totp = string.Empty;
    private readonly Dictionary<Focus, string> _flowErrors = new();
    private readonly Dictionary<Focus, (Field Field, Text Hint, LocaleText HintText)> _flowFields = new();
    private TwoFactorSetup? _setup;
    private Pill? _flowSend;
    private Text? _waitingDots;
    private int _dots;

    public bool FlowOpen => _flowOpen;

    // Any card's window being up, so no other copy of the card puts the notice over it.
    private static int _flowsShowing;

    // CARD ROW

    private void BuildAccountRow(Slot parent)
    {
        _accountRow = Row(parent, "AccountRow", AccountRowTop, 32f, Pad, Pad);
        _sendPill = AttachPill(SettingsUI.Child(_accountRow, "Send", float2.Zero, new float2(0.36f, 1f), float2.Zero, new float2(-3f, 0f)),
            AccountEmailLocale.SendAgain.Resolve(), 13f, SendFromCard);
        _checkPill = AttachPill(SettingsUI.Child(_accountRow, "Check", new float2(0.36f, 0f), new float2(0.68f, 1f), new float2(3f, 0f), new float2(-3f, 0f)),
            AccountEmailLocale.Check.Resolve(), 13f, CheckFromCard);
        _lockPill = AttachPill(SettingsUI.Child(_accountRow, "TwoFactor", new float2(0.68f, 0f), float2.One, new float2(3f, 0f), float2.Zero),
            AccountFlowLocale.TwoFactorPill.Resolve(), 13f, () => OpenFlow(FlowPage.TwoFactorOffer));
        SetActive(_accountRow, false);
    }

    private void PlaceAccountRow()
    {
        if (_accountRow == null || _accountRow.IsDestroyed)
            return;
        float top = AccountRowTop + (HasGroupStorage ? GroupRowsHeight : 0f);
        SettingsUI.SetOffsets(SettingsUI.Rect(_accountRow), new float2(Pad, -(top + 32f)), new float2(-Pad, -top));
    }

    private bool EmailVerifiedNow => AccountEmail.Known ? AccountEmail.Verified : _profileVerified;

    private void OnEmailChanged() => OnUi(World, () =>
    {
        ApplyEmailState();
        if (_flowOpen && _page == FlowPage.VerifyEmail && AccountEmail.Known && AccountEmail.Verified)
            ShowPage(FlowPage.Verified);
        else if (_flowOpen)
            RefreshFlowSend();
    });

    // The state line, the badge and the row, from whatever is known of the account now.
    private void ApplyEmailState()
    {
        bool signedIn = SignedIn;
        bool known = signedIn && (AccountEmail.Known || _profileKnown || ProfileName.Length > 0);
        bool verified = EmailVerifiedNow;
        if (_accountState != null && !_accountState.IsDestroyed)
        {
            ListingStyle.SetText(_accountState, !known ? "Signed in"
                : verified ? AccountEmailLocale.Verified.Resolve() : AccountEmailLocale.NotVerified.Resolve());
            ListingStyle.SetTextColor(_accountState, known && !verified ? DashTheme.Warning : DashTheme.TextDim);
        }
        SetActive(_verified, known && verified);

        bool needsEmail = known && !verified;
        bool needsLock = known && !_twoFactorOn && ProfileName.Length > 0;
        SetActive(_sendPill?.Slot, needsEmail);
        SetActive(_checkPill?.Slot, needsEmail);
        SetActive(_lockPill?.Slot, needsLock);
        if (_lockPill != null && !_lockPill.Slot.IsDestroyed)
        {
            // Alone on the row it takes the row's left end rather than its last third.
            SettingsUI.SetAnchors(SettingsUI.Rect(_lockPill.Slot), needsEmail ? new float2(0.68f, 0f) : float2.Zero,
                needsEmail ? float2.One : new float2(0.36f, 1f));
            SettingsUI.SetOffsets(SettingsUI.Rect(_lockPill.Slot), needsEmail ? new float2(3f, 0f) : float2.Zero,
                needsEmail ? float2.Zero : new float2(-3f, 0f));
        }
        bool row = signedIn && (needsEmail || needsLock);
        SetActive(_accountRow, row);
        if (HasAccountRow != row)
        {
            HasAccountRow = row;
            FitCard();
        }
        RefreshCardPills();
        Repaint();
    }

    private void RefreshCardPills()
    {
        _sendPill?.SetRamp(DashTheme.Accent, DashTheme.AccentHover, DashTheme.AccentPressed, WidgetScreen.OnFill(DashTheme.Accent));
        _checkPill?.SetRamp(DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.Text);
        _lockPill?.SetRamp(DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.Text);
        int wait = AccountEmail.ResendWaitSeconds;
        if (_sendPill != null && !_sendPill.Text.IsDestroyed)
        {
            ListingStyle.SetText(_sendPill.Text, _sending ? AccountEmailLocale.Sending.Resolve()
                : wait > 0 ? AccountEmailLocale.SendAgainIn(wait).Resolve() : AccountEmailLocale.SendAgain.Resolve());
            ListingStyle.SetInteractable(_sendPill.Button, wait == 0 && !_sending);
            if (wait > 0 || _sending)
                _sendPill.SetRamp(DashTheme.Field, DashTheme.Field, DashTheme.Field, DashTheme.TextMuted);
        }
        if (_checkPill != null && !_checkPill.Text.IsDestroyed)
            ListingStyle.SetText(_checkPill.Text, _checking ? AccountEmailLocale.Checking.Resolve() : AccountEmailLocale.Check.Resolve());
        if (wait > 0)
            TickCooldown();
    }

    // The wait on a resend counts down on the buttons, a second at a time, while there is one.
    private void TickCooldown()
    {
        if (_cooldownTicking || World == null)
            return;
        _cooldownTicking = true;
        World.RunInSeconds(1f, () =>
        {
            _cooldownTicking = false;
            if (IsDestroyed)
                return;
            RefreshCardPills();
            RefreshFlowSend();
            Repaint();
        });
    }

    private async void SendFromCard()
    {
        if (_sending || AccountEmail.ResendWaitSeconds > 0)
            return;
        var world = World;
        _sending = true;
        RefreshCardPills();
        var outcome = await AccountEmail.ResendAsync();
        OnUi(world, () =>
        {
            _sending = false;
            SetStatus(AccountEmail.Describe(outcome).Resolve(), outcome == ResendOutcome.Sent ? DashTheme.Positive : DashTheme.Warning);
            ApplyEmailState();
        });
    }

    private async void CheckFromCard()
    {
        if (_checking)
            return;
        var world = World;
        _checking = true;
        RefreshCardPills();
        await AccountEmail.RecheckAsync(force: true);
        OnUi(world, () =>
        {
            _checking = false;
            bool verified = AccountEmail.Known && AccountEmail.Verified;
            SetStatus((verified ? AccountEmailLocale.NowVerified : AccountEmailLocale.StillNotVerified).Resolve(),
                verified ? DashTheme.Positive : DashTheme.Warning);
            ApplyEmailState();
        });
    }

    // NOTICE
    //
    // Once per account a run, right after a sign in finds something held: the dashboard's own modal, with the way to
    // the mail. Dismissed, it is gone for the run. The account window making an account says the same thing on its own
    // page, so a notice due then is taken and dropped. -xlinka
    private void PollEmailNotice()
    {
        if (!OnDashboard || !AccountEmail.TakeNotice())
            return;
        if (_flowsShowing > 0)
            return;
        var request = new ModalRequest
        {
            Title = AccountFlowLocale.NoticeTitle,
            Message = AccountFlowLocale.NoticeText(AccountEmail.SavesHeld, AccountEmail.JoinsHeld, AccountEmail.Address ?? string.Empty),
            Invoker = Slot,
            CancelIndex = 0,
            Completed = result =>
            {
                if (result.Index == 1)
                    OpenFlow(FlowPage.VerifyEmail, send: true);
            },
        };
        request.AddOption(AccountFlowLocale.Later);
        request.AddOption(AccountFlowLocale.NoticeSend, ModalOptionStyle.Accent);
        if (ModalHost.For(Slot)?.Open(request) != true)
            Logging.Logger.Log("Account: the verify-your-email notice had nowhere to show.");
    }

    // WINDOW

    private void BuildFlowShell()
    {
        var host = Dash?.Slot;
        if (host == null)
            return;
        _flowBackdrop = host.AddSlot("AccountFlowBackdrop");
        var back = _flowBackdrop.AttachComponent<RectTransform>();
        back.AnchorMin.Value = float2.Zero;
        back.AnchorMax.Value = float2.One;
        back.OffsetMin.Value = float2.Zero;
        back.OffsetMax.Value = float2.Zero;
        _flowBackdrop.OrderOffset.Value = 9000L;
        _flowBackdrop.AttachComponent<GraphicChunkRoot>().OverlayLevel = 1;
        // A click outside the window does nothing here: half a sign-up lost to a stray click is worse than one more
        // press on Later or Not now.
        _flowBackdrop.AttachComponent<Button>();
        _flowBackdrop.AttachComponent<Image>().Tint.Value = ScrimFill;
        _flowBackdrop.ActiveSelf.Value = false;

        _flowPanel = host.AddSlot("AccountFlow");
        _flowRect = _flowPanel.AttachComponent<RectTransform>();
        _flowRect.AnchorMin.Value = new float2(0.5f, 0.5f);
        _flowRect.AnchorMax.Value = new float2(0.5f, 0.5f);
        _flowPanel.OrderOffset.Value = 10000L;
        _flowPanel.AttachComponent<GraphicChunkRoot>().OverlayLevel = 2;
        SettingsUI.Panel(_flowPanel, DashTheme.Panel, DashTheme.OutlineStrong, DashTheme.RadiusPanel);
        _flowPanel.AttachComponent<Button>();
        _flowPanel.ActiveSelf.Value = false;
    }

    private void DestroyFlow()
    {
        if (_flowBackdrop != null && !_flowBackdrop.IsDestroyed)
            _flowBackdrop.Destroy();
        if (_flowPanel != null && !_flowPanel.IsDestroyed)
            _flowPanel.Destroy();
        if (_flowOpen)
            _flowsShowing = System.Math.Max(0, _flowsShowing - 1);
        _flowOpen = false;
    }

    private void OpenFlow(FlowPage page, bool send = false)
    {
        if (_flowPanel == null || _flowPanel.IsDestroyed)
            return;
        if (page != FlowPage.Register && !SignedIn)
            return;
        CloseSignInDialog();
        // The window's keys come through the Home screen, and only the screen in front gets keys.
        HomeHost?.MakeRoomForAccount();
        if (!_flowOpen)
            _flowsShowing++;
        _flowOpen = true;
        SetActive(_flowBackdrop, true);
        SetActive(_flowPanel, true);
        ShowPage(page);
        if (send)
            SendFromFlow();
    }

    public void CloseFlow()
    {
        if (!_flowOpen)
            return;
        _flowOpen = false;
        _flowsShowing = System.Math.Max(0, _flowsShowing - 1);
        _flowSerial++;
        _page = FlowPage.None;
        ForgetTyped();
        _setup = null;
        _focus = Focus.None;
        SetActive(_flowBackdrop, false);
        SetActive(_flowPanel, false);
        _flowContent?.Destroy();
        _flowContent = null;
        Repaint();
    }

    private void ForgetTyped()
    {
        _newPassword = string.Empty;
        _newConfirm = string.Empty;
        _totp = string.Empty;
        _flowErrors.Clear();
    }

    private void ShowPage(FlowPage page)
    {
        if (_flowPanel == null || _flowPanel.IsDestroyed || _flowRect == null)
            return;
        _flowSerial++;
        _page = page;
        _flowBusy = false;
        _flowFields.Clear();
        _flowStatus = null;
        _flowSend = null;
        _waitingDots = null;
        _flowContent?.Destroy();
        _flowContent = SettingsUI.Fill(_flowPanel, "Page");
        float height = page switch
        {
            FlowPage.Register => BuildRegisterPage(_flowContent),
            FlowPage.TwoFactorOffer => BuildOfferPage(_flowContent),
            FlowPage.TwoFactorSetup => BuildSetupPage(_flowContent),
            FlowPage.RecoveryCodes => BuildCodesPage(_flowContent),
            FlowPage.VerifyEmail => BuildVerifyPage(_flowContent),
            FlowPage.Verified => BuildVerifiedPage(_flowContent),
            _ => 200f,
        };
        SettingsUI.SetOffsets(_flowRect, new float2(-FlowWidth * 0.5f, -height * 0.5f), new float2(FlowWidth * 0.5f, height * 0.5f));
        _focus = page switch
        {
            FlowPage.Register => Focus.NewName,
            FlowPage.TwoFactorSetup => Focus.Totp,
            _ => Focus.None,
        };
        UpdateFlowFields();
        Repaint();
    }

    // The next page once the account exists: the lock while it is off, then the email while it waits, then done.
    private void ShowAfter(FlowPage done)
    {
        if (done is FlowPage.Register && !_twoFactorOn)
        {
            ShowPage(FlowPage.TwoFactorOffer);
            return;
        }
        if (!EmailVerifiedNow)
        {
            ShowPage(FlowPage.VerifyEmail);
            return;
        }
        CloseFlow();
    }

    // PAGE PARTS

    private Text FlowTitle(Slot page, string text, float top, float left = 0f, float size = 22f)
        => Label(page, "Title", text, size, DashTheme.Text, TextHorizontalAlignment.Left, new float2(0f, 1f), new float2(1f, 1f),
            new float2(DialogPad + left, -(top + 32f)), new float2(-DialogPad, -top), BoldFont);

    private Text FlowText(Slot page, string name, string text, float top, float height, float size, in color tint,
        float left = 0f, float right = 0f, TextHorizontalAlignment align = TextHorizontalAlignment.Left)
    {
        var label = Label(page, name, text, size, tint, align, new float2(0f, 1f), new float2(1f, 1f),
            new float2(DialogPad + left, -(top + height)), new float2(-(DialogPad + right), -top));
        label.WordWrap.Value = true;
        label.Ellipsis.Value = false;
        label.VerticalAlignment.Value = TextVerticalAlignment.Top;
        return label;
    }

    private Text FlowStatusAt(Slot page, float top, float height = 40f)
    {
        _flowStatus = FlowText(page, "Status", string.Empty, top, height, DashTheme.FontSmall, DashTheme.TextDim);
        return _flowStatus;
    }

    private void SetFlowStatus(LocaleText text, in color tint)
    {
        if (_flowStatus == null || _flowStatus.IsDestroyed)
            return;
        ListingStyle.SetText(_flowStatus, text.Resolve());
        ListingStyle.SetTextColor(_flowStatus, tint);
        Repaint();
    }

    private Pill FlowButton(Slot page, string name, string label, float left, float top, float width, bool accent, Action onClick)
    {
        var slot = SettingsUI.Child(page, name, new float2(0f, 1f), new float2(0f, 1f),
            new float2(DialogPad + left, -(top + ActionHeight)), new float2(DialogPad + left + width, -top));
        var pill = AttachPill(slot, label, DashTheme.FontBody, onClick);
        if (accent)
            pill.SetRamp(DashTheme.Accent, DashTheme.AccentHover, DashTheme.AccentPressed, WidgetScreen.OnFill(DashTheme.Accent));
        else
            pill.SetRamp(DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.Text);
        return pill;
    }

    private void FlowField(Slot page, Focus focus, LocaleText label, LocaleText hint, float top, float left = 0f, float right = 0f)
    {
        var slot = SettingsUI.Child(page, focus.ToString(), new float2(0f, 1f), new float2(1f, 1f),
            new float2(DialogPad + left, -(top + FieldHeight)), new float2(-(DialogPad + right), -top));
        CardLabel(slot, "Label", label.Resolve(), 0f, 0f, 0f, 18f, 13f, DashTheme.TextDim);
        var entry = Row(slot, "Entry", 22f, 44f, 0f, 0f);
        var field = new Field
        {
            Slot = slot,
            Panel = SettingsUI.Panel(entry, DashTheme.Field, DashTheme.Outline, DashTheme.RadiusControl),
            Button = entry.AttachComponent<Button>(),
        };
        entry.AttachComponent<Mask>().ShowMaskGraphic.Value = true;
        field.Text = Label(entry, "Text", string.Empty, focus == Focus.Totp ? 24f : 16f, DashTheme.TextMuted,
            TextHorizontalAlignment.Left, float2.Zero, new float2(1f, 1f), new float2(12f, 0f), new float2(-12f, 0f),
            focus == Focus.Totp ? MonoFont : null);
        field.Text.WordWrap.Value = false;
        field.Text.KeepCaretVisible.Value = true;
        field.Button.Clicked += (_, _) => SetFocus(focus);
        var hintText = Label(page, focus + "Hint", hint.Resolve(), 12f, DashTheme.TextMuted, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(DialogPad + left, -(top + FieldHeight + 20f)),
            new float2(-(DialogPad + right), -(top + FieldHeight + 2f)));
        _flowFields[focus] = (field, hintText, hint);
    }

    private IAssetProvider<FontSet>? MonoFont => Dash?.FontMono.Target ?? SemiboldFont;

    private void UpdateFlowFields()
    {
        if (!_flowOpen)
            return;
        foreach (var (focus, parts) in _flowFields)
        {
            string value = focus switch
            {
                Focus.NewName => _newName,
                Focus.NewEmail => _newEmail,
                Focus.NewPassword => new string('•', _newPassword.Length),
                Focus.NewConfirm => new string('•', _newConfirm.Length),
                Focus.Totp => Spaced(_totp),
                _ => string.Empty,
            };
            Paint(parts.Field, focus, value, string.Empty);
            if (_flowErrors.TryGetValue(focus, out var error))
            {
                SettingsUI.SetPaint(parts.Field.Panel, DashTheme.Field, _focus == focus ? DashTheme.Accent : DashTheme.Negative);
                ListingStyle.SetText(parts.Hint, error);
                ListingStyle.SetTextColor(parts.Hint, DashTheme.Negative);
            }
            else
            {
                ListingStyle.SetText(parts.Hint, parts.HintText.Resolve());
                ListingStyle.SetTextColor(parts.Hint, DashTheme.TextMuted);
            }
        }
    }

    private static string Spaced(string digits) => digits.Length <= 3 ? digits : digits[..3] + " " + digits[3..];

    // KEYS

    private string? FlowCopyText => _focus switch
    {
        Focus.NewName => _newName,
        Focus.NewEmail => _newEmail,
        Focus.Totp => _totp,
        _ => null,
    };

    private bool FlowChar(char c)
    {
        if (_focus == Focus.None || char.IsControl(c) || _flowBusy)
            return true;
        switch (_focus)
        {
            case Focus.NewName when _newName.Length < NameMax:
                _newName += c;
                break;
            case Focus.NewEmail when _newEmail.Length < 254 && !char.IsWhiteSpace(c):
                _newEmail += c;
                break;
            case Focus.NewPassword when _newPassword.Length < PasswordMax:
                _newPassword += c;
                break;
            case Focus.NewConfirm when _newConfirm.Length < PasswordMax:
                _newConfirm += c;
                break;
            case Focus.Totp when _totp.Length < 6 && c >= '0' && c <= '9':
                _totp += c;
                break;
        }
        _flowErrors.Remove(_focus);
        UpdateFlowFields();
        Repaint();
        return true;
    }

    private bool FlowBackspace()
    {
        if (_flowBusy)
            return true;
        switch (_focus)
        {
            case Focus.NewName:
                _newName = Chop(_newName);
                break;
            case Focus.NewEmail:
                _newEmail = Chop(_newEmail);
                break;
            case Focus.NewPassword:
                _newPassword = Chop(_newPassword);
                break;
            case Focus.NewConfirm:
                _newConfirm = Chop(_newConfirm);
                break;
            case Focus.Totp:
                _totp = Chop(_totp);
                break;
        }
        UpdateFlowFields();
        Repaint();
        return true;
    }

    // Enter walks down the form and sends it from the last field.
    private bool FlowEnter()
    {
        switch (_page)
        {
            case FlowPage.Register:
                if (_focus is Focus.NewName or Focus.NewEmail or Focus.NewPassword)
                    SetFocus(_focus + 1);
                else
                    SubmitRegister();
                break;
            case FlowPage.TwoFactorSetup:
                ConfirmTwoFactor();
                break;
        }
        return true;
    }

    // REGISTER

    private float BuildRegisterPage(Slot page)
    {
        FlowTitle(page, AccountFlowLocale.RegisterTitle.Resolve(), 24f);
        FlowText(page, "Subtitle", AccountFlowLocale.RegisterSubtitle.Resolve(), 58f, 20f, 14f, DashTheme.TextDim);
        FlowField(page, Focus.NewName, AccountFlowLocale.FieldName, AccountFlowLocale.FieldNameHint, 92f);
        FlowField(page, Focus.NewEmail, AccountFlowLocale.FieldEmail, AccountFlowLocale.FieldEmailHint, 92f + FlowFieldBlock);
        FlowField(page, Focus.NewPassword, AccountFlowLocale.FieldPassword, AccountFlowLocale.FieldPasswordHint, 92f + FlowFieldBlock * 2f);
        FlowField(page, Focus.NewConfirm, AccountFlowLocale.FieldConfirm, AccountFlowLocale.FieldConfirmHint, 92f + FlowFieldBlock * 3f);
        float buttons = 92f + FlowFieldBlock * 4f + 4f;
        FlowButton(page, "Submit", AccountFlowLocale.RegisterSubmit.Resolve(), 0f, buttons, 250f, true, SubmitRegister);
        FlowButton(page, "Back", AccountFlowLocale.RegisterBack.Resolve(), 250f + DashTheme.Gap + 4f, buttons, 150f, false, () =>
        {
            CloseFlow();
            OnLoginPressed();
        });
        FlowStatusAt(page, buttons + ActionHeight + 10f);
        return buttons + ActionHeight + 60f;
    }

    // The same rules the account service holds a new account to, checked here first so a typo costs no round trip.
    private bool CheckRegister()
    {
        _flowErrors.Clear();
        string name = _newName.Trim();
        if (name.Length < NameMin || name.Length > NameMax)
            _flowErrors[Focus.NewName] = AccountFlowLocale.ErrorNameLength.Resolve();
        else if (!NamePattern.IsMatch(name))
            _flowErrors[Focus.NewName] = AccountFlowLocale.ErrorNameChars.Resolve();
        string email = _newEmail.Trim();
        int at = email.IndexOf('@');
        if (at <= 0 || at != email.LastIndexOf('@') || at == email.Length - 1)
            _flowErrors[Focus.NewEmail] = AccountFlowLocale.ErrorEmail.Resolve();
        if (!PasswordFits(_newPassword))
            _flowErrors[Focus.NewPassword] = AccountFlowLocale.ErrorPassword.Resolve();
        if (_newConfirm != _newPassword)
            _flowErrors[Focus.NewConfirm] = AccountFlowLocale.ErrorConfirm.Resolve();
        return _flowErrors.Count == 0;
    }

    private static bool PasswordFits(string password)
    {
        if (password.Length < PasswordMin || password.Length > PasswordMax)
            return false;
        bool upper = false, lower = false, digit = false, symbol = false;
        foreach (char c in password)
        {
            upper |= char.IsUpper(c);
            lower |= char.IsLower(c);
            digit |= char.IsDigit(c);
            symbol |= !char.IsLetterOrDigit(c);
        }
        return upper && lower && digit && symbol;
    }

    private async void SubmitRegister()
    {
        if (_flowBusy || _page != FlowPage.Register)
            return;
        var client = Client;
        if (client == null || !Connectivity.IsOnline)
        {
            SetFlowStatus(AccountFlowLocale.Offline, DashTheme.Warning);
            return;
        }
        if (!CheckRegister())
        {
            foreach (var focus in new[] { Focus.NewName, Focus.NewEmail, Focus.NewPassword, Focus.NewConfirm })
            {
                if (_flowErrors.ContainsKey(focus))
                {
                    _focus = focus;
                    break;
                }
            }
            UpdateFlowFields();
            Repaint();
            return;
        }

        var world = World;
        int serial = _flowSerial;
        string name = _newName.Trim();
        string email = _newEmail.Trim();
        string password = _newPassword;
        _flowBusy = true;
        _focus = Focus.None;
        UpdateFlowFields();
        SetFlowStatus(AccountFlowLocale.RegisterBusy, DashTheme.TextDim);

        var made = await client.Register(name, email, password);
        if (!made.Success)
        {
            OnUi(world, () =>
            {
                if (serial != _flowSerial)
                    return;
                _flowBusy = false;
                ShowRegisterRefusal(made);
            });
            return;
        }

        OnUi(world, () =>
        {
            if (serial == _flowSerial)
                SetFlowStatus(AccountFlowLocale.SigningIn, DashTheme.TextDim);
        });
        _username = name;
        var signed = await client.SignIn(name, password, _remember, null);
        OnUi(world, () =>
        {
            if (serial != _flowSerial)
                return;
            _flowBusy = false;
            ForgetTyped();
            if (!signed.Success)
            {
                SetFlowStatus(AccountFlowLocale.SignInAfterFailed, DashTheme.Warning);
                return;
            }
            ProfileName = name;
            ShowPage(FlowPage.TwoFactorOffer);
        });
    }

    // The service's own words, put on the field they are about: a bad field from its form check comes keyed by name,
    // anything else as one message that names the field it means.
    private void ShowRegisterRefusal(ApiResponse refusal)
    {
        _flowErrors.Clear();
        SetFlowStatus(LocaleText.Plain(string.Empty), DashTheme.TextDim);
        if (refusal.Status == HttpStatusCode.Conflict)
            _flowErrors[Focus.NewName] = AccountFlowLocale.ErrorNameTaken.Resolve();
        else if (refusal.Status == HttpStatusCode.TooManyRequests)
            SetFlowStatus(AccountFlowLocale.ErrorTooMany, DashTheme.Warning);
        else if (refusal.Status == HttpStatusCode.BadRequest)
            ReadFieldErrors(refusal.RawBody, refusal.Message);
        else
            SetFlowStatus((int)refusal.Status == 0 || refusal.Status == HttpStatusCode.ServiceUnavailable
                ? AccountFlowLocale.Offline : AccountFlowLocale.ErrorFailed, DashTheme.Warning);
        foreach (var focus in new[] { Focus.NewName, Focus.NewEmail, Focus.NewPassword })
        {
            if (_flowErrors.ContainsKey(focus))
            {
                _focus = focus;
                break;
            }
        }
        UpdateFlowFields();
        Repaint();
    }

    private void ReadFieldErrors(string? body, string? message)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in errors.EnumerateObject())
                {
                    var focus = FieldOf(entry.Name);
                    if (focus != Focus.None && entry.Value.ValueKind == JsonValueKind.Array && entry.Value.GetArrayLength() > 0)
                        _flowErrors[focus] = entry.Value[0].GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
        }
        if (_flowErrors.Count > 0 || string.IsNullOrEmpty(message))
        {
            if (_flowErrors.Count == 0)
                SetFlowStatus(AccountFlowLocale.ErrorFailed, DashTheme.Warning);
            return;
        }
        if (message.Contains("email", StringComparison.OrdinalIgnoreCase))
            _flowErrors[Focus.NewEmail] = message.Contains("registered", StringComparison.OrdinalIgnoreCase)
                ? AccountFlowLocale.ErrorEmailTaken.Resolve() : message;
        else if (message.Contains("password", StringComparison.OrdinalIgnoreCase))
            _flowErrors[Focus.NewPassword] = message;
        else if (message.Contains("username", StringComparison.OrdinalIgnoreCase))
            _flowErrors[Focus.NewName] = message;
        else
            SetFlowStatus(LocaleText.Plain(message), DashTheme.Warning);
    }

    private static Focus FieldOf(string key) => key.ToLowerInvariant() switch
    {
        "username" => Focus.NewName,
        "email" => Focus.NewEmail,
        "password" => Focus.NewPassword,
        _ => Focus.None,
    };

    // TWO-FACTOR

    private float BuildOfferPage(Slot page)
    {
        FlowTitle(page, ProfileName.Length > 0 ? AccountFlowLocale.OfferTitle(ProfileName).Resolve() : AccountFlowLocale.SetUp.Resolve(), 24f);
        var tile = SettingsUI.Child(page, "Lock", new float2(0f, 1f), new float2(0f, 1f), new float2(DialogPad, -142f), new float2(DialogPad + 60f, -82f));
        SettingsUI.Panel(tile, DashTheme.AccentSoft, color.Transparent, 30f);
        var glyph = SettingsUI.Child(tile, "Glyph", float2.Zero, float2.One, new float2(15f, 15f), new float2(-15f, -15f));
        var icon = glyph.AttachComponent<ImageProvider>();
        icon.GenerateMipmaps.Value = false;
        icon.URL.Value = new Uri("res://Assets/Textures/Icons/lock.svg");
        var image = glyph.AttachComponent<Image>();
        image.Texture.Target = icon;
        image.Tint.Value = DashTheme.Accent;
        Label(page, "Lead", AccountFlowLocale.OfferLead.Resolve(), 18f, DashTheme.Text, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(DialogPad + 78f, -106f), new float2(-DialogPad, -82f), SemiboldFont);
        FlowText(page, "Text", AccountFlowLocale.OfferText.Resolve(), 110f, 76f, 14f, DashTheme.TextDim, left: 78f);
        FlowButton(page, "SetUp", AccountFlowLocale.SetUp.Resolve(), 0f, 202f, 230f, true, StartTwoFactor);
        FlowButton(page, "Skip", AccountFlowLocale.NotNow.Resolve(), 230f + DashTheme.Gap + 4f, 202f, 150f, false,
            () => ShowAfter(FlowPage.TwoFactorOffer));
        FlowStatusAt(page, 256f);
        return 306f;
    }

    private async void StartTwoFactor()
    {
        var client = Client;
        if (_flowBusy || client == null)
            return;
        var world = World;
        int serial = _flowSerial;
        _flowBusy = true;
        SetFlowStatus(AccountFlowLocale.Starting, DashTheme.TextDim);
        var setup = await client.Enable2FA();
        OnUi(world, () =>
        {
            if (serial != _flowSerial)
                return;
            _flowBusy = false;
            if (setup.Success && setup.Data != null && !string.IsNullOrEmpty(setup.Data.QrCode))
            {
                _setup = setup.Data;
                ShowPage(FlowPage.TwoFactorSetup);
                return;
            }
            bool already = setup.Message?.Contains("already enabled", StringComparison.OrdinalIgnoreCase) == true;
            if (already)
                _twoFactorOn = true;
            SetFlowStatus(already ? AccountFlowLocale.TwoFactorAlreadyOn : AccountFlowLocale.TwoFactorFailed, DashTheme.Warning);
        });
    }

    private float BuildSetupPage(Slot page)
    {
        var setup = _setup;
        FlowTitle(page, AccountFlowLocale.ScanTitle.Resolve(), 24f, size: 20f);
        // White card behind the code: phone cameras read black on white, whatever the dash's own colours are.
        var card = SettingsUI.Child(page, "Qr", new float2(0f, 1f), new float2(0f, 1f), new float2(DialogPad, -(70f + QrSize)), new float2(DialogPad + QrSize, -70f));
        SettingsUI.Panel(card, color.White, color.Transparent, 10f);
        var code = SettingsUI.Child(card, "Code", float2.Zero, float2.One, new float2(6f, 6f), new float2(-6f, -6f));
        var texture = code.AttachComponent<QrTextureProvider>();
        var image = code.AttachComponent<Image>();
        image.Texture.Target = texture;
        image.Tint.Value = color.White;
        // A frame later, once the image is listening for the texture.
        if (setup != null)
            World?.RunInUpdates(1, () =>
            {
                if (!texture.IsDestroyed)
                    texture.Show(setup.QrCode);
            });

        float left = QrSize + 22f;
        FlowText(page, "KeyLabel", AccountFlowLocale.KeyLabel.Resolve(), 70f, 18f, 13f, DashTheme.TextDim, left: left, right: 64f);
        var copy = SettingsUI.Child(page, "Copy", new float2(1f, 1f), new float2(1f, 1f), new float2(-(DialogPad + 60f), -90f), new float2(-DialogPad, -68f));
        var copyText = Label(copy, "Text", AccountFlowLocale.CopyKey.Resolve(), 13f, DashTheme.Accent, TextHorizontalAlignment.Right,
            float2.Zero, float2.One, float2.Zero, float2.Zero, SemiboldFont);
        var copyButton = copy.AttachComponent<Button>();
        var ink = copyButton.AddColorDriver(copyText.Color, DashTheme.Accent, InteractionColorMode.Direct);
        ink.HighlightColor.Value = DashTheme.AccentHover;
        ink.PressedColor.Value = DashTheme.AccentPressed;
        copyButton.Clicked += (_, _) =>
        {
            var clipboard = Engine.Current?.InputInterface?.ClipboardText;
            if (clipboard == null || _setup == null)
                return;
            clipboard.SetText(_setup.Secret);
            SetFlowStatus(AccountFlowLocale.Copied, DashTheme.TextDim);
        };
        var key = FlowText(page, "Key", Grouped(setup?.Secret ?? string.Empty), 94f, 60f, 17f, DashTheme.Text, left: left);
        key.Font.Target = MonoFont!;
        FlowField(page, Focus.Totp, AccountFlowLocale.CodeLabel, LocaleText.Plain(string.Empty), 160f, left: left);
        FlowButton(page, "TurnOn", AccountFlowLocale.TurnOn.Resolve(), left, 254f, FlowWidth - DialogPad * 2f - left, true, ConfirmTwoFactor);
        FlowStatusAt(page, 70f + QrSize + 14f, 36f);
        FlowButton(page, "Skip", AccountFlowLocale.NotNow.Resolve(), 0f, 70f + QrSize + 56f, 150f, false, () => ShowAfter(FlowPage.TwoFactorSetup));
        return 70f + QrSize + 56f + ActionHeight + DialogPad;
    }

    // "JBSW Y3DP EHPK ...": four at a time is how a key gets typed in without losing the place.
    private static string Grouped(string secret)
    {
        var grouped = new StringBuilder(secret.Length + secret.Length / 4);
        for (int i = 0; i < secret.Length; i++)
        {
            if (i > 0 && i % 4 == 0)
                grouped.Append(' ');
            grouped.Append(secret[i]);
        }
        return grouped.ToString();
    }

    private async void ConfirmTwoFactor()
    {
        var client = Client;
        if (_flowBusy || client == null || _page != FlowPage.TwoFactorSetup)
            return;
        if (_totp.Length != 6)
        {
            _flowErrors[Focus.Totp] = AccountFlowLocale.SixDigits.Resolve();
            UpdateFlowFields();
            Repaint();
            return;
        }
        var world = World;
        int serial = _flowSerial;
        string code = _totp;
        _flowBusy = true;
        SetFlowStatus(AccountFlowLocale.Checking, DashTheme.TextDim);
        var answer = await client.Verify2FA(code);
        OnUi(world, () =>
        {
            if (serial != _flowSerial)
                return;
            _flowBusy = false;
            if (answer.Success)
            {
                _twoFactorOn = true;
                _totp = string.Empty;
                ShowPage(FlowPage.RecoveryCodes);
                ApplyEmailState();
                return;
            }
            _totp = string.Empty;
            _focus = Focus.Totp;
            _flowErrors[Focus.Totp] = (answer.Status == HttpStatusCode.BadRequest ? AccountFlowLocale.WrongCode : AccountFlowLocale.TwoFactorFailed).Resolve();
            SetFlowStatus(LocaleText.Plain(string.Empty), DashTheme.TextDim);
            UpdateFlowFields();
            Repaint();
        });
    }

    private float BuildCodesPage(Slot page)
    {
        var badge = SettingsUI.Child(page, "Badge", new float2(0f, 1f), new float2(0f, 1f), new float2(DialogPad, -62f), new float2(DialogPad + 38f, -24f));
        SettingsUI.Panel(badge, DashTheme.Positive, color.Transparent, 19f);
        CheckIcon(badge, DashTheme.TextOnLight);
        FlowTitle(page, AccountFlowLocale.OnTitle.Resolve(), 27f, left: 52f);
        FlowText(page, "Text", AccountFlowLocale.CodesText.Resolve(), 76f, 44f, 14f, DashTheme.TextDim);
        var box = SettingsUI.Child(page, "Codes", new float2(0f, 1f), new float2(1f, 1f), new float2(DialogPad, -276f), new float2(-DialogPad, -132f));
        SettingsUI.Panel(box, DashTheme.Field, DashTheme.OutlineStrong, DashTheme.RadiusCard);
        var codes = _setup?.RecoveryCodes ?? new List<string>();
        for (int i = 0; i < codes.Count && i < 10; i++)
        {
            float column = i % 2 == 0 ? 0f : 0.5f;
            float top = 14f + (i / 2) * 30f;
            var cell = SettingsUI.Child(box, "Code" + i, new float2(column, 1f), new float2(column + 0.5f, 1f),
                new float2(18f, -(top + 28f)), new float2(-8f, -top));
            var text = SettingsUI.FillLabel(cell, "Text", MonoFont, 18f, DashTheme.Text, TextHorizontalAlignment.Left);
            text.Content.Value = codes[i];
        }
        FlowButton(page, "Saved", AccountFlowLocale.SavedThem.Resolve(), 0f, 296f, 200f, true, () => ShowAfter(FlowPage.RecoveryCodes));
        return 296f + ActionHeight + DialogPad;
    }

    // EMAIL

    private float BuildVerifyPage(Slot page)
    {
        var badge = SettingsUI.Child(page, "Badge", new float2(0f, 1f), new float2(0f, 1f), new float2(DialogPad, -84f), new float2(DialogPad + 60f, -24f));
        SettingsUI.Panel(badge, DashTheme.AccentSoft, color.Transparent, 30f);
        var at = SettingsUI.FillLabel(badge, "At", BoldFont, 30f, DashTheme.Accent);
        at.Content.Value = "@";
        FlowTitle(page, AccountFlowLocale.VerifyTitle.Resolve(), 38f, left: 78f);
        FlowText(page, "Text", AccountFlowLocale.VerifyText(AccountEmail.Address ?? string.Empty).Resolve(), 100f, 44f, 15f, DashTheme.TextDim);
        _waitingDots = FlowText(page, "Waiting", AccountFlowLocale.Waiting.Resolve(), 154f, 22f, 14f, DashTheme.Accent);
        FlowStatusAt(page, 182f, 40f);
        _flowSend = FlowButton(page, "Send", AccountEmailLocale.SendAgain.Resolve(), 0f, 230f, 180f, true, SendFromFlow);
        FlowButton(page, "Check", AccountEmailLocale.Check.Resolve(), 180f + DashTheme.Gap + 4f, 230f, 144f, false, CheckFromFlow);
        FlowButton(page, "Later", AccountFlowLocale.Later.Resolve(), 324f + (DashTheme.Gap + 4f) * 2f, 230f, 104f, false, CloseFlow);
        RefreshFlowSend();
        PollVerify(_flowSerial);
        return 230f + ActionHeight + DialogPad + 8f;
    }

    // While the page is up the account is read every few seconds, so the click on the link in the mail shows here on
    // its own. The dots under the address are the sign that it is still looking.
    private void PollVerify(int serial)
    {
        World?.RunInSeconds(VerifyPollSeconds, () =>
        {
            if (IsDestroyed || serial != _flowSerial || _page != FlowPage.VerifyEmail || !_flowOpen)
                return;
            _dots = (_dots + 1) % 4;
            if (_waitingDots != null && !_waitingDots.IsDestroyed)
                ListingStyle.SetText(_waitingDots, AccountFlowLocale.Waiting.Resolve() + new string('.', _dots));
            _ = AccountEmail.RecheckAsync(force: true);
            Repaint();
            PollVerify(serial);
        });
    }

    private void RefreshFlowSend()
    {
        var pill = _flowSend;
        if (pill == null || pill.Text.IsDestroyed)
            return;
        int wait = AccountEmail.ResendWaitSeconds;
        bool live = wait == 0 && !_sending;
        ListingStyle.SetText(pill.Text, _sending ? AccountEmailLocale.Sending.Resolve()
            : wait > 0 ? AccountEmailLocale.SendAgainIn(wait).Resolve() : AccountEmailLocale.SendAgain.Resolve());
        ListingStyle.SetInteractable(pill.Button, live);
        if (live)
            pill.SetRamp(DashTheme.Accent, DashTheme.AccentHover, DashTheme.AccentPressed, WidgetScreen.OnFill(DashTheme.Accent));
        else
            pill.SetRamp(DashTheme.Field, DashTheme.Field, DashTheme.Field, DashTheme.TextMuted);
        if (wait > 0)
            TickCooldown();
    }

    private async void SendFromFlow()
    {
        if (_sending || AccountEmail.ResendWaitSeconds > 0)
            return;
        var world = World;
        int serial = _flowSerial;
        _sending = true;
        RefreshFlowSend();
        var outcome = await AccountEmail.ResendAsync();
        OnUi(world, () =>
        {
            _sending = false;
            RefreshFlowSend();
            RefreshCardPills();
            if (serial == _flowSerial)
                SetFlowStatus(AccountEmail.Describe(outcome), outcome == ResendOutcome.Sent ? DashTheme.Positive : DashTheme.Warning);
            if (outcome == ResendOutcome.AlreadyVerified && _page == FlowPage.VerifyEmail)
                ShowPage(FlowPage.Verified);
        });
    }

    private async void CheckFromFlow()
    {
        if (_checking)
            return;
        var world = World;
        int serial = _flowSerial;
        _checking = true;
        SetFlowStatus(AccountEmailLocale.Checking, DashTheme.TextDim);
        await AccountEmail.RecheckAsync(force: true);
        OnUi(world, () =>
        {
            _checking = false;
            if (serial != _flowSerial)
                return;
            if (AccountEmail.Known && AccountEmail.Verified)
                ShowPage(FlowPage.Verified);
            else
                SetFlowStatus(AccountEmailLocale.StillNotVerified, DashTheme.Warning);
        });
    }

    // The click that lands: a green seal that grows into place, the words, and what it opened up.
    private float BuildVerifiedPage(Slot page)
    {
        const float badge = 112f;
        var seal = SettingsUI.Child(page, "Seal", new float2(0.5f, 1f), new float2(0.5f, 1f),
            new float2(-badge * 0.5f, -(30f + badge)), new float2(badge * 0.5f, -30f));
        var glow = SettingsUI.Child(seal, "Glow", float2.Zero, float2.One, new float2(-10f, -10f), new float2(10f, 10f));
        SettingsUI.Panel(glow, new color(DashTheme.Positive.r, DashTheme.Positive.g, DashTheme.Positive.b, 0.18f), color.Transparent, badge * 0.5f + 10f);
        var disc = SettingsUI.Child(seal, "Disc", float2.Zero, float2.One, float2.Zero, float2.Zero);
        SettingsUI.Panel(disc, DashTheme.Positive, color.Transparent, badge * 0.5f);
        var check = SettingsUI.Child(disc, "Check", float2.Zero, float2.One, new float2(26f, 26f), new float2(-26f, -26f));
        var icon = check.AttachComponent<ImageProvider>();
        icon.GenerateMipmaps.Value = false;
        icon.URL.Value = new Uri("res://Assets/Textures/Icons/check.svg");
        var mark = check.AttachComponent<Image>();
        mark.Texture.Target = icon;
        mark.Tint.Value = DashTheme.TextOnLight;
        FlowText(page, "Title", AccountFlowLocale.VerifiedTitle.Resolve(), 30f + badge + 22f, 36f, 28f, DashTheme.Text,
            align: TextHorizontalAlignment.Center).Font.Target = BoldFont!;
        FlowText(page, "Text", AccountFlowLocale.VerifiedText.Resolve(), 30f + badge + 64f, 44f, 15f, DashTheme.TextDim,
            align: TextHorizontalAlignment.Center);
        float buttons = 30f + badge + 124f;
        FlowButton(page, "Done", AccountFlowLocale.Done.Resolve(), (FlowWidth - DialogPad * 2f - 200f) * 0.5f, buttons, 200f, true, CloseFlow);
        GrowSeal(SettingsUI.Rect(seal), badge, _flowSerial, 0f);
        return buttons + ActionHeight + DialogPad + 8f;
    }

    private void GrowSeal(RectTransform rect, float size, int serial, float elapsed)
    {
        float t = System.Math.Clamp(elapsed / BadgeGrowSeconds, 0f, 1f);
        // ease out with a little overshoot, so it lands rather than slides
        float s = 1f + 2.2f * MathF.Pow(t - 1f, 3f) + 1.2f * MathF.Pow(t - 1f, 2f);
        float half = size * 0.5f * System.Math.Max(0.05f, s);
        float centre = 30f + size * 0.5f;
        SettingsUI.SetOffsets(rect, new float2(-half, -(centre + half)), new float2(half, -(centre - half)));
        Repaint();
        if (t >= 1f)
            return;
        World?.RunInUpdates(1, () =>
        {
            if (!IsDestroyed && serial == _flowSerial && !rect.IsDestroyed)
                GrowSeal(rect, size, serial, elapsed + (float)(World?.Time.Delta ?? 1.0 / 60.0));
        });
    }
}
