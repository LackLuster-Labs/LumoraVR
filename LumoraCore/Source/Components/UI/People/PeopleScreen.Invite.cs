// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components.Network;
using Lumora.Core.Networking.Session;
using Lumora.Core.Social;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// INVITES: sending one from a friend's profile, and the card one makes in the chat.
public sealed partial class PeopleScreen
{
    private const double InviteStateSeconds = 30.0;
    private const double InviteRightSeconds = 0.5;
    private const int InviteNameMax = 18;

    private readonly Dictionary<string, ChatInviteStateInfo> _inviteStates = new(StringComparer.Ordinal);
    private double _inviteStatesAt = double.NegativeInfinity;
    private bool _inviteStatesBusy;
    private double _inviteRightAt = double.NegativeInfinity;
    private string _inviteRightKey = string.Empty;
    private bool _inviting;

    // The world a friend can be invited to from here: the focused one, when this account may invite there.
    private static World? InviteWorld(out InviteRight right)
    {
        var world = Engine.Current?.WorldManager?.FocusedWorld;
        right = SessionInvites.RightOf(world, world?.LocalUser);
        return right.Allowed ? world : null;
    }

    private static bool IsIn(World world, string accountId)
    {
        foreach (var user in world.GetAllUsers())
        {
            if (user != null && !user.IsDestroyed && string.Equals(user.AccountId.Value, accountId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string InviteWorldName(World world)
    {
        string name = string.IsNullOrEmpty(world.WorldName?.Value) ? world.Name : world.WorldName!.Value;
        return name.Length > InviteNameMax ? name.Substring(0, InviteNameMax - 1).TrimEnd() + "…" : name;
    }

    // What the header's Invite pill hangs on, looked at twice a second: focus moving, the level or a role
    // changing, the person on show arriving in that world or leaving it.
    private string InviteKey()
    {
        var world = InviteWorld(out var right);
        return world == null ? string.Empty
            : world.SessionID.Value + "|" + right.Way + "|" + InviteWorldName(world) + "|" + (_personId.Length > 0 && IsIn(world, _personId));
    }

    private void TickInvites()
    {
        double now = Clock;
        if (_pane == PaneView.Person && now - _inviteRightAt >= InviteRightSeconds)
        {
            _inviteRightAt = now;
            string key = InviteKey();
            if (!string.Equals(key, _inviteRightKey, StringComparison.Ordinal))
            {
                _inviteRightKey = key;
                _headerDirty = true;
            }
        }
        if (_chatVisible && _pane == PaneView.Person && _tab == PersonTab.Chat && now - _inviteStatesAt >= InviteStateSeconds)
            RefreshInviteStates();
    }

    // FROM A PROFILE

    private void InvitePerson(string person)
    {
        if (_inviting || string.IsNullOrEmpty(person))
            return;
        var world = InviteWorld(out var right);
        if (world == null)
        {
            _paneError = InviteLocale.Bar(right.Bar).Resolve();
            _paneDirty = true;
            return;
        }
        _inviting = true;
        _paneError = null;
        _paneNotice = InviteLocale.Sending.Resolve();
        _paneDirty = true;
        string name = ShownName();
        var own = World;
        _ = InviteSender.SendAsync(world, new[] { person }).ContinueWith(t =>
        {
            var outcome = t.IsCompletedSuccessfully && t.Result.Count > 0 ? t.Result[0] : null;
            string? why = t.IsCompletedSuccessfully ? null : t.Exception?.GetBaseException().Message;
            OnUi(own, () =>
            {
                _inviting = false;
                if (IsDestroyed)
                    return;
                if (outcome != null && outcome.Sent)
                {
                    _paneNotice = InviteLocale.SentTo(name).Resolve();
                    _paneError = null;
                    // the card is the proof it went; their chat is where it sits
                    if (string.Equals(person, _personId, StringComparison.Ordinal) && _tab == PersonTab.Profile)
                        SwitchTab(PersonTab.Chat);
                }
                else
                {
                    _paneNotice = null;
                    _paneError = InviteLocale.Failed(name, outcome != null ? Why(outcome) : why ?? PeopleLocale.ActionFailed.Resolve()).Resolve();
                }
                _paneDirty = true;
            });
        });
    }

    internal static string Why(InviteOutcome outcome) => outcome.Problem switch
    {
        InviteProblem.Refused => InviteLocale.ProblemRefused.Resolve(),
        InviteProblem.NoAnswer => InviteLocale.ProblemNoAnswer.Resolve(),
        InviteProblem.NotListed => InviteLocale.ProblemNotListed.Resolve(),
        InviteProblem.Service => string.IsNullOrWhiteSpace(outcome.Message) ? PeopleLocale.ActionFailed.Resolve() : outcome.Message!,
        _ => InviteLocale.BarNoSession.Resolve(),
    };

    // THE CARD

    private PeopleInviteMessageItem InviteItem(ChatMessageInfo message, bool mine, string meta)
    {
        var invite = message.Invite!;
        var state = InviteCardState.Live;
        int users = invite.Users;
        int max = invite.MaxUsers;
        if (_inviteStates.TryGetValue(message.Id, out var known))
        {
            if (!known.Live)
                state = InviteCardState.Ended;
            else
            {
                users = known.Users;
                max = known.MaxUsers;
            }
        }
        if (state == InviteCardState.Live && message.ClaimableUntil is DateTime until && until.ToUniversalTime() < DateTime.UtcNow)
            state = InviteCardState.Expired;
        return new PeopleInviteMessageItem("msg." + message.Id)
        {
            Message = message,
            Mine = mine,
            FromName = ShownName(),
            Meta = meta,
            State = state,
            Users = users,
            MaxUsers = max,
            Here = OpenWorldFor(invite.SessionId) != null,
        };
    }

    private static World? OpenWorldFor(string sessionId)
    {
        var manager = Engine.Current?.WorldManager;
        if (manager == null || string.IsNullOrEmpty(sessionId))
            return null;
        foreach (var world in manager.Worlds)
        {
            if (world == null || world.IsDestroyed || world.IsUserspace)
                continue;
            if (string.Equals(world.SessionID.Value, sessionId, StringComparison.Ordinal)
                || string.Equals(world.Session?.SessionId, sessionId, StringComparison.Ordinal))
                return world;
        }
        return null;
    }

    // Whether the sessions the open chat's invites name still run, newest first, at most the service's twenty.
    private async void RefreshInviteStates()
    {
        var social = Social;
        string person = _chatFor;
        _inviteStatesAt = Clock;
        if (social == null || person.Length == 0 || _inviteStatesBusy)
            return;
        var ids = new List<string>();
        for (int i = _messages.Count - 1; i >= 0 && ids.Count < 20; i--)
        {
            var message = _messages[i];
            if (!message.IsInvite || IsLocal(message))
                continue;
            if (message.ClaimableUntil is DateTime until && until.ToUniversalTime() < DateTime.UtcNow)
                continue;
            if (_inviteStates.TryGetValue(message.Id, out var known) && !known.Live)
                continue;
            ids.Add(message.Id);
        }
        if (ids.Count == 0)
            return;
        _inviteStatesBusy = true;
        var world = World;
        try
        {
            var answer = await social.GetInviteStatesAsync(person, ids);
            OnUi(world, () =>
            {
                _inviteStatesBusy = false;
                if (!answer.Success || answer.Data == null)
                    return;
                bool moved = false;
                foreach (var state in answer.Data)
                {
                    if (!_inviteStates.TryGetValue(state.MessageId, out var was) || was != state)
                    {
                        _inviteStates[state.MessageId] = state;
                        moved = true;
                    }
                }
                // the cards carry their state, so they are made again rather than rebound
                if (moved && string.Equals(person, _chatFor, StringComparison.Ordinal) && _tab == PersonTab.Chat)
                    _paneDirty = true;
            });
        }
        catch (Exception)
        {
            OnUi(world, () => _inviteStatesBusy = false);
        }
    }

    private void PressInvite(PeopleInviteMessageItem card, int index)
    {
        switch (index)
        {
            case 0:
                JoinInvite(card);
                break;
            case 1:
                CopyInviteLink(card);
                break;
            default:
                OpenMessageMenu(card.Message);
                break;
        }
    }

    // Focus it when it is open here; else the directory's listing, as any join from a friend's profile goes; else
    // the addresses the invite carries, which is how an account the directory would not show the session to
    // gets there.
    private void JoinInvite(PeopleInviteMessageItem card)
    {
        var invite = card.Message.Invite;
        if (invite == null || card.State != InviteCardState.Live)
            return;
        var manager = Engine.Current?.WorldManager;
        var open = OpenWorldFor(invite.SessionId);
        if (open != null)
        {
            manager?.FocusWorld(open);
            return;
        }
        _paneError = null;
        _paneNotice = InviteLocale.Joining(invite.WorldName).Resolve();
        _paneDirty = true;
        JoinInviteAsync(manager?.UserspaceWorld?.RootSlot?.GetComponent<SessionBrowser>(), invite);
    }

    private async void JoinInviteAsync(SessionBrowser? browser, ChatInviteInfo invite)
    {
        var world = World;
        SessionListEntry? entry = null;
        if (browser != null)
        {
            try
            {
                entry = await browser.FindSessionAsync(invite.SessionId);
            }
            catch (Exception ex)
            {
                Lumora.Core.Logging.Logger.Warn($"PeopleScreen: invite lookup failed - {ex.Message}");
            }
        }
        OnUi(world, () =>
        {
            if (IsDestroyed)
                return;
            var dash = _dashboard ?? Dashboard.Of(Slot);
            if (entry != null && dash != null)
            {
                foreach (var screen in dash.Screens)
                {
                    if (screen is WorldsScreen worlds)
                    {
                        worlds.JoinListedSession(entry);
                        return;
                    }
                }
            }
            var addresses = SessionJoinLinks.Dialable(invite.Addresses);
            var loader = Engine.Current?.WorldLoadingService;
            if (addresses.Count == 0 || loader == null || loader.JoinSessionAsync(invite.WorldName, addresses, focusWhenReady: true) == null)
            {
                _paneNotice = null;
                _paneError = InviteLocale.JoinFailed.Resolve();
                _paneDirty = true;
            }
        });
    }

    private void CopyInviteLink(PeopleInviteMessageItem card)
    {
        string? link = card.State == InviteCardState.Live ? SessionJoinLinks.For(card.Message.Invite?.Addresses) : null;
        var clipboard = Engine.Current?.InputInterface?.ClipboardText;
        if (link == null || clipboard == null)
        {
            _paneNotice = null;
            _paneError = InviteLocale.NoLink.Resolve();
        }
        else
        {
            clipboard.SetText(link);
            _paneError = null;
            _paneNotice = InviteLocale.Copied.Resolve();
        }
        _paneDirty = true;
    }
}
