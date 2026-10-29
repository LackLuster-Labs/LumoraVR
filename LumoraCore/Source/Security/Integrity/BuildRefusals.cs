// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Security.Integrity;

// The words a host turns a joiner on other game files away with. They go over the wire in English: this build swaps
// in the reader's language, an older one prints them as they are, so they have to read fine on their own and must not
// change once out.
public static class BuildRefusals
{
    public const string Modified = "Your game files are modified. Verify the game files in Steam to join this session.";

    public const string HostModified = "The host's game files are modified, so this session only takes games with the same changes.";

    // What every build before the integrity check said for any mismatch; kept so older hosts read right too.
    public const string OtherVersion = "Incompatible client version";

    // What the account service answers a host fetching the key of a joiner who reported other game files.
    public const string ServiceCode = "build_mismatch";

    public static bool IsBuildRefusal(string? reason)
        => reason is Modified or HostModified or OtherVersion;

    // The refusal in the reader's language when it is one of ours, else as the host wrote it.
    public static string Localize(string? reason) => reason switch
    {
        Modified => IntegrityLocale.Modified.Resolve(),
        HostModified => IntegrityLocale.HostModified.Resolve(),
        OtherVersion => IntegrityLocale.OtherVersion.Resolve(),
        _ => reason ?? string.Empty,
    };
}
