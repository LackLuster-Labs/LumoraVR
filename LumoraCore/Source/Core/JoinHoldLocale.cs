// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Localization;

namespace Lumora.Core;

// What the loading panel says about a join waiting for a verified email.
public static class JoinHoldLocale
{
    public static LocaleText Joining(string world)
        => LocaleText.Keyed("WorldLoad.Hold.Joining", "Joining {0}", world);

    public static readonly LocaleText Title = LocaleText.Keyed("WorldLoad.Hold.Title", "Verify your email to join worlds");

    public static LocaleText Body(string address) => string.IsNullOrEmpty(address)
        ? LocaleText.Keyed("WorldLoad.Hold.BodyNoAddress",
            "Open the verification link in your email, then check again. Your home and the worlds you host keep working.")
        : LocaleText.Keyed("WorldLoad.Hold.Body",
            "Open the verification link sent to {0}, then check again. Your home and the worlds you host keep working.", address);

    public static readonly LocaleText HostRefused = LocaleText.Keyed("WorldLoad.Hold.HostRefused",
        "The host could not let you in: your email is not verified yet.");

    public static readonly LocaleText CheckFailed = LocaleText.Keyed("WorldLoad.Hold.CheckFailed",
        "Could not check right now. Try again in a moment.");

    public static readonly LocaleText ReadyTitle = LocaleText.Keyed("WorldLoad.Hold.ReadyTitle", "Email verified");

    public static LocaleText ReadyBody(string world)
        => LocaleText.Keyed("WorldLoad.Hold.ReadyBody", "Join {0} now, or cancel to stay where you are.", world);

    public static readonly LocaleText JoinNow = LocaleText.Keyed("WorldLoad.Hold.JoinNow", "Join now");

    public static readonly LocaleText Cancel = LocaleText.Keyed("WorldLoad.Hold.Cancel", "Cancel");
}
