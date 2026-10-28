// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Localization;
using Lumora.Core.Networking.Session;

namespace Lumora.Core.Components.UI;

// Every word of asking to edit somebody, and of being asked: the prompt, the notices, the inspector line, the ring
// item and the Users list.
public static class EditConsentLocale
{
    public static LocaleText PromptTitle(string asker) => "Consent.Prompt.Title".AsLocale("Let {0} edit you?", asker);

    public static LocaleText PromptMessage(string asker, string world)
        => "Consent.Prompt.Message".AsLocale(
            "{0} wants to edit your avatar and the things you hold, in {1}.\nChoose this session, or remember this person on this device. Take access back in Session > Users.",
            asker, world);

    public static readonly LocaleText Remember = "Consent.Prompt.Remember".AsLocale("Remember on this device");
    public static readonly LocaleText RememberedMessage = "Consent.Granted.Remembered".AsLocale("This account can edit your avatar and held items whenever you meet on this device. Session > Users lists remembered access, including people who are offline, and lets you revoke it.");
    public static readonly LocaleText RememberedHeading = "Consent.Users.Remembered".AsLocale("Remembered edit access on this device");
    public static readonly LocaleText Forget = "Consent.Users.Forget".AsLocale("Revoke remembered access");

    public static readonly LocaleText Deny = "Consent.Prompt.Deny".AsLocale("Deny");
    public static readonly LocaleText Allow = "Consent.Prompt.Allow".AsLocale("Allow for this session");

    public static LocaleText GrantedTitle(string asker) => "Consent.Granted.Title".AsLocale("{0} can edit you", asker);

    public static readonly LocaleText GrantedMessage =
        "Consent.Granted.Message".AsLocale("Your avatar and the things you hold, until one of you leaves. Session > Users shows who can edit you.");

    public static readonly LocaleText TakeBack = "Consent.Granted.TakeBack".AsLocale("Take it back");
    public static readonly LocaleText Ok = "Consent.Granted.Ok".AsLocale("OK");

    public static LocaleText YesTitle(string owner) => "Consent.Notice.Yes.Title".AsLocale("{0} said yes", owner);

    public static LocaleText YesText(string owner)
        => "Consent.Notice.Yes.Text".AsLocale("You can edit {0}'s avatar and what they hold until one of you leaves or {0} takes it back.", owner);

    public static LocaleText NoTitle(string owner) => "Consent.Notice.No.Title".AsLocale("{0} said no", owner);
    public static readonly LocaleText NoText = "Consent.Notice.No.Text".AsLocale("You can ask again in a few minutes.");

    public static LocaleText NoAnswerTitle(string owner) => "Consent.Notice.NoAnswer.Title".AsLocale("No answer from {0}", owner);
    public static readonly LocaleText NoAnswerText = "Consent.Notice.NoAnswer.Text".AsLocale("You can ask again in a minute.");

    public static LocaleText TakenBackTitle(string owner) => "Consent.Notice.TakenBack.Title".AsLocale("{0} took it back", owner);

    public static LocaleText TakenBackText(string owner)
        => "Consent.Notice.TakenBack.Text".AsLocale("You can no longer edit {0}'s avatar or what they hold.", owner);

    public static LocaleText RefusedTitle(string owner) => "Consent.Notice.Refused.Title".AsLocale("That is {0}'s", owner);

    public static LocaleText RefusedText(string owner)
        => "Consent.Notice.Refused.Text".AsLocale("Only {0} changes their body, their avatar and what they hold. Ask {0} from the tool's ring or the inspector.", owner);

    public static LocaleText RootText(string owner)
        => "Consent.Notice.Root.Text".AsLocale("Where {0} stands stays theirs, even with a yes. Pick a part of the avatar instead.", owner);

    public static readonly LocaleText AskItem = "Consent.Ring.Ask".AsLocale("Ask to Edit");
    public static readonly LocaleText AskedItem = "Consent.Ring.Asked".AsLocale("Asked");
    public static readonly LocaleText WaitItem = "Consent.Ring.Wait".AsLocale("Ask Later");

    public static LocaleText InspectorLocked(string owner)
        => "Consent.Inspector.Locked".AsLocale("Part of {0}: you can look, not change it.", owner);

    public static LocaleText InspectorAsk(string owner) => "Consent.Inspector.Ask".AsLocale("Ask {0} for permission", owner);

    public static LocaleText InspectorPending(string owner)
        => "Consent.Inspector.Pending".AsLocale("Asked {0}, waiting for an answer.", owner);

    public static LocaleText InspectorDenied(string owner, string wait)
        => "Consent.Inspector.Denied".AsLocale("{0} said no. You can ask again in {1}.", owner, wait);

    public static LocaleText InspectorWaiting(string owner, string wait)
        => "Consent.Inspector.Waiting".AsLocale("You can ask {0} again in {1}.", owner, wait);

    public static LocaleText InspectorGranted(string owner)
        => "Consent.Inspector.Granted".AsLocale("{0} lets you edit them this session.", owner);

    public static LocaleText ReadOnlyTip(string owner) => "Consent.Inspector.ReadOnlyTip".AsLocale("Part of {0}: ask them first", owner);

    public static LocaleText InspectorRoot(string owner)
        => "Consent.Inspector.Root".AsLocale("{0}'s root: where they stand stays theirs.", owner);

    public static readonly LocaleText UsersAsk = "Consent.Users.Ask".AsLocale("Ask to edit");
    public static readonly LocaleText UsersAsked = "Consent.Users.Asked".AsLocale("Asked");
    public static readonly LocaleText UsersDenied = "Consent.Users.Denied".AsLocale("Said no");
    public static readonly LocaleText UsersWait = "Consent.Users.Wait".AsLocale("Ask later");
    public static readonly LocaleText UsersCanEdit = "Consent.Users.CanEdit".AsLocale("You can edit");
    public static readonly LocaleText UsersTakeBack = "Consent.Users.TakeBack".AsLocale("Stop their edits");

    // m:ss, for a wait that is mostly a few minutes.
    public static string Clock(long seconds)
    {
        seconds = System.Math.Max(0, seconds);
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    // What the ring and the Users list say for where this machine's user stands with someone.
    public static LocaleText AskLabel(EditAskState state) => state switch
    {
        EditAskState.Pending => UsersAsked,
        EditAskState.Denied => UsersDenied,
        EditAskState.Waiting => UsersWait,
        EditAskState.Granted => UsersCanEdit,
        _ => UsersAsk,
    };
}
