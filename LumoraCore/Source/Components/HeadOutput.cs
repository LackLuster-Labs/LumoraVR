using Lumora.Core;
using AquaLogger = Lumora.Core.Logging.Logger;

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
                AquaLogger.Warn("HeadOutput: No UserRoot found!");
                _loggedMissingUserRoot = true;
            }
            return;
        }

        var activeUser = UserRoot.ActiveUser;
        if (activeUser == null)
        {
            if (!_loggedMissingUser)
            {
                AquaLogger.Warn("HeadOutput: UserRoot has no ActiveUser yet");
                _loggedMissingUser = true;
            }
            return;
        }

        AquaLogger.Log($"HeadOutput: Initialized for user '{activeUser.UserName.Value}'");
    }

    public override void OnStart()
    {
        base.OnStart();

        // Hook will handle camera creation
        AquaLogger.Log($"HeadOutput: OnStart called for slot '{Slot.SlotName.Value}'");
    }

    public override void OnDestroy()
    {
        UserRoot = null;
        base.OnDestroy();
        AquaLogger.Log("HeadOutput: Destroyed");
    }
}
