// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Core.Assets;

// A provider that can load its asset again from scratch, for the debug reload and for a cloud part that arrived
// after its load failed (AssetReload). Three steps, every provider taking each one before any takes the next, so
// an asset shared by several of them is let go of by all of them before it is dropped.
internal interface IAssetReload
{
    // The address the asset was asked for, or null when nothing was; read without letting go.
    Uri? Held { get; }

    // Lets go of the asset, telling whatever uses it that it is gone. The address it was asked for, or null
    // when it had asked for nothing.
    Uri? LetGo();

    // Drops the shared instance let go of, unless something else still holds it. True when it went.
    bool Drop();

    // Asks for the asset again, which is a fresh load once it was dropped.
    void AskAgain();
}
