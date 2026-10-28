// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Helio.UI;
using Lumora.Core.Localization;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// YOUR OWN PROFILE, EDITED, and the inventory picker it and the chat share.
//
// The page edits drafts; Save sends only what moved, so a field nobody touched is never written back over
// an edit made from another machine. Pictures come out of the inventory: a photo is an item whose picture is
// the photo, so a saved thing's picture hash is the image itself, already stored on the service. Choosing one
// sends that hash and the service keeps it for as long as the profile points at it. -xlinka
public sealed partial class PeopleScreen
{
    private static readonly string[] SettableModes = { StatusModes.Online, StatusModes.Away, StatusModes.Busy, StatusModes.Invisible };
    private static readonly string[] PrivacyModes = { LocationPrivacyModes.Friends, LocationPrivacyModes.Nobody };

    private bool _drafting;
    private string _draftBio = string.Empty;
    private string _draftStatusText = string.Empty;
    private string _draftMode = StatusModes.Online;
    private readonly List<string> _draftLanguages = new();
    private string _draftZone = string.Empty;
    private string _draftPronouns = string.Empty;
    private string? _draftAvatar;
    private string? _draftBanner;
    private bool _avatarPicked;
    private bool _bannerPicked;
    private string? _draftPrivacy;
    private bool _saving;

    private PickPurpose _pick;
    private PersonTab _pickReturnTab = PersonTab.Profile;
    private InventoryPicker? _picker;

    private InventoryPicker Picker => _picker ??= new InventoryPicker(() => InventorySource, () => World,
        () => _pane == PaneView.Pick, () =>
        {
            _paneDirty = true;
            _headerDirty = true;
        });

    private void BeginEdit()
    {
        if (!IsSelf)
            return;
        SeedDrafts();
        SetTab(PersonTab.Edit, force: true);
    }

    private void SeedDrafts()
    {
        var shown = Shown();
        _draftBio = shown?.Bio ?? string.Empty;
        _draftStatusText = shown?.StatusText ?? string.Empty;
        _draftMode = shown != null && Array.IndexOf(SettableModes, shown.StatusMode) >= 0 ? shown.StatusMode : StatusModes.Online;
        _draftLanguages.Clear();
        if (shown != null)
            _draftLanguages.AddRange(shown.Languages);
        _draftZone = shown?.TimeZone ?? string.Empty;
        _draftPronouns = shown?.Pronouns ?? string.Empty;
        _draftAvatar = shown?.AvatarHash;
        _draftBanner = shown?.BannerHash;
        _avatarPicked = false;
        _bannerPicked = false;
        _draftPrivacy = shown?.LocationPrivacy;
        _drafting = true;
    }

    private void DropDrafts()
    {
        _drafting = false;
        _avatarPicked = false;
        _bannerPicked = false;
    }

    private void CancelEdit()
    {
        DropDrafts();
        _paneNotice = null;
        SetTab(PersonTab.Profile, force: true);
    }

    private void BuildEditItems()
    {
        if (!_drafting)
            SeedDrafts();
        var shown = Shown();
        string name = shown?.Username ?? string.Empty;
        var tint = PeopleFormat.NameColor(shown?.NameColor);
        // A supporter sees a picked animation play; anyone else is told before Save that it will not go through.
        bool supporter = shown?.AnimatedPictures == true;

        _bodyItems.AddRoot(new PeoplePictureItem("edit.picture", PeopleLocale.EditSectionPicture)
        {
            Hash = _draftAvatar,
            Name = name,
            Tint = tint,
            Status = _avatarPicked ? PeopleLocale.PicturePicked.Resolve()
                : string.IsNullOrEmpty(_draftAvatar) ? PeopleLocale.PictureNone.Resolve() : string.Empty,
            Animate = supporter,
            AnimatedStatus = _avatarPicked && !supporter ? PeopleLocale.AnimatedPicked.Resolve() : string.Empty,
        });
        _bodyItems.AddRoot(new PeoplePictureItem("edit.banner", PeopleLocale.EditSectionBanner)
        {
            Banner = true,
            Hash = _draftBanner,
            Name = name,
            Tint = tint,
            Status = _bannerPicked ? PeopleLocale.PicturePicked.Resolve()
                : string.IsNullOrEmpty(_draftBanner) ? PeopleLocale.BannerNone.Resolve() : string.Empty,
            Animate = supporter,
            AnimatedStatus = _bannerPicked && !supporter ? PeopleLocale.AnimatedPicked.Resolve() : string.Empty,
        });
        _bodyItems.AddRoot(new PeoplePronounsItem("edit.pronouns", PeopleLocale.EditSectionPronouns)
        {
            Read = () => _draftPronouns,
            Write = text => _draftPronouns = text,
        });

        var modes = new string[SettableModes.Length];
        for (int i = 0; i < modes.Length; i++)
            modes[i] = PeopleFormat.StatusLabel(SettableModes[i]);
        _bodyItems.AddRoot(new PeopleSegmentItem("edit.mode", PeopleLocale.EditSectionStatus)
        {
            Options = modes,
            Read = () => Array.IndexOf(SettableModes, _draftMode),
            Write = index =>
            {
                if (index >= 0 && index < SettableModes.Length)
                    _draftMode = SettableModes[index];
            },
        });
        _bodyItems.AddRoot(new PeopleFieldItem("edit.statustext", PeopleLocale.EditStatusText)
        {
            Placeholder = PeopleLocale.StatusTextHint.Resolve(),
            MaxLength = SocialLimits.StatusTextMax,
            Read = () => _draftStatusText,
            Write = text => _draftStatusText = text,
        });
        _bodyItems.AddRoot(new PeopleBioEditItem("edit.bio", PeopleLocale.EditSectionAbout)
        {
            Read = () => _draftBio,
            Write = text => _draftBio = text,
        });

        var languages = new PeopleLanguagesItem("edit.languages", PeopleLocale.EditSectionLanguages);
        languages.Codes.AddRange(_draftLanguages);
        languages.Hint = _draftLanguages.Count >= SocialLimits.LanguagesMax
            ? PeopleLocale.LanguagesFull.Resolve()
            : PeopleFormat.LanguageNames(_draftLanguages);
        _bodyItems.AddRoot(languages);

        string? their = PeopleFormat.TheirTime(_draftZone);
        _bodyItems.AddRoot(new PeopleChoiceItem("edit.zone", PeopleLocale.EditSectionZone)
        {
            Value = string.IsNullOrEmpty(_draftZone) ? PeopleLocale.FactNone.Resolve()
                : their != null ? _draftZone + "  (" + their + ")" : _draftZone,
            PrimaryLabel = PeopleLocale.ChooseZone.Resolve(),
            Primary = PeopleAction.ChooseZone,
            SecondaryLabel = PeopleLocale.ZoneLocal.Resolve(),
            Secondary = PeopleAction.LocalZone,
        });

        _bodyItems.AddRoot(new PeopleSegmentItem("edit.privacy", PeopleLocale.EditSectionLocation)
        {
            Options = new[] { PeopleLocale.PrivacyFriends.Resolve(), PeopleLocale.PrivacyNobody.Resolve() },
            Read = () => _draftPrivacy == null ? -1 : Array.IndexOf(PrivacyModes, _draftPrivacy),
            Write = index =>
            {
                if (index >= 0 && index < PrivacyModes.Length)
                    _draftPrivacy = PrivacyModes[index];
            },
            Hint = PeopleLocale.PrivacyHint.Resolve(),
        });

        _bodyItems.AddRoot(new PeopleActionsItem("edit.actions")
            .Add(PeopleLocale.Save.Resolve(), PeopleAction.Save, lit: true)
            .Add(PeopleLocale.Cancel.Resolve(), PeopleAction.CancelEdit));
    }

    // Whatever is being typed goes into its draft first: a press on Save is also a click away from it.
    private void FlushTyping()
    {
        var typing = TextInput.Focused;
        if (typing != null && !typing.IsDestroyed && typing.Slot != null && _personPane != null
            && typing.Slot.IsDescendantOf(_personPane))
            typing.Unfocus();
    }

    private async void SaveProfile()
    {
        var social = Social;
        var shown = Shown();
        if (social == null || shown == null || !IsSelf || _saving)
            return;
        FlushTyping();

        string statusText = _draftStatusText.Trim();
        // the service holds the same rule; saying so here keeps a refusal from costing the rest of the edit
        string? pronouns = Lumora.Core.Social.ProfilePronouns.Normalize(_draftPronouns);
        if (pronouns == null)
        {
            _paneError = PeopleLocale.PronounsBad.Resolve();
            _paneDirty = true;
            return;
        }
        var update = new ProfileUpdate
        {
            Bio = _draftBio != shown.Bio ? _draftBio : null,
            StatusText = statusText != shown.StatusText ? statusText : null,
            StatusMode = _draftMode != shown.StatusMode ? _draftMode : null,
            Languages = SameLanguages(_draftLanguages, shown.Languages) ? null : new List<string>(_draftLanguages),
            TimeZone = _draftZone != shown.TimeZone ? _draftZone : null,
            Pronouns = pronouns != (shown.Pronouns ?? string.Empty) ? pronouns : null,
            AvatarHash = (_draftAvatar ?? string.Empty) != (shown.AvatarHash ?? string.Empty) ? _draftAvatar ?? string.Empty : null,
            BannerHash = (_draftBanner ?? string.Empty) != (shown.BannerHash ?? string.Empty) ? _draftBanner ?? string.Empty : null,
            LocationPrivacy = _draftPrivacy != shown.LocationPrivacy ? _draftPrivacy : null,
        };
        if (update.Bio == null && update.StatusText == null && update.StatusMode == null && update.Languages == null
            && update.TimeZone == null && update.AvatarHash == null && update.BannerHash == null && update.LocationPrivacy == null
            && update.Pronouns == null)
        {
            _paneNotice = PeopleLocale.NothingChanged.Resolve();
            _paneDirty = true;
            return;
        }

        _saving = true;
        var world = World;
        string self = _personId;
        try
        {
            var result = await social.UpdateProfileAsync(update);
            OnUi(world, () =>
            {
                _saving = false;
                if (result.Success && result.Data != null)
                {
                    _profiles[self] = result.Data;
                    _self = result.Data;
                    if (string.Equals(self, _personId, StringComparison.Ordinal))
                        _profile = result.Data;
                    _paneError = null;
                    _paneNotice = PeopleLocale.Saved.Resolve();
                    DropDrafts();
                    SetTab(PersonTab.Profile, force: true);
                    _listDirty = true;
                }
                else
                {
                    _paneError = RefusalOf(result);
                    _paneDirty = true;
                }
                _headerDirty = true;
            });
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            OnUi(world, () =>
            {
                _saving = false;
                _paneError = message;
                _paneDirty = true;
            });
        }
    }

    // What a refused save says. The service's own codes get the game's words, and the supporter perk says where
    // the perk comes from.
    private static string RefusalOf(ApiResponse result) => result.Error switch
    {
        SupporterPerkError => PeopleLocale.AnimatedPerk.Resolve(),
        PictureCheckError => PeopleLocale.PictureCheck.Resolve(),
        _ => string.IsNullOrWhiteSpace(result.Message) ? PeopleLocale.ActionFailed.Resolve() : result.Message!,
    };

    // The codes PUT /api/user/me/profile refuses an animated picture with (SOCIAL.md).
    public const string SupporterPerkError = "supporter-perk";
    public const string PictureCheckError = "picture-check";

    // where the perk comes from; the refusal offers it under the error
    public static readonly Uri PatreonPage = new("https://www.patreon.com/LumoraVR");

    private static bool SameLanguages(List<string> a, List<string> b)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private void ClearPicture(bool banner)
    {
        if (banner)
        {
            _draftBanner = string.Empty;
            _bannerPicked = false;
        }
        else
        {
            _draftAvatar = string.Empty;
            _avatarPicked = false;
        }
        _paneDirty = true;
    }

    private void RemoveLanguage(string code)
    {
        _draftLanguages.Remove(code);
        _paneDirty = true;
    }

    private void PickLanguage()
    {
        if (_draftLanguages.Count >= SocialLimits.LanguagesMax)
            return;
        var all = PeopleFormat.Languages();
        var offered = new List<string>();
        var request = new ModalRequest
        {
            Title = PeopleLocale.PickLanguage,
            Invoker = Slot,
            SearchChoices = true,
            CloseOnChoice = true,
            CancelIndex = 0,
            ChoiceChanged = index =>
            {
                if (index < 0 || index >= offered.Count || IsDestroyed || _draftLanguages.Count >= SocialLimits.LanguagesMax)
                    return;
                if (!_draftLanguages.Contains(offered[index]))
                    _draftLanguages.Add(offered[index]);
                _paneDirty = true;
            },
        };
        foreach (var (code, label) in all)
        {
            if (_draftLanguages.Contains(code))
                continue;
            offered.Add(code);
            request.AddChoice(label);
        }
        request.AddOption(PeopleLocale.Cancel);
        ModalHost.For(Slot)?.Open(request);
    }

    private void PickZone()
    {
        var zones = PeopleFormat.TimeZones();
        var request = new ModalRequest
        {
            Title = PeopleLocale.PickZone,
            Invoker = Slot,
            SearchChoices = true,
            CloseOnChoice = true,
            CancelIndex = 0,
            ChoiceChanged = index =>
            {
                if (index < 0 || index >= zones.Count || IsDestroyed)
                    return;
                _draftZone = zones[index].Id;
                _paneDirty = true;
            },
        };
        for (int i = 0; i < zones.Count; i++)
        {
            request.AddChoice(zones[i].Label);
            if (string.Equals(zones[i].Id, _draftZone, StringComparison.Ordinal))
                request.SelectedChoice = i;
        }
        request.AddOption(PeopleLocale.Cancel);
        ModalHost.For(Slot)?.Open(request);
    }

    // The common sets, and none at all. A typed set stays in the box; picking one replaces it.
    private void PickPronouns()
    {
        if (!_drafting)
            return;
        FlushTyping();
        var sets = Lumora.Core.Social.ProfilePronouns.Common;
        var request = new ModalRequest
        {
            Title = PeopleLocale.PickPronouns,
            Invoker = Slot,
            CloseOnChoice = true,
            CancelIndex = 0,
            ChoiceChanged = index =>
            {
                if (index < 0 || index > sets.Length || IsDestroyed || !_drafting)
                    return;
                _draftPronouns = index == 0 ? string.Empty : sets[index - 1];
                _paneDirty = true;
            },
        };
        request.AddChoice(PeopleLocale.PronounsClear.Resolve());
        for (int i = 0; i < sets.Length; i++)
        {
            request.AddChoice(sets[i]);
            if (string.Equals(sets[i], _draftPronouns, StringComparison.Ordinal))
                request.SelectedChoice = i + 1;
        }
        if (_draftPronouns.Length == 0)
            request.SelectedChoice = 0;
        request.AddOption(PeopleLocale.Cancel);
        ModalHost.For(Slot)?.Open(request);
    }

    private void UseLocalZone()
    {
        var local = PeopleFormat.LocalZoneId();
        if (string.IsNullOrEmpty(local))
            return;
        _draftZone = local!;
        _paneDirty = true;
    }

    // THE PICKER

    private void OpenPicker(PickPurpose purpose)
    {
        if (_pane != (purpose == PickPurpose.Report ? PaneView.Report : PaneView.Person))
            return;
        FlushTyping();
        _pick = purpose;
        _pickReturnTab = _tab;
        LeaveChat();
        _pane = PaneView.Pick;
        _paneError = null;
        _paneNotice = null;
        _headerDirty = true;
        _body?.ResetToTop();
        Picker.Open(items: purpose == PickPurpose.Item);
    }

    private void ClosePicker()
    {
        if (_pane != PaneView.Pick)
            return;
        if (_pick == PickPurpose.Report)
        {
            _pane = PaneView.Report;
            _headerDirty = true;
            _paneDirty = true;
            _body?.ResetToTop();
            return;
        }
        _pane = PaneView.Person;
        var back = _pickReturnTab;
        _tab = back == PersonTab.Chat ? PersonTab.Profile : back;
        SetTab(back, force: true);
        _headerDirty = true;
        _paneDirty = true;
    }

    private string PickPath() => Picker.Path(PeopleLocale.Inventory.Resolve());

    private void BuildPickItems()
    {
        _bodyItems.AddRoot(new PeopleNoteItem("pick.hint", _pick == PickPurpose.Item ? PeopleLocale.PickItemHint : PeopleLocale.PickPictureHint));
        if (Picker.Load == InventoryPicker.LoadState.Loading)
        {
            _bodyItems.AddRoot(new PeopleNoteItem("pick.loading", PeopleLocale.Loading));
            return;
        }
        if (!string.IsNullOrEmpty(Picker.Problem))
            _bodyItems.AddRoot(new PeopleNoteItem("pick.problem", Picker.Problem!) { Tint = DashTheme.Warning, Wrap = true, Height = 40f });

        var tiles = Picker.Tiles(out int choosable);
        if (choosable == 0 && Picker.Load == InventoryPicker.LoadState.Loaded)
            _bodyItems.AddRoot(new PeopleNoteItem("pick.empty", PeopleLocale.PickEmpty));
        Picker.AddRows(_bodyItems, tiles, BodyWidth());
    }

    private void PickTile(PeopleTile tile)
    {
        if (!tile.Enabled)
        {
            if (_pick == PickPurpose.Item)
            {
                _paneError = PeopleLocale.NotUploaded.Resolve();
                _paneDirty = true;
            }
            return;
        }
        if (Picker.Step(tile))
        {
            _body?.ResetToTop();
            _headerDirty = true;
            return;
        }
        switch (_pick)
        {
            case PickPurpose.Picture:
            case PickPurpose.Banner:
                var purpose = _pick;
                Picker.PictureOf(tile, hash =>
                {
                    if (_pane != PaneView.Pick || _pick != purpose)
                        return;
                    if (purpose == PickPurpose.Picture)
                    {
                        _draftAvatar = hash;
                        _avatarPicked = true;
                    }
                    else
                    {
                        _draftBanner = hash;
                        _bannerPicked = true;
                    }
                    ClosePicker();
                });
                break;
            case PickPurpose.Item:
                var entry = tile.Entry;
                ClosePicker();
                SendItem(entry);
                break;
            case PickPurpose.Report:
                Picker.PictureOf(tile, hash =>
                {
                    if (_pane != PaneView.Pick || _pick != PickPurpose.Report)
                        return;
                    ClosePicker();
                    UseReportPhoto(hash);
                });
                break;
        }
    }
}
