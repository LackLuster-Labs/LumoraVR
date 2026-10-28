// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Text;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI.Worlds;

// Only open worlds expose a roster. Discovery announces counts and hosts, not individual users.
internal sealed class WorldDetailPeople
{
    public readonly Slot Root;
    private readonly BrowserParts _parts;
    private readonly Text _heading;
    private readonly Slot _rows;
    private string _signature = string.Empty;

    public WorldDetailPeople(BrowserParts parts, Slot parent)
    {
        _parts = parts;
        Root = parent.AddSlot("People");
        Root.AttachComponent<RectTransform>();
        var column = Root.AttachComponent<VerticalLayout>();
        column.Spacing.Value = DashTheme.Gap;
        column.ForceExpandWidth.Value = true;
        column.ForceExpandHeight.Value = false;
        var title = Root.AddSlot("Heading");
        title.AttachComponent<RectTransform>();
        BrowserParts.Height(title, 26f);
        _heading = parts.Label(title, "People here", DashTheme.FontHeading, DashTheme.Text, BrowserParts.Weight.Semibold);
        _rows = Root.AddSlot("Users");
        _rows.AttachComponent<RectTransform>();
        var wrap = _rows.AttachComponent<WrapLayout>();
        wrap.Spacing.Value = wrap.LineSpacing.Value = DashTheme.Gap;
        Root.ActiveSelf.Value = false;
    }

    public void Apply(World? world)
    {
        bool show = world != null && !world.IsDestroyed;
        BrowserParts.SetActive(Root, show);
        if (!show) return;
        var users = world!.GetAllUsers();
        var signature = new StringBuilder();
        int count = 0;
        foreach (var user in users)
        {
            if (user == null || user.IsDestroyed) continue;
            count++;
            signature.Append(user.ReferenceID).Append('|').Append(SessionPermissionsPanel.DisplayName(user))
                .Append('|').Append(user.AccountId.Value).Append('|').Append(Status(world, user)).Append('\n');
        }
        BrowserParts.SetText(_heading, $"People here · {count}");
        string key = signature.ToString();
        if (key == _signature && _rows.Children.Count > 0) return;
        _signature = key;
        _rows.DestroyChildren();
        foreach (var user in users)
        {
            if (user == null || user.IsDestroyed) continue;
            var row = _rows.AddSlot("User");
            row.AttachComponent<RectTransform>();
            BrowserParts.Size(row, 220f, 58f);
            BrowserParts.Panel(row, DashTheme.Surface, DashTheme.RadiusControl);
            var name = row.AddSlot("Name");
            BrowserParts.PinTop(name.AttachComponent<RectTransform>(), 8f, 23f, 12f);
            ProfileLink.On(_parts.Label(name, SessionPermissionsPanel.DisplayName(user), DashTheme.FontBody, DashTheme.Text,
                BrowserParts.Weight.Semibold)).Bind(user);
            var status = row.AddSlot("Status");
            BrowserParts.PinBottom(status.AttachComponent<RectTransform>(), 7f, 18f, 12f);
            _parts.Label(status, Status(world, user), DashTheme.FontSmall, DashTheme.TextDim, BrowserParts.Weight.Regular);
        }
        if (count == 0)
        {
            var empty = _rows.AddSlot("Empty");
            empty.AttachComponent<RectTransform>();
            BrowserParts.Size(empty, 300f, 32f);
            _parts.Label(empty, "No users in this world.", DashTheme.FontBody, DashTheme.TextDim, BrowserParts.Weight.Regular);
        }
    }

    private static string Status(World world, User user)
    {
        var permissions = world.DataModelPermissions;
        bool host = permissions != null && ReferenceEquals(permissions.GetRole(user), permissions.HostRole);
        string role = ReferenceEquals(user, world.LocalUser) ? host ? "You · Host" : "You" : host ? "Host" : "Guest";
        string device = user.VRActive.Value ? "VR" : "Desktop";
        return role + " · " + device + Capture(user) + (user.PresentInWorld.Value ? string.Empty : " · Away");
    }

    // The same strongest-first reading as the mark over their name.
    private static string Capture(User user) => user.Capture.Value switch
    {
        _ when user.IsLive.Value => " · Live",
        Lumora.Core.Photos.CaptureState.Streaming => " · Live",
        Lumora.Core.Photos.CaptureState.Recording => " · Recording",
        _ => string.Empty,
    };
}
