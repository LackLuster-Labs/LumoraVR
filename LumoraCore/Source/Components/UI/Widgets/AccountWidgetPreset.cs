// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using Helio.UI;
using Helio.UI.Layout;
using Helio.UI.Listing;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// A compact profile header followed by a separate cloud-storage section.
// The form that used to live in the card is a modal on the dashboard slot now, so the card stays a card
// and the typing happens in one place.
//
// This is still the only thing in the client that calls the account service sign-in, so read it twice
// before changing it: the typed password lives in a private field and nowhere else, it is never put in a
// sync member, never written to the settings store and never logged. What CAN be remembered is the
// session token the server hands back, and only when the box is ticked. -xlinka
[ComponentCategory("Hidden")]
public sealed partial class AccountWidgetPreset : HomeWidgetPreset
{
    private const string UserKey = "Account.UserId";
    private const string TokenKey = "Account.Token";

    // CARD. The picture and name share the header; storage spans the full width below it.
    private const float AvatarSize = 52f;
    private const float AvatarGap = 12f;
    private const float TextLeft = Pad + AvatarSize + AvatarGap;
    private const float HeadSize = 20f;
    private const float ShoulderWidth = 36f;
    private const float ShoulderHeight = 20f;
    private const float SignOutWidth = 88f;
    private const float SignOutHeight = 36f;
    private const float BarHeight = 7f;
    private const float CardBaseHeight = 196f;
    // The group storage row and its bar, under the account's own.
    private const float GroupRowsHeight = 44f;
    // Right-hand strip the sign-out pill sits in, kept clear of the status line.
    private const float StatusRightInset = Pad + 96f;

    // DIALOG.
    private const float DialogWidth = 460f;
    private const float DialogPad = 28f;
    private const float FieldHeight = 68f;
    private const float RememberHeight = 32f;
    private const float RememberToggleWidth = 200f;
    private const float CodeLinkWidth = 150f;
    private const float StatusHeight = 28f;
    private const float TitleHeight = 28f;
    private const float SubmitWidth = 260f;
    private const float CancelWidth = 132f;
    private const float ActionHeight = 44f;

    // Row tops for the two shapes the dialog takes, and the panel height that goes with each. The
    // two-factor field only shows once the service asks for a code or you open it from the link, and
    // reserving its space up front left the form floating above its own buttons. Order: title, username,
    // password, code, remember, buttons, status.
    private static readonly float[] TopsPlain = { 24f, 88f, 166f, 0f, 244f, 294f, 352f };
    private static readonly float[] TopsWithCode = { 24f, 88f, 166f, 244f, 322f, 372f, 430f };
    private const float DialogHeightPlain = 400f;
    private const float DialogHeightWithCode = 478f;

    // Same dim the create-world modal uses, so two dialogs raised from the same screen read as one thing.
    private static readonly color ScrimFill = DashTheme.ModalScrim;

    // STORAGE. Read when the card comes up, and again whenever the sync says the account's storage may
    // read differently (a save went up or was parked, something was deleted, the out of space flag came or
    // went): at most once every StorageGap seconds, and once more StorageSettle seconds after the last
    // change, by when the service has counted what that change did. -xlinka
    private const double StorageGap = 2.0;
    private const double StorageSettle = 5.0;
    private const double StorageRefresh = 30.0;
    private const long Megabyte = 1024L * 1024L;

    private enum Focus { None, Username, Password, Code, NewName, NewEmail, NewPassword, NewConfirm, Totp }

    private sealed class Pill
    {
        public Slot Slot = null!;
        public RoundedPanel Panel = null!;
        public Text Text = null!;
        public Button Button = null!;
        public ColorDriver? Driver;

        public void SetRamp(in color normal, in color hover, in color pressed, in color textColor)
        {
            SetCardRamp(Driver, normal, hover, pressed);
            ListingStyle.SetTextColor(Text, textColor);
        }
    }

    private sealed class Field
    {
        public Slot Slot = null!;
        public RoundedPanel Panel = null!;
        public Text Text = null!;
        public Button Button = null!;
    }

    // Only one widget may try the remembered token per run, however many copies of this card exist.
    private static bool _resumeTried;

    private string _username = string.Empty;
    private string _password = string.Empty;
    private string _code = string.Empty;
    private bool _remember;
    private bool _needsCode;
    private bool _busy;
    private bool _dialogOpen;
    private Focus _focus = Focus.None;

    private Slot? _signedOutRoot;
    private Slot? _signedInRoot;
    private Pill? _loginPill;
    private Pill? _signOutPill;
    private Text? _status;
    private Slot? _avatar;
    private Text? _name;
    private ProfileLink? _nameLink;
    private color _nameInk = DashTheme.Text;
    private Text? _accountState;
    private Slot? _verified;
    private Text? _storage;
    private Text? _storageHint;
    private RoundedPanel? _barPaint;
    private RectTransform? _barFill;
    private Text? _tier;
    private Slot? _groupRows;
    private Text? _groupTitle;
    private Text? _groupStorage;
    private RoundedPanel? _groupBarPaint;
    private RectTransform? _groupBarFill;

    private Slot? _dialogBackdrop;
    private Slot? _dialogPanel;
    private RectTransform? _dialogRect;
    private RectTransform? _titleRect;
    private Field? _userField;
    private Field? _passField;
    private Field? _codeField;
    private RoundedPanel? _rememberBox;
    private Slot? _rememberCheck;
    private RectTransform? _rememberRect;
    private Button? _rememberButton;
    private Text? _codeLink;
    private RectTransform? _buttonRowRect;
    private Pill? _submit;
    private Pill? _cancel;
    private Text? _dialogStatus;
    private bool _hooked;

    private long _storageSeen;
    private double _storageChangedAt;
    private double _storageReadAt = double.NegativeInfinity;
    private bool _storageDue;
    private bool _storageSettling;
    // Only the newest read is drawn, so an answer that comes back late never paints over a fresher one.
    private int _storageReads;
    private int _accountGeneration;

    private static LumoraClient? Client => Engine.Current?.CDNClient;

    // The Home screen of the dash this card is on (the desk, or the top bar over it). It owns which of the
    // two dashboard modals is up.
    private HomeScreen? HomeHost
    {
        get
        {
            var screen = Slot.GetComponentInParents<HomeScreen>() ?? Slot.GetComponentInParents<Dashboard>()?.DeskScreen as HomeScreen;
            return screen != null && !screen.IsDestroyed ? screen : null;
        }
    }

    // What the dash's top bar shows for this account: the name once the profile is in, empty before that.
    public bool IsSignedIn => SignedIn;
    public string ProfileName { get; private set; } = string.Empty;
    public bool DialogOpen => _dialogOpen || _flowOpen;
    public bool HasStorageUsage { get; private set; }
    public string StorageLabel { get; private set; } = "Loading storage…";
    public long StorageUsedBytes { get; private set; }
    public long StorageLimitBytes { get; private set; }
    public float StorageFraction => !HasStorageUsage ? 0f : StorageLimitBytes <= 0 ? 1f
        : (float)System.Math.Clamp((double)StorageUsedBytes / StorageLimitBytes, 0d, 1d);
    public bool HasGroupStorage { get; private set; }
    public float CardHeight => CardBaseHeight + (HasGroupStorage ? GroupRowsHeight : 0f) + (HasAccountRow ? AccountRowHeight : 0f);

    public AccountWidgetPreset()
    {
        MinSize.Value = new float2(300f, 188f);
        PreferredSize.Value = new float2(360f, 196f);
        MaxSize.Value = new float2(720f, 300f);
    }

    protected override void Build(Widget widget, Slot root)
    {
        _remember = Lumora.Core.Settings.HasValue(TokenKey);

        if (OnDashboard)
        {
            BuildDialog();
            BuildFlowShell();
        }

        BuildAvatar(root);
        BuildSignedOut(root);
        BuildSignedIn(root);
        BuildStatus(root);

        Hook();
        ApplyState();

        _storageSeen = InventorySync.StorageRevision;
        if (Client?.IsAuthenticated == true)
            LoadProfile();
        else
            TryResume();
    }

    public override void OnDestroy()
    {
        Unhook();
        // The dialog hangs off the dashboard slot, not off this widget, so nothing tears it down when the
        // card is dragged off the grid and destroyed. Take it with us. -xlinka
        if (_dialogBackdrop != null && !_dialogBackdrop.IsDestroyed)
            _dialogBackdrop.Destroy();
        if (_dialogPanel != null && !_dialogPanel.IsDestroyed)
            _dialogPanel.Destroy();
        DestroyFlow();
        base.OnDestroy();
    }

    protected override void Poll()
    {
        // Only the pills on the card are gated: the dialog lives above the grid, never under a drag.
        GateForEdit(_loginPill?.Button);
        GateForEdit(_signOutPill?.Button);
        _nameLink?.Bind(_nameLink.Account, _nameInk, !WidgetPanel.EditMode);
        PollStorage();
    }

    // The top bar calls this too: a closed account popover does not receive component updates.
    internal void PollStorage()
    {
        PollEmailNotice();
        double now = World.Time.TotalTime;
        if (SignedIn && now - _storageReadAt >= StorageRefresh)
            _storageDue = true;
        long revision = InventorySync.StorageRevision;
        if (revision != _storageSeen)
        {
            _storageSeen = revision;
            _storageChangedAt = now;
            _storageDue = true;
            _storageSettling = true;
        }
        if (!_storageDue && !_storageSettling)
            return;
        if (!SignedIn)
        {
            _storageDue = false;
            _storageSettling = false;
            return;
        }
        bool settled = now - _storageChangedAt >= StorageSettle;
        if (now - _storageReadAt < StorageGap || (!_storageDue && !settled))
            return;
        _storageDue = false;
        if (settled)
            _storageSettling = false;
        ReadStorage();
    }

    // CARD

    // The disc, and the silhouette that stands in for a picture. Nothing in the profile carries a picture
    // URL, so this is what everyone gets rather than a broken image slot. The head and the shoulder bar sit
    // under a circular stencil mask, which is what lets the shoulders run off the bottom of the disc
    // instead of squaring off inside it. -xlinka
    private void BuildAvatar(Slot root)
    {
        float half = AvatarSize * 0.5f;
        var disc = SettingsUI.Child(root, "Avatar", new float2(0f, 1f), new float2(0f, 1f),
            new float2(Pad, -Pad - AvatarSize), new float2(Pad + AvatarSize, -Pad));
        SettingsUI.Panel(disc, DashTheme.AccentSoft, color.Transparent, half);
        _avatar = disc;

        var clip = SettingsUI.Fill(disc, "Clip");
        SettingsUI.Panel(clip, color.White, color.Transparent, half);
        var mask = clip.AttachComponent<Mask>();
        mask.StencilMasking.Value = true;
        mask.ShowMaskGraphic.Value = false;

        float headHalf = HeadSize * 0.5f;
        var head = SettingsUI.Child(clip, "Head", new float2(0.5f, 1f), new float2(0.5f, 1f),
            new float2(-headHalf, -(10f + HeadSize)), new float2(headHalf, -10f));
        SettingsUI.Panel(head, DashTheme.TextMuted, color.Transparent, headHalf);

        float shoulderHalf = ShoulderWidth * 0.5f;
        var shoulders = SettingsUI.Child(clip, "Shoulders", new float2(0.5f, 1f), new float2(0.5f, 1f),
            new float2(-shoulderHalf, -(34f + ShoulderHeight)), new float2(shoulderHalf, -34f));
        SettingsUI.Panel(shoulders, DashTheme.TextMuted, color.Transparent, ShoulderHeight * 0.5f);
    }

    private void BuildSignedOut(Slot root)
    {
        _signedOutRoot = SettingsUI.Fill(root, "SignedOut");
        CardLabel(_signedOutRoot, "Welcome", "Welcome to Lumora", TextLeft, 18f, Pad, 24f, 18f, DashTheme.Text, BoldFont);
        CardLabel(_signedOutRoot, "Subtitle", "Make yourself at home.", TextLeft, 44f, Pad, 20f, 13f, DashTheme.TextDim);
        if (_dialogPanel != null)
        {
            var row = Row(_signedOutRoot, "Login", 88f, 40f, Pad, Pad);
            var signIn = SettingsUI.Child(row, "SignIn", float2.Zero, new float2(0.5f, 1f), float2.Zero, new float2(-4f, 0f));
            _loginPill = AttachPill(signIn, "Sign in", 16f, OnLoginPressed);
            var join = SettingsUI.Child(row, "Create", new float2(0.5f, 0f), float2.One, new float2(4f, 0f), float2.Zero);
            _createPill = AttachPill(join, AccountFlowLocale.CreatePill.Resolve(), 16f, () => OpenFlow(FlowPage.Register));
        }
        else
            CardLabel(_signedOutRoot, "LoginNote", "Sign in from this widget on the workspace", Pad, 88f, Pad, 40f, 15f, DashTheme.TextDim);
        var note = CardLabel(_signedOutRoot, "Note", "Your worlds and inventory, wherever you play.", Pad, 138f, Pad, 34f, 13f, DashTheme.TextDim);
        note.WordWrap.Value = true;
    }

    private void BuildSignedIn(Slot root)
    {
        _signedInRoot = SettingsUI.Fill(root, "SignedIn");
        _name = CardLabel(_signedInRoot, "Name", "Your account", TextLeft, 18f, Pad, 26f, 20f, DashTheme.Text, BoldFont);
        _nameLink = ProfileLink.On(_name);
        if (_avatar != null)
            _nameLink.Add(_avatar);
        _accountState = CardLabel(_signedInRoot, "AccountState", "Signed in", TextLeft, 46f, Pad, 18f, 13f, DashTheme.TextDim);
        _verified = SettingsUI.Child(_signedInRoot, "Verified", new float2(0f, 1f), new float2(0f, 1f),
            new float2(Pad + AvatarSize - 15f, -Pad - AvatarSize), new float2(Pad + AvatarSize + 3f, -Pad - AvatarSize + 18f));
        SettingsUI.Panel(_verified, DashTheme.Positive, DashTheme.Surface, 9f);
        CheckIcon(_verified, DashTheme.TextOnLight);
        SetActive(_verified, false);
        // The tier as the account service names and colours it; the game keeps no list of tiers.
        _tier = CardLabel(_signedInRoot, "Tier", string.Empty, TextLeft, 65f, Pad, 18f, 13f, DashTheme.Text, SemiboldFont);
        SetActive(_tier.Slot.Parent, false);
        CardLabel(_signedInRoot, "StorageTitle", "Cloud storage", Pad, 88f, 160f, 20f, 13f, DashTheme.TextDim);
        _storage = CardLabel(_signedInRoot, "Storage", "Loading…", 140f, 88f, Pad, 20f, 13f, DashTheme.Text, SemiboldFont);
        _storage.HorizontalAlignment.Value = TextHorizontalAlignment.Right;
        var track = Row(_signedInRoot, "StorageTrack", 118f, BarHeight, Pad, Pad);
        SettingsUI.Panel(track, DashTheme.Field, color.Transparent, BarHeight * 0.5f);
        var fill = SettingsUI.Child(track, "Fill", float2.Zero, new float2(0f, 1f), float2.Zero, float2.Zero);
        _barPaint = SettingsUI.Panel(fill, DashTheme.Accent, color.Transparent, BarHeight * 0.5f);
        _barFill = SettingsUI.Rect(fill);
        _groupRows = SettingsUI.Fill(_signedInRoot, "GroupStorage");
        _groupTitle = CardLabel(_groupRows, "GroupTitle", "Group storage", Pad, 138f, 160f, 20f, 13f, DashTheme.TextDim);
        _groupStorage = CardLabel(_groupRows, "GroupValue", string.Empty, 140f, 138f, Pad, 20f, 13f, DashTheme.Text, SemiboldFont);
        _groupStorage.HorizontalAlignment.Value = TextHorizontalAlignment.Right;
        var groupTrack = Row(_groupRows, "GroupTrack", 166f, BarHeight, Pad, Pad);
        SettingsUI.Panel(groupTrack, DashTheme.Field, color.Transparent, BarHeight * 0.5f);
        var groupFill = SettingsUI.Child(groupTrack, "Fill", float2.Zero, new float2(0f, 1f), float2.Zero, float2.Zero);
        _groupBarPaint = SettingsUI.Panel(groupFill, DashTheme.Accent, color.Transparent, BarHeight * 0.5f);
        _groupBarFill = SettingsUI.Rect(groupFill);
        SetActive(_groupRows, false);
        BuildAccountRow(_signedInRoot);
        _storageHint = Label(_signedInRoot, "StorageHint", "Checking storage…", 13f, DashTheme.TextDim,
            TextHorizontalAlignment.Left, float2.Zero, new float2(1f, 0f),
            new float2(Pad, Pad + 8f), new float2(-StatusRightInset, Pad + 28f));
        FitLabel(_storageHint);
        var signOut = SettingsUI.Child(_signedInRoot, "SignOut", new float2(1f, 0f), new float2(1f, 0f),
            new float2(-(Pad + SignOutWidth), Pad), new float2(-Pad, Pad + SignOutHeight));
        _signOutPill = AttachPill(signOut, "Sign out", 13f, SignOut);
        // Forgets the saved sign-in, with nothing to confirm it.
        _signOutPill.Button.ActOnRelease.Value = true;
    }

    private void BuildStatus(Slot root)
    {
        _status = Label(root, "Status", string.Empty, 12f, DashTheme.TextDim,
            TextHorizontalAlignment.Left, float2.Zero, new float2(1f, 0f),
            new float2(Pad, Pad), new float2(-StatusRightInset, Pad + 36f));
        _status.WordWrap.Value = true;
        _status.AutoSizeHorizontal.Value = _status.AutoSizeVertical.Value = true;
        _status.AutoSizeMin.Value = 11f;
        SetActive(_status.Slot, false);
    }

    private Text CardLabel(Slot parent, string name, string value, float left, float top, float right, float height,
        float size, color tint, Lumora.Core.Assets.IAssetProvider<Lumora.Core.Assets.FontSet>? font = null)
    {
        var viewport = Row(parent, name, top, height, left, right);
        viewport.AttachComponent<Mask>();
        var text = Label(viewport, "Text", value, size, tint, TextHorizontalAlignment.Left,
            float2.Zero, float2.One, float2.Zero, float2.Zero, font);
        FitLabel(text);
        return text;
    }

    private static void FitLabel(Text text)
    {
        text.WordWrap.Value = false;
        text.AutoSizeHorizontal.Value = true;
        text.AutoSizeMin.Value = 12f;
    }

    private static Slot CheckIcon(Slot parent, color tint)
    {
        var icon = SettingsUI.Child(parent, "Check", float2.Zero, float2.One, new float2(3f, 3f), new float2(-3f, -3f));
        var source = icon.AttachComponent<Lumora.Core.Assets.ImageProvider>();
        source.GenerateMipmaps.Value = false;
        source.URL.Value = new Uri("res://Assets/Textures/Icons/check.svg");
        var image = icon.AttachComponent<Image>();
        image.Texture.Target = source;
        image.Tint.Value = tint;
        return icon;
    }
    // DIALOG
    // Hosted on the CANVAS ROOT (the dashboard slot), the same place the create-world modal goes: the
    // backdrop has to cover the whole dash and the panel has to draw above the nav chrome, and neither is
    // possible from inside a widget on the grid. Owned here all the same, because the fields, the focus,
    // the private password and the whole sign-in flow live in this component. -xlinka

    private void BuildDialog()
    {
        var host = Dash?.Slot;
        if (host == null)
            return;

        _dialogBackdrop = host.AddSlot("SignInBackdrop");
        var backRect = _dialogBackdrop.AttachComponent<RectTransform>();
        backRect.AnchorMin.Value = float2.Zero;
        backRect.AnchorMax.Value = float2.One;
        backRect.OffsetMin.Value = float2.Zero;
        backRect.OffsetMax.Value = float2.Zero;
        _dialogBackdrop.OrderOffset.Value = 9000L;
        // OverlayLevel 1 reserves a render band above all normal UI so the dim covers the nav bar and the
        // header rather than fighting them for order.
        _dialogBackdrop.AttachComponent<GraphicChunkRoot>().OverlayLevel = 1;
        _dialogBackdrop.AttachComponent<Button>().Clicked += (_, ctx) =>
        {
            // Dismiss only when the click lands OUTSIDE the panel, so a click on a field never closes the
            // dialog even though the full-screen backdrop caught the hit.
            var panelRect = _dialogPanel?.GetComponent<RectTransform>()?.LocalComputeRect;
            if (panelRect.HasValue && panelRect.Value.Contains(ctx.PointIn(_dialogPanel)))
                return;
            CloseSignInDialog();
        };
        // The dismiss button must not auto-drive the full-screen image's tint on hover or press.
        _dialogBackdrop.AttachComponent<Image>().Tint.Value = ScrimFill;
        _dialogBackdrop.ActiveSelf.Value = false;

        _dialogPanel = host.AddSlot("SignInDialog");
        _dialogRect = _dialogPanel.AttachComponent<RectTransform>();
        _dialogRect.AnchorMin.Value = new float2(0.5f, 0.5f);
        _dialogRect.AnchorMax.Value = new float2(0.5f, 0.5f);
        _dialogPanel.OrderOffset.Value = 10000L;
        _dialogPanel.AttachComponent<GraphicChunkRoot>().OverlayLevel = 2;
        SettingsUI.Panel(_dialogPanel, DashTheme.Panel, DashTheme.OutlineStrong, DashTheme.RadiusPanel);
        // Absorb clicks on the panel background so they never fall through to the backdrop's dismiss.
        _dialogPanel.AttachComponent<Button>();

        var title = Label(_dialogPanel, "Title", "Welcome back", 24f, DashTheme.Text,
            TextHorizontalAlignment.Left, new float2(0f, 1f), new float2(1f, 1f),
            new float2(DialogPad, -52f), new float2(-DialogPad, -24f), BoldFont);
        _titleRect = SettingsUI.Rect(title.Slot);
        CardLabel(_dialogPanel, "Subtitle", "Sign in to your Lumora account", DialogPad, 57f, DialogPad + CreateLinkWidth, 22f, 14f, DashTheme.TextDim);
        // The way to a new account sits where a newcomer looks first, beside the line that asks for one.
        var createLink = SettingsUI.Child(_dialogPanel, "CreateLink", new float2(1f, 1f), new float2(1f, 1f),
            new float2(-(DialogPad + CreateLinkWidth), -79f), new float2(-DialogPad, -57f));
        var createText = Label(createLink, "Text", AccountFlowLocale.CreateLink.Resolve(), 14f, DashTheme.Accent,
            TextHorizontalAlignment.Right, float2.Zero, float2.One, float2.Zero, float2.Zero, SemiboldFont);
        var createButton = createLink.AttachComponent<Button>();
        var createInk = createButton.AddColorDriver(createText.Color, DashTheme.Accent, InteractionColorMode.Direct);
        createInk.HighlightColor.Value = DashTheme.AccentHover;
        createInk.PressedColor.Value = DashTheme.AccentPressed;
        createButton.Clicked += (_, _) =>
        {
            if (!_busy)
                OpenFlow(FlowPage.Register);
        };

        _userField = BuildField(_dialogPanel, "Username", TopsPlain[1]);
        _passField = BuildField(_dialogPanel, "Password", TopsPlain[2]);
        _codeField = BuildField(_dialogPanel, "Code", TopsWithCode[3]);

        var rememberRow = Row(_dialogPanel, "Remember", TopsPlain[4], RememberHeight, DialogPad, DialogPad);
        _rememberRect = SettingsUI.Rect(rememberRow);
        // The toggle is the box and its words only, so the right end of the row is free for the code link.
        var toggle = SettingsUI.Child(rememberRow, "Toggle", float2.Zero, new float2(0f, 1f),
            float2.Zero, new float2(RememberToggleWidth, 0f));
        var box = SettingsUI.Child(toggle, "Box", new float2(0f, 0.5f), new float2(0f, 0.5f),
            new float2(0f, -11f), new float2(22f, 11f));
        _rememberBox = SettingsUI.Panel(box, DashTheme.Field, DashTheme.Outline, DashTheme.RadiusChip);
        _rememberCheck = CheckIcon(box, DashTheme.OnAccent);
        Label(toggle, "Text", "Keep me signed in", 14f, DashTheme.TextDim,
            TextHorizontalAlignment.Left, float2.Zero, new float2(1f, 1f), new float2(34f, 0f), float2.Zero);
        _rememberButton = toggle.AttachComponent<Button>();
        _rememberButton.Clicked += (_, _) =>
        {
            _remember = !_remember;
            if (!_remember)
                ForgetToken();
            UpdateRemember();
        };

        // Anyone who knows their account has two-factor on can open the code field before the first try,
        // instead of failing once to be asked for it. -xlinka
        var codeLink = SettingsUI.Child(rememberRow, "CodeLink", new float2(1f, 0f), new float2(1f, 1f),
            new float2(-CodeLinkWidth, 0f), float2.Zero);
        _codeLink = Label(codeLink, "Text", "Use 2FA code", 14f, DashTheme.Accent,
            TextHorizontalAlignment.Right, float2.Zero, float2.One, float2.Zero, float2.Zero);
        var codeButton = codeLink.AttachComponent<Button>();
        var ink = codeButton.AddColorDriver(_codeLink.Color, DashTheme.Accent, InteractionColorMode.Direct);
        ink.HighlightColor.Value = DashTheme.AccentHover;
        ink.PressedColor.Value = DashTheme.AccentPressed;
        codeButton.Clicked += (_, _) =>
        {
            if (!_busy)
                ShowCodeField(!_needsCode);
        };

        var buttonRow = Row(_dialogPanel, "Actions", TopsPlain[5], ActionHeight, DialogPad, DialogPad);
        _buttonRowRect = SettingsUI.Rect(buttonRow);
        _submit = BuildPill(buttonRow, "Submit", "Sign in", 0f, SubmitWidth, SignIn);
        _cancel = BuildPill(buttonRow, "Cancel", "Cancel", SubmitWidth + DashTheme.Gap + 4f, CancelWidth,
            CloseSignInDialog);

        _dialogStatus = Label(_dialogPanel, "Status", string.Empty, DashTheme.FontSmall, DashTheme.TextMuted,
            TextHorizontalAlignment.Left, new float2(0f, 1f), new float2(1f, 1f),
            new float2(DialogPad, -(TopsPlain[6] + StatusHeight)), new float2(-DialogPad, -TopsPlain[6]));
        _dialogStatus.WordWrap.Value = true;
        _dialogStatus.AutoSizeHorizontal.Value = _dialogStatus.AutoSizeVertical.Value = true;
        _dialogStatus.AutoSizeMin.Value = 12f;

        _userField.Button.Clicked += (_, _) => SetFocus(Focus.Username);
        _passField.Button.Clicked += (_, _) => SetFocus(Focus.Password);
        _codeField.Button.Clicked += (_, _) => SetFocus(Focus.Code);

        _cancel.SetRamp(DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.Text);
        _dialogPanel.ActiveSelf.Value = false;
    }

    private Pill BuildPill(Slot parent, string name, string label, float left, float width, Action onClick)
    {
        var slot = SettingsUI.Child(parent, name, new float2(0f, 0f), new float2(0f, 1f),
            new float2(left, 0f), new float2(left + width, 0f));
        return AttachPill(slot, label, DashTheme.FontBody, onClick);
    }

    private Pill AttachPill(Slot slot, string label, float fontSize, Action onClick)
    {
        var pill = new Pill
        {
            Slot = slot,
            Panel = SettingsUI.Panel(slot, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusControl),
            Button = slot.AttachComponent<Button>(),
        };
        pill.Text = SettingsUI.FillLabel(slot, "Text", SemiboldFont, fontSize, DashTheme.Text);
        pill.Text.Content.Value = label;
        pill.Driver = pill.Button.AddColorDriver(pill.Panel.Color, DashTheme.Surface, InteractionColorMode.Direct);
        pill.Button.Clicked += (_, _) => onClick();
        return pill;
    }

    private Field BuildField(Slot parent, string name, float top)
    {
        var slot = Row(parent, name, top, FieldHeight, DialogPad, DialogPad);
        CardLabel(slot, "Label", name == "Username" ? "Username or email" : name == "Code" ? "Authentication code" : "Password",
            0f, 0f, 0f, 18f, 13f, DashTheme.TextDim);
        var entry = Row(slot, "Entry", 24f, 44f, 0f, 0f);
        var field = new Field
        {
            Slot = slot,
            Panel = SettingsUI.Panel(entry, DashTheme.Field, DashTheme.Outline, DashTheme.RadiusControl),
            Button = entry.AttachComponent<Button>(),
        };
        entry.AttachComponent<Mask>().ShowMaskGraphic.Value = true;
        field.Text = Label(entry, "Text", string.Empty, 16f, DashTheme.TextMuted,
            TextHorizontalAlignment.Left, float2.Zero, new float2(1f, 1f),
            new float2(10f, 0f), new float2(-10f, 0f));
        field.Text.WordWrap.Value = false;
        field.Text.KeepCaretVisible.Value = true;
        return field;
    }

    public void OpenSignInDialog()
    {
        if (_dialogPanel == null || _dialogPanel.IsDestroyed || SignedIn)
            return;
        _dialogOpen = true;
        SetActive(_dialogBackdrop, true);
        SetActive(_dialogPanel, true);
        SetFocus(Focus.Username);
        UpdateSubmitButton();
        Repaint();
    }

    public void CloseSignInDialog()
    {
        if (!_dialogOpen)
            return;
        _dialogOpen = false;
        SetActive(_dialogBackdrop, false);
        SetActive(_dialogPanel, false);
        // Nothing typed outlives the dialog. The username is not a secret and coming back to a filled-in
        // name is worth keeping; the password and the code are neither.
        _password = string.Empty;
        _code = string.Empty;
        _focus = Focus.None;
        UpdateFields();
        Repaint();
    }

    private void OnLoginPressed()
    {
        // The Home screen decides which modal is up, because the create-world dialog is the other one and
        // two of them open at once is two backdrops deep.
        var home = HomeHost;
        if (home != null)
            home.OpenSignIn();
        else
            OpenSignInDialog();
    }

    // STATE

    private bool SignedIn => Client?.IsAuthenticated == true;

    private void ApplyState()
    {
        bool signedIn = SignedIn;
        SetActive(_signedOutRoot, !signedIn);
        SetActive(_signedInRoot, signedIn);
        SetActive(_codeField?.Slot, !signedIn && _needsCode);
        if (signedIn)
            CloseSignInDialog();

        _loginPill?.SetRamp(DashTheme.Accent, DashTheme.AccentHover, DashTheme.AccentPressed,
            WidgetScreen.OnFill(DashTheme.Accent));
        _signOutPill?.SetRamp(DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.Text);
        ApplyDialogLayout();
        UpdateFields();
        UpdateRemember();
        UpdateCodeLink();
        UpdateSubmitButton();
        Repaint();
    }

    private void ShowCodeField(bool show)
    {
        _needsCode = show;
        if (!show)
            _code = string.Empty;
        SetActive(_codeField?.Slot, show);
        ApplyDialogLayout();
        UpdateCodeLink();
        UpdateSubmitButton();
        SetFocus(show ? Focus.Code : _focus == Focus.Code ? Focus.Password : _focus);
    }

    private void UpdateCodeLink()
    {
        if (_codeLink != null && !_codeLink.IsDestroyed)
            ListingStyle.SetText(_codeLink, _needsCode ? "Hide 2FA code" : "Use 2FA code");
    }

    private void ApplyDialogLayout()
    {
        if (_dialogPanel == null || _dialogPanel.IsDestroyed || _dialogRect == null)
            return;
        var tops = _needsCode ? TopsWithCode : TopsPlain;
        float height = _needsCode ? DialogHeightWithCode : DialogHeightPlain;
        SettingsUI.SetOffsets(_dialogRect,
            new float2(-DialogWidth * 0.5f, -height * 0.5f),
            new float2(DialogWidth * 0.5f, height * 0.5f));

        Place(_titleRect, tops[0], TitleHeight);
        Place(_userField?.Slot, tops[1], FieldHeight);
        Place(_passField?.Slot, tops[2], FieldHeight);
        if (_needsCode)
            Place(_codeField?.Slot, tops[3], FieldHeight);
        Place(_rememberRect, tops[4], RememberHeight);
        Place(_buttonRowRect, tops[5], ActionHeight);
        if (_dialogStatus != null && !_dialogStatus.IsDestroyed)
            Place(SettingsUI.Rect(_dialogStatus.Slot), tops[6], StatusHeight);
    }

    private static void Place(Slot? slot, float top, float height)
    {
        if (slot != null && !slot.IsDestroyed)
            Place(SettingsUI.Rect(slot), top, height);
    }

    private static void Place(RectTransform? rect, float top, float height)
    {
        if (rect == null || rect.IsDestroyed)
            return;
        SettingsUI.SetOffsets(rect, new float2(DialogPad, -(top + height)), new float2(-DialogPad, -top));
    }

    private void SetFocus(Focus focus)
    {
        _focus = focus;
        UpdateFields();
        Repaint();
    }

    private void UpdateFields()
    {
        Paint(_userField, Focus.Username, _username, "Username or email");
        Paint(_passField, Focus.Password, new string('•', _password.Length), "Password");
        Paint(_codeField, Focus.Code, _code, "Two-factor code");
        UpdateFlowFields();
    }

    private void Paint(Field? field, Focus which, string value, string placeholder)
    {
        if (field == null)
            return;
        bool focused = _focus == which;
        SettingsUI.SetPaint(field.Panel, DashTheme.Field, focused ? DashTheme.Accent : DashTheme.Outline);
        if (value.Length == 0)
        {
            ListingStyle.SetText(field.Text, placeholder);
            ListingStyle.SetTextColor(field.Text, DashTheme.TextMuted);
        }
        else
        {
            ListingStyle.SetText(field.Text, value);
            ListingStyle.SetTextColor(field.Text, DashTheme.Text);
        }
        field.Text.CaretPosition.Value = focused ? value.Length : -1;
    }

    private void UpdateRemember()
    {
        if (_rememberBox == null || _rememberBox.IsDestroyed)
            return;
        SettingsUI.SetPaint(_rememberBox,
            _remember ? DashTheme.Accent : DashTheme.Field,
            _remember ? DashTheme.Accent : DashTheme.Outline);
        SetActive(_rememberCheck, _remember);
        Repaint();
    }

    private void UpdateSubmitButton()
    {
        if (_submit == null)
            return;
        bool live = !_busy && Connectivity.IsOnline;
        ListingStyle.SetInteractable(_submit.Button, live);
        ListingStyle.SetText(_submit.Text, _busy ? "Signing in…" : _needsCode ? "Verify & sign in" : "Sign in");
        if (live)
            _submit.SetRamp(DashTheme.Accent, DashTheme.AccentHover, DashTheme.AccentPressed,
                WidgetScreen.OnFill(DashTheme.Accent));
        else
            _submit.SetRamp(DashTheme.Field, DashTheme.Field, DashTheme.Field, DashTheme.TextMuted);

        if (!Connectivity.IsOnline && !SignedIn)
            SetStatus("Offline", DashTheme.TextMuted);
    }

    // One message, written to the card and to the dialog at once: the dialog shows it while you are in it,
    // the card keeps it after the dialog goes away.
    private void SetStatus(string text, in color tint)
    {
        WriteStatus(_status, true, text, tint);
        WriteStatus(_dialogStatus, false, text, tint);
        SetActive(_storageHint?.Slot, text.Length == 0);
        SetActive(_signedOutRoot?.FindChild("Note", false), text.Length == 0);
        Repaint();
    }

    private static void WriteStatus(Text? label, bool hideWhenEmpty, string text, in color tint)
    {
        if (label == null || label.IsDestroyed)
            return;
        ListingStyle.SetText(label, text);
        ListingStyle.SetTextColor(label, tint);
        if (hideWhenEmpty)
            SetActive(label.Slot, text.Length > 0);
    }

    // KEY INPUT (routed here by HomeScreen while this widget is docked on it)
    // The dialog is modal, so while it is up it takes the keyboard whole and everything else answers false.

    public bool HasFieldFocus => (_dialogOpen || _flowOpen) && _focus != Focus.None;
    public string? CopyText => _flowOpen ? FlowCopyText : !_dialogOpen ? null : _focus switch
    {
        Focus.Username => _username,
        Focus.Code => _code,
        _ => null,
    };

    public bool ConsumeChar(char c)
    {
        if (_flowOpen)
            return FlowChar(c);
        if (!_dialogOpen)
            return false;
        if (_focus == Focus.None || char.IsControl(c))
            return true;
        switch (_focus)
        {
            case Focus.Username when _username.Length < 64:
                _username += c;
                break;
            case Focus.Password when _password.Length < 128:
                _password += c;
                break;
            case Focus.Code when _code.Length < 12:
                _code += c;
                break;
        }
        UpdateFields();
        Repaint();
        return true;
    }

    public bool ConsumeBackspace()
    {
        if (_flowOpen)
            return FlowBackspace();
        if (!_dialogOpen)
            return false;
        switch (_focus)
        {
            case Focus.Username:
                _username = Chop(_username);
                break;
            case Focus.Password:
                _password = Chop(_password);
                break;
            case Focus.Code:
                _code = Chop(_code);
                break;
        }
        UpdateFields();
        Repaint();
        return true;
    }

    public bool ConsumeEnter()
    {
        if (_flowOpen)
            return FlowEnter();
        if (!_dialogOpen)
            return false;
        SetFocus(Focus.None);
        SignIn();
        return true;
    }

    public bool ConsumeEscape()
    {
        if (_flowOpen)
        {
            CloseFlow();
            return true;
        }
        if (!_dialogOpen)
            return false;
        CloseSignInDialog();
        return true;
    }

    private static string Chop(string value) => Lumora.Core.Input.TextElements.RemoveLast(value);

    // ACCOUNT SERVICE

    private async void SignIn()
    {
        if (_busy || SignedIn)
            return;

        var client = Client;
        if (client == null)
        {
            SetStatus("No account service.", DashTheme.Negative);
            return;
        }
        if (!Connectivity.IsOnline)
        {
            SetStatus("Offline", DashTheme.TextMuted);
            return;
        }
        if (_username.Length == 0 || _password.Length == 0)
        {
            SetStatus("Username and password are needed.", DashTheme.Warning);
            return;
        }

        _busy = true;
        UpdateSubmitButton();
        SetStatus("Signing in...", DashTheme.TextDim);

        var world = World;
        string user = _username;
        string secret = _password;
        string? code = _code.Length > 0 ? _code : null;
        bool remember = _remember;

        try
        {
            var result = await client.SignIn(user, secret, remember, code);
            OnUi(world, () =>
            {
                _busy = false;
                if (result.Success)
                {
                    // The Authenticated event has already flipped the view and closed the dialog; this only
                    // makes sure nothing typed outlives the form.
                    _password = string.Empty;
                    _code = string.Empty;
                    UpdateFields();
                    return;
                }
                if (result.Status == HttpStatusCode.Forbidden)
                {
                    // 403 is the answer when the account has two-factor on and no code came with the
                    // request. Same credentials, resubmitted with the code.
                    ShowCodeField(true);
                    SetStatus(result.Message ?? "Enter your two-factor code.", DashTheme.Warning);
                }
                else
                {
                    SetStatus(result.Message ?? "Sign in failed.", DashTheme.Negative);
                }
                UpdateSubmitButton();
            });
        }
        catch (Exception)
        {
            OnUi(world, () =>
            {
                _busy = false;
                UpdateSubmitButton();
                SetStatus("Unable to sign in. Please try again.", DashTheme.Negative);
            });
        }
    }

    private async void SignOut()
    {
        var client = Client;
        if (client == null || _busy)
            return;
        _busy = true;
        SetStatus("Signing out...", DashTheme.TextDim);
        ForgetToken();

        var world = World;
        try
        {
            var result = await client.SignOut();
            OnUi(world, () =>
            {
                _busy = false;
                if (result.Failed)
                    SetStatus(result.Message ?? "Signed out locally.", DashTheme.TextMuted);
            });
        }
        catch (Exception ex)
        {
            string message = ex.Message;
            OnUi(world, () =>
            {
                _busy = false;
                SetStatus(message, DashTheme.Negative);
            });
        }
    }

    private void TryResume()
    {
        var client = Client;
        if (client == null || client.IsAuthenticated || _resumeTried || !Connectivity.IsOnline)
            return;
        string id = Lumora.Core.Settings.ReadValue<string>(UserKey, string.Empty) ?? string.Empty;
        string token = ReadSealedToken();
        if (id.Length == 0 || token.Length == 0)
            return;
        _resumeTried = true;
        Resume(id, token);
    }

    private async void Resume(string id, string token)
    {
        var client = Client;
        if (client == null)
            return;
        _busy = true;
        UpdateSubmitButton();
        SetStatus("Resuming...", DashTheme.TextDim);

        var world = World;
        try
        {
            var result = await client.SignInWithToken(id, token);
            OnUi(world, () =>
            {
                _busy = false;
                UpdateSubmitButton();
                if (result.Success)
                    return;
                // Only a refusal ends the saved sign in. The service being away, slow or erroring keeps it
                // for the next launch, or one bad connection at startup signs you out for good.
                if (result.Status is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
                {
                    ForgetToken();
                    SetStatus("Saved sign-in expired.", DashTheme.TextMuted);
                    return;
                }
                SetStatus("Could not reach the sign-in service.", DashTheme.TextMuted);
            });
        }
        catch (Exception ex)
        {
            string message = ex.Message;
            OnUi(world, () =>
            {
                _busy = false;
                UpdateSubmitButton();
                SetStatus(message, DashTheme.Negative);
            });
        }
    }

    private async void LoadProfile()
    {
        var client = Client;
        if (client == null || !client.IsAuthenticated)
            return;

        var world = World;
        int generation = _accountGeneration;
        int read = ++_storageReads;
        _storageReadAt = world?.Time.TotalTime ?? 0.0;
        try
        {
            var profile = await client.GetCurrentUser();
            var quota = await client.GetQuota();
            var groups = await client.GetMyGroups();
            var pool = await client.GetGroupStorage();
            OnUi(world, () =>
            {
                if (!SignedIn || generation != _accountGeneration)
                    return;
                ApplyProfile(profile);
                ApplyQuota(quota, read, keepOnFailure: false);
                ApplyGroupStorage(groups, pool, keepOnFailure: false);
            });
        }
        catch (Exception ex)
        {
            string message = ex.Message;
            OnUi(world, () =>
            {
                if (!SignedIn || generation != _accountGeneration)
                    return;
                if (!HasStorageUsage)
                    StorageLabel = "Storage unavailable";
                SetStatus(message, DashTheme.Negative);
            });
        }
    }

    // The storage figure alone, for a change the sync reported. One that does not get through leaves the
    // last figure up rather than blanking a card that was right a moment ago.
    private async void ReadStorage()
    {
        var client = Client;
        if (client == null || !client.IsAuthenticated)
            return;

        var world = World;
        int read = ++_storageReads;
        _storageReadAt = world?.Time.TotalTime ?? 0.0;
        try
        {
            var quota = await client.GetQuota();
            var groups = await client.GetMyGroups();
            var pool = await client.GetGroupStorage();
            int generation = _accountGeneration;
            OnUi(world, () =>
            {
                ApplyQuota(quota, read, keepOnFailure: true);
                if (SignedIn && generation == _accountGeneration)
                    ApplyGroupStorage(groups, pool, keepOnFailure: true);
            });
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"Account: could not read your storage: {ex.Message}");
        }
    }

    private void ApplyProfile(ApiResponse<UserProfile> profile)
    {
        if (IsDestroyed)
            return;

        if (profile.Success && profile.Data != null)
        {
            var data = profile.Data;
            ProfileName = data.Username ?? string.Empty;
            if (_name != null && !_name.IsDestroyed)
            {
                ListingStyle.SetText(_name, ProfileName);
                // The display color comes back as hex, written in sRGB like every other token, so it has
                // to be decoded before it lands on a vertex.
                _nameInk = ColorSwatchStore.TryParseHex(data.NameColor, out var tint) ? tint.ToLinear() : DashTheme.Text;
                _nameLink?.Bind(data.Id, _nameInk, !WidgetPanel.EditMode);
            }
            _profileKnown = true;
            _profileVerified = data.IsVerified;
            _twoFactorOn = data.TwoFactorEnabled;
            ApplyEmailState();
            ApplyTier(data.PatreonData);
            SetStatus(string.Empty, DashTheme.TextMuted);
            RememberToken(data.Id);
        }
        else
        {
            SetStatus(profile.Message ?? "Could not load your profile.", DashTheme.Negative);
        }

        Repaint();
    }

    // The line and the bar from the newest read, in exact bytes.
    private void ApplyQuota(ApiResponse<UserQuotaResponse> quota, int read, bool keepOnFailure)
    {
        if (IsDestroyed || !SignedIn || read != _storageReads || _storage == null || _storage.IsDestroyed)
            return;

        if (quota.Success && quota.Data != null)
        {
            var data = quota.Data;
            long used = System.Math.Max(0, data.UsedBytes);
            long limit = System.Math.Max(0, data.LimitBytes);
            HasStorageUsage = true;
            StorageUsedBytes = used;
            StorageLimitBytes = limit;
            StorageLabel = $"{FormatSize(used)} / {FormatSize(limit)}";
            ListingStyle.SetText(_storage, $"{FormatSize(used)} of {FormatSize(limit)}");
            float fraction = StorageFraction;
            color tint = fraction >= 1f ? DashTheme.Negative : fraction >= 0.85f ? DashTheme.Warning : DashTheme.Accent;
            if (_barPaint != null && !_barPaint.IsDestroyed)
                _barPaint.Color.Value = tint;
            if (_storageHint != null)
            {
                ListingStyle.SetText(_storageHint, limit <= 0 ? "No storage available" : used >= limit ? "Storage full" : $"{FormatSize(limit - used)} free");
                ListingStyle.SetTextColor(_storageHint, fraction >= 0.85f ? tint : DashTheme.TextDim);
            }
            if (_barFill != null && !_barFill.IsDestroyed)
                SettingsUI.SetAnchors(_barFill, float2.Zero, new float2(System.Math.Clamp(fraction, 0f, 1f), 1f));
            InventorySync.QuotaSeen(string.IsNullOrEmpty(data.UserId) ? Client?.AccountUserId : data.UserId, used, limit);
        }
        else if (!keepOnFailure || !HasStorageUsage)
        {
            StorageLabel = "Storage unavailable";
            ListingStyle.SetText(_storage, "Unavailable");
            if (_storageHint != null) ListingStyle.SetText(_storageHint, "Check your connection");
        }

        Repaint();
    }

    // The tier name in the tier's own colour, both straight from the account service. A tier with no
    // colour of its own reads in the text colour; nobody who is not an active supporter shows a tier.
    private void ApplyTier(PatreonInfo? patreon)
    {
        if (_tier == null || _tier.IsDestroyed)
            return;
        bool show = patreon is { IsActiveSupporter: true } && !string.IsNullOrWhiteSpace(patreon.TierName);
        SetActive(_tier.Slot.Parent, show);
        if (!show)
            return;
        ListingStyle.SetText(_tier, patreon!.TierName);
        ListingStyle.SetTextColor(_tier,
            ColorSwatchStore.TryParseHex(patreon.TierColor, out var tint) ? tint.ToLinear() : DashTheme.Text);
    }

    // The group storage this account answers for. An owner's tier gives ONE pool for all the groups they own,
    // so an owner sees the whole of it, used across every one of those groups; anyone else sees their own
    // slice of a group they are in. Neither, and there is no second bar. A read that fails leaves the last
    // bar up rather than blanking it.
    private void ApplyGroupStorage(ApiResponse<List<GroupInfo>> groups, ApiResponse<GroupStorageInfo> pool, bool keepOnFailure)
    {
        if (IsDestroyed)
            return;

        if (pool.Success && pool.Data is { TotalBytes: > 0 } owned && owned.Groups.Count > 0)
        {
            int count = owned.Groups.Count;
            DrawGroupBar(count == 1 ? "Group storage · 1 group" : $"Group storage · {count} groups",
                owned.UsedBytes, owned.TotalBytes);
            return;
        }

        if (!groups.Success || groups.Data == null || (!pool.Success && keepOnFailure))
        {
            if (!keepOnFailure)
                ShowGroupStorage(false);
            return;
        }

        GroupInfo? slice = null;
        foreach (var group in groups.Data)
        {
            if (group.MyRole != "Owner" && group.MyAllocatedBytes > 0)
            {
                slice = group;
                break;
            }
        }
        if (slice == null)
        {
            ShowGroupStorage(false);
            return;
        }

        string name = string.IsNullOrWhiteSpace(slice.Tag) ? slice.Name : slice.Tag;
        DrawGroupBar($"Group storage · {name}", slice.MyUsedBytes, slice.MyAllocatedBytes);
    }

    private void DrawGroupBar(string title, long usedBytes, long limitBytes)
    {
        long used = System.Math.Max(0, usedBytes);
        long limit = System.Math.Max(0, limitBytes);
        float fraction = limit <= 0 ? 1f : (float)System.Math.Clamp((double)used / limit, 0d, 1d);
        if (_groupTitle != null && !_groupTitle.IsDestroyed)
            ListingStyle.SetText(_groupTitle, title);
        if (_groupStorage != null && !_groupStorage.IsDestroyed)
            ListingStyle.SetText(_groupStorage, $"{FormatSize(used)} of {FormatSize(limit)}");
        if (_groupBarPaint != null && !_groupBarPaint.IsDestroyed)
            _groupBarPaint.Color.Value = fraction >= 1f ? DashTheme.Negative : fraction >= 0.85f ? DashTheme.Warning : DashTheme.Accent;
        if (_groupBarFill != null && !_groupBarFill.IsDestroyed)
            SettingsUI.SetAnchors(_groupBarFill, float2.Zero, new float2(fraction, 1f));
        ShowGroupStorage(true);
    }

    private void ShowGroupStorage(bool show)
    {
        SetActive(_groupRows, show);
        if (HasGroupStorage == show)
            return;
        HasGroupStorage = show;
        FitCard();
    }

    // The card grows a row for group storage and one for the account's own actions, the actions under whichever
    // comes last.
    private void FitCard()
    {
        float extra = (HasGroupStorage ? GroupRowsHeight : 0f) + (HasAccountRow ? AccountRowHeight : 0f);
        MinSize.Value = new float2(MinSize.Value.x, 188f + extra);
        PreferredSize.Value = new float2(PreferredSize.Value.x, CardBaseHeight + extra);
        PlaceAccountRow();
        Repaint();
    }

    // Megabytes are what a person reads for a save, gigabytes once there are thousands of them. One decimal,
    // and no trailing .0 so a round quota stays "5 GB".
    private static string FormatSize(long bytes)
    {
        double megabytes = bytes / (double)Megabyte;
        if (megabytes >= 1024d)
            return (megabytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
        return megabytes.ToString("0.#", CultureInfo.InvariantCulture) + " MB";
    }

    // REMEMBERED SESSION
    // The token, not the password: the password is never persisted anywhere. The store is the engine keyed
    // config (a binary data tree in the user roaming folder), the same one the color swatches use, but a
    // bearer token in a plain data tree is a bearer token for anyone who can read the file, so it goes
    // through LocalEncryption (AES-GCM under the platform-sealed vault key, the same wrap saved worlds
    // get) and only the base64 of that blob is written. A blob that fails to open (tampered, or the vault
    // key was re-sealed on another machine) counts as no token. Signing out, or clearing the box, deletes
    // it. -xlinka

    private void RememberToken(string userId)
    {
        var token = Client?.CurrentSession?.Token;
        if (!_remember || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(userId))
        {
            if (!_remember)
                ForgetToken();
            return;
        }
        string sealedToken;
        try
        {
            sealedToken = Convert.ToBase64String(
                Persistence.LocalEncryption.Encrypt(System.Text.Encoding.UTF8.GetBytes(token)));
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"Account: could not seal the session token, not remembering it ({ex.Message})");
            ForgetToken();
            return;
        }
        Lumora.Core.Settings.WriteValue(UserKey, userId);
        Lumora.Core.Settings.WriteValue(TokenKey, sealedToken);
    }

    private static string ReadSealedToken()
    {
        string stored = Lumora.Core.Settings.ReadValue<string>(TokenKey, string.Empty) ?? string.Empty;
        if (stored.Length == 0)
            return string.Empty;
        try
        {
            var blob = Convert.FromBase64String(stored);
            if (!Persistence.LocalEncryption.IsEncrypted(blob))
                return string.Empty;
            return System.Text.Encoding.UTF8.GetString(Persistence.LocalEncryption.Decrypt(blob));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void ForgetToken()
    {
        Lumora.Core.Settings.DeleteValue(UserKey);
        Lumora.Core.Settings.DeleteValue(TokenKey);
    }

    // EVENTS

    private void Hook()
    {
        var client = Client;
        if (client == null || _hooked)
            return;
        _hooked = true;
        client.Authenticated += OnAuthenticated;
        client.SignedOut += OnSignedOut;
        AccountEmail.Changed += OnEmailChanged;
    }

    private void Unhook()
    {
        var client = Client;
        if (client == null || !_hooked)
            return;
        _hooked = false;
        client.Authenticated -= OnAuthenticated;
        client.SignedOut -= OnSignedOut;
        AccountEmail.Changed -= OnEmailChanged;
    }

    private void OnAuthenticated(Lumora.Nexus.Cloud.Cdn.Session session)
    {
        OnUi(World, () =>
        {
            ResetStorage();
            _password = string.Empty;
            _code = string.Empty;
            _needsCode = false;
            _focus = Focus.None;
            ApplyState();
            LoadProfile();
        });
    }

    private void OnSignedOut()
    {
        OnUi(World, () =>
        {
            ResetStorage();
            _username = string.Empty;
            _password = string.Empty;
            _code = string.Empty;
            _needsCode = false;
            _focus = Focus.None;
            _resumeTried = true;
            ProfileName = string.Empty;
            _profileKnown = false;
            _profileVerified = false;
            _twoFactorOn = false;
            CloseFlow();
            ApplyState();
            ApplyEmailState();
        });
    }

    private void ResetStorage()
    {
        ++_accountGeneration;
        ++_storageReads;
        ProfileName = string.Empty;
        _nameLink?.Bind(null, _nameInk);
        HasStorageUsage = false;
        StorageUsedBytes = StorageLimitBytes = 0;
        StorageLabel = "Loading storage…";
        _storageDue = _storageSettling = false;
        if (_storage != null && !_storage.IsDestroyed)
            ListingStyle.SetText(_storage, "Loading…");
        if (_storageHint != null && !_storageHint.IsDestroyed)
            ListingStyle.SetText(_storageHint, "Checking storage…");
        if (_barFill != null && !_barFill.IsDestroyed)
            SettingsUI.SetAnchors(_barFill, float2.Zero, new float2(0f, 1f));
        ApplyTier(null);
        ShowGroupStorage(false);
    }

    // The account service answers on a background thread; nothing here may touch the UI tree until it is
    // back on the world update thread. The widget can also be dragged off the grid (and destroyed) while a
    // request is in flight, so the continuation checks it is still alive before it paints anything.
    private void OnUi(World? world, Action action)
    {
        if (world == null || world.IsDestroyed)
            return;
        world.RunInUpdates(0, () =>
        {
            if (!IsDestroyed)
                action();
        });
    }
}
