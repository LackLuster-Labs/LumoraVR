// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading.Tasks;

namespace Lumora.Core;

// Joining a world somebody else hosts waits for a verified email address. The account service is the real gate: it
// will not hand a host the key of an account it holds back, and every host turns a joiner away without that key.
// This side only spares the trip and says why on the loading panel instead of a bare failure. -xlinka
public static class EmailJoinGate
{
    // What a host tells a joiner the account service held back. The words ARE the signal: this build matches them
    // exactly and shows the verify panel, an older build prints them as the reason its join failed. So they have to
    // read fine on their own, and changing them breaks the panel for every joiner already out there.
    public const string Refusal = "Verify your email address to join worlds.";

    // What hosts say when the account check fails for any other reason, and all an older host says for this one.
    public const string Unverifiable = "Account verification failed";

    private static readonly TimeSpan FirstReadWait = TimeSpan.FromSeconds(3);

    // Whether this account may not join anybody's world yet. Right after a sign in the first read may still be out,
    // so it waits a moment for that; offline or slow it lets the join go and the host decides.
    public static async Task<bool> HoldsAsync()
    {
        await AccountEmail.EnsureKnownAsync(FirstReadWait).ConfigureAwait(false);
        return AccountEmail.JoinsHeld;
    }

    // A host turned the join away: was it the email? An older host only says the account check failed, which an
    // unverified account always gets, so that one asks the account service again before reading it as the email.
    public static async Task<bool> RefusedForEmailAsync(string? reason)
    {
        string said = reason?.Trim() ?? string.Empty;
        if (string.Equals(said, Refusal, StringComparison.Ordinal))
        {
            AccountEmail.Refused(saves: false, joins: true);
            return true;
        }
        if (!string.Equals(said, Unverifiable, StringComparison.Ordinal))
            return false;
        await AccountEmail.RecheckAsync(force: true).ConfigureAwait(false);
        return AccountEmail.JoinsHeld;
    }
}
