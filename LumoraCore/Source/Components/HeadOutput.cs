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

    public override void OnAwake()
    {
        base.OnAwake();

        UserRoot = Slot.GetComponent<UserRoot>();
        if (UserRoot == null)
        {
            AquaLogger.Warn("HeadOutput: No UserRoot found!");
            return;
        }

        AquaLogger.Log($"HeadOutput: Initialized for user '{UserRoot.ActiveUser.UserName.Value}'");
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
