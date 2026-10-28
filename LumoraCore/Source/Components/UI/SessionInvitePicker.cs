// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Core.Networking.Session;
using Lumora.Core.Social;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// INVITING FRIENDS FROM THE SESSION SCREEN
//
// Takes the screen's page area while it is up: friends to tick, online ones first, and one button that invites
// every friend ticked. Friends already in the world are listed, marked, and can't be ticked. Someone who may
// not invite in this world gets the reason in place of the list, since a list they can't send from would only
// waste their time. What became of each invite stays at the foot until the next send. -xlinka
internal sealed class SessionInvitePicker
{
    private const float HeaderHeight = 40f;
    private const float HintHeight = 40f;
    private const float FooterHeight = 52f;
    private const float SendWidth = 150f;
    private const string KindPick = "invitepick";

    private readonly SettingsFonts _fonts;
    private readonly Func<string, IAssetProvider<TextureAsset>?> _icons;
    private readonly ListingItemSource _items = new();
    private readonly HashSet<string> _picked = new(StringComparer.Ordinal);
    private Slot? _root;
    private Text? _title;
    private Text? _hint;
    private Text? _bar;
    private Slot? _listArea;
    private ListingView? _list;
    private DashScrollbar? _bar2;
    private Text? _status;
    private SettingsChip? _send;
    private PeoplePictureCache? _pictures;
    private World? _world;
    private bool _sending;
    private string _signature = string.Empty;

    public SessionInvitePicker(SettingsFonts fonts, Func<string, IAssetProvider<TextureAsset>?> icons)
    {
        _fonts = fonts;
        _icons = icons;
    }

    public bool IsShown => _root != null && !_root.IsDestroyed && _root.ActiveSelf.Value;
    public Slot? Root => _root;
    internal ListingView? List => _list;
    internal IReadOnlyCollection<string> Picked => _picked;
    internal string StatusText => _status?.Content.Value ?? string.Empty;
    internal bool Sending => _sending;

    public event Action? Closed;

    public void Build(Slot host, Action markDirty)
    {
        _root = SettingsUI.Fill(host, "Invite");
        var pictureHost = _root.AddSlot("Pictures");
        pictureHost.ActiveSelf.Value = false;
        _pictures = new PeoplePictureCache(pictureHost);

        var header = SettingsUI.Child(_root, "Header", new float2(0f, 1f), new float2(1f, 1f), new float2(0f, -HeaderHeight), float2.Zero);
        _title = SettingsUI.Label(header, "Title", _fonts.Strong, DashTheme.FontTitle, DashTheme.Text, TextHorizontalAlignment.Left,
            float2.Zero, float2.One, new float2(4f, 0f), new float2(-48f, 0f), shrinkToFit: true);
        var close = SettingsUI.Child(header, "Close", new float2(1f, 0.5f), new float2(1f, 0.5f), new float2(-36f, -17f), new float2(-2f, 17f));
        var closePlate = SettingsUI.Panel(close, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusControl);
        var closeButton = close.AttachComponent<Button>();
        closeButton.Tip.Value = InviteLocale.Close.Resolve();
        closeButton.Clicked += (_, _) => Hide();
        var closeFill = closeButton.AddColorDriver(closePlate.Color, DashTheme.Surface, InteractionColorMode.Direct);
        closeFill.NormalColor.Value = DashTheme.Surface;
        closeFill.HighlightColor.Value = DashTheme.SurfaceHover;
        closeFill.PressedColor.Value = DashTheme.SurfacePressed;
        closeFill.DisabledColor.Value = DashTheme.Surface;
        closeFill.Apply();
        var glyph = SettingsUI.Child(close, "Glyph", new float2(0.5f, 0.5f), new float2(0.5f, 0.5f), new float2(-8f, -8f), new float2(8f, 8f));
        var image = glyph.AttachComponent<Image>();
        image.Texture.Target = _icons("close")!;
        image.PreserveAspect.Value = true;
        image.InteractionTarget.Value = false;
        image.Tint.Value = DashTheme.TextDim;

        _hint = SettingsUI.Label(_root, "Hint", _fonts.Body, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(4f, -(HeaderHeight + HintHeight)), new float2(-4f, -HeaderHeight));
        _hint.WordWrap.Value = true;
        _hint.Ellipsis.Value = false;

        _listArea = SettingsUI.Fill(_root, "List", 0f, FooterHeight + 8f, 0f, HeaderHeight + HintHeight + 6f);
        var viewport = SettingsUI.Fill(_listArea, "Viewport", 0f, 0f, DashScrollbar.Width + 6f, 0f);
        var templates = new ListingTemplateMapper { DefaultHeight = PeopleMetrics.PersonRowHeight };
        templates.Map<ListingHeader>(new SettingsSectionTemplate(_fonts));
        templates.MapKind(KindPick, new InvitePickTemplate(_fonts, _pictures, _icons("check"), Toggle));
        _list = ListingView.Attach(viewport, Style(), templates, _items);
        _bar2 = DashScrollbar.OnRightEdge(_listArea, 0f, 0f, 0f);
        _bar2.Bind(_list.Scroll, SettingsUI.Rect(viewport), _list.ContentSlot == null ? null : SettingsUI.Rect(_list.ContentSlot), markDirty);

        _bar = SettingsUI.Label(_root, "Bar", _fonts.Medium, DashTheme.FontBody, DashTheme.TextDim, TextHorizontalAlignment.Center,
            new float2(0f, 0.5f), new float2(1f, 0.5f), new float2(24f, -40f), new float2(-24f, 40f));
        _bar.WordWrap.Value = true;
        _bar.Ellipsis.Value = false;

        var footer = SettingsUI.Child(_root, "Footer", float2.Zero, new float2(1f, 0f), float2.Zero, new float2(0f, FooterHeight));
        var rule = SettingsUI.Child(footer, "Rule", new float2(0f, 1f), new float2(1f, 1f), new float2(0f, -1f), float2.Zero);
        rule.AttachComponent<Image>().Tint.Value = DashTheme.Divider;
        _status = SettingsUI.Label(footer, "Status", _fonts.Body, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Left,
            float2.Zero, float2.One, new float2(4f, 4f), new float2(-(SendWidth + 12f), -6f));
        _status.WordWrap.Value = true;
        _status.Ellipsis.Value = true;
        var send = SettingsUI.Child(footer, "Send", new float2(1f, 0.5f), new float2(1f, 0.5f), new float2(-SendWidth, -16f), new float2(0f, 16f));
        _send = SettingsChip.Build(send, _fonts.Medium, DashTheme.FontBody, DashTheme.RadiusControl);
        _send.Button.Clicked += (_, _) => Send();

        _root.ActiveSelf.Value = false;
    }

    private static ListingStyle Style() => new()
    {
        RowHeight = PeopleMetrics.PersonRowHeight,
        RowSpacing = 6f,
        RowFill = DashTheme.Surface,
        RowBorder = DashTheme.Outline,
        RowSelectedFill = DashTheme.SurfaceHover,
        ControlFill = DashTheme.Surface,
        NeutralFill = DashTheme.Surface,
        AccentFill = DashTheme.Accent,
        WarningFill = DashTheme.Negative,
        DisabledFill = DashTheme.Field,
        Accent = DashTheme.Accent,
        TextPrimary = DashTheme.Text,
        TextDim = DashTheme.TextDim,
        TextDisabled = DashTheme.TextMuted,
        HeaderText = DashTheme.TextMuted,
    };

    public void Show(World? world)
    {
        if (_root == null || _root.IsDestroyed)
            return;
        if (!ReferenceEquals(world, _world))
        {
            _picked.Clear();
            SetStatus(string.Empty, DashTheme.TextDim);
        }
        _world = world;
        _signature = string.Empty;
        _root.ActiveSelf.Value = true;
        Refresh();
        _list?.ResetToTop();
    }

    public void Hide(bool notify = true)
    {
        if (_root == null || _root.IsDestroyed || !_root.ActiveSelf.Value)
            return;
        _root.ActiveSelf.Value = false;
        if (notify)
            Closed?.Invoke();
    }

    // Friends move between online and offline, and people come and go, while it is up.
    public void Tick()
    {
        if (!IsShown)
            return;
        if (_pictures != null && _pictures.Settle())
            _list?.RefreshValues();
        _bar2?.Refresh();
        if (!string.Equals(Signature(), _signature, StringComparison.Ordinal))
            Refresh();
    }

    private string Signature()
    {
        var social = Engine.Current?.Social;
        var right = SessionInvites.RightOf(_world, _world?.LocalUser);
        var text = new System.Text.StringBuilder();
        text.Append(right.Way).Append(right.Bar).Append('|').Append(_world?.UserCount ?? 0).Append('|').Append(social?.IsReady ?? false);
        if (social != null)
        {
            foreach (var friend in social.Friends)
                text.Append(friend.UserId).Append(friend.Presence?.Mode).Append(friend.Presence?.WorldName).Append(',');
        }
        return text.ToString();
    }

    private void Refresh()
    {
        if (_list == null || _title == null || _hint == null || _bar == null || _listArea == null || _send == null)
            return;
        _signature = Signature();
        var world = _world;
        string name = world == null ? string.Empty
            : string.IsNullOrEmpty(world.WorldName?.Value) ? world.Name : world.WorldName!.Value;
        ListingStyle.SetText(_title, InviteLocale.InviteTo(name).Resolve());
        var right = SessionInvites.RightOf(world, world?.LocalUser);
        var social = Engine.Current?.Social;

        _items.ClearAll();
        bool open = right.Allowed && social != null;
        if (open)
            AddFriends(social!, world!);
        bool empty = open && _items.ItemsAt(string.Empty).Count == 0;
        PeopleShow.Set(_listArea, open && !empty);
        PeopleShow.Set(_hint.Slot, open);
        PeopleShow.Set(_bar.Slot, !open || empty);
        ListingStyle.SetText(_hint, InviteLocale.PickerHint.Resolve());
        ListingStyle.SetText(_bar, !right.Allowed ? InviteLocale.Bar(right.Bar).Resolve()
            : social == null ? InviteLocale.BarSignedOut.Resolve() : InviteLocale.PickerEmpty.Resolve());
        PaintSend(open);
        _list.RefreshValues();
    }

    private void AddFriends(ISocialService social, World world)
    {
        var here = new HashSet<string>(StringComparer.Ordinal);
        foreach (var user in world.GetAllUsers())
        {
            var id = user?.AccountId?.Value;
            if (!string.IsNullOrEmpty(id))
                here.Add(id);
        }
        var online = new List<FriendInfo>();
        var offline = new List<FriendInfo>();
        foreach (var friend in social.Friends)
            (PeopleFormat.IsOnline(friend.Presence?.Mode) ? online : offline).Add(friend);
        Comparison<FriendInfo> byName = (a, b) => string.Compare(a.Username, b.Username, StringComparison.OrdinalIgnoreCase);
        online.Sort(byName);
        offline.Sort(byName);
        _picked.RemoveWhere(id => social.FindFriend(id) == null || here.Contains(id));
        Section("online", InviteLocale.PickerOnline, online, here);
        Section("offline", InviteLocale.PickerOffline, offline, here);
    }

    private void Section(string key, Lumora.Core.Localization.LocaleText title, List<FriendInfo> friends, HashSet<string> here)
    {
        if (friends.Count == 0)
            return;
        _items.AddRoot(new ListingHeader("h." + key, title.Resolve() + "  " + friends.Count));
        foreach (var friend in friends)
        {
            var presence = friend.Presence;
            _items.AddRoot(new InvitePickItem("f." + friend.UserId)
            {
                UserId = friend.UserId,
                Name = friend.Username,
                NameTint = PeopleFormat.NameColor(friend.NameColor),
                AvatarHash = friend.AvatarHash,
                Mode = presence?.Mode ?? StatusModes.Offline,
                Line = PeopleFormat.WhereLine(presence),
                Here = here.Contains(friend.UserId),
                Picked = _picked.Contains(friend.UserId),
            });
        }
    }

    internal void Toggle(InvitePickItem item)
    {
        if (_sending || item.Here)
            return;
        if (!_picked.Remove(item.UserId))
            _picked.Add(item.UserId);
        item.Picked = _picked.Contains(item.UserId);
        PaintSend(true);
        _list?.RefreshValues();
    }

    private void PaintSend(bool open)
    {
        if (_send == null)
            return;
        PeopleShow.Set(_send.Slot, open);
        int count = _picked.Count;
        string label = _sending ? InviteLocale.Sending.Resolve() : count > 0 ? InviteLocale.SendCount(count).Resolve() : InviteLocale.Send.Resolve();
        GroupBand.PaintAction(_send, label, !_sending && count > 0, false, count > 0);
    }

    private void SetStatus(string text, in color tint)
    {
        if (_status == null)
            return;
        ListingStyle.SetText(_status, text);
        ListingStyle.SetTextColor(_status, tint);
    }

    private void Send()
    {
        var world = _world;
        var social = Engine.Current?.Social;
        if (_sending || world == null || social == null || _picked.Count == 0 || _root == null)
            return;
        var ids = new List<string>(_picked);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in ids)
            names[id] = social.FindFriend(id)?.Username ?? id;
        _sending = true;
        PaintSend(true);
        SetStatus(InviteLocale.Sending.Resolve(), DashTheme.TextDim);
        var own = _root.World;
        _ = InviteSender.SendAsync(world, ids).ContinueWith(t =>
        {
            var outcomes = t.IsCompletedSuccessfully ? t.Result : null;
            string? why = t.IsCompletedSuccessfully ? null : t.Exception?.GetBaseException().Message;
            own?.RunInUpdates(0, () => Done(outcomes, why, names));
        });
    }

    private void Done(IReadOnlyList<InviteOutcome>? outcomes, string? why, Dictionary<string, string> names)
    {
        _sending = false;
        if (_root == null || _root.IsDestroyed)
            return;
        var sent = new List<string>();
        var failed = new List<string>();
        foreach (var outcome in outcomes ?? Array.Empty<InviteOutcome>())
        {
            string name = names.TryGetValue(outcome.UserId, out var known) ? known : outcome.UserId;
            if (outcome.Sent)
            {
                sent.Add(name);
                _picked.Remove(outcome.UserId);
            }
            else
            {
                failed.Add(InviteLocale.Failed(name, PeopleScreen.Why(outcome)).Resolve());
            }
        }
        if (outcomes == null)
            failed.Add(why ?? PeopleLocale.ActionFailed.Resolve());
        string done = sent.Count == 1 ? InviteLocale.SentTo(sent[0]).Resolve()
            : sent.Count > 1 ? InviteLocale.SentMany(sent.Count).Resolve() : string.Empty;
        string text = string.Join("  ", failed.Count > 0 && done.Length > 0 ? new[] { done, string.Join("  ", failed) }
            : failed.Count > 0 ? failed.ToArray() : new[] { done });
        SetStatus(text, failed.Count > 0 ? DashTheme.Warning : DashTheme.Positive);
        _signature = string.Empty;
        Refresh();
    }
}

internal sealed class InvitePickItem : ListingItem
{
    public string UserId = string.Empty;
    public string Name = string.Empty;
    public color NameTint = DashTheme.Text;
    public string? AvatarHash;
    public string? Mode;
    public string Line = string.Empty;
    public bool Here;
    public bool Picked;

    public InvitePickItem(string key) : base(key)
    {
        Kind = "invitepick";
    }
}

// A friend to tick: face with their status, name, where they are, and the box. Ticked, the box fills in the
// accent and the row takes the accent's edge.
internal sealed class InvitePickTemplate : ListingRowTemplate
{
    private const float FaceLeft = 10f;
    private const float TextLeft = FaceLeft + PeopleMetrics.RowFace + 12f;
    private const float Box = 22f;
    private const float HereWidth = 104f;

    private readonly SettingsFonts _fonts;
    private readonly PeoplePictureCache _pictures;
    private readonly IAssetProvider<TextureAsset>? _check;
    private readonly Action<InvitePickItem> _toggle;

    public InvitePickTemplate(SettingsFonts fonts, PeoplePictureCache pictures, IAssetProvider<TextureAsset>? check, Action<InvitePickItem> toggle)
    {
        _fonts = fonts;
        _pictures = pictures;
        _check = check;
        _toggle = toggle;
    }

    public override float Height => PeopleMetrics.PersonRowHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var background = SettingsUI.Panel(row, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusControl);
        var press = row.AttachComponent<Button>();

        float half = PeopleMetrics.RowFace * 0.5f;
        var faceHost = SettingsUI.Child(row, "Face", new float2(0f, 0.5f), new float2(0f, 0.5f),
            new float2(FaceLeft, -half), new float2(FaceLeft + PeopleMetrics.RowFace, half));
        var face = PeopleAvatar.Build(faceHost, _fonts.Strong, PeopleMetrics.RowFace, 0f, PeopleMetrics.RowDot, DashTheme.FontBody);
        var name = SettingsUI.Label(row, "Name", _fonts.Medium, DashTheme.FontBody, DashTheme.Text,
            TextHorizontalAlignment.Left, new float2(0f, 1f), new float2(1f, 1f), new float2(TextLeft, -27f), new float2(-(HereWidth + 16f), -7f));
        var line = SettingsUI.Label(row, "Line", _fonts.Body, DashTheme.FontSmall, DashTheme.TextMuted,
            TextHorizontalAlignment.Left, new float2(0f, 0f), new float2(1f, 0f), new float2(TextLeft, 7f), new float2(-(HereWidth + 16f), 25f));

        var box = SettingsUI.Child(row, "Box", new float2(1f, 0.5f), new float2(1f, 0.5f), new float2(-(12f + Box), -Box * 0.5f), new float2(-12f, Box * 0.5f));
        var boxPanel = SettingsUI.Panel(box, DashTheme.Field, DashTheme.OutlineStrong, 6f);
        var tick = SettingsUI.Child(box, "Tick", new float2(0.5f, 0.5f), new float2(0.5f, 0.5f), new float2(-7f, -7f), new float2(7f, 7f));
        var tickImage = tick.AttachComponent<Image>();
        if (_check != null)
            tickImage.Texture.Target = _check;
        tickImage.PreserveAspect.Value = true;
        tickImage.InteractionTarget.Value = false;
        tickImage.Tint.Value = DashTheme.OnAccent;

        var here = SettingsUI.Child(row, "Here", new float2(1f, 0.5f), new float2(1f, 0.5f), new float2(-(12f + HereWidth), -11f), new float2(-12f, 11f));
        SettingsUI.Panel(here, DashTheme.Positive.WithAlpha(0.16f), color.Transparent, DashTheme.RadiusChip);
        var hereText = SettingsUI.FillLabel(here, "Text", _fonts.Medium, DashTheme.FontLabel, DashTheme.Positive, TextHorizontalAlignment.Center, true);

        var pickRow = new PickRow
        {
            Background = background, Press = press, Face = face, Name = name, Line = line, Box = box, BoxPanel = boxPanel,
            Tick = tick, Here = here, HereText = hereText, Pictures = _pictures,
        };
        press.HoverEntered += _ => pickRow.Hover(true);
        press.HoverExited += _ => pickRow.Hover(false);
        press.Clicked += (_, _) =>
        {
            if (pickRow.Item is InvitePickItem item)
                _toggle(item);
        };
        return pickRow;
    }

    private sealed class PickRow : ListingRow
    {
        public required RoundedPanel Background;
        public required Button Press;
        public required PeopleAvatar Face;
        public required Text Name;
        public required Text Line;
        public required Slot Box;
        public required RoundedPanel BoxPanel;
        public required Slot Tick;
        public required Slot Here;
        public required Text HereText;
        public required PeoplePictureCache Pictures;
        private bool _hovered;

        public override void Bind(ListingItem item)
        {
            if (item is not InvitePickItem pick)
                return;
            Face.Bind(Pictures, pick.AvatarHash, pick.Name, pick.NameTint, pick.Mode);
            ListingStyle.SetText(Name, pick.Name);
            ListingStyle.SetTextColor(Name, pick.Here ? DashTheme.TextDim : pick.NameTint);
            ListingStyle.SetText(Line, pick.Line);
            PeopleShow.Set(Box, !pick.Here);
            PeopleShow.Set(Here, pick.Here);
            if (pick.Here)
                ListingStyle.SetText(HereText, InviteLocale.PickerHere.Resolve());
            PeopleShow.Set(Tick, pick.Picked);
            SettingsUI.SetPaint(BoxPanel, pick.Picked ? DashTheme.Accent : DashTheme.Field, pick.Picked ? DashTheme.Accent : DashTheme.OutlineStrong);
            ListingStyle.SetInteractable(Press, !pick.Here);
            Paint();
        }

        public override void Unbind() => _hovered = false;

        public void Hover(bool on)
        {
            _hovered = on;
            Paint();
        }

        private void Paint()
        {
            var pick = Item as InvitePickItem;
            bool picked = pick?.Picked == true;
            var fill = pick?.Here == true ? DashTheme.Surface : _hovered ? DashTheme.SurfaceHover : DashTheme.Surface;
            SettingsUI.SetPaint(Background, fill, picked ? DashTheme.Accent : DashTheme.Outline);
        }
    }
}
