// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Localization;

namespace Lumora.Core.Security;

// Every word Avatar Protection shows, in one place so the keys and the English behind them stay together.
public static class ProtectionLocale
{
    public static LocaleText By(string owner) => LocaleText.Keyed("Protection.By", "Protected by {0}", owner);
    public static readonly LocaleText SomeoneElse = LocaleText.Keyed("Protection.SomeoneElse", "another account");
    public static readonly LocaleText BrokenReason = LocaleText.Keyed("Protection.BrokenReason", "its Avatar Protection doesn't check out");
    public static readonly LocaleText CheckingReason = LocaleText.Keyed("Protection.CheckingReason", "its Avatar Protection is still being checked");

    public static readonly LocaleText RefusedText = LocaleText.Keyed("Protection.Refused.Text",
        "Only its owner can save, wear, copy, export or look inside it.");
    public static readonly LocaleText BrokenTitle = LocaleText.Keyed("Protection.Broken.Title", "Avatar Protection broken");
    public static readonly LocaleText BrokenText = LocaleText.Keyed("Protection.Broken.Text",
        "Its Avatar Protection doesn't check out, so nobody can take, wear or open it.");
    public static readonly LocaleText CheckingTitle = LocaleText.Keyed("Protection.Checking.Title", "Still checking");
    public static readonly LocaleText CheckingText = LocaleText.Keyed("Protection.Checking.Text",
        "Its Avatar Protection is still being checked. Try again in a moment.");

    public static readonly LocaleText MenuRemove = LocaleText.Keyed("Protection.Menu.Remove", "Remove Avatar Protection");
    public static readonly LocaleText MenuProtected = LocaleText.Keyed("Protection.Menu.Protected", "Protected");

    public static readonly LocaleText DoneTitle = LocaleText.Keyed("Protection.Done.Title", "Avatar Protection on");
    public static readonly LocaleText DoneText = LocaleText.Keyed("Protection.Done.Text",
        "Only you can save, wear, copy, export or look inside it now.");
    public static readonly LocaleText RemovedTitle = LocaleText.Keyed("Protection.Removed.Title", "Avatar Protection removed");
    public static readonly LocaleText RemovedText = LocaleText.Keyed("Protection.Removed.Text",
        "It is an ordinary object again.");
    public static readonly LocaleText FailedTitle = LocaleText.Keyed("Protection.Failed.Title", "Couldn't add Avatar Protection");
    public static readonly LocaleText FailedSignIn = LocaleText.Keyed("Protection.Failed.SignIn", "Sign in to use Avatar Protection.");
    public static readonly LocaleText FailedNotYours = LocaleText.Keyed("Protection.Failed.NotYours",
        "Only something you brought into this world can get Avatar Protection.");
    public static readonly LocaleText FailedAlready = LocaleText.Keyed("Protection.Failed.Already", "It has Avatar Protection already.");
    public static readonly LocaleText FailedUnavailable = LocaleText.Keyed("Protection.Failed.Unavailable",
        "Avatar Protection isn't available right now. Try again later.");
    public static LocaleText FailedService(string message) => LocaleText.Keyed("Protection.Failed.Service", "The service said no: {0}", message);
    public static readonly LocaleText OnlyOwnerRemoves = LocaleText.Keyed("Protection.OnlyOwnerRemoves",
        "Only its owner can remove its Avatar Protection.");

    public static readonly LocaleText TakenOffTitle = LocaleText.Keyed("Protection.TakenOff.Title", "Your avatar was taken off");
    public static LocaleText TakenOffSealed(string owner) => LocaleText.Keyed("Protection.TakenOff.Sealed",
        "It is protected by {0}, and only they can wear it.", owner);
    public static readonly LocaleText TakenOffBroken = LocaleText.Keyed("Protection.TakenOff.Broken",
        "Its Avatar Protection doesn't check out, so nobody can wear it.");
    public static readonly LocaleText TakenOutTitle = LocaleText.Keyed("Protection.TakenOut.Title", "Taken out of the world");
    public static LocaleText TakenOutBroken(string name) => LocaleText.Keyed("Protection.TakenOut.Broken",
        "\"{0}\" carries an Avatar Protection that doesn't check out.", name);
    public static LocaleText TakenOutStripped(string name, string owner) => LocaleText.Keyed("Protection.TakenOut.Stripped",
        "Someone other than {1} took the Avatar Protection off \"{0}\".", name, owner);

    public static readonly LocaleText LeftOutTitle = LocaleText.Keyed("Protection.LeftOut.Title", "Protected objects left out");
    public static LocaleText LeftOutText(int count) => LocaleText.Keyed("Protection.LeftOut.Text",
        "{0} protected object(s) belonging to someone else only open from their owner's saves.", count);

    public static readonly LocaleText InspectorOwner = LocaleText.Keyed("Protection.Inspector.Owner", "Owner");
    public static readonly LocaleText InspectorState = LocaleText.Keyed("Protection.Inspector.State", "State");
    public static readonly LocaleText InspectorBlocks = LocaleText.Keyed("Protection.Inspector.Blocks", "Blocks");
    public static readonly LocaleText InspectorForOthers = LocaleText.Keyed("Protection.Inspector.ForOthers", "for everyone but the owner");
    public static readonly LocaleText StateChecking = LocaleText.Keyed("Protection.State.Checking", "Checking");
    public static readonly LocaleText StateVerified = LocaleText.Keyed("Protection.State.Verified", "Verified");
    public static readonly LocaleText StateBroken = LocaleText.Keyed("Protection.State.Broken", "Broken");
    public static readonly LocaleText StateUnsigned = LocaleText.Keyed("Protection.State.Unsigned", "Waiting to be signed");
    public static readonly LocaleText RuleEquip = LocaleText.Keyed("Protection.Rule.Equip", "wear");
    public static readonly LocaleText RuleSaveCopy = LocaleText.Keyed("Protection.Rule.SaveCopy", "save or copy");
    public static readonly LocaleText RuleExport = LocaleText.Keyed("Protection.Rule.Export", "export");
    public static readonly LocaleText RuleInspect = LocaleText.Keyed("Protection.Rule.Inspect", "inspect");
    public static LocaleText InspectorHidden(string owner) => LocaleText.Keyed("Protection.Inspector.Hidden",
        "Protected by {0}. Only its owner can look inside.", owner);

    public static readonly LocaleText StudioToggle = LocaleText.Keyed("Protection.Studio.Toggle", "Avatar Protection");
}
