// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Localization;
using Lumora.Core.Networking.Session;

namespace Lumora.Core.Components.UI;

// Every string an invite puts on screen: the buttons that send one, the friend picker, the card in a chat.
public static class InviteLocale
{
    public static readonly LocaleText Invite = "Invite.Button".AsLocale("Invite");
    public static LocaleText InviteTo(string world) => "Invite.To".AsLocale("Invite to {0}", world);

    public static readonly LocaleText PickerHint = "Invite.Picker.Hint".AsLocale(
        "Pick the friends to invite. Each gets a message with a picture of the world and a way in.");
    public static readonly LocaleText PickerOnline = "Invite.Picker.Online".AsLocale("Online");
    public static readonly LocaleText PickerOffline = "Invite.Picker.Offline".AsLocale("Offline");
    public static readonly LocaleText PickerEmpty = "Invite.Picker.Empty".AsLocale("No friends to invite yet. Add some in the Friends window.");
    public static readonly LocaleText PickerHere = "Invite.Picker.Here".AsLocale("Here already");
    public static readonly LocaleText Send = "Invite.Send".AsLocale("Send invites");
    public static LocaleText SendCount(int count) => "Invite.Send.Count".AsLocale("Invite {0}", count);
    public static readonly LocaleText Close = "Invite.Close".AsLocale("Close");

    public static readonly LocaleText Sending = "Invite.Sending".AsLocale("Sending invites...");
    public static LocaleText SentTo(string name) => "Invite.Sent.One".AsLocale("Invite sent to {0}.", name);
    public static LocaleText SentMany(int count) => "Invite.Sent.Many".AsLocale("Invites sent to {0} friends.", count);
    public static LocaleText Failed(string name, string why) => "Invite.Failed".AsLocale("Couldn't invite {0}: {1}", name, why);

    public static readonly LocaleText BarNoSession = "Invite.Bar.NoSession".AsLocale("You're not in a world others can join.");
    public static readonly LocaleText BarHome = "Invite.Bar.Home".AsLocale("Your home is yours alone. Open a world to invite people.");
    public static readonly LocaleText BarSignedOut = "Invite.Bar.SignedOut".AsLocale("Sign in to invite friends.");
    public static readonly LocaleText BarHostOnly = "Invite.Bar.HostOnly".AsLocale("Only the host can invite people to a private or LAN world.");
    public static readonly LocaleText BarHostTeam = "Invite.Bar.HostTeam".AsLocale("Only the host and co-hosts can invite people here.");
    public static readonly LocaleText BarHostsFriends = "Invite.Bar.HostsFriends".AsLocale("Only the host's friends can invite people here.");

    public static readonly LocaleText ProblemRefused = "Invite.Problem.Refused".AsLocale("the host didn't let it through");
    public static readonly LocaleText ProblemNoAnswer = "Invite.Problem.NoAnswer".AsLocale("the host didn't answer");
    public static readonly LocaleText ProblemNotListed = "Invite.Problem.NotListed".AsLocale("the world couldn't be listed for invites");

    public static LocaleText CardFrom(string name) => "Invite.Card.From".AsLocale("{0} invites you", name);
    public static readonly LocaleText CardMine = "Invite.Card.Mine".AsLocale("Your invite");
    public static LocaleText CardHost(string name) => "Invite.Card.Host".AsLocale("Hosted by {0}", name);
    public static LocaleText CardHere(int users, int max) => "Invite.Card.Here".AsLocale("{0} / {1} here", users, max);
    public static readonly LocaleText CardJoin = "Invite.Card.Join".AsLocale("Join");
    public static readonly LocaleText CardOpen = "Invite.Card.Open".AsLocale("Go there");
    public static readonly LocaleText CardCopy = "Invite.Card.Copy".AsLocale("Copy link");
    public static readonly LocaleText CardEnded = "Invite.Card.Ended".AsLocale("This session has ended");
    public static readonly LocaleText CardExpired = "Invite.Card.Expired".AsLocale("This invite has run out");
    public static readonly LocaleText CardEndedPill = "Invite.Card.EndedPill".AsLocale("Ended");

    public static readonly LocaleText Copied = "Invite.Copied".AsLocale("Link copied. On a computer with Lumora it opens the game and joins.");
    public static readonly LocaleText NoLink = "Invite.NoLink".AsLocale("This invite has no address to copy.");
    public static LocaleText Joining(string world) => "Invite.Joining".AsLocale("Joining {0}...", world);
    public static readonly LocaleText JoinFailed = "Invite.JoinFailed".AsLocale("Couldn't reach that session.");

    public static LocaleText Bar(InviteBar bar) => bar switch
    {
        InviteBar.Home => BarHome,
        InviteBar.SignedOut => BarSignedOut,
        InviteBar.HostOnly => BarHostOnly,
        InviteBar.HostTeam => BarHostTeam,
        InviteBar.HostsFriends => BarHostsFriends,
        _ => BarNoSession,
    };

    // The level as the directory names it, in the words every screen uses for it.
    public static LocaleText Access(string? level) => level switch
    {
        "Public" => AccessLabels.Anyone,
        "Contacts" => AccessLabels.Friends,
        "LAN" => AccessLabels.Lan,
        _ => AccessLabels.Private,
    };
}
