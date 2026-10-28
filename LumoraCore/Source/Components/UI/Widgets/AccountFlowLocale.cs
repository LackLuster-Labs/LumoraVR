// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Localization;

namespace Lumora.Core.Components.UI;

// What the account card says while an account is made, gets its second lock and confirms its email.
public static class AccountFlowLocale
{
    public static readonly LocaleText CreatePill = LocaleText.Keyed("Account.Flow.CreatePill", "Create account");
    public static readonly LocaleText CreateLink = LocaleText.Keyed("Account.Flow.CreateLink", "New? Make an account");

    public static readonly LocaleText RegisterTitle = LocaleText.Keyed("Account.Flow.RegisterTitle", "Make your Lumora account");
    public static readonly LocaleText RegisterSubtitle = LocaleText.Keyed("Account.Flow.RegisterSubtitle",
        "Your worlds, avatars and friends, on every device you play on.");
    public static readonly LocaleText FieldName = LocaleText.Keyed("Account.Flow.FieldName", "Username");
    public static readonly LocaleText FieldNameHint = LocaleText.Keyed("Account.Flow.FieldNameHint", "3 to 32 letters, numbers, spaces, _ or -");
    public static readonly LocaleText FieldEmail = LocaleText.Keyed("Account.Flow.FieldEmail", "Email");
    public static readonly LocaleText FieldEmailHint = LocaleText.Keyed("Account.Flow.FieldEmailHint", "One link goes here to confirm it.");
    public static readonly LocaleText FieldPassword = LocaleText.Keyed("Account.Flow.FieldPassword", "Password");
    public static readonly LocaleText FieldPasswordHint = LocaleText.Keyed("Account.Flow.FieldPasswordHint",
        "8 or more, with upper and lower case, a number and a symbol");
    public static readonly LocaleText FieldConfirm = LocaleText.Keyed("Account.Flow.FieldConfirm", "Password again");
    public static readonly LocaleText FieldConfirmHint = LocaleText.Keyed("Account.Flow.FieldConfirmHint", "The same once more, to catch a typo.");
    public static readonly LocaleText RegisterSubmit = LocaleText.Keyed("Account.Flow.RegisterSubmit", "Make my account");
    public static readonly LocaleText RegisterBusy = LocaleText.Keyed("Account.Flow.RegisterBusy", "Making your account…");
    public static readonly LocaleText RegisterBack = LocaleText.Keyed("Account.Flow.RegisterBack", "I have one");
    public static readonly LocaleText SigningIn = LocaleText.Keyed("Account.Flow.SigningIn", "Made. Signing you in…");
    public static readonly LocaleText SignInAfterFailed = LocaleText.Keyed("Account.Flow.SignInAfterFailed",
        "Your account is made. Sign in with it from the account card.");

    public static readonly LocaleText ErrorNameLength = LocaleText.Keyed("Account.Flow.ErrorNameLength", "Use 3 to 32 characters.");
    public static readonly LocaleText ErrorNameChars = LocaleText.Keyed("Account.Flow.ErrorNameChars", "Only letters, numbers, spaces, _ and - work here.");
    public static readonly LocaleText ErrorNameTaken = LocaleText.Keyed("Account.Flow.ErrorNameTaken", "That name is taken.");
    public static readonly LocaleText ErrorEmail = LocaleText.Keyed("Account.Flow.ErrorEmail", "That doesn't look like an email address.");
    public static readonly LocaleText ErrorEmailTaken = LocaleText.Keyed("Account.Flow.ErrorEmailTaken", "That address has an account already.");
    public static readonly LocaleText ErrorPassword = LocaleText.Keyed("Account.Flow.ErrorPassword",
        "8 to 100 characters, with an upper and a lower case letter, a number and a symbol.");
    public static readonly LocaleText ErrorConfirm = LocaleText.Keyed("Account.Flow.ErrorConfirm", "The two passwords differ.");
    public static readonly LocaleText ErrorTooMany = LocaleText.Keyed("Account.Flow.ErrorTooMany",
        "Too many new accounts from this connection lately. Try again later.");
    public static readonly LocaleText ErrorFailed = LocaleText.Keyed("Account.Flow.ErrorFailed",
        "The account could not be made right now. Try again in a moment.");
    public static readonly LocaleText Offline = LocaleText.Keyed("Account.Flow.Offline", "You're offline.");

    public static LocaleText OfferTitle(string name) => LocaleText.Keyed("Account.Flow.OfferTitle", "You're in, {0}", name);
    public static readonly LocaleText OfferLead = LocaleText.Keyed("Account.Flow.OfferLead", "Add a second lock?");
    public static readonly LocaleText OfferText = LocaleText.Keyed("Account.Flow.OfferText",
        "Two-factor asks for a 6-digit code from an app on your phone each time you sign in, so a password on its own gets nobody in. Optional, and it takes a minute.");
    public static readonly LocaleText SetUp = LocaleText.Keyed("Account.Flow.SetUp", "Set up two-factor");
    public static readonly LocaleText NotNow = LocaleText.Keyed("Account.Flow.NotNow", "Not now");
    public static readonly LocaleText Starting = LocaleText.Keyed("Account.Flow.Starting", "Getting your key…");

    public static readonly LocaleText ScanTitle = LocaleText.Keyed("Account.Flow.ScanTitle", "Scan this with your authenticator app");
    public static readonly LocaleText KeyLabel = LocaleText.Keyed("Account.Flow.KeyLabel", "Or type this key:");
    public static readonly LocaleText CopyKey = LocaleText.Keyed("Account.Flow.CopyKey", "Copy");
    public static readonly LocaleText Copied = LocaleText.Keyed("Account.Flow.Copied", "Key copied.");
    public static readonly LocaleText CodeLabel = LocaleText.Keyed("Account.Flow.CodeLabel", "Then the 6-digit code it shows:");
    public static readonly LocaleText TurnOn = LocaleText.Keyed("Account.Flow.TurnOn", "Turn on two-factor");
    public static readonly LocaleText Checking = LocaleText.Keyed("Account.Flow.Checking", "Checking…");
    public static readonly LocaleText WrongCode = LocaleText.Keyed("Account.Flow.WrongCode",
        "That code didn't match. Codes change every 30 seconds; use the newest one.");
    public static readonly LocaleText SixDigits = LocaleText.Keyed("Account.Flow.SixDigits", "The code is 6 digits.");
    public static readonly LocaleText TwoFactorFailed = LocaleText.Keyed("Account.Flow.TwoFactorFailed",
        "Two-factor could not be set up right now. Try again in a moment.");
    public static readonly LocaleText TwoFactorAlreadyOn = LocaleText.Keyed("Account.Flow.TwoFactorAlreadyOn", "Two-factor is on already.");

    public static readonly LocaleText OnTitle = LocaleText.Keyed("Account.Flow.OnTitle", "Two-factor is on");
    public static readonly LocaleText CodesText = LocaleText.Keyed("Account.Flow.CodesText",
        "Recovery codes: each one signs you in once without your phone. Save them somewhere safe now; they are not shown again.");
    public static readonly LocaleText SavedThem = LocaleText.Keyed("Account.Flow.SavedThem", "I saved them");
    public static readonly LocaleText TwoFactorPill = LocaleText.Keyed("Account.Flow.TwoFactorPill", "Two-factor");

    public static readonly LocaleText VerifyTitle = LocaleText.Keyed("Account.Flow.VerifyTitle", "Confirm your email");
    public static LocaleText VerifyText(string address) => string.IsNullOrEmpty(address)
        ? LocaleText.Keyed("Account.Flow.VerifyTextNoAddress", "We sent a link to your email. Open it on any device; this window notices by itself.")
        : LocaleText.Keyed("Account.Flow.VerifyText", "We sent a link to {0}. Open it on any device; this window notices by itself.", address);
    public static readonly LocaleText Waiting = LocaleText.Keyed("Account.Flow.Waiting", "Waiting for the link");
    public static readonly LocaleText Later = LocaleText.Keyed("Account.Flow.Later", "Later");

    public static readonly LocaleText VerifiedTitle = LocaleText.Keyed("Account.Flow.VerifiedTitle", "Email verified");
    public static readonly LocaleText VerifiedText = LocaleText.Keyed("Account.Flow.VerifiedText",
        "Saving to your inventory and joining other people's worlds are open to you now.");
    public static readonly LocaleText Done = LocaleText.Keyed("Account.Flow.Done", "Done");

    public static readonly LocaleText NoticeTitle = LocaleText.Keyed("Account.Flow.NoticeTitle", "Verify your email");
    public static LocaleText NoticeText(bool saves, bool joins, string address)
    {
        string where = string.IsNullOrEmpty(address) ? NoticeYourAddress.Resolve() : address;
        if (saves && joins)
            return LocaleText.Keyed("Account.Flow.NoticeBoth",
                "Saving to your inventory and joining other people's worlds wait until you open the link we sent to {0}.", where);
        if (joins)
            return LocaleText.Keyed("Account.Flow.NoticeJoins", "Joining other people's worlds waits until you open the link we sent to {0}.", where);
        return LocaleText.Keyed("Account.Flow.NoticeSaves", "Saving to your inventory waits until you open the link we sent to {0}.", where);
    }
    public static readonly LocaleText NoticeYourAddress = LocaleText.Keyed("Account.Flow.NoticeYourAddress", "your email address");
    public static readonly LocaleText NoticeSend = LocaleText.Keyed("Account.Flow.NoticeSend", "Send the email again");
}
