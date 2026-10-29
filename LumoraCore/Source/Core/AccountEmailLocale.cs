// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Localization;

namespace Lumora.Core;

// What the game says about an account whose email address is not confirmed yet.
public static class AccountEmailLocale
{
    public static readonly LocaleText SaveRefused = LocaleText.Keyed("Account.Email.SaveRefused",
        "Verify your email to save to your inventory.");

    public static readonly LocaleText SaveRefusedTitle = LocaleText.Keyed("Account.Email.SaveRefusedTitle", "Not saved");

    public static readonly LocaleText SaveRefusedHint = LocaleText.Keyed("Account.Email.SaveRefusedHint",
        "Verify your email to save. Your account card on the dash sends the link again.");

    public static readonly LocaleText VerifyFirst = LocaleText.Keyed("Account.Email.VerifyFirst", "Verify email first");

    public static readonly LocaleText ChipWaiting = LocaleText.Keyed("Account.Email.ChipWaiting", "Verify email");

    public static readonly LocaleText SyncWaiting = LocaleText.Keyed("Account.Email.SyncWaiting", "Waiting for a verified email");

    public static readonly LocaleText NotVerified = LocaleText.Keyed("Account.Email.NotVerified", "Email not verified");

    public static readonly LocaleText Verified = LocaleText.Keyed("Account.Email.Verified", "Verified account");

    public static readonly LocaleText SendAgain = LocaleText.Keyed("Account.Email.SendAgain", "Send again");

    public static LocaleText SendAgainIn(int seconds)
        => LocaleText.Keyed("Account.Email.SendAgainIn", "Send again in {0} s", seconds);

    public static readonly LocaleText Sending = LocaleText.Keyed("Account.Email.Sending", "Sending…");

    public static readonly LocaleText Check = LocaleText.Keyed("Account.Email.Check", "I clicked it");

    public static readonly LocaleText Checking = LocaleText.Keyed("Account.Email.Checking", "Checking…");

    public static LocaleText Sent(string address) => string.IsNullOrEmpty(address)
        ? LocaleText.Keyed("Account.Email.SentNoAddress", "Sent. Open the link in the email, then come back here.")
        : LocaleText.Keyed("Account.Email.Sent", "Sent to {0}. Open the link in it, then come back here.", address);

    public static LocaleText WaitSeconds(int seconds)
        => LocaleText.Keyed("Account.Email.WaitSeconds", "One email a minute: you can send it again in {0} s.", seconds);

    public static readonly LocaleText MailDown = LocaleText.Keyed("Account.Email.MailDown",
        "Could not send the email right now. Try again later.");

    public static readonly LocaleText AlreadyVerified = LocaleText.Keyed("Account.Email.AlreadyVerified", "Your email is verified already.");

    public static readonly LocaleText SignInFirst = LocaleText.Keyed("Account.Email.SignInFirst", "Sign in first.");

    public static readonly LocaleText SendFailed = LocaleText.Keyed("Account.Email.SendFailed",
        "The email did not go out. Try again in a moment.");

    public static readonly LocaleText StillNotVerified = LocaleText.Keyed("Account.Email.StillNotVerified",
        "Not verified yet. Open the link in the email first.");

    public static readonly LocaleText NowVerified = LocaleText.Keyed("Account.Email.NowVerified", "Verified. Saving works now.");

    public static LocaleText InventoryLine(string address) => string.IsNullOrEmpty(address)
        ? LocaleText.Keyed("Account.Email.InventoryLineNoAddress", "Verify your email to save here. Everything you have stays as it is.")
        : LocaleText.Keyed("Account.Email.InventoryLine", "Verify your email to save here. The link went to {0}.", address);
}
