// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Creates a camera that follows the user's head.
/// This is now an ImplementableComponent so it can have platform-specific hooks.
/// </summary>
[ComponentCategory("Users")]
public class HeadOutput : ImplementableComponent
{
    public UserRoot UserRoot { get; private set; }
    private bool _loggedMissingUserRoot;
    private bool _loggedMissingUser;

    public override void OnAwake()
    {
        base.OnAwake();

        UserRoot = Slot.GetComponent<UserRoot>();
        if (UserRoot == null)
        {
            if (!_loggedMissingUserRoot)
            {
                LumoraLogger.Warn("HeadOutput: No UserRoot found!");
                _loggedMissingUserRoot = true;
            }
            return;
        }

        var activeUser = UserRoot.ActiveUser;
        if (activeUser == null)
        {
            if (!_loggedMissingUser)
            {
                LumoraLogger.Warn("HeadOutput: UserRoot has no ActiveUser yet");
                _loggedMissingUser = true;
            }
            return;
        }

        LumoraLogger.Log($"HeadOutput: Initialized for user '{activeUser.UserName.Value}'");
    }

    public override void OnStart()
    {
        base.OnStart();

        // Hook will handle camera creation
        LumoraLogger.Log($"HeadOutput: OnStart called for slot '{Slot.SlotName.Value}'");
    }

    public override void OnDestroy()
    {
        UserRoot = null;
        base.OnDestroy();
        LumoraLogger.Log("HeadOutput: Destroyed");
    }
}
