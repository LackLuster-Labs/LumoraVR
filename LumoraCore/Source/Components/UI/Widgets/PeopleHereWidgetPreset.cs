// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// Who is in the world you are in right now, the host first, each with the role the session gives them.
// The same people the Session window lists, at a glance from the workspace; pressing the card opens that
// window, pressing a name opens that person's profile. Rows are made once and filled from the session on a slow clock, as many as the card is tall.
// -xlinka
[ComponentCategory("Hidden")]
public sealed class PeopleHereWidgetPreset : HomeWidgetPreset
{
    private const int MaxRows = 8;
    private const float HeaderHeight = 30f;
    private const float RowHeight = 24f;
    private const float Dot = 16f;

    private static readonly color[] Hues =
    {
        DashTheme.IconBlue, DashTheme.IconTeal, DashTheme.IconPink, DashTheme.Warning, DashTheme.Positive, DashTheme.Accent,
    };

    private sealed class PersonRow
    {
        public Slot Slot = null!;
        public RoundedPanel Dot = null!;
        public Text Name = null!;
        public ProfileLink Link = null!;
        public Text Role = null!;
    }

    private readonly PersonRow[] _rows = new PersonRow[MaxRows];
    private readonly List<User> _users = new();
    private Text? _title;
    private Text? _more;
    private Button? _button;

    public PeopleHereWidgetPreset()
    {
        MinSize.Value = new float2(200f, 80f);
        PreferredSize.Value = new float2(300f, 150f);
        MaxSize.Value = new float2(520f, 320f);
    }

    protected override float PollInterval => 1f;

    protected override void Build(Widget widget, Slot root)
    {
        _title = Label(root, "Title", "PEOPLE HERE", DashTheme.FontLabel, DashTheme.TextMuted, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(Pad, -HeaderHeight), new float2(-Pad, -6f), SemiboldFont);
        _more = Label(root, "More", string.Empty, DashTheme.FontLabel, DashTheme.TextMuted, TextHorizontalAlignment.Right,
            new float2(0f, 1f), new float2(1f, 1f), new float2(Pad, -HeaderHeight), new float2(-Pad, -6f), SemiboldFont);

        for (int i = 0; i < MaxRows; i++)
        {
            float top = HeaderHeight + i * RowHeight;
            var slot = Row(root, "Person" + i, top, RowHeight, Pad, Pad);
            var dotSlot = SettingsUI.Child(slot, "Dot", new float2(0f, 0.5f), new float2(0f, 0.5f),
                new float2(0f, -Dot * 0.5f), new float2(Dot, Dot * 0.5f));
            var dot = SettingsUI.Panel(dotSlot, Hues[i % Hues.Length], color.Transparent, Dot * 0.5f);
            var name = Label(slot, "Name", string.Empty, DashTheme.FontSmall, DashTheme.Text, TextHorizontalAlignment.Left,
                float2.Zero, new float2(0.62f, 1f), new float2(Dot + 8f, 0f), float2.Zero, SemiboldFont);
            name.AutoSizeHorizontal.Value = true;
            name.AutoSizeMin.Value = 10f;
            var role = Label(slot, "Role", string.Empty, DashTheme.FontLabel, DashTheme.TextMuted, TextHorizontalAlignment.Right,
                new float2(0.62f, 0f), float2.One, new float2(4f, 0f), float2.Zero);
            _rows[i] = new PersonRow { Slot = slot, Dot = dot, Name = name, Link = ProfileLink.On(name), Role = role };
            slot.ActiveSelf.Value = false;
        }

        _button = AddCardButton(OpenSession);
        DriveCard(_button, DashTheme.GlassCard, DashTheme.GlassCardHover, DashTheme.GlassCardPressed);
        Poll();
    }

    protected override void Poll()
    {
        if (_rows[0] == null)
            return;
        if (_button != null && !_button.IsDestroyed)
            ListingStyle.SetInteractable(_button, !WidgetPanel.EditMode);

        var world = FocusedWorld;
        _users.Clear();
        world?.GetAllUsers(_users);
        _users.RemoveAll(u => u == null || u.IsDestroyed);
        // Host first, then the order the session has them in. A sort would shuffle everyone else on every
        // poll, so the host is moved up and nobody else moves.
        int host = _users.FindIndex(u => u.IsHost);
        if (host > 0)
        {
            var first = _users[host];
            _users.RemoveAt(host);
            _users.Insert(0, first);
        }

        float height = Slot.GetComponent<RectTransform>()?.LocalComputeRect.height ?? 0f;
        int fit = height > HeaderHeight ? (int)((height - HeaderHeight - 6f) / RowHeight) : MaxRows;
        fit = System.Math.Clamp(fit, 1, MaxRows);
        int shown = System.Math.Min(_users.Count, fit);

        for (int i = 0; i < MaxRows; i++)
        {
            var row = _rows[i];
            bool on = i < shown;
            SetActive(row.Slot, on);
            if (!on)
                continue;
            var user = _users[i];
            string name = user.UserName?.Value ?? "Someone";
            if (ReferenceEquals(user, world?.LocalUser))
                name += " (you)";
            ListingStyle.SetText(row.Name, name);
            row.Link.Bind(user, !WidgetPanel.EditMode);
            string role = user.IsHost ? RoleLabels.Host.Resolve() : RoleLabels.Of(world?.DataModelPermissions?.GetRole(user));
            if (user.IsBot)
                role = PeopleLocale.ChipBot.Resolve() + " · " + role;
            ListingStyle.SetText(row.Role, role);
            SettingsUI.SetFill(row.Dot, Hues[Hue(user)]);
        }

        if (_more != null)
        {
            string more = world == null ? "No world" : _users.Count > shown ? "+" + (_users.Count - shown) : _users.Count.ToString();
            ListingStyle.SetText(_more, more);
        }
        _users.Clear();
    }

    // The same hue the status island gives the same person.
    private static int Hue(User user)
    {
        var id = user.UserID?.Value ?? user.UserName?.Value ?? string.Empty;
        int hash = 17;
        foreach (char c in id)
            hash = hash * 31 + c;
        return (hash & 0x7fffffff) % Hues.Length;
    }

    private void OpenSession()
    {
        var dash = Dash;
        if (dash == null)
            return;
        foreach (var screen in dash.Screens)
        {
            if (screen is SessionScreen)
            {
                dash.SwitchTo(screen);
                return;
            }
        }
    }
}
