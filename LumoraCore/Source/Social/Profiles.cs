// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Core.Social;

public enum PersonView
{
    Profile,
    Chat,
    Report,
}

// Opening a person from anywhere: a name in a list, pointing at someone in the world, the account card. The
// People window listens and shows them; nothing that asks needs to know where that window lives. A guest has
// no account id and so no profile, and asking for one does nothing.
public static class Profiles
{
    public static event Action<string, PersonView>? OpenRequested;

    public static void Open(string? accountId, PersonView view = PersonView.Profile)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return;
        OpenRequested?.Invoke(accountId, view);
    }
}
