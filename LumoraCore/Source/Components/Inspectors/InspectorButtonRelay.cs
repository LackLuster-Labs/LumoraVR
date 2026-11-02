// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Lumora.Core;
using Lumora.Core.Components.UI;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Components;

// press relay carrying a synced string argument to an IInspectorActionHandler - the duplication-
// and network-safe replacement for closure button actions in inspector lists
[ComponentCategory("Utility/Inspectors")]
public class InspectorButtonRelay : Component
{
    public readonly Sync<string> Argument;
    public readonly SyncRef<Component> Handler;

    public InspectorButtonRelay()
    {
        Argument = new Sync<string>(this, "");
        Handler = new SyncRef<Component>(this);
    }

    [SyncMethod]
    public void OnPressed(Button button, UIInteractionContext context)
    {
        // Context-aware handlers get the presser's context (destructive actions confirm on THAT
        // user's menu); plain handlers keep the simple string contract.
        if (Handler.Target is IInspectorActionContextHandler contextHandler)
        {
            contextHandler.HandleInspectorAction(Argument.Value ?? "", context);
            return;
        }
        (Handler.Target as IInspectorActionHandler)?.HandleInspectorAction(Argument.Value ?? "");
    }
}
