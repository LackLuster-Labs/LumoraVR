// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Lumora.Core.Social;

namespace Lumora.Core.Components.UI;

// A PERSON'S NAME, AND THE FACE BESIDE IT, AS THE WAY TO THEIR PROFILE
//
// Every list that names somebody goes through here, so they all answer alike: the name lights the accent
// under the pointer, the tip says where it goes, a press opens the profile. A guest has no account and so
// no profile. Their name keeps the step-back ink their name plate wears and answers nothing, rather than
// lighting up for a press that goes nowhere.
//
// The press is the presser's alone. A panel in a world is drawn on every machine there and anyone's beam
// can land on it, so only a press by this machine's own user opens anything, and it opens this machine's
// dash. -xlinka
internal sealed class ProfileLink
{
    public static readonly LocaleText ViewProfile = "Profile.View".AsLocale("View profile");

    private readonly Text _name;
    private readonly List<Button> _buttons = new(2);
    private string _account = string.Empty;
    private color _ink = DashTheme.Text;
    private bool _enabled = true;

    private ProfileLink(Text name) => _name = name;

    public string Account => _account;
    public IReadOnlyList<Button> Buttons => _buttons;
    public bool IsLive => _enabled && _account.Length > 0;

    public static ProfileLink On(Text name)
    {
        var link = new ProfileLink(name);
        link.Add(name.Slot);
        return link;
    }

    // Another part that leads to the same person, a face. A button takes an Image already on its slot as
    // its own tint, so a slot with one gets a child to hold the press instead.
    public Button Add(Slot slot)
    {
        var host = slot.GetComponent<Image>() == null ? slot : SettingsUI.Fill(slot, "Profile");
        var button = host.AttachComponent<Button>();
        button.HoverEntered += _ => Paint();
        button.HoverExited += _ => Paint();
        button.Pressed += _ => Paint();
        button.Released += _ => Paint();
        button.Clicked += OnClicked;
        _buttons.Add(button);
        Apply();
        return button;
    }

    // Somebody in a session: their account, or the guest look when they have none.
    public void Bind(User? user, bool enabled = true)
    {
        bool guest = user == null || user.IsDestroyed || user.IsGuest;
        Bind(guest ? null : user!.AccountId.Value, guest ? NameplateTheme.GuestInk : DashTheme.Text, enabled);
    }

    // A name that came with its account, or with none known, and the ink it rests in.
    public void Bind(string? account, in color ink, bool enabled = true)
    {
        _account = account ?? string.Empty;
        _ink = ink;
        _enabled = enabled;
        Apply();
    }

    private void Apply()
    {
        bool live = IsLive;
        string tip = live ? ViewProfile.Resolve() : string.Empty;
        foreach (var button in _buttons)
        {
            if (button.IsDestroyed)
                continue;
            ListingStyle.SetInteractable(button, live);
            if (button.Tip.Value != tip)
                button.Tip.Value = tip;
        }
        Paint();
    }

    private void Paint()
    {
        if (_name.IsDestroyed)
            return;
        bool hot = false;
        bool down = false;
        foreach (var button in _buttons)
        {
            if (button.IsDestroyed || !button.CanInteract)
                continue;
            hot |= button.IsHovering.Value;
            down |= button.IsPressed.Value;
        }
        ListingStyle.SetTextColor(_name, down ? DashTheme.AccentPressed : hot ? DashTheme.Accent : _ink);
    }

    private void OnClicked(Button button, UIInteractionContext context)
    {
        var local = button.World?.LocalUser;
        if (context.Actor != null && local != null && !ReferenceEquals(context.Actor, local))
            return;
        if (IsLive)
            Profiles.Open(_account);
    }
}
